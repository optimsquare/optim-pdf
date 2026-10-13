using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OptimPdf.Core.Annotations;
using OptimPdf.App.Dialogs;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Forms;
using OptimPdf.Core.IO;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Security;
using OptimPdf.Core.Signatures;
using PdfSharp.Pdf;

namespace OptimPdf.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public enum ToolMode
{
    Select, Highlight, Underline, StrikeOut, Note, FreeText, Ink, Stamp, Crop, AddText, AddImage, Sign,
    EditText, Redact, TextField, CheckBoxField, DropdownField, SignatureField, DigitalSign, SelectObject,
}

/// <summary>A selected item on a page: page content (text box, image, signature, drawing) or an annotation.</summary>
public sealed record SelectionTarget(int PageIndex, PdfRect Bounds, Core.Editing.PageItem? Content, AnnotationInfo? Annotation, string Label);

/// <summary>State of one open document tab.</summary>
public sealed class DocumentViewModel : ObservableObject, IDisposable
{
    /// <summary>DIPs per PDF point at 100% zoom (actual size on a 96-DPI screen).</summary>
    public const double DipPerPoint = 96.0 / 72.0;
    public static readonly double[] ZoomSteps = [0.1, 0.25, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 6, 8];

    double _zoom = 1;
    int _viewRotation;
    int _currentPage;
    int _version;
    int _currentHit = -1;

    public static readonly RecoveryStore Recovery = new(RecoveryStore.DefaultDirectory);
    readonly System.Windows.Threading.DispatcherTimer _autosave;
    long _autosavedVersion = -1;

    public DocumentViewModel(DocumentSession session)
    {
        Session = session;
        Session.Changed += (_, _) => Application.Current.Dispatcher.Invoke(Reload);
        Reload();

        // Snapshot unsaved changes every 30 seconds so they survive a crash or power loss.
        _autosave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosave.Tick += (_, _) => Autosave();
        _autosave.Start();
    }

    /// <summary>Writes a recovery snapshot when there are unsaved changes not yet snapshotted.</summary>
    public void Autosave()
    {
        if (!Session.IsDirty)
        {
            if (_autosavedVersion != -1) { Recovery.Delete(Session.Id); _autosavedVersion = -1; }
            return;
        }
        if (_autosavedVersion == Session.ContentVersion) return;
        var (bytes, version) = (Session.CurrentBytes, Session.ContentVersion);
        var (id, path, name, encrypted) = (Session.Id, Session.FilePath, Session.DisplayName, Session.IsEncrypted);
        _autosavedVersion = version;
        Task.Run(() =>
        {
            try { Recovery.Save(id, bytes, path, name, encrypted); }
            catch (Exception ex) { App.LogError(ex); }
        });
    }

    public DocumentSession Session { get; }
    public ObservableCollection<PageViewModel> Pages { get; } = [];
    public ObservableCollection<OutlineNode> Outline { get; } = [];
    public ObservableCollection<CommentItem> Comments { get; } = [];
    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];
    public ObservableCollection<FieldItem> Fields { get; } = [];

    /// <summary>Areas marked for redaction; nothing is removed until <see cref="ApplyRedactions"/>.</summary>
    public ObservableCollection<RedactionArea> Redactions { get; } = [];

    /// <summary>Validation results of the document's digital signatures (empty when unsigned).</summary>
    public IReadOnlyList<SignatureStatus> Signatures { get; private set; } = [];

    /// <summary>One-line summary of the signatures, for the banner; null when unsigned.</summary>
    public string? SignatureSummary => Signatures.Count == 0 ? null
        : Signatures.Any(sg => !sg.IsValid)
            ? "At least one signature is invalid: the document was changed after signing, or the signature is damaged."
        : Signatures.Any(sg => !sg.CertificateTrusted)
            ? $"Signed ({Signatures.Count}). The signed content is intact, but the signer's identity is not confirmed by a trusted certificate."
            : $"Signed ({Signatures.Count}). All signatures are valid.";

    public bool SignaturesValid => Signatures.All(sg => sg.IsValid);

    public IReadOnlyList<SignatureItem> SignatureItems => Signatures.Select(sg => new SignatureItem(sg)).ToList();

    /// <summary>Short explanation shown when the document cannot be edited as opened.</summary>
    public string? AccessNotice => Session.IsReadOnly
        ? "Protected document opened for viewing. Reopen it with its permissions password to make changes."
        : null;

    public string Title => Session.DisplayName + (Session.IsDirty ? " •" : "");
    public string? FilePath => Session.FilePath;

    /// <summary>Incremented whenever document content changes, invalidating rendered bitmaps.</summary>
    public int Version => _version;

    /// <summary>Raised when page sizes or positions change (zoom, rotation, page set).</summary>
    public event EventHandler? LayoutChanged;
    /// <summary>Raised when search highlights or selection overlays must be redrawn.</summary>
    public event EventHandler? OverlaysChanged;

    public double Zoom
    {
        get => _zoom;
        set
        {
            value = Math.Clamp(value, ZoomSteps[0], ZoomSteps[^1]);
            if (Set(ref _zoom, value))
            {
                OnPropertyChanged(nameof(ZoomPercent));
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string ZoomPercent => $"{Math.Round(_zoom * 100)}%";

    /// <summary>View-only rotation in quarter turns; does not modify the document.</summary>
    public int ViewRotation
    {
        get => _viewRotation;
        set
        {
            if (Set(ref _viewRotation, ((value % 4) + 4) % 4))
                LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (Set(ref _currentPage, Math.Clamp(value, 0, Math.Max(0, Pages.Count - 1))))
                OnPropertyChanged(nameof(PageLabel));
        }
    }

    public string PageLabel => $"{CurrentPage + 1} / {Pages.Count}";

    public int CurrentHit
    {
        get => _currentHit;
        set
        {
            if (Set(ref _currentHit, value)) OverlaysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public TextSelection? Selection { get; private set; }

    /// <summary>The item with move/resize handles, if any.</summary>
    public SelectionTarget? SelectedObject { get; private set; }

    public void SelectObject(SelectionTarget? target)
    {
        SelectedObject = target;
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    bool _highlightFields = true;

    /// <summary>Tints form fields on the page so they are easy to find (display only).</summary>
    public bool HighlightFields
    {
        get => _highlightFields;
        set { if (Set(ref _highlightFields, value)) OverlaysChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Fields in reading order: page, then top to bottom, then left to right (Tab follows it).</summary>
    public IReadOnlyList<FieldItem> FieldsInOrder => Fields
        .Where(f => f.Info.PageIndex >= 0 && f.Info.Rect is not null)
        .OrderBy(f => f.Info.PageIndex).ThenByDescending(f => Math.Round(f.Info.Rect!.Value.Top / 6)).ThenBy(f => f.Info.Rect!.Value.Left)
        .ToList();

    public void SetSelection(TextSelection? selection)
    {
        Selection = selection;
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => Zoom = ZoomSteps.FirstOrDefault(z => z > Zoom + 0.001, ZoomSteps[^1]);
    public void ZoomOut() => Zoom = ZoomSteps.LastOrDefault(z => z < Zoom - 0.001, ZoomSteps[0]);

    public void FitWidth(double viewportWidth)
    {
        if (Pages.Count == 0) return;
        double widest = Pages.Max(p => p.RotatedWidthPt);
        Zoom = Math.Max(0.1, (viewportWidth - 48) / (widest * DipPerPoint));
    }

    public void FitPage(double viewportWidth, double viewportHeight)
    {
        if (Pages.Count == 0) return;
        var page = Pages[CurrentPage];
        Zoom = Math.Max(0.1, Math.Min((viewportWidth - 48) / (page.RotatedWidthPt * DipPerPoint),
            (viewportHeight - 32) / (page.RotatedHeightPt * DipPerPoint)));
    }

    /// <summary>Applies an edit; on failure shows the error and leaves the document untouched.</summary>
    public bool Apply(string description, Action<PdfDocument> edit) => Run(description, () => Session.Apply(description, edit));

    /// <summary>A two-stage edit: PDFium first, then the PDF writer.</summary>
    public bool Apply(string description, Func<byte[], string?, byte[]> prepare, Action<PdfDocument> edit) =>
        Run(description, () => Session.Apply(description, prepare, edit));

    bool _acceptedSignatureLoss;

    /// <summary>
    /// Runs a change to the document with the common checks: protected documents, the open password needed to keep
    /// protection, and a warning that rewriting a signed document invalidates its signatures.
    /// </summary>
    public bool Run(string description, Action change, bool keepsSignatures = false)
    {
        var owner = Application.Current.MainWindow;
        if (Session.IsReadOnly)
        {
            MessageBox.Show(owner, AccessNotice, "Protected document", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        // A document opened with its permissions password may also have an open password we cannot read
        // from the file. Ask once, so the saved file stays protected the same way.
        while (Session.NeedsOpenPassword && !keepsSignatures)
        {
            var pw = InputDialog.ShowPassword(owner, "Keep document protected",
                "This PDF also has an open password. Enter it so the file stays protected after your changes.\n\n" +
                "Leave it empty to remove the open password (the permissions password stays).");
            if (pw is null) return false;
            try { Session.ProvideOpenPassword(pw.Length == 0 ? null : pw); }
            catch (UnauthorizedAccessException ex) { MessageBox.Show(owner, ex.Message, "Keep document protected"); }
        }
        if (!keepsSignatures && !_acceptedSignatureLoss && Signatures.Count > 0)
        {
            var answer = MessageBox.Show(owner,
                $"This document has {Signatures.Count} digital signature(s). Changing it will make them invalid.\n\n" +
                "To keep the signed version, save a copy before editing. Continue anyway?",
                "Signed document", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return false;
            _acceptedSignatureLoss = true;
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            change();
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Mouse.OverrideCursor = null;
            MessageBox.Show(owner, $"{description} failed:\n\n{ex.Message}", "Optim PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    /// <summary>Validates the signatures again (after the user changed which identities they trust).</summary>
    public void Revalidate()
    {
        try { Signatures = Session.SignatureCount == 0 ? [] : SignatureValidator.Validate(Session.CurrentBytes, Session.Password, Services.DigitalIdStore.TrustAnchors()); }
        catch (Exception ex) { App.LogError(ex); }
        OnPropertyChanged(nameof(Signatures));
        OnPropertyChanged(nameof(SignatureSummary));
        OnPropertyChanged(nameof(SignatureItems));
    }

    // ---------- Redaction marks ----------

    public void AddRedaction(RedactionArea area)
    {
        Redactions.Add(area);
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearRedactions()
    {
        Redactions.Clear();
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks every match of a search for redaction. Returns the number of matches.</summary>
    public int MarkSearchForRedaction(string query, bool matchCase, bool wholeWord)
    {
        var hits = Session.Renderer.Search(query, matchCase, wholeWord);
        foreach (var hit in hits)
            foreach (var r in hit.Rects)
                Redactions.Add(new RedactionArea(hit.PageIndex, new PdfRect(r.Left - 1, r.Bottom - 1, r.Right + 1, r.Top + 1)));
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
        return hits.Count;
    }

    public RedactionReport? ApplyRedactions()
    {
        if (Redactions.Count == 0) return null;
        var areas = Redactions.ToList();
        RedactionReport? report = null;
        if (!Run("Redact", () => report = Redactor.Redact(Session, areas))) return null;
        ClearRedactions();
        return report;
    }

    CancellationTokenSource? _search;

    /// <summary>
    /// Searches on a background thread so long documents never freeze the window; a new search cancels the
    /// previous one. Returns the number of matches, or -1 when the search was superseded.
    /// </summary>
    public async Task<int> SearchAsync(string query, bool matchCase, bool wholeWord)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        SearchResults.Clear();
        _currentHit = -1;
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var renderer = Session.Renderer;
        List<SearchResultItem> items;
        try
        {
            items = await Task.Run(() => renderer.Search(query, matchCase, wholeWord, cts.Token).Select(hit =>
            {
                int start = Math.Max(0, hit.CharIndex - 30);
                string context = renderer.GetText(hit.PageIndex, start, hit.CharCount + 60).Replace('\r', ' ').Replace('\n', ' ').Trim();
                return new SearchResultItem(hit, context);
            }).ToList(), cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return -1; } // Superseded, or the document changed.
        if (cts.IsCancellationRequested || !ReferenceEquals(renderer, Session.Renderer)) return -1;
        foreach (var item in items) SearchResults.Add(item);
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
        return items.Count;
    }

    public void ClearSearch()
    {
        SearchResults.Clear();
        CurrentHit = -1;
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    void Reload()
    {
        _version++;
        var renderer = Session.Renderer;
        int count = renderer.PageCount;

        // Reuse page view models where possible so bound controls stay in place.
        while (Pages.Count > count) Pages.RemoveAt(Pages.Count - 1);
        for (int i = 0; i < count; i++)
        {
            var size = renderer.GetPageSize(i);
            if (i < Pages.Count) Pages[i].Reset(size.Width, size.Height);
            else Pages.Add(new PageViewModel(this, i, size.Width, size.Height));
        }
        CurrentPage = Math.Min(CurrentPage, count - 1);

        Outline.Clear();
        foreach (var item in renderer.GetOutline()) Outline.Add(new OutlineNode(item));

        Comments.Clear();
        Fields.Clear();
        try
        {
            // One object-model load for both lists, and none at all when the file has no annotations or form.
            if (Session.MayHaveAnnotationsOrForm())
            {
                var (annotations, fields) = Session.Query(d => (AnnotationService.List(d), FormService.ListFields(d)));
                foreach (var a in annotations) Comments.Add(new CommentItem(a));
                foreach (var f in fields) Fields.Add(new FieldItem(this, f));
            }
        }
        catch (Exception ex) { App.LogError(ex); } // Comments and fields are optional; never block viewing.

        try { Signatures = renderer.GetSignatures().Count == 0 ? [] : SignatureValidator.Validate(Session.CurrentBytes, Session.Password, Services.DigitalIdStore.TrustAnchors()); }
        catch (Exception ex) { App.LogError(ex); Signatures = []; }
        if (Signatures.Count == 0) _acceptedSignatureLoss = false;
        // Marks on pages that no longer exist are dropped.
        for (int i = Redactions.Count - 1; i >= 0; i--) if (Redactions[i].PageIndex >= count) Redactions.RemoveAt(i);
        OnPropertyChanged(nameof(Signatures));
        OnPropertyChanged(nameof(SignatureSummary));
        OnPropertyChanged(nameof(SignatureItems));

        // Search results refer to old character positions.
        if (SearchResults.Count > 0) SearchResults.Clear();
        _currentHit = -1;
        Selection = null;
        SelectedObject = null; // Object indices refer to the old version; the view reselects after its own edits.

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageLabel));
        ReleaseMemoryIfLarge();
        OnPropertyChanged(nameof(AccessNotice));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// After an edit of a large file the previous versions' buffers are garbage; compact the large-object heap so the
    /// memory goes back to Windows instead of staying reserved by the app.
    /// </summary>
    void ReleaseMemoryIfLarge()
    {
        if (Session.CurrentBytes.Length < 32 * 1024 * 1024) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Closes the document; its recovery snapshot is no longer needed.</summary>
    public void Dispose()
    {
        _autosave.Stop();
        Recovery.Delete(Session.Id);
        Session.Dispose();
    }
}

/// <summary>A text selection on one page, as a character range.</summary>
public sealed record TextSelection(int PageIndex, int CharIndex, int CharCount, IReadOnlyList<PdfRect> Rects, string Text);

public sealed class PageViewModel : ObservableObject
{
    readonly DocumentViewModel _owner;
    BitmapSource? _image;
    BitmapSource? _thumbnail;
    (double Zoom, int Rotation, int Version, double Dpi) _imageKey;
    int _thumbVersion = -1;
    bool _rendering;

    public PageViewModel(DocumentViewModel owner, int index, double widthPt, double heightPt)
    {
        _owner = owner;
        Index = index;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    public int Index { get; }
    public int Number => Index + 1;
    public double WidthPt { get; private set; }
    public double HeightPt { get; private set; }
    public double RotatedWidthPt => _owner.ViewRotation % 2 == 0 ? WidthPt : HeightPt;
    public double RotatedHeightPt => _owner.ViewRotation % 2 == 0 ? HeightPt : WidthPt;
    public double DisplayWidth => RotatedWidthPt * DocumentViewModel.DipPerPoint * _owner.Zoom;
    public double DisplayHeight => RotatedHeightPt * DocumentViewModel.DipPerPoint * _owner.Zoom;

    public const double ThumbnailWidth = 132;
    public double ThumbnailHeight => ThumbnailWidth * HeightPt / WidthPt;

    public BitmapSource? Image { get => _image; private set => Set(ref _image, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }

    internal void Reset(double widthPt, double heightPt)
    {
        WidthPt = widthPt;
        HeightPt = heightPt;
        OnPropertyChanged(nameof(ThumbnailHeight));
        _thumbVersion = -1;
        if (_thumbVisible) EnsureThumbnail();
    }

    bool _thumbVisible;

    /// <summary>Thumbnails render only while their list item is realized, so huge documents stay cheap.</summary>
    public void SetThumbnailVisible(bool visible)
    {
        _thumbVisible = visible;
        if (visible) EnsureThumbnail();
    }

    /// <summary>Renders the page for the current zoom and screen DPI if not already rendered.</summary>
    public void EnsureRendered(double dpiScale)
    {
        var key = (_owner.Zoom, _owner.ViewRotation, _owner.Version, dpiScale);
        if (_rendering || (_image is not null && _imageKey == key)) return;
        _rendering = true;
        var renderer = _owner.Session.Renderer;
        double scale = DocumentViewModel.DipPerPoint * key.Zoom * dpiScale;

        Task.Run(() => ToBitmap(renderer.Render(Index, scale, key.ViewRotation), dpiScale))
            .ContinueWith(t =>
            {
                _rendering = false;
                _ = t.Exception; // A render can race with an edit that disposes the old renderer; just retry.
                if (t.IsCompletedSuccessfully)
                {
                    _imageKey = key;
                    Image = t.Result;
                }
                // The document may have changed while rendering; render again with the new state.
                if (key != (_owner.Zoom, _owner.ViewRotation, _owner.Version, dpiScale) && _visible)
                    EnsureRendered(dpiScale);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    bool _visible;

    public void SetVisible(bool visible, double dpiScale)
    {
        _visible = visible;
        if (visible) EnsureRendered(dpiScale);
        else
        {
            Image = null; // Free memory for pages scrolled out of view.
            _imageKey = default;
        }
    }

    public void EnsureThumbnail()
    {
        if (_thumbVersion == _owner.Version) return;
        int version = _owner.Version;
        _thumbVersion = version;
        var renderer = _owner.Session.Renderer;
        double scale = ThumbnailWidth * 1.5 / WidthPt; // 1.5x for crisp thumbnails on high-DPI screens.

        Task.Run(() => ToBitmap(renderer.Render(Index, scale), 1.5))
            .ContinueWith(t =>
            {
                _ = t.Exception;
                if (t.IsCompletedSuccessfully && version == _owner.Version) Thumbnail = t.Result;
                else if (version != _owner.Version)
                {
                    _thumbVersion = -1;
                    if (_thumbVisible) EnsureThumbnail();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static BitmapSource ToBitmap(RenderedPage page, double dpiScale)
    {
        var bmp = BitmapSource.Create(page.PixelWidth, page.PixelHeight, 96 * dpiScale, 96 * dpiScale,
            PixelFormats.Pbgra32, null, page.Pixels, page.Stride);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>A form field as shown in the Form panel; edits are applied to the document.</summary>
public sealed class FieldItem(DocumentViewModel owner, FormFieldInfo info)
{
    public FormFieldInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Label => Info.Name + (Info.Required ? " *" : "") + (Info.PageIndex >= 0 ? $"  · p.{Info.PageIndex + 1}" : "");
    public bool IsText => Info.Kind == FormFieldKind.Text;
    public bool IsCheck => Info.Kind == FormFieldKind.CheckBox;
    public bool IsChoice => Info.Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox or FormFieldKind.RadioGroup;
    public bool IsOther => !IsText && !IsCheck && !IsChoice;
    public bool Editable => !Info.ReadOnly;
    public string KindLabel => Info.Kind switch
    {
        FormFieldKind.PushButton => "Button",
        FormFieldKind.Signature => "Signature field",
        _ => Info.Kind.ToString(),
    };
    public IReadOnlyList<string> Options => Info.Options;
    public bool MultiLine => Info.MultiLine;

    public string Text
    {
        get => Info.Value ?? "";
        set => Commit(value);
    }

    public bool Checked
    {
        get => Info.Value == "true";
        set => Commit(value ? "true" : "false");
    }

    public string? Choice
    {
        get => Info.Value;
        set { if (value is not null) Commit(value); }
    }

    void Commit(string value)
    {
        if (value == (Info.Value ?? "")) return;
        owner.Apply($"Fill “{Info.Name}”", d => FormService.SetValue(d, Info.Name, value));
    }
}

/// <summary>A digital signature as shown in the Signatures panel.</summary>
public sealed class SignatureItem(SignatureStatus value)
{
    public SignatureStatus Value { get; } = value;
    public bool CanTrust => Value.IsValid && !Value.CertificateTrusted && Value.Certificate is not null;
    public string Signer => Value.SignerName.Length > 0 ? Value.SignerName : "Unknown signer";

    public string Icon => !Value.IsValid ? "\uE783" : Value.CertificateTrusted ? "\uE73E" : "\uE7BA";

    public Brush StatusBrush => new SolidColorBrush(!Value.IsValid ? Color.FromRgb(220, 38, 38)
        : Value.CertificateTrusted ? Color.FromRgb(22, 163, 74) : Color.FromRgb(217, 119, 6));

    public string Status =>
        !Value.IsValid ? Value.Error ?? "Invalid signature."
        : (Value.ChangedAfterSigning ? "Valid for the version that was signed; changes were added later. " : "Valid: the document has not changed since it was signed. ")
          + Value.TrustDetail;

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Value.TimestampTime is { } ts) parts.Add($"Time-stamped {ts.ToLocalTime():g} (trusted time)");
            else if (Value.ClaimedTime is { } t) parts.Add($"Signed {t.ToLocalTime():g} (time from the signer's computer)");
            if (!string.IsNullOrWhiteSpace(Value.Reason)) parts.Add("Reason: " + Value.Reason);
            if (Value.Certificate is { } c) parts.Add($"Certificate: {c.Subject}; issued by {c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, true)}; valid until {c.NotAfter:d}");
            parts.Add(Value.SubFilter);
            return string.Join("\n", parts);
        }
    }
}

public sealed class OutlineNode(OutlineItem item)
{
    public string Title { get; } = item.Title;
    public int PageIndex { get; } = item.PageIndex;
    public List<OutlineNode> Children { get; } = item.Children.Select(c => new OutlineNode(c)).ToList();
}

public sealed class CommentItem(AnnotationInfo info)
{
    public AnnotationInfo Info { get; } = info;
    public string Kind => Info.Subtype switch
    {
        "Text" => "Note",
        "FreeText" => "Text box",
        "StrikeOut" => "Strikethrough",
        "Ink" => "Drawing",
        _ => Info.Subtype,
    };
    public string Header => $"{Kind} · page {Info.PageIndex + 1}";
    public string Body => Info.Contents ?? "";
    public string Author => Info.Author ?? "";
}

public sealed class SearchResultItem(SearchHit hit, string context)
{
    public SearchHit Hit { get; } = hit;
    public string Page => $"Page {Hit.PageIndex + 1}";
    public string Context { get; } = context;
}
