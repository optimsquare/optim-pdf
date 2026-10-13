using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OptimPdf.App.Dialogs;
using OptimPdf.App.Printing;
using OptimPdf.App.ViewModels;
using OptimPdf.App.Views;
using OptimPdf.App.Services;
using OptimPdf.Core.Conversion;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Forms;
using OptimPdf.Core.Ocr;
using OptimPdf.Core.Optimization;
using OptimPdf.Core.Security;
using OptimPdf.Core.IO;
using OptimPdf.Core.Pages;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;

namespace OptimPdf.App;

public partial class MainWindow : Window
{
    const string PdfFilter = "PDF documents (*.pdf)|*.pdf";
    bool _syncingTools;

    public MainWindow()
    {
        InitializeComponent();
        AddShortcuts();
        SyncToolButtons(ToolState.Mode.ToString());
        switch (App.Settings.Theme)
        {
            case "Light": ThemeLight.IsChecked = true; break;
            case "Dark": ThemeDark.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }
        RefreshRecent();
        UpdateChrome();

        FontPicker.ItemsSource = ContentEditor.InstalledFamilies.ToList();
        FontPicker.SelectedItem = ToolState.TextStyle.FontFamily;
        RefreshSignatures();
        Loaded += (_, _) => Dispatcher.BeginInvoke(OfferRecovery, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    DocumentView? ActiveView => (DocTabs.SelectedItem as TabItem)?.Content as DocumentView;
    DocumentViewModel? Active => ActiveView?.ViewModel;

    // ---------- Opening and closing ----------

    public void OpenFile(string path)
    {
        // Focus the tab if the file is already open.
        foreach (TabItem tab in DocTabs.Items)
            if (tab.Content is DocumentView v && string.Equals(v.ViewModel.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                DocTabs.SelectedItem = tab;
                return;
            }

        string? password = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var session = DocumentSession.Open(path, password);
                AddTab(new DocumentViewModel(session));
                App.Settings.AddRecent(session.FilePath!);
                RefreshRecent();
                return;
            }
            catch (PdfPasswordRequiredException ex) when (attempt < 3)
            {
                password = InputDialog.ShowPassword(this, "Password required",
                    $"{(attempt == 0 ? "" : ex.Message + "\n\n")}Enter the password to open “{Path.GetFileName(path)}”:");
                if (password is null) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfOpenException or PdfPasswordRequiredException)
            {
                MessageBox.Show(this, $"“{Path.GetFileName(path)}” could not be opened.\n\n{ex.Message}", "Optim PDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
    }

    void AddTab(DocumentViewModel vm)
    {
        var view = new DocumentView(vm);
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DocumentViewModel.Title)) { Source = vm });
        var close = new Button
        {
            Content = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 10,
            Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Close (Ctrl+W)",
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = vm.FilePath };
        header.Children.Add(title);
        header.Children.Add(close);

        var tab = new TabItem { Header = header, Content = view };
        close.Click += (_, _) => CloseTab(tab);
        header.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        vm.PropertyChanged += (_, e) =>
        {
            if (vm == Active && e.PropertyName is nameof(DocumentViewModel.PageLabel) or nameof(DocumentViewModel.ZoomPercent) or nameof(DocumentViewModel.Title))
                UpdateChrome();
        };
        vm.Session.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            header.ToolTip = vm.FilePath;
            if (vm == Active) UpdateChrome();
        });

        DocTabs.Items.Add(tab);
        DocTabs.SelectedItem = tab;
        UpdateChrome();
    }

    /// <returns>False when the user cancelled.</returns>
    bool CloseTab(TabItem tab)
    {
        if (tab.Content is not DocumentView view) return true;
        if (!ConfirmDiscard(view.ViewModel)) return false;
        DocTabs.Items.Remove(tab);
        view.ViewModel.Dispose();
        UpdateChrome();
        return true;
    }

    bool ConfirmDiscard(DocumentViewModel vm)
    {
        if (!vm.Session.IsDirty) return true;
        var answer = MessageBox.Show(this, $"Save changes to “{vm.Session.DisplayName}”?", "Optim PDF",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(vm, saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        foreach (var tab in DocTabs.Items.Cast<TabItem>().ToList())
        {
            DocTabs.SelectedItem = tab;
            if (!CloseTab(tab)) { e.Cancel = true; return; }
        }
    }

    bool Save(DocumentViewModel vm, bool saveAs)
    {
        try
        {
            if (saveAs || vm.Session.FilePath is null)
            {
                var dialog = new SaveFileDialog
                {
                    Filter = PdfFilter, FileName = vm.Session.DisplayName,
                    InitialDirectory = vm.Session.FilePath is { } p ? Path.GetDirectoryName(p) : null,
                };
                if (dialog.ShowDialog(this) != true) return false;
                vm.Session.SaveAs(dialog.FileName);
                App.Settings.AddRecent(dialog.FileName);
                RefreshRecent();
            }
            else vm.Session.Save();
            Status($"Saved {vm.Session.DisplayName}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The file could not be saved. The original file is unchanged.\n\n{ex.Message}", "Optim PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ---------- Chrome state ----------

    void DocTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == DocTabs) UpdateChrome();
    }

    void UpdateChrome()
    {
        bool hasDoc = DocTabs.Items.Count > 0;
        DocTabs.Visibility = hasDoc ? Visibility.Visible : Visibility.Collapsed;
        StartPage.Visibility = hasDoc ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.IsEnabled = hasDoc;

        var vm = Active;
        Title = vm is null ? "Optim PDF" : $"{vm.Title} — Optim PDF";
        PageBox.Text = vm is null ? "" : (vm.CurrentPage + 1).ToString();
        PageCountLabel.Text = vm is null ? "" : $"of {vm.Pages.Count}";
        ZoomLabel.Text = vm?.ZoomPercent ?? "";
        UndoButton.IsEnabled = QatUndo.IsEnabled = vm?.Session.CanUndo == true;
        QatUndo.ToolTip = vm?.Session.UndoDescription is { } qu ? $"Undo {qu} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        QatRedo.ToolTip = vm?.Session.RedoDescription is { } qr ? $"Redo {qr} (Ctrl+Y)" : "Redo (Ctrl+Y)";
        HeaderDocTitle.Text = vm is null ? "" : vm.Title;
        UndoButton.ToolTip = vm?.Session.UndoDescription is { } u ? $"Undo {u} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        RedoButton.IsEnabled = QatRedo.IsEnabled = vm?.Session.CanRedo == true;
        RedoButton.ToolTip = vm?.Session.RedoDescription is { } r ? $"Redo {r} (Ctrl+Y)" : "Redo (Ctrl+Y)";
        if (vm?.AccessNotice is { } notice) Status(notice);
    }

    public void Status(string message) => StatusMessage.Text = message;

    void RefreshRecent()
    {
        var recent = App.Settings.RecentFiles.Where(File.Exists).Take(8).ToList();
        RecentList.ItemsSource = recent;
        RecentHeader.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Tools ----------

    void Tool_Checked(object sender, RoutedEventArgs e)
    {
        // XAML raises Checked for the default tool while InitializeComponent is still running.
        if (_syncingTools || DocTabs is null || sender is not RadioButton { CommandParameter: string name }) return;
        ToolState.Mode = Enum.Parse<ToolMode>(name);
        SyncToolButtons(name);
        Active?.SetSelection(null);
        Status(ToolState.Mode switch
        {
            ToolMode.Select => Active?.Fields.Count > 0 ? "Click a form field to type in it (Tab: next field). Drag across text to select it." : "Select text by dragging; Ctrl+C copies it.",
            ToolMode.Highlight or ToolMode.Underline or ToolMode.StrikeOut => "Drag across text to mark it up.",
            ToolMode.Note => "Click on a page to add a note.",
            ToolMode.FreeText => "Click or drag on a page to add a text box.",
            ToolMode.Ink => "Draw on the page with the mouse or pen.",
            ToolMode.Stamp => $"Click on a page to place the {ToolState.StampLabel} stamp.",
            ToolMode.Crop => "Drag a rectangle around the area to keep.",
            ToolMode.EditText => "Click a line of text to edit it in place.",
            ToolMode.SelectObject => "Click text, an image, a signature or a comment to select it; then drag to move or drag a corner to resize.",
            ToolMode.AddText => "Click where the text should start and type; drag a box to wrap text inside it.",
            ToolMode.Redact => "Drag over content to mark it for redaction, or click a word. Nothing is removed until you Apply.",
            ToolMode.TextField => "Drag a box on the page for the text field.",
            ToolMode.CheckBoxField => "Click where the check box should go.",
            ToolMode.DropdownField => "Drag a box on the page for the dropdown.",
            ToolMode.SignatureField => "Drag a box where the signer should sign.",
            ToolMode.DigitalSign => "Drag a box where your digital signature should appear.",
            _ => "",
        });
    }

    /// <summary>The same tool appears on several ribbon tabs; keep all their check states in sync.</summary>
    void SyncToolButtons(string name)
    {
        _syncingTools = true;
        try
        {
            foreach (var rb in FindTools(Ribbon))
                rb.IsChecked = (string)rb.CommandParameter == name;
        }
        finally { _syncingTools = false; }
    }

    static IEnumerable<RadioButton> FindTools(DependencyObject root)
    {
        // Ribbon tabs that were never shown have no visual tree yet, so walk the logical tree.
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is RadioButton { GroupName: "Tools" } rb) yield return rb;
            foreach (var nested in FindTools(child)) yield return nested;
        }
    }

    void SelectTool(ToolMode mode)
    {
        foreach (var rb in FindTools(Ribbon))
            if ((string)rb.CommandParameter == mode.ToString()) { rb.IsChecked = true; return; }
    }

    void ColorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ToolState.ColorOverride = (((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Yellow" => PdfColor.Yellow,
            "Green" => PdfColor.Green,
            "Blue" => PdfColor.Blue,
            "Red" => PdfColor.Red,
            "Black" => PdfColor.Black,
            _ => null,
        };

    void StampPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((StampPicker.SelectedItem as ComboBoxItem)?.Content is string label) ToolState.StampLabel = label;
    }

    // ---------- File commands ----------

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = PdfFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            foreach (var f in dialog.FileNames) OpenFile(f);
    }

    void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenFile(path);
    }

    void Save_Click(object sender, RoutedEventArgs e) { if (Active is { } vm) Save(vm, saveAs: false); }
    void SaveAs_Click(object sender, RoutedEventArgs e) { if (Active is { } vm) Save(vm, saveAs: true); }

    void Print_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true, CurrentPageEnabled = true,
            MinPage = 1, MaxPage = (uint)vm.Pages.Count,
            PageRange = new System.Windows.Controls.PageRange(1, vm.Pages.Count),
        };
        if (dialog.ShowDialog() != true) return;

        IReadOnlyList<int> pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [vm.CurrentPage],
            PageRangeSelection.UserPages => Enumerable.Range(dialog.PageRange.PageFrom - 1,
                Math.Max(0, Math.Min(dialog.PageRange.PageTo, vm.Pages.Count) - dialog.PageRange.PageFrom + 1)).ToList(),
            _ => Enumerable.Range(0, vm.Pages.Count).ToList(),
        };
        if (pages.Count == 0) return;
        var paper = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            dialog.PrintDocument(new PdfPaginator(vm.Session.Renderer, pages, paper), vm.Session.DisplayName);
            Status($"Sent {pages.Count} page(s) to the printer.");
        }
        finally { Mouse.OverrideCursor = null; }
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Active?.Session.Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => Active?.Session.Redo();
    void Copy_Click(object sender, RoutedEventArgs e) => ActiveView?.CopySelection();
    void Find_Click(object sender, RoutedEventArgs e) => ActiveView?.ShowSearch();

    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        var props = DocumentProperties.Read(vm.Session);
        if (PropertiesDialog.Show(this, props, vm.FilePath) is { } edited)
            vm.Apply("Edit properties", d => DocumentProperties.Write(d, edited.Title, edited.Author, edited.Subject, edited.Keywords));
    }

    void Merge_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = PdfFilter, Multiselect = true, Title = "Choose the PDFs to combine (in order)" };
        if (dialog.ShowDialog(this) != true) return;
        var files = dialog.FileNames.ToList();
        if (files.Count < 2)
        {
            MessageBox.Show(this, "Choose at least two files to combine.", "Combine files");
            return;
        }
        try
        {
            var sources = files.Select(f => PageOperations.OpenForImport(f)).ToList();
            try
            {
                using var merged = PageOperations.Merge(sources);
                var session = DocumentSession.FromBytes(DocumentSession.Serialize(merged), suggestedPath: null);
                AddTab(new DocumentViewModel(session));
                Status($"Combined {files.Count} files ({session.PageCount} pages). Save to keep the result.");
            }
            finally
            {
                foreach (var s in sources) s.Dispose();
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            MessageBox.Show(this, $"The files could not be combined.\n\n{ex.Message}", "Combine files", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Page commands ----------

    void RotateLeft_Click(object sender, RoutedEventArgs e) => RotatePages(-90);
    void RotateRight_Click(object sender, RoutedEventArgs e) => RotatePages(90);

    void RotatePages(int degrees)
    {
        if (ActiveView is not { } view) return;
        var pages = view.TargetPages;
        view.ViewModel.Apply("Rotate pages", d => PageOperations.Rotate(d, pages, degrees));
    }

    void Delete_Click(object sender, RoutedEventArgs e) => DeletePages();

    public void DeletePages()
    {
        if (ActiveView is not { } view) return;
        var pages = view.TargetPages;
        if (pages.Count >= view.ViewModel.Pages.Count)
        {
            MessageBox.Show(this, "A PDF must keep at least one page.", "Delete pages");
            return;
        }
        var label = pages.Count == 1 ? $"page {pages[0] + 1}" : $"{pages.Count} pages";
        if (MessageBox.Show(this, $"Delete {label}? You can undo this.", "Delete pages", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            view.ViewModel.Apply("Delete pages", d => PageOperations.Delete(d, pages));
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => MovePage(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => MovePage(1);

    void MovePage(int delta)
    {
        if (ActiveView is not { } view) return;
        int from = view.TargetPages[0], to = from + delta;
        if (to < 0 || to >= view.ViewModel.Pages.Count) return;
        if (view.ViewModel.Apply("Move page", d => PageOperations.Move(d, from, to)))
        {
            view.SelectThumbnails([to]);
            view.GoToPage(to);
        }
    }

    void InsertFile_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        var dialog = new OpenFileDialog { Filter = PdfFilter, Title = "Insert pages from" };
        if (dialog.ShowDialog(this) != true) return;
        int at = view.ViewModel.CurrentPage + 1;
        try
        {
            using var source = PageOperations.OpenForImport(dialog.FileName);
            int count = 0;
            if (view.ViewModel.Apply("Insert pages", d => count = PageOperations.InsertFrom(d, source, at)))
                Status($"Inserted {count} page(s) after page {at}.");
        }
        catch (Exception ex) when (ex is IOException or PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException)
        {
            MessageBox.Show(this, $"The file could not be read.\n\n{ex.Message}", "Insert pages", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void InsertBlank_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        int at = vm.CurrentPage + 1;
        var size = vm.Pages[vm.CurrentPage];
        vm.Apply("Insert blank page", d => PageOperations.InsertBlank(d, at, size.WidthPt, size.HeightPt));
    }

    void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        var vm = view.ViewModel;
        string initial = string.Join(", ", view.TargetPages.Select(p => p + 1));
        if (InputDialog.Show(this, "Extract pages", $"Pages to extract (1–{vm.Pages.Count}), e.g. 1-3, 7:", initial) is not { } text) return;
        try
        {
            var indices = OptimPdf.Core.Pages.PageRange.Parse(text, vm.Pages.Count).SelectMany(r => r.Indices()).ToList();
            var dialog = new SaveFileDialog { Filter = PdfFilter, FileName = $"{Path.GetFileNameWithoutExtension(vm.Session.DisplayName)} (pages {text.Replace(' ', '_')}).pdf" };
            if (dialog.ShowDialog(this) != true) return;
            using var source = PageOperations.OpenForImport(vm.Session.CurrentBytes, vm.Session.Password);
            using var output = PageOperations.Extract(source, indices);
            SafeFileWriter.Write(dialog.FileName, DocumentSession.Serialize(output));
            Status($"Extracted {indices.Count} page(s) to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Extract pages", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Split_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (InputDialog.Show(this, "Split document",
                "Enter a number to split every N pages, or ranges for each output file (e.g. 1-3, 4-10, 11-).", "1") is not { } text) return;
        try
        {
            using var source = PageOperations.OpenForImport(vm.Session.CurrentBytes, vm.Session.Password);
            var parts = int.TryParse(text.Trim(), out int every)
                ? PageOperations.SplitEvery(source, every)
                : PageOperations.SplitByRanges(source, OptimPdf.Core.Pages.PageRange.Parse(text, vm.Pages.Count));

            var folder = new OpenFolderDialog { Title = $"Save {parts.Count} files to" };
            if (vm.FilePath is { } path) folder.InitialDirectory = Path.GetDirectoryName(path);
            if (folder.ShowDialog(this) != true) return;

            string baseName = Path.GetFileNameWithoutExtension(vm.Session.DisplayName);
            int digits = parts.Count.ToString().Length;
            for (int i = 0; i < parts.Count; i++)
            {
                using var part = parts[i];
                SafeFileWriter.Write(Path.Combine(folder.FolderName, $"{baseName} part {(i + 1).ToString().PadLeft(digits, '0')}.pdf"),
                    DocumentSession.Serialize(part));
            }
            Status($"Split into {parts.Count} files in {folder.FolderName}.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Split document", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- Edit, Fill & Sign ----------

    void TextStyle_Changed(object sender, RoutedEventArgs e)
    {
        if (FontPicker is null || SizePicker is null) return; // Raised during InitializeComponent.
        var sizeText = (SizePicker.SelectedItem as ComboBoxItem)?.Content as string ?? SizePicker.Text;
        double size = double.TryParse(sizeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var s) ? Math.Clamp(s, 4, 200) : 12;
        ToolState.TextStyle = new TextStyle(FontPicker.SelectedItem as string ?? "Segoe UI", size, Bold: BoldPicker.IsChecked == true, Italic: ItalicPicker.IsChecked == true);
    }

    void FillForm_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        view.ShowFormPanel();
        if (view.ViewModel.Fields.Count == 0)
            Status("This document has no form fields. Use Add text to type anywhere on it.");
        else if (view.ViewModel.FieldsInOrder.FirstOrDefault(f => f.Editable && f.IsText) is { } first)
        {
            view.EditFieldInline(first.Name);
        }
    }

    void HighlightFields_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        vm.HighlightFields = !vm.HighlightFields;
        Status(vm.HighlightFields ? "Form fields are highlighted." : "Field highlighting is off.");
    }

    void Flatten_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (vm.Fields.Count == 0) { Status("This document has no form fields to flatten."); return; }
        if (MessageBox.Show(this, "Flatten the form? The current values become fixed page content and can no longer be edited as fields.\n\nYou can undo this until you save.",
                "Flatten form", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int count = 0;
        if (vm.Apply("Flatten form", d => count = FormService.Flatten(d))) Status($"Flattened {count} field widget(s).");
    }

    bool _refreshingSignatures;

    public void RefreshSignatures()
    {
        _refreshingSignatures = true;
        try
        {
            var all = SignatureStore.Load();
            SignaturePicker.ItemsSource = all;
            SignaturePicker.SelectedItem = all.FirstOrDefault(s => s.Id == ToolState.Signature?.Id) ?? all.FirstOrDefault();
            ToolState.Signature = SignaturePicker.SelectedItem as SavedSignature;
        }
        finally { _refreshingSignatures = false; }
    }

    void SignaturePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSignatures) return;
        ToolState.Signature = SignaturePicker.SelectedItem as SavedSignature;
    }

    void NewSignature_Click(object sender, RoutedEventArgs e)
    {
        if (SignatureDialog.Show(this) is { } created)
        {
            ToolState.Signature = created;
            RefreshSignatures();
            SelectTool(ToolMode.Sign);
        }
    }

    void DeleteSignature_Click(object sender, RoutedEventArgs e)
    {
        if (SignaturePicker.SelectedItem is not SavedSignature s) return;
        if (MessageBox.Show(this, $"Delete the saved signature “{s.Name}” from this PC?", "Delete signature", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        SignatureStore.Remove(s.Id);
        ToolState.Signature = null;
        RefreshSignatures();
    }

    // ---------- Protect ----------

    /// <summary>Runs a session-level command (protect/unprotect) with uniform error reporting.</summary>
    bool RunSessionCommand(DocumentViewModel vm, string title, Action action)
    {
        if (vm.Session.IsReadOnly)
        {
            MessageBox.Show(this, vm.AccessNotice, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            MessageBox.Show(this, $"{title} failed:\n\n{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void Protect_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (vm.Session.IsReadOnly) { RunSessionCommand(vm, "Protect", () => { }); return; }
        if (ProtectDialog.Show(this) is not { } options) return;
        if (RunSessionCommand(vm, "Protect", () => vm.Session.Protect(options)))
            Status($"Protected with {(options.Strength == EncryptionStrength.Aes256 ? "AES-256" : "AES-128")}. Save to apply it to the file.");
    }

    void RemoveSecurity_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (!vm.Session.IsEncrypted) { Status("This document is not protected."); return; }
        if (MessageBox.Show(this, "Remove all passwords and restrictions from this document?", "Remove security",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (RunSessionCommand(vm, "Remove security", vm.Session.RemoveProtection))
            Status("Security removed. Save to apply it to the file.");
    }

    void Sanitize_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        IReadOnlyList<string> findings;
        try { findings = vm.Session.Query(Sanitizer.Inspect); }
        catch (Exception ex) { App.LogError(ex); findings = []; }
        if (SanitizeDialog.Show(this, findings) is not { } options) return;
        SanitizeReport? report = null;
        if (vm.Apply("Remove hidden information", d => report = Sanitizer.Sanitize(d, options)))
            Status(report!.Total == 0 ? "No hidden information found." : $"Removed {report.Total} item(s) of hidden information. Save to apply.");
    }

    // ---------- Convert and optimize ----------

    void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        int choice = ChoiceDialog.Show(this, "Compress PDF", "Choose how much to reduce the file:",
        [
            ("Balanced (recommended)", "Images up to 150 DPI, good JPEG quality. Fine for screens and office printing."),
            ("High quality", "Images up to 300 DPI, high JPEG quality. For professional printing."),
            ("Smallest file", "Images up to 96 DPI, lower quality. For email and web."),
            ("Lossless", "Keeps every image pixel-perfect; only compresses structure and removes duplicates."),
        ]);
        if (choice < 0) return;
        var options = choice switch { 1 => OptimizeOptions.HighQuality, 2 => OptimizeOptions.Smallest, 3 => OptimizeOptions.Lossless, _ => OptimizeOptions.Balanced };
        long before = vm.Session.CurrentBytes.LongLength;
        OptimizeReport? report = null;
        Mouse.OverrideCursor = Cursors.Wait;
        bool ok;
        try { ok = vm.Apply("Compress", d => report = PdfOptimizer.Optimize(d, options)); }
        finally { Mouse.OverrideCursor = null; }
        if (!ok) return;

        long after = vm.Session.CurrentBytes.LongLength;
        if (after >= before)
        {
            vm.Session.Undo(); // Nothing gained; keep the original bytes.
            MessageBox.Show(this, $"This file is already compact ({FormatSize(before)}); compressing it would not make it smaller.", "Compress PDF");
            return;
        }
        var skipped = report!.ImagesSkipped > 0 ? $"\n{report.ImagesSkipped} image(s) use formats that are kept as they are." : "";
        MessageBox.Show(this,
            $"{FormatSize(before)} → {FormatSize(after)} ({100 - after * 100 / before}% smaller).\n" +
            $"{report.ImagesRecompressed} image(s) recompressed, {report.ImagesDownsampled} downsampled, {report.DuplicatesMerged} duplicate(s) merged.{skipped}\n\n" +
            "Save to keep the smaller file, or Undo to go back.", "Compress PDF", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1 << 10 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";

    void ImagesToPdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose images (pages are created in the order selected)",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        int size = ChoiceDialog.Show(this, "PDF from images", "Page size:",
        [
            ("Same as each image", "Each page is exactly the size of its image."),
            ("Letter (8.5 × 11 in)", "Images are fitted inside the page with a margin."),
            ("A4 (210 × 297 mm)", "Images are fitted inside the page with a margin."),
        ]);
        if (size < 0) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var images = dialog.FileNames.Select(File.ReadAllBytes).ToList();
            using var doc = ImageConversion.ImagesToPdf(images, size switch { 1 => PageSizeMode.Letter, 2 => PageSizeMode.A4, _ => PageSizeMode.MatchImage });
            var session = DocumentSession.FromBytes(DocumentSession.Serialize(doc));
            AddTab(new DocumentViewModel(session));
            Status($"Created {session.PageCount} page(s) from {dialog.FileNames.Length} image file(s). Save to keep the PDF.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"The PDF could not be created.\n\n{ex.Message}", "PDF from images", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Mouse.OverrideCursor = null; }
    }

    void ExportImages_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        var vm = view.ViewModel;
        var initial = vm.Pages.Count == 1 ? "1" : $"1-{vm.Pages.Count}";
        if (ExportImagesDialog.Show(this, vm.Pages.Count, initial) is not { } options) return;
        string baseName = Path.GetFileNameWithoutExtension(vm.Session.DisplayName);
        try
        {
            if (options.MultiPageTiff)
            {
                var save = new SaveFileDialog { Filter = "TIFF image (*.tif)|*.tif", FileName = baseName + ".tif" };
                if (save.ShowDialog(this) != true) return;
                Mouse.OverrideCursor = Cursors.Wait;
                SafeFileWriter.Write(save.FileName, ImageConversion.PdfToMultiPageTiff(vm.Session.Renderer, options.Pages, options.Dpi));
                Status($"Saved {options.Pages.Count} page(s) to {Path.GetFileName(save.FileName)}.");
                return;
            }
            var folder = new OpenFolderDialog { Title = "Save images to" };
            if (vm.FilePath is { } p) folder.InitialDirectory = Path.GetDirectoryName(p);
            if (folder.ShowDialog(this) != true) return;
            string ext = options.Format switch { ImageFileFormat.Jpeg => "jpg", ImageFileFormat.Tiff => "tif", _ => "png" };
            int digits = vm.Pages.Count.ToString().Length;
            Mouse.OverrideCursor = Cursors.Wait;
            var files = ImageConversion.PdfToImages(vm.Session.Renderer, options.Pages, options.Dpi, options.Format,
                i => Path.Combine(folder.FolderName, $"{baseName} p{(i + 1).ToString().PadLeft(digits, '0')}.{ext}"), options.JpegQuality);
            Status($"Saved {files.Count} image(s) to {folder.FolderName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"The images could not be saved.\n\n{ex.Message}", "Export images", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Mouse.OverrideCursor = null; }
    }

    // ---------- Command bar ----------

    void HeaderSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        HeaderSearchHint.Visibility = HeaderSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async void HeaderSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HeaderSearch.Clear(); ActiveView?.Focus(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (ActiveView is not { } view) { Status("Open a document to search it."); return; }
        Status("Searching…");
        int hits = await view.SearchFor(HeaderSearch.Text);
        if (hits >= 0) Status(HeaderSearch.Text.Trim().Length == 0 ? "" : hits == 0 ? "No matches." : $"{hits} match(es). F3 for the next one.");
    }

    void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        var next = App.IsDarkActive ? "Light" : "Dark";
        (next == "Light" ? ThemeLight : ThemeDark).IsChecked = true;
    }

    bool _ribbonCollapsed;

    void CollapseRibbon_Click(object sender, RoutedEventArgs e) => SetRibbonCollapsed(!_ribbonCollapsed);

    /// <summary>Collapsed: only the tab names show; clicking a tab opens the ribbon again.</summary>
    void SetRibbonCollapsed(bool collapsed)
    {
        _ribbonCollapsed = collapsed;
        Ribbon.MaxHeight = collapsed ? 44 : double.PositiveInfinity;
        Ribbon.ClipToBounds = collapsed;
        CollapseRibbonButton.Content = collapsed ? "\uE70D" : "\uE70E";
        CollapseRibbonButton.ToolTip = collapsed ? "Show the ribbon (Ctrl+F1)" : "Collapse the ribbon (Ctrl+F1)";
        System.Windows.Automation.AutomationProperties.SetName(CollapseRibbonButton, collapsed ? "Show ribbon" : "Collapse ribbon");
    }

    void Ribbon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_ribbonCollapsed && e.OriginalSource is DependencyObject d && FindAncestor<TabItem>(d) is not null) SetRibbonCollapsed(false);
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }

    // ---------- Phase 3: digital signatures, redaction, Office, OCR, batch ----------

    void SignInvisibly_Click(object sender, RoutedEventArgs e) => ActiveView?.SignDigitally(Active!.CurrentPage, null, null);

    void Signatures_Click(object sender, RoutedEventArgs e) => ActiveView?.ShowSignatures();

    void MarkText_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (InputDialog.Show(this, "Mark text for redaction", "Mark every occurrence of (not case-sensitive):",
                vm.Selection?.Text.Trim() ?? "") is not { Length: > 0 } text) return;
        int hits = vm.MarkSearchForRedaction(text, matchCase: false, wholeWord: false);
        Status(hits == 0 ? $"“{text}” was not found. Scanned pages need Recognize text first."
            : $"Marked {hits} occurrence(s). Review the red marks, then Apply.");
    }

    void ClearRedactions_Click(object sender, RoutedEventArgs e)
    {
        Active?.ClearRedactions();
        Status("Redaction marks cleared.");
    }

    void ApplyRedactions_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (vm.Redactions.Count == 0) { Status("Mark areas first (Mark area or Mark text)."); return; }
        int choice = ChoiceDialog.Show(this, "Apply redactions",
            $"Permanently remove the content in {vm.Redactions.Count} marked area(s)? Text, images and drawings under the marks are deleted from the file " +
            "and replaced by black boxes. You can undo until you save.",
        [
            ("Redact and remove hidden information (recommended)", "Also removes metadata, attachments, scripts and other hidden data that could contain the same information."),
            ("Redact only", "Only the marked areas are changed."),
        ]);
        if (choice < 0) return;
        var report = vm.ApplyRedactions();
        if (report is null) return;
        if (choice == 0) vm.Apply("Remove hidden information", d => Sanitizer.Sanitize(d, new SanitizeOptions()));
        Status($"Redacted {report.Areas} area(s): {report.ObjectsRemoved} content object(s) removed" +
               (report.AnnotationsRemoved > 0 ? $", {report.AnnotationsRemoved} comment(s)/field(s) removed" : "") + ". Save to make it final.");
    }

    async void OfficeToPdf_Click(object sender, RoutedEventArgs e)
    {
        if (!OfficeConversion.IsInstalled(OfficeApp.Word) && !OfficeConversion.IsInstalled(OfficeApp.Excel) && !OfficeConversion.IsInstalled(OfficeApp.PowerPoint))
        {
            MessageBox.Show(this, "Converting Office files needs Microsoft Office (Word, Excel or PowerPoint) on this PC.", "PDF from Office",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Choose Office files", Multiselect = true,
            Filter = "Office files|*.docx;*.doc;*.docm;*.rtf;*.odt;*.txt;*.xlsx;*.xls;*.xlsm;*.ods;*.csv;*.pptx;*.ppt;*.pptm;*.odp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            var app = OfficeConversion.AppFor(file);
            if (app is null || !OfficeConversion.IsInstalled(app.Value))
            {
                MessageBox.Show(this, $"“{Path.GetFileName(file)}” needs Microsoft {app?.ToString() ?? "Office"}, which is not installed.", "PDF from Office");
                continue;
            }
            var temp = Path.Combine(Path.GetTempPath(), "OptimPdf", "Converted", Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(file) + ".pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            Status($"Converting {Path.GetFileName(file)} with Microsoft {app}…");
            Mouse.OverrideCursor = Cursors.AppStarting;
            try
            {
                await OfficeConversion.ToPdfAsync(file, temp);
                var target = Path.ChangeExtension(file, ".pdf");
                var session = DocumentSession.FromBytes(File.ReadAllBytes(temp), File.Exists(target) ? null : target);
                AddTab(new DocumentViewModel(session));
                Status($"Converted {Path.GetFileName(file)}. Save to keep the PDF.");
            }
            catch (OfficeConversionException ex)
            {
                MessageBox.Show(this, ex.Message, "PDF from Office", MessageBoxButton.OK, MessageBoxImage.Warning);
                Status("");
            }
            finally
            {
                Mouse.OverrideCursor = null;
                try { Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true); } catch (IOException) { }
            }
        }
    }

    async void ToWord_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (!OfficeConversion.IsInstalled(OfficeApp.Word))
        {
            MessageBox.Show(this, "Converting to Word needs Microsoft Word on this PC.", "Convert to Word", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (vm.Session.IsEncrypted)
        {
            MessageBox.Show(this, "Remove security first (Protect › Remove security); Word cannot open protected PDFs without asking for the password.",
                "Convert to Word", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var save = new SaveFileDialog
        {
            Filter = "Word document (*.docx)|*.docx", FileName = Path.GetFileNameWithoutExtension(vm.Session.DisplayName) + ".docx",
            InitialDirectory = vm.FilePath is { } p ? Path.GetDirectoryName(p) : null,
        };
        if (save.ShowDialog(this) != true) return;
        var dir = Path.Combine(Path.GetTempPath(), "OptimPdf", "Converted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pdf = Path.Combine(dir, "source.pdf");
        var docx = Path.Combine(dir, "result.docx");
        Status("Converting with Microsoft Word… (layout is rebuilt for editing, so it may differ from the PDF)");
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            File.WriteAllBytes(pdf, vm.Session.CurrentBytes);
            await OfficeConversion.PdfToWordAsync(pdf, docx);
            SafeFileWriter.Write(save.FileName, File.ReadAllBytes(docx));
            Status($"Saved {Path.GetFileName(save.FileName)}.");
        }
        catch (Exception ex) when (ex is OfficeConversionException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Convert to Word", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    async void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not { } vm) return;
        if (vm.Session.IsReadOnly) { vm.Run("Recognize text", () => { }); return; }
        if (OcrDialog.Show(this, vm.Pages.Count, vm.CurrentPage) is not { } choice) return;
        long version = vm.Session.ContentVersion;
        IReadOnlyList<OcrPage> pages;
        Ribbon.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => Status($"Recognizing text… page {p.Done} of {p.Total}"));
            pages = await OcrService.RecognizeAsync(vm.Session.Renderer, choice.Pages, new OcrOptions(choice.Language, SkipPagesWithText: choice.SkipPagesWithText), progress);
        }
        catch (OcrUnavailableException ex)
        {
            MessageBox.Show(this, ex.Message, "Recognize text", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        finally
        {
            Ribbon.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
        if (vm.Session.ContentVersion != version) { Status("The document changed during recognition; run it again."); return; }
        int skipped = pages.Count(p => p.Skipped), words = pages.Sum(p => p.WordCount);
        if (words == 0)
        {
            Status(skipped == pages.Count ? "All chosen pages already have text; nothing to recognize." : "No text was recognized on the chosen pages.");
            return;
        }
        if (vm.Apply("Recognize text", d => OcrService.AddTextLayer(d, pages)))
            Status($"Recognized {words} word(s) on {pages.Count - skipped} page(s)" + (skipped > 0 ? $"; {skipped} page(s) already had text." : ".") +
                   " The text is now searchable.");
    }

    void Batch_Click(object sender, RoutedEventArgs e)
    {
        var initial = Active?.FilePath is { } path ? new[] { path } : null;
        new BatchDialog(this, initial).ShowDialog();
    }

    // ---------- Crash recovery ----------

    void OfferRecovery()
    {
        IReadOnlyList<RecoveryEntry> entries;
        try { entries = DocumentViewModel.Recovery.ListOrphaned(); }
        catch (Exception ex) { App.LogError(ex); return; }
        if (entries.Count == 0) return;

        var restore = RecoveryDialog.Show(this, entries);
        if (restore is null) return; // Decide next time.
        foreach (var entry in entries)
        {
            if (restore.Contains(entry))
            {
                try
                {
                    var data = File.ReadAllBytes(entry.DataPath);
                    string? password = null;
                    while (true)
                    {
                        try
                        {
                            var session = DocumentSession.FromBytes(data, entry.OriginalPath, password);
                            AddTab(new DocumentViewModel(session));
                            break;
                        }
                        catch (PdfPasswordRequiredException)
                        {
                            password = InputDialog.ShowPassword(this, "Password required", $"Enter the password of “{entry.DisplayName}”:");
                            if (password is null) throw new OperationCanceledException();
                        }
                    }
                }
                catch (OperationCanceledException) { continue; } // Keep the snapshot for later.
                catch (Exception ex) when (ex is IOException or PdfOpenException)
                {
                    MessageBox.Show(this, $"“{entry.DisplayName}” could not be restored.\n\n{ex.Message}", "Recover unsaved work");
                }
            }
            DocumentViewModel.Recovery.Delete(entry.Id);
        }
    }

    // ---------- View commands ----------

    void ZoomIn_Click(object sender, RoutedEventArgs e) => Active?.ZoomIn();
    void ZoomOut_Click(object sender, RoutedEventArgs e) => Active?.ZoomOut();
    void ActualSize_Click(object sender, RoutedEventArgs e) { if (Active is { } vm) vm.Zoom = 1; }
    void FitWidth_Click(object sender, RoutedEventArgs e) { if (ActiveView is { } v) v.ViewModel.FitWidth(v.ViewportWidth); }
    void FitPage_Click(object sender, RoutedEventArgs e) { if (ActiveView is { } v) v.ViewModel.FitPage(v.ViewportWidth, v.ViewportHeight); }
    void ViewRotateLeft_Click(object sender, RoutedEventArgs e) { if (Active is { } vm) vm.ViewRotation -= 1; }
    void ViewRotateRight_Click(object sender, RoutedEventArgs e) { if (Active is { } vm) vm.ViewRotation += 1; }
    void TogglePanel_Click(object sender, RoutedEventArgs e) => ActiveView?.ToggleSidePanel();

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Content: string theme } && theme != App.Settings.Theme) App.ApplyTheme(theme);

    }

    void About_Click(object sender, RoutedEventArgs e) => AboutDialog.Show(this);

    void PrevPage_Click(object sender, RoutedEventArgs e) { if (ActiveView is { } v) v.GoToPage(v.ViewModel.CurrentPage - 1); }
    void NextPage_Click(object sender, RoutedEventArgs e) { if (ActiveView is { } v) v.GoToPage(v.ViewModel.CurrentPage + 1); }

    void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ActiveView is not { } v) return;
        if (int.TryParse(PageBox.Text, out int n)) v.GoToPage(Math.Clamp(n, 1, v.ViewModel.Pages.Count) - 1);
        v.Focus();
        e.Handled = true;
    }

    // ---------- Drag and drop ----------

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))) OpenFile(f);
    }

    // ---------- Keyboard ----------

    void AddShortcuts()
    {
        void Bind(Key key, ModifierKeys mods, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action), key, mods));

        Bind(Key.O, ModifierKeys.Control, () => Open_Click(this, new RoutedEventArgs()));
        Bind(Key.S, ModifierKeys.Control, () => Save_Click(this, new RoutedEventArgs()));
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, () => SaveAs_Click(this, new RoutedEventArgs()));
        Bind(Key.P, ModifierKeys.Control, () => Print_Click(this, new RoutedEventArgs()));
        Bind(Key.W, ModifierKeys.Control, () => { if (DocTabs.SelectedItem is TabItem t) CloseTab(t); });
        Bind(Key.Z, ModifierKeys.Control, () => Active?.Session.Undo());
        Bind(Key.Y, ModifierKeys.Control, () => Active?.Session.Redo());
        Bind(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, () => Active?.Session.Redo());
        Bind(Key.F, ModifierKeys.Control, () => { HeaderSearch.Focus(); HeaderSearch.SelectAll(); });
        Bind(Key.F, ModifierKeys.Control | ModifierKeys.Shift, () => ActiveView?.ShowSearch());
        Bind(Key.F1, ModifierKeys.Control, () => SetRibbonCollapsed(!_ribbonCollapsed));
        Bind(Key.F3, ModifierKeys.None, () => ActiveView?.NextSearchResult(1));
        Bind(Key.F3, ModifierKeys.Shift, () => ActiveView?.NextSearchResult(-1));
        Bind(Key.D, ModifierKeys.Control, () => Properties_Click(this, new RoutedEventArgs()));
        Bind(Key.OemPlus, ModifierKeys.Control, () => Active?.ZoomIn());
        Bind(Key.Add, ModifierKeys.Control, () => Active?.ZoomIn());
        Bind(Key.OemMinus, ModifierKeys.Control, () => Active?.ZoomOut());
        Bind(Key.Subtract, ModifierKeys.Control, () => Active?.ZoomOut());
        Bind(Key.D0, ModifierKeys.Control, () => { if (Active is { } vm) vm.Zoom = 1; });
        Bind(Key.F4, ModifierKeys.None, () => ActiveView?.ToggleSidePanel());
        Bind(Key.Escape, ModifierKeys.None, () => { SelectTool(ToolMode.Select); Active?.SetSelection(null); });
        Bind(Key.Tab, ModifierKeys.Control, () => { if (DocTabs.Items.Count > 0) DocTabs.SelectedIndex = (DocTabs.SelectedIndex + 1) % DocTabs.Items.Count; });
    }
}

sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
