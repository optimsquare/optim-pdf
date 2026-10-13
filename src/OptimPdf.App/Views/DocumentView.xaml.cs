using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OptimPdf.App.Dialogs;
using OptimPdf.App.ViewModels;
using OptimPdf.Core.Annotations;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Forms;
using OptimPdf.Core.Security;
using OptimPdf.Core.Signatures;
using OptimPdf.Core.Pages;
using OptimPdf.Core.Rendering;

namespace OptimPdf.App.Views;

public partial class DocumentView : UserControl
{
    const double PageGap = 16;
    const double PageMargin = 20;
    /// <summary>Extra area above and below the viewport where pages are pre-rendered.</summary>
    const double RenderBuffer = 800;

    readonly Dictionary<int, PageView> _realized = [];
    double[] _tops = [];
    bool _suppressThumbnailNavigation;

    public DocumentView(DocumentViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.LayoutChanged += (_, _) => Relayout(keepPosition: true);
        viewModel.OverlaysChanged += (_, _) => RedrawOverlays();
        viewModel.Outline.CollectionChanged += (_, _) => UpdateEmptyStates();
        viewModel.Comments.CollectionChanged += (_, _) => UpdateEmptyStates();
        viewModel.Fields.CollectionChanged += (_, _) => UpdateEmptyStates();
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DocumentViewModel.SignatureSummary)) UpdateSignatureBanner(); };
        Loaded += (_, _) =>
        {
            Relayout(keepPosition: false);
            UpdateEmptyStates();
            UpdateSignatureBanner();
        };
        UpdateEmptyStates();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection(),
            (_, e) => e.CanExecute = ViewModel.Selection is not null));
        PreviewKeyDown += SelectionKeys;
    }

    public DocumentViewModel ViewModel { get; }

    /// <summary>Device pixels per DIP on the monitor currently showing this view.</summary>
    double DpiScale => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Pages the page commands act on: a multi-selection in the thumbnails, else the current page.</summary>
    public IReadOnlyList<int> TargetPages =>
        Thumbnails.SelectedItems.Count > 1
            ? Thumbnails.SelectedItems.Cast<PageViewModel>().Select(p => p.Index).Order().ToList()
            : [ViewModel.CurrentPage];

    public double ViewportWidth => Scroller.ViewportWidth;
    public double ViewportHeight => Scroller.ViewportHeight;

    // ---------- Layout and virtualization ----------

    void Relayout(bool keepPosition)
    {
        // Remember where we are (page + fraction) so zoom and edits keep the reading position.
        int anchorPage = ViewModel.CurrentPage;
        double fraction = 0;
        // Decide before _tops is rebuilt: on the first layout there is no previous position to keep.
        bool restore = keepPosition && anchorPage < _tops.Length;
        if (restore)
        {
            double pageHeight = Math.Max(1, ViewModel.Pages.Count > anchorPage ? ViewModel.Pages[anchorPage].DisplayHeight : 1);
            fraction = (Scroller.VerticalOffset - _tops[anchorPage]) / pageHeight;
        }
        double horizontalRatio = Scroller.ExtentWidth > 0 ? (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / Scroller.ExtentWidth : 0.5;

        var pages = ViewModel.Pages;
        _tops = new double[pages.Count];
        double y = PageMargin, maxWidth = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            _tops[i] = y;
            y += pages[i].DisplayHeight + PageGap;
            maxWidth = Math.Max(maxWidth, pages[i].DisplayWidth);
        }
        PageCanvas.Height = y - PageGap + PageMargin;
        // The ScrollViewer centers the canvas when it is narrower than the viewport.
        PageCanvas.Width = maxWidth + 2 * PageMargin;

        // Pages that no longer exist, or whose view model was replaced, are dropped.
        foreach (var (index, view) in _realized.ToList())
        {
            if (index >= pages.Count || !ReferenceEquals(view.Page, pages[index]))
            {
                PageCanvas.Children.Remove(view);
                _realized.Remove(index);
            }
        }
        foreach (var (index, view) in _realized)
        {
            view.UpdateSize();
            Position(view, index);
        }

        // Only allow horizontal scrolling when pages are wider than the view; otherwise the
        // Fluent ScrollViewer shows a needless scrollbar a few pixels wide.
        UpdateHorizontalScrollBar();

        if (restore && anchorPage < _tops.Length)
        {
            Scroller.ScrollToVerticalOffset(_tops[anchorPage] + fraction * pages[anchorPage].DisplayHeight);
            Scroller.ScrollToHorizontalOffset(horizontalRatio * PageCanvas.Width - Scroller.ViewportWidth / 2);
        }
        UpdateVisiblePages();
    }

    void UpdateHorizontalScrollBar()
    {
        // ViewportWidth lags behind on the first layout, so compare with the room actually available
        // next to the vertical scrollbar.
        double available = Scroller.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        var wanted = PageCanvas.Width > available ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (Scroller.HorizontalScrollBarVisibility != wanted) Scroller.HorizontalScrollBarVisibility = wanted;
    }

    void Position(PageView view, int index)
    {
        Canvas.SetTop(view, _tops[index]);
        Canvas.SetLeft(view, (PageCanvas.Width - view.Width) / 2);
    }

    void UpdateVisiblePages()
    {
        if (_tops.Length == 0) return;
        double top = Scroller.VerticalOffset - RenderBuffer;
        double bottom = Scroller.VerticalOffset + Scroller.ViewportHeight + RenderBuffer;
        int first = Math.Max(0, LastPageStartingBefore(top));
        var visible = new HashSet<int>();
        for (int i = first; i < _tops.Length && _tops[i] <= bottom; i++)
            if (_tops[i] + ViewModel.Pages[i].DisplayHeight >= top) visible.Add(i);

        foreach (var (index, view) in _realized.ToList())
        {
            if (visible.Contains(index)) continue;
            view.Page.SetVisible(false, DpiScale);
            PageCanvas.Children.Remove(view);
            _realized.Remove(index);
        }
        foreach (int i in visible)
        {
            if (!_realized.TryGetValue(i, out var view))
            {
                view = new PageView(this, ViewModel.Pages[i]);
                _realized[i] = view;
                Position(view, i);
                PageCanvas.Children.Add(view);
                view.RedrawOverlays();
            }
            view.Page.SetVisible(true, DpiScale);
        }

        // The current page is the one under the upper third of the viewport.
        ViewModel.CurrentPage = Math.Max(0, LastPageStartingBefore(Scroller.VerticalOffset + Scroller.ViewportHeight / 3));
    }

    int LastPageStartingBefore(double y)
    {
        int index = Array.BinarySearch(_tops, y);
        return index >= 0 ? index : ~index - 1;
    }

    void RedrawOverlays()
    {
        foreach (var view in _realized.Values) view.RedrawOverlays();
    }

    public void GoToPage(int index, double? pdfY = null)
    {
        if ((uint)index >= (uint)_tops.Length) return;
        double offset = _tops[index] - PageGap / 2;
        if (pdfY is { } y && _realized.TryGetValue(index, out var view))
            offset = _tops[index] + view.ToView(0, y).Y - Scroller.ViewportHeight / 3;
        else if (pdfY is { } y2)
        {
            // Not realized yet: approximate assuming no page rotation.
            var page = ViewModel.Pages[index];
            offset = _tops[index] + (1 - y2 / page.HeightPt) * page.DisplayHeight - Scroller.ViewportHeight / 3;
        }
        Scroller.ScrollToVerticalOffset(Math.Max(0, offset));
        ViewModel.CurrentPage = index;
    }

    /// <summary>Moving the window to a monitor with a different scale re-renders pages at the new resolution.</summary>
    protected override void OnDpiChanged(System.Windows.DpiScale oldDpi, System.Windows.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        foreach (var view in _realized.Values) view.Page.SetVisible(true, newDpi.DpiScaleX);
    }

    void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) UpdateHorizontalScrollBar();
        UpdateVisiblePages();
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout(keepPosition: true);

    void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Delta > 0) ViewModel.ZoomIn(); else ViewModel.ZoomOut();
        e.Handled = true;
    }

    // ---------- Tool gestures (called by PageView) ----------

    public void UpdateSelection(PageView page, (double X, double Y) a, (double X, double Y) b) =>
        ViewModel.SetSelection(ComputeSelection(page.Page.Index, a, b));

    TextSelection? ComputeSelection(int pageIndex, (double X, double Y) a, (double X, double Y) b)
    {
        var renderer = ViewModel.Session.Renderer;
        int ia = NearestChar(renderer, pageIndex, a);
        int ib = NearestChar(renderer, pageIndex, b);
        if (ia < 0 || ib < 0) return null;
        int start = Math.Min(ia, ib), count = Math.Abs(ib - ia) + 1;
        var rects = renderer.GetTextRects(pageIndex, start, count);
        return rects.Count == 0 ? null : new TextSelection(pageIndex, start, count, rects, renderer.GetText(pageIndex, start, count));
    }

    static int NearestChar(PdfiumDocument renderer, int pageIndex, (double X, double Y) p)
    {
        int i = renderer.GetCharIndexAt(pageIndex, p.X, p.Y, 4);
        return i >= 0 ? i : renderer.GetCharIndexAt(pageIndex, p.X, p.Y, 18);
    }

    public void PageClicked(PageView page, (double X, double Y) p)
    {
        int index = page.Page.Index;
        switch (ToolState.Mode)
        {
            case ToolMode.Select:
                ViewModel.SetSelection(null);
                // Clicking a form field jumps to its editor in the Form panel; an empty signature field offers to sign.
                ViewModel.SelectObject(null);
                if (FieldAt(index, p) is { } field) ActivateField(field);
                break;

            case ToolMode.SelectObject:
                SelectAt(index, p);
                break;

            case ToolMode.EditText:
                BeginTextEdit(page, index, p);
                break;

            case ToolMode.Redact:
                MarkWordForRedaction(index, p);
                break;

            case ToolMode.TextField:
                AddFormField(index, new PdfRect(p.X, p.Y - 22, p.X + 200, p.Y));
                break;
            case ToolMode.CheckBoxField:
                AddFormField(index, new PdfRect(p.X - 7, p.Y - 7, p.X + 7, p.Y + 7));
                break;
            case ToolMode.DropdownField:
                AddFormField(index, new PdfRect(p.X, p.Y - 22, p.X + 160, p.Y));
                break;
            case ToolMode.SignatureField:
                AddFormField(index, new PdfRect(p.X, p.Y - 60, p.X + 200, p.Y));
                break;
            case ToolMode.DigitalSign:
                SignDigitally(index, new PdfRect(p.X, p.Y - 60, p.X + 210, p.Y), null);
                break;

            case ToolMode.AddText:
                BeginNewText(page, index, p.X, p.Y, maxWidth: null);
                break;

            case ToolMode.AddImage:
            {
                var (iw, ih) = FitOnPage(index, p, 220, 220);
                PlaceImage(index, p.X, p.Y, iw, ih);
                break;
            }

            case ToolMode.Sign:
            {
                var (sw, sh) = FitOnPage(index, p, 180, 70);
                PlaceSignature(index, p.X, p.Y, sw, sh);
                break;
            }

            case ToolMode.Highlight:
            case ToolMode.Underline:
            case ToolMode.StrikeOut:
                ViewModel.SetSelection(null);
                break;

            case ToolMode.Note:
                if (InputDialog.Show(Window.GetWindow(this), "Add note", "Note text:", multiline: true) is { Length: > 0 } note)
                    ViewModel.Apply("Add note", d => AnnotationService.AddNote(d, index, p.X, p.Y, note, ToolState.ColorOverride));
                break;

            case ToolMode.FreeText:
                BeginTextBoxComment(page, index, p.X, p.Y, 240);
                break;

            case ToolMode.Stamp:
                const double w = 170, h = 56;
                ViewModel.Apply("Add stamp", d => AnnotationService.AddStamp(d, index,
                    new PdfRect(p.X - w / 2, p.Y - h / 2, p.X + w / 2, p.Y + h / 2), ToolState.StampLabel, ToolState.StampColor));
                break;
        }
    }

    public void PageDragged(PageView page, PdfRect rect)
    {
        int index = page.Page.Index;
        switch (ToolState.Mode)
        {
            case ToolMode.Highlight:
            case ToolMode.Underline:
            case ToolMode.StrikeOut:
                // Use the selected text lines; on pages without text (scans) use the dragged area.
                var rects = ViewModel.Selection is { } sel && sel.PageIndex == index ? sel.Rects : (IReadOnlyList<PdfRect>)[rect];
                var kind = ToolState.Mode switch
                {
                    ToolMode.Highlight => TextMarkupKind.Highlight,
                    ToolMode.Underline => TextMarkupKind.Underline,
                    _ => TextMarkupKind.StrikeOut,
                };
                ViewModel.SetSelection(null);
                ViewModel.Apply($"Add {kind.ToString().ToLowerInvariant()}", d =>
                    AnnotationService.AddTextMarkup(d, index, kind, rects, ToolState.Color));
                break;

            case ToolMode.FreeText:
            {
                var (fx, fy, fw, _) = ContentEditor.UprightBox(rect, ViewModel.Session.Renderer.GetPageRotation(index));
                BeginTextBoxComment(page, index, fx, fy, Math.Max(60, fw));
                break;
            }

            case ToolMode.AddText:
            case ToolMode.AddImage:
            case ToolMode.Sign:
                // The dragged box, expressed along the page's displayed axes.
                var (bx, by, bw, bh) = ContentEditor.UprightBox(rect, ViewModel.Session.Renderer.GetPageRotation(index));
                if (ToolState.Mode == ToolMode.AddText) BeginNewText(page, index, bx, by, maxWidth: bw);
                else if (ToolState.Mode == ToolMode.AddImage) PlaceImage(index, bx, by, bw, bh);
                else PlaceSignature(index, bx, by, bw, bh);
                break;

            case ToolMode.Redact:
                ViewModel.AddRedaction(new RedactionArea(index, rect));
                Status($"{ViewModel.Redactions.Count} area(s) marked. Protect › Apply redactions removes them.");
                break;

            case ToolMode.TextField:
            case ToolMode.CheckBoxField:
            case ToolMode.DropdownField:
            case ToolMode.SignatureField:
                AddFormField(index, rect);
                break;

            case ToolMode.DigitalSign:
                SignDigitally(index, rect.Width >= 40 && rect.Height >= 16 ? rect : new PdfRect(rect.Left, rect.Top - 60, rect.Left + 210, rect.Top), null);
                break;

            case ToolMode.Crop:
                var answer = MessageBox.Show(Window.GetWindow(this),
                    $"Crop to the selected area?\n\nYes: all {ViewModel.Pages.Count} pages\nNo: page {index + 1} only",
                    "Crop pages", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return;
                IReadOnlyList<int> pages = answer == MessageBoxResult.Yes ? Enumerable.Range(0, ViewModel.Pages.Count).ToList() : [index];
                ViewModel.Apply("Crop", d => PageOperations.Crop(d, pages, rect));
                break;
        }
    }

    public void FinishInk(PageView page, List<(double X, double Y)> points)
    {
        if (points.Count == 0) return;
        int index = page.Page.Index;
        ViewModel.Apply("Draw", d => AnnotationService.AddInk(d, index, [points], ToolState.Color, ToolState.InkWidth));
    }

    void AddFreeText(int pageIndex, PdfRect rect)
    {
        if (rect.Width < 40 || rect.Height < 16) rect = new PdfRect(rect.Left, rect.Top - 60, rect.Left + 240, rect.Top);
        if (InputDialog.Show(Window.GetWindow(this), "Add text box", "Text:", multiline: true) is { Length: > 0 } text)
            ViewModel.Apply("Add text box", d => AnnotationService.AddFreeText(d, pageIndex, rect, text, 12, ToolState.Color));
    }

    void AddPageText(int pageIndex, double x, double y, double? maxWidth)
    {
        if (InputDialog.Show(Window.GetWindow(this), "Add text", $"Text ({ToolState.TextStyle.FontFamily}, {ToolState.TextStyle.Size:0.#} pt):", multiline: true)
            is not { Length: > 0 } text) return;
        if (ContentEditor.NeedsComplexShaping(text))
        {
            MessageBox.Show(Window.GetWindow(this), ContentEditor.ComplexShapingMessage, "Add text", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var style = ToolState.TextStyle with { Color = ToolState.ColorOverride ?? Core.Writing.PdfColor.Black };
        ViewModel.Apply("Add text", d => ContentEditor.AddText(d, pageIndex, x, y, text, style, maxWidth));
    }

    /// <summary>Asks for an image and places it inside a box (points along the displayed axes), keeping proportions.</summary>
    void PlaceImage(int pageIndex, double x, double y, double boxWidth, double boxHeight)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        byte[] data;
        double aspect;
        try
        {
            data = System.IO.File.ReadAllBytes(dialog.FileName);
            using var bmp = ImageCodec.Decode(data);
            aspect = bmp.Width / (double)bmp.Height;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Add image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        double w = boxWidth, h = boxWidth / aspect;
        if (h > boxHeight) { h = boxHeight; w = boxHeight * aspect; }
        int id = ContentEditor.NewItemId();
        if (ViewModel.Apply("Add image", d => ContentEditor.AddImage(d, pageIndex, x, y, w, h, data, id)))
            SelectItem(pageIndex, id, "Image");
    }

    void PlaceSignature(int pageIndex, double x, double y, double boxWidth, double boxHeight)
    {
        if (ToolState.Signature is null)
        {
            var created = SignatureDialog.Show(Window.GetWindow(this));
            if (created is null) return;
            ToolState.Signature = created;
            (Window.GetWindow(this) as MainWindow)?.RefreshSignatures();
        }
        var mark = ToolState.Signature.Mark;
        var color = ToolState.ColorOverride;
        int id = ContentEditor.NewItemId();
        if (ViewModel.Apply("Sign", d => VisualSignatureService.Place(d, pageIndex, x, y, boxWidth, boxHeight, mark, color, id)))
            SelectItem(pageIndex, id, "Signature");
    }

    void Status(string message) => (Window.GetWindow(this) as MainWindow)?.Status(message);

    // ---------- Typing new text on the page ----------

    void BeginNewText(PageView page, int index, double x, double y, double? maxWidth)
    {
        var style = ToolState.TextStyle with { Color = ToolState.ColorOverride ?? Core.Writing.PdfColor.Black };
        Status("Type your text. Enter adds a line; click outside or press Ctrl+Enter to place it; Esc cancels.");
        page.ShowNewTextEditor((x, y), maxWidth, style, text =>
        {
            if (text is null) { Status(""); return; }
            if (ContentEditor.NeedsComplexShaping(text))
            {
                MessageBox.Show(Window.GetWindow(this), ContentEditor.ComplexShapingMessage, "Add text", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int id = ContentEditor.NewItemId();
            if (ViewModel.Apply("Add text", d => ContentEditor.AddText(d, index, x, y, text.TrimEnd(), style, maxWidth, id)))
                SelectItem(index, id, "Text");
        });
    }

    /// <summary>A text-box comment typed in place; the box is sized to the text.</summary>
    void BeginTextBoxComment(PageView page, int index, double x, double y, double width)
    {
        var style = new TextStyle("Segoe UI", 12, ToolState.Color);
        page.ShowNewTextEditor((x, y), width - 4, style, text =>
        {
            if (text is null) return;
            if (ContentEditor.NeedsComplexShaping(text))
            {
                MessageBox.Show(Window.GetWindow(this), ContentEditor.ComplexShapingMessage, "Add text box", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var (_, h) = ContentEditor.MeasureText(text, style, width - 4);
            var rect = new PdfRect(x, y - h - 6, x + width, y);
            ViewModel.Apply("Add text box", d => AnnotationService.AddFreeText(d, index, rect, text, 12, ToolState.Color));
        });
    }

    // ---------- Selecting, moving and resizing items ----------

    const string SelectionHelp = "drag to move, drag a corner to resize, arrow keys to nudge, Delete to remove.";

    void SelectItem(int index, int itemId, string label)
    {
        var item = PageObjectEditor.FindItem(ViewModel.Session.CurrentBytes, ViewModel.Session.Password, index, itemId);
        if (item is null) return;
        ViewModel.SelectObject(new SelectionTarget(index, item.Bounds, item, null, label));
        Status($"{label} selected: {SelectionHelp}");
    }

    /// <summary>Selects what is under the point: a comment or stamp first (they sit on top), then page content.</summary>
    void SelectAt(int index, (double X, double Y) p)
    {
        static bool Inside(PdfRect r, (double X, double Y) q) => q.X >= r.Left - 2 && q.X <= r.Right + 2 && q.Y >= r.Bottom - 2 && q.Y <= r.Top + 2;
        if (ViewModel.Comments.LastOrDefault(c => c.Info.PageIndex == index && Inside(c.Info.Rect, p)) is { } comment)
        {
            ViewModel.SelectObject(new SelectionTarget(index, comment.Info.Rect, null, comment.Info, comment.Kind));
            Status($"{comment.Kind} selected: {SelectionHelp}");
            return;
        }
        PageItem? item = null;
        try { item = PageObjectEditor.FindAt(ViewModel.Session.CurrentBytes, ViewModel.Session.Password, index, p.X, p.Y); }
        catch (Exception ex) { App.LogError(ex); }
        if (item is null)
        {
            ViewModel.SelectObject(null);
            Status("Nothing to select here. Click text, an image, a signature or a comment.");
            return;
        }
        string label = item.IsOptimItem ? "Item" : item.Kind switch
        {
            PageObjectKind.Text => "Text", PageObjectKind.Image => "Image", PageObjectKind.Path => "Drawing", _ => "Object",
        };
        ViewModel.SelectObject(new SelectionTarget(index, item.Bounds, item, null, label));
        Status($"{label} selected: {SelectionHelp}");
    }

    public void MoveSelected(double dx, double dy)
    {
        if (ViewModel.SelectedObject is not { } t || (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)) return;
        if (t.Annotation is { } a)
        {
            if (ViewModel.Apply("Move", d => AnnotationService.Move(d, a.PageIndex, a.IndexOnPage, dx, dy)))
                ReselectAnnotation(a);
            return;
        }
        var item = t.Content!;
        if (ViewModel.Run("Move", () => PageObjectEditor.Move(ViewModel.Session, item, dx, dy)))
            ReselectContent(t, item, new PdfRect(t.Bounds.Left + dx, t.Bounds.Bottom + dy, t.Bounds.Right + dx, t.Bounds.Top + dy));
    }

    public void ResizeSelected(double factor, (double X, double Y) anchor)
    {
        if (ViewModel.SelectedObject is not { } t || Math.Abs(factor - 1) < 0.01) return;
        if (t.Annotation is { } a)
        {
            if (ViewModel.Apply("Resize", d => AnnotationService.Resize(d, a.PageIndex, a.IndexOnPage, factor, anchor)))
                ReselectAnnotation(a);
            return;
        }
        var item = t.Content!;
        var b = t.Bounds;
        (double X, double Y) Map(double x, double y) => (anchor.X + (x - anchor.X) * factor, anchor.Y + (y - anchor.Y) * factor);
        var (l, bt) = Map(b.Left, b.Bottom);
        var (r, tp) = Map(b.Right, b.Top);
        if (ViewModel.Run("Resize", () => PageObjectEditor.Resize(ViewModel.Session, item, factor, anchor)))
            ReselectContent(t, item, PdfRect.FromPoints(l, bt, r, tp));
    }

    void DeleteSelected()
    {
        if (ViewModel.SelectedObject is not { } t) return;
        bool ok = t.Annotation is { } a
            ? ViewModel.Apply("Delete", d => AnnotationService.Remove(d, a.PageIndex, a.IndexOnPage))
            : ViewModel.Run("Delete", () => PageObjectEditor.Delete(ViewModel.Session, t.Content!));
        if (ok) { ViewModel.SelectObject(null); Status($"{t.Label} deleted. Ctrl+Z brings it back."); }
    }

    void ReselectAnnotation(AnnotationInfo a)
    {
        if (ViewModel.Comments.FirstOrDefault(c => c.Info.PageIndex == a.PageIndex && c.Info.IndexOnPage == a.IndexOnPage) is { } c)
            ViewModel.SelectObject(new SelectionTarget(a.PageIndex, c.Info.Rect, null, c.Info, c.Kind));
    }

    /// <summary>After an edit the object list is new: find the item again (by its id, or where it now is).</summary>
    void ReselectContent(SelectionTarget old, PageItem item, PdfRect expected)
    {
        var s = ViewModel.Session;
        PageItem? found = item.IsOptimItem
            ? PageObjectEditor.FindItem(s.CurrentBytes, s.Password, old.PageIndex, item.ItemId)
            : PageObjectEditor.FindAt(s.CurrentBytes, s.Password, old.PageIndex, (expected.Left + expected.Right) / 2, (expected.Bottom + expected.Top) / 2);
        ViewModel.SelectObject(found is null ? null : new SelectionTarget(old.PageIndex, found.Bounds, found, null, old.Label));
    }

    /// <summary>Arrow keys nudge the selection (Shift: 10 points), Delete removes it, Esc deselects.</summary>
    void SelectionKeys(object sender, KeyEventArgs e)
    {
        if (ViewModel.SelectedObject is not { } t) return;
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or ComboBox || (e.OriginalSource is DependencyObject d && IsWithin(d, Thumbnails))) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        (double X, double Y)? view = e.Key switch
        {
            Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => null,
        };
        if (view is { } v && _realized.TryGetValue(t.PageIndex, out var page))
        {
            // Nudge along the page as displayed, whatever its rotation.
            double k = DocumentViewModel.DipPerPoint * ViewModel.Zoom;
            var c = page.ToView((t.Bounds.Left + t.Bounds.Right) / 2, (t.Bounds.Bottom + t.Bounds.Top) / 2);
            var p0 = page.ToPdf(c);
            var p1 = page.ToPdf(new Point(c.X + v.X * k, c.Y + v.Y * k));
            MoveSelected(p1.X - p0.X, p1.Y - p0.Y);
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ViewModel.SelectObject(null); Status(""); e.Handled = true; }
    }

    static bool IsWithin(DependencyObject node, DependencyObject container)
    {
        for (var n = node; n is not null; n = System.Windows.Media.VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
            if (ReferenceEquals(n, container)) return true;
        return false;
    }

    // ---------- Filling form fields on the page ----------

    public FieldItem? FieldAt(int index, (double X, double Y) p) =>
        ViewModel.Fields.LastOrDefault(f => f.Info.PageIndex == index && f.Info.Rect is { } r
            && p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top);

    /// <summary>Clicking a field: type in text fields, pick from lists, toggle check boxes, sign signature fields.</summary>
    void ActivateField(FieldItem field)
    {
        if (!field.Editable) { Status($"“{field.Name}” is read-only."); return; }
        switch (field.Info.Kind)
        {
            case FormFieldKind.CheckBox:
                bool on = !field.Checked;
                field.Checked = on;
                Status($"“{field.Name}” {(on ? "checked" : "unchecked")}.");
                break;
            case FormFieldKind.Signature:
                if (!ViewModel.Signatures.Any()) SignDigitally(field.Info.PageIndex, null, field.Name);
                break;
            case FormFieldKind.Text:
            case FormFieldKind.ComboBox:
            case FormFieldKind.ListBox:
                EditFieldInline(field.Name);
                break;
            default:
                ShowField(field); // Radio groups and buttons: use the Form panel.
                break;
        }
    }

    public void EditFieldInline(string name, int attempt = 0)
    {
        if (ViewModel.Fields.FirstOrDefault(f => f.Name == name) is not { Info.Rect: { } rect } field) return;
        if (!_realized.TryGetValue(field.Info.PageIndex, out var page))
        {
            if (attempt > 2) { ShowField(field); return; }
            GoToPage(field.Info.PageIndex, rect.Top);
            Dispatcher.BeginInvoke(() => EditFieldInline(name, attempt + 1), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        bool choice = field.Info.Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox;
        Status(field.MultiLine ? $"Typing in “{name}”. Ctrl+Enter saves, Tab moves to the next field." : $"Typing in “{name}”. Enter saves, Tab moves to the next field.");
        page.ShowFieldEditor(rect, choice ? field.Choice ?? "" : field.Text, field.MultiLine, choice ? field.Options : null, false, (value, move) =>
        {
            if (value is not null)
            {
                if (choice) field.Choice = value; else field.Text = value;
            }
            if (move == 0) { Status(value is null ? "" : $"Saved “{name}”."); return; }
            var order = ViewModel.FieldsInOrder.Where(f => f.Editable && f.Info.Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox).ToList();
            int at = order.FindIndex(f => f.Name == name);
            if (order.Count == 0) return;
            var next = order[((at < 0 ? 0 : at) + move + order.Count) % order.Count];
            Dispatcher.BeginInvoke(() => EditFieldInline(next.Name), System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// A default box for a click-placed item, shrunk so it stays on the page (unrotated pages; on rotated pages the
    /// default size is kept). Dragging a box gives full control.
    /// </summary>
    (double W, double H) FitOnPage(int index, (double X, double Y) p, double w, double h)
    {
        if (ViewModel.Session.Renderer.GetPageRotation(index) != 0) return (w, h);
        var page = ViewModel.Pages[index];
        const double margin = 12;
        return (Math.Max(24, Math.Min(w, page.WidthPt - p.X - margin)), Math.Max(16, Math.Min(h, p.Y - margin)));
    }

    // ---------- Edit existing text ----------

    void BeginTextEdit(PageView page, int index, (double X, double Y) p)
    {
        TextLine? line;
        try { line = ExistingTextEditor.FindLine(ViewModel.Session.CurrentBytes, ViewModel.Session.Password, index, p.X, p.Y); }
        catch (Exception ex) { App.LogError(ex); line = null; }
        if (line is null)
        {
            Status("No editable text here. Click on a line of text (scanned pages need Recognize text first).");
            return;
        }
        string font = System.Text.RegularExpressions.Regex.Replace(line.PdfFontName, "^[A-Z]{6}\\+", "");
        Status(line.FontInstalled ? $"Editing in {line.Family}, {line.FontSize:0.#} pt. Enter applies, Esc cancels."
            : $"The original font ({font}) is not installed; the edited line will use {line.Family}. Enter applies, Esc cancels.");
        page.ShowTextEditor(line.Bounds, line.Text, line.FontSize, RtlText.IsRtlParagraph(line.Text), newText =>
        {
            if (newText is null) { Status(""); return; }
            if (newText.Trim().Length > 0 && ContentEditor.NeedsComplexShaping(newText))
            {
                MessageBox.Show(Window.GetWindow(this), ContentEditor.ComplexShapingMessage, "Edit text", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ViewModel.Run(newText.Trim().Length == 0 ? "Delete text" : "Edit text", () => ExistingTextEditor.Replace(ViewModel.Session, line, newText)))
                Status(newText.Trim().Length == 0 ? "Line deleted." : "Text changed. The old text is removed from the file, not hidden.");
        });
    }

    // ---------- Redaction ----------

    /// <summary>Clicking a word in redaction mode marks that word.</summary>
    void MarkWordForRedaction(int index, (double X, double Y) p)
    {
        var renderer = ViewModel.Session.Renderer;
        int at = renderer.GetCharIndexAt(index, p.X, p.Y, 3);
        if (at < 0) { Status("Drag a rectangle over the area to redact, or click a word."); return; }
        var text = renderer.GetPageText(index);
        if (at >= text.Length || char.IsWhiteSpace(text[at])) return;
        int start = at, end = at;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1])) end++;
        foreach (var r in renderer.GetTextRects(index, start, end - start + 1))
            ViewModel.AddRedaction(new RedactionArea(index, new PdfRect(r.Left - 1, r.Bottom - 1, r.Right + 1, r.Top + 1)));
        Status($"Marked “{text[start..(end + 1)]}”. {ViewModel.Redactions.Count} area(s) marked; Protect › Apply redactions removes them.");
    }

    // ---------- Form fields ----------

    void AddFormField(int index, PdfRect rect)
    {
        var mode = ToolState.Mode;
        var (kind, prefix) = mode switch
        {
            ToolMode.CheckBoxField => ("check box", "CheckBox"),
            ToolMode.DropdownField => ("dropdown", "Dropdown"),
            ToolMode.SignatureField => ("signature field", "Signature"),
            _ => ("text field", "Text"),
        };
        if (mode == ToolMode.CheckBoxField && (rect.Width < 8 || rect.Height < 8)) rect = new PdfRect(rect.Left, rect.Bottom, rect.Left + 14, rect.Bottom + 14);
        if (mode != ToolMode.CheckBoxField && (rect.Width < 20 || rect.Height < 10)) return;
        var names = ViewModel.Fields.Select(f => f.Name).ToHashSet();
        string suggestion = Enumerable.Range(1, 999).Select(i => prefix + i).First(n => !names.Contains(n));
        var owner = Window.GetWindow(this);
        if (InputDialog.Show(owner, $"Add {kind}", "Field name:", suggestion) is not { Length: > 0 } name) return;
        name = name.Trim().Replace('.', '_');
        if (names.Contains(name)) { MessageBox.Show(owner, $"A field named “{name}” already exists.", $"Add {kind}"); return; }

        IReadOnlyList<string> options = [];
        if (mode == ToolMode.DropdownField)
        {
            if (InputDialog.Show(owner, "Dropdown options", "One option per line:", "Option 1\nOption 2\nOption 3", multiline: true) is not { } text) return;
            options = text.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
            if (options.Count == 0) return;
        }
        if (ViewModel.Apply($"Add {kind}", d =>
            {
                switch (mode)
                {
                    case ToolMode.CheckBoxField: FormBuilder.AddCheckBox(d, index, name, rect); break;
                    case ToolMode.DropdownField: FormBuilder.AddComboBox(d, index, name, rect, options); break;
                    case ToolMode.SignatureField: FormBuilder.AddSignatureField(d, index, name, rect); break;
                    default: FormBuilder.AddTextField(d, index, name, rect, fontSize: Math.Clamp((rect.Height - 4) * 0.7, 6, 14)); break;
                }
            }))
            Status($"Added {kind} “{name}”.");
    }

    void DeleteField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FieldItem field }) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete the field “{field.Name}” and its value? You can undo this.", "Delete field",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ViewModel.Apply("Delete field", d => FormBuilder.RemoveField(d, field.Name));
    }

    // ---------- Digital signatures ----------

    /// <summary>Signs with a certificate: in a new visible field (rect), an existing empty field (fieldName), or invisibly (both null).</summary>
    public void SignDigitally(int index, PdfRect? rect, string? fieldName)
    {
        var owner = Window.GetWindow(this);
        var session = ViewModel.Session;
        if (session.IsEncrypted)
        {
            MessageBox.Show(owner, "Password-protected documents can't be signed yet. Remove security first (Protect › Remove security), then sign.",
                "Sign digitally", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (DigitalSignDialog.Show(owner, ToolState.Signature?.Mark, visible: rect is not null || fieldName is not null) is not { } choice) return;
        var options = new DigitalSignOptions(choice.Certificate, index, rect, choice.Reason, choice.Location, FieldName: fieldName,
            Mark: choice.Mark, TimestampServer: choice.TimestampServer);
        if (ViewModel.Run("Sign digitally", () => session.ApplyRaw("Sign digitally", data => DigitalSignatureService.Sign(data, options)), keepsSignatures: true))
        {
            Status("Signed. Save the file now to keep the signature; further changes (other than signing) would invalidate it.");
            ShowSignatures();
        }
    }

    public void ShowSignatures()
    {
        SideTabs.SelectedItem = SignaturesTab;
        if (SidePanelColumn.Width.Value < 50) SidePanelColumn.Width = new GridLength(250);
    }

    void SignatureBanner_Click(object sender, MouseButtonEventArgs e) => ShowSignatures();

    void TrustSigner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SignatureItem { Value.Certificate: { } cert } item }) return;
        var c = cert;
        if (MessageBox.Show(Window.GetWindow(this),
                $"Trust signatures made with this certificate?\n\n{c.Subject}\nFingerprint (SHA-1): {c.Thumbprint}\n\n" +
                "Only continue if you have confirmed with the signer (for example by phone) that this fingerprint is theirs.",
                "Trust signer", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Services.DigitalIdStore.Trust(c);
            ViewModel.Revalidate();
            Status($"{item.Signer} is now trusted on this PC.");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            MessageBox.Show(Window.GetWindow(this), "The certificate could not be added: " + ex.Message, "Trust signer");
        }
    }

    void UpdateSignatureBanner()
    {
        var summary = ViewModel.SignatureSummary;
        SignatureBanner.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        SignatureBannerText.Text = summary ?? "";
        bool invalid = !ViewModel.SignaturesValid, untrusted = ViewModel.Signatures.Any(s => !s.CertificateTrusted);
        SignatureBannerIcon.Text = invalid ? "\uE783" : untrusted ? "\uE7BA" : "\uE73E";
        SignatureBannerIcon.Foreground = new System.Windows.Media.SolidColorBrush(invalid ? System.Windows.Media.Color.FromRgb(220, 38, 38)
            : untrusted ? System.Windows.Media.Color.FromRgb(217, 119, 6) : System.Windows.Media.Color.FromRgb(22, 163, 74));
        NoSignatures.Visibility = ViewModel.Signatures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Keep the first page clear of the banner.
        Dispatcher.BeginInvoke(() => Scroller.Margin = new Thickness(0, summary is null ? 0 : SignatureBanner.ActualHeight, 0, 0),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ShowField(FieldItem field)
    {
        SideTabs.SelectedItem = FormTab;
        if (SidePanelColumn.Width.Value < 50) SidePanelColumn.Width = new GridLength(250);
        Dispatcher.BeginInvoke(() =>
        {
            if (FieldList.ItemContainerGenerator.ContainerFromItem(field) is FrameworkElement container)
            {
                container.BringIntoView();
                container.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ShowFormPanel()
    {
        SideTabs.SelectedItem = FormTab;
        if (SidePanelColumn.Width.Value < 50) SidePanelColumn.Width = new GridLength(250);
    }

    void FieldText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { AcceptsReturn: false } box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    void Field_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FieldItem { Info.PageIndex: >= 0 } f } && f.Info.PageIndex != ViewModel.CurrentPage)
            GoToPage(f.Info.PageIndex, f.Info.Rect?.Top);
    }

    public void CopySelection()
    {
        if (ViewModel.Selection is { Text.Length: > 0 } sel) Clipboard.SetText(sel.Text);
    }

    // ---------- Side panel ----------

    public void ShowSearch()
    {
        SideTabs.SelectedItem = SearchTab;
        if (SidePanelColumn.Width.Value < 50) SidePanelColumn.Width = new GridLength(250);
        if (ViewModel.Selection is { Text.Length: > 0 and < 100 } sel) SearchBox.Text = sel.Text.Trim();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Runs a search from the command bar and shows the results panel. Returns the number of matches.</summary>
    public async Task<int> SearchFor(string query)
    {
        SideTabs.SelectedItem = SearchTab;
        if (SidePanelColumn.Width.Value < 50) SidePanelColumn.Width = new GridLength(250);
        SearchBox.Text = query;
        SearchStatus.Tag = query;
        return await RunSearch();
    }

    public void ToggleSidePanel() =>
        SidePanelColumn.Width = SidePanelColumn.Width.Value < 50 ? new GridLength(250) : new GridLength(0);

    async Task<int> RunSearch()
    {
        var query = SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(query)) SearchStatus.Text = "Searching…";
        int n = await ViewModel.SearchAsync(query, MatchCase.IsChecked == true, WholeWord.IsChecked == true);
        if (n < 0) return n; // A newer search is running.
        SearchStatus.Text = string.IsNullOrWhiteSpace(query) ? "" : n == 0 ? "No matches." : n == 1 ? "1 match" : $"{n} matches";
        if (n > 0) SearchResultsList.SelectedIndex = 0;
        return n;
    }

    public void NextSearchResult(int direction)
    {
        int n = ViewModel.SearchResults.Count;
        if (n == 0) { ShowSearch(); return; }
        SearchResultsList.SelectedIndex = ((SearchResultsList.SelectedIndex + direction) % n + n) % n;
        SearchResultsList.ScrollIntoView(SearchResultsList.SelectedItem);
    }

    async void Search_Click(object sender, RoutedEventArgs e) => await RunSearch();

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Enter runs a new search; Enter again steps through the results of the same query.
        if (ViewModel.SearchResults.Count > 0 && SearchStatus.Tag as string == SearchBox.Text)
            NextSearchResult(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
        else
        {
            SearchStatus.Tag = SearchBox.Text;
            _ = RunSearch();
        }
        e.Handled = true;
    }

    void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not SearchResultItem item) return;
        ViewModel.CurrentHit = SearchResultsList.SelectedIndex;
        GoToPage(item.Hit.PageIndex, item.Hit.Rects.Count > 0 ? item.Hit.Rects[0].Top : null);
    }

    void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThumbnailNavigation || Thumbnails.SelectedItems.Count != 1) return;
        if (Thumbnails.SelectedItem is PageViewModel page) GoToPage(page.Index);
    }

    void Thumbnails_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Window.GetWindow(this) is MainWindow main)
        {
            main.DeletePages();
            e.Handled = true;
        }
    }

    public void SelectThumbnails(IEnumerable<int> indices)
    {
        _suppressThumbnailNavigation = true;
        try
        {
            Thumbnails.SelectedItems.Clear();
            foreach (int i in indices)
                if (i < ViewModel.Pages.Count) Thumbnails.SelectedItems.Add(ViewModel.Pages[i]);
        }
        finally { _suppressThumbnailNavigation = false; }
    }

    void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageViewModel page }) page.SetThumbnailVisible(true);
    }

    void Thumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageViewModel page }) page.SetThumbnailVisible(false);
    }

    void Bookmarks_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNode { PageIndex: >= 0 } node) GoToPage(node.PageIndex);
    }

    void CommentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommentList.SelectedItem is CommentItem c) GoToPage(c.Info.PageIndex, c.Info.Rect.Top);
    }

    void DeleteComment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommentItem c })
            ViewModel.Apply("Delete comment", d => AnnotationService.Remove(d, c.Info.PageIndex, c.Info.IndexOnPage));
    }

    void UpdateEmptyStates()
    {
        NoBookmarks.Visibility = ViewModel.Outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoComments.Visibility = ViewModel.Comments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoFields.Visibility = ViewModel.Fields.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
