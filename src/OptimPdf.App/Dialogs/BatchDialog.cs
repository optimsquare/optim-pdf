using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OptimPdf.Core.Batch;
using OptimPdf.Core.Conversion;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Ocr;
using OptimPdf.Core.Optimization;
using OptimPdf.Core.Security;

namespace OptimPdf.App.Dialogs;

/// <summary>Runs one action over many files: compress, protect, OCR, convert, export, rename or merge.</summary>
public sealed class BatchDialog : Window
{
    readonly ObservableCollection<string> _files = [];
    readonly ListBox _fileList = new() { Height = 150, SelectionMode = SelectionMode.Extended };
    readonly ComboBox _action = new();
    readonly ContentControl _options = new();
    readonly TextBox _output = new();
    readonly TextBox _template = new() { Text = "{name}" };
    readonly ProgressBar _progress = new() { Height = 6, Margin = new Thickness(0, 12, 0, 4), Visibility = Visibility.Collapsed };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly ListBox _results = new() { Height = 130, Visibility = Visibility.Collapsed, FontSize = 12 };
    readonly Button _run = new() { Content = "Run", MinWidth = 96, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _close = new() { Content = "Close", MinWidth = 96, IsCancel = true };
    CancellationTokenSource? _cancel;

    sealed record Lang(string Tag, string Name)
    {
        public override string ToString() => Name; // Read by screen readers.
    }

    // Option controls (created once, shown per action).
    readonly ComboBox _preset = new() { ItemsSource = new[] { "Balanced", "High quality", "Smallest file", "Lossless" }, SelectedIndex = 0 };
    readonly PasswordBox _openPw = new(), _ownerPw = new();
    readonly ComboBox _ocrLanguage = new() { DisplayMemberPath = "Name" };
    readonly ComboBox _rotate = new() { ItemsSource = new[] { "90° clockwise", "180°", "90° counter-clockwise" }, SelectedIndex = 0 };
    readonly ComboBox _imageFormat = new() { ItemsSource = new[] { "PNG", "JPEG", "TIFF" }, SelectedIndex = 0 };
    readonly ComboBox _dpi = new() { ItemsSource = new[] { "72", "150", "300" }, SelectedIndex = 1 };
    readonly ComboBox _imagePage = new() { ItemsSource = new[] { "Same as each image", "Letter", "A4" }, SelectedIndex = 0 };
    readonly TextBox _mergeName = new() { Text = "Combined" };

    static readonly string[] Actions =
    [
        "Compress", "Add password", "Remove hidden information", "Recognize text (OCR)", "Rotate pages",
        "Convert to PDF (Office files and images)", "Export pages as images", "Rename (copy with new names)", "Merge into one PDF",
    ];

    public BatchDialog(Window owner, IEnumerable<string>? initialFiles = null)
    {
        Title = "Batch process";
        Owner = owner;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        foreach (var f in initialFiles ?? []) _files.Add(f);
        _fileList.ItemsSource = _files;
        _action.ItemsSource = Actions;
        _action.SelectedIndex = 0;
        _action.SelectionChanged += (_, _) => ShowOptions();
        var langs = OcrService.AvailableLanguages.Select(l => new Lang(l.Tag, l.Name)).ToList();
        _ocrLanguage.ItemsSource = langs;
        _ocrLanguage.SelectedIndex = langs.Count > 0 ? 0 : -1;
        _output.Text = initialFiles?.FirstOrDefault() is { } first ? Path.Combine(Path.GetDirectoryName(first)!, "Processed") : "";

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Heading("Files"));
        root.Children.Add(_fileList);
        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        fileButtons.Children.Add(Small("Add files…", AddFiles));
        fileButtons.Children.Add(Small("Add folder…", AddFolder));
        fileButtons.Children.Add(Small("Remove", () => { foreach (var f in _fileList.SelectedItems.Cast<string>().ToList()) _files.Remove(f); }));
        fileButtons.Children.Add(Small("Clear", _files.Clear));
        root.Children.Add(fileButtons);

        root.Children.Add(Heading("Action"));
        root.Children.Add(_action);
        _options.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_options);

        root.Children.Add(Heading("Output"));
        var outRow = new DockPanel();
        var browse = Small("Browse…", BrowseOutput);
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);
        outRow.Children.Add(browse);
        outRow.Children.Add(_output);
        root.Children.Add(Rows(("Folder", outRow), ("File names", _template)));
        root.Children.Add(Note("Name fields: {name} original name, {n} or {n:000} number, {date} today, {title} PDF title. " +
                               "Originals are never changed, and existing files are never overwritten."));

        root.Children.Add(_progress);
        root.Children.Add(_status);
        root.Children.Add(_results);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_run);
        buttons.Children.Add(_close);
        root.Children.Add(buttons);
        Content = root;

        _run.Click += async (_, _) => await RunAsync();
        _close.Click += (_, _) => { if (_cancel is not null) _cancel.Cancel(); else Close(); };
        Closing += (_, e) => { if (_cancel is not null) { _cancel.Cancel(); e.Cancel = true; } };
        ShowOptions();
    }

    readonly Dictionary<int, FrameworkElement?> _panels = [];

    void ShowOptions()
    {
        int action = _action.SelectedIndex;
        // Each panel is built once: its controls can only have one parent.
        if (!_panels.TryGetValue(action, out var panel)) _panels[action] = panel = BuildOptions(action);
        _options.Content = panel;
        _template.IsEnabled = action != 8;
    }

    FrameworkElement? BuildOptions(int action) => action switch
        {
            0 => Rows(("Quality", _preset)),
            1 => new StackPanel { Children = { Rows(("Open password", _openPw), ("Permissions password", _ownerPw)),
                    Note("AES-256. The open password may be left empty (permissions only).") } },
            3 => new StackPanel { Children = { Rows(("Language", _ocrLanguage)), Note("Pages that already have text are left as they are.") } },
            4 => Rows(("Rotate", _rotate)),
            5 => new StackPanel { Children = { Rows(("Image page size", _imagePage)),
                    Note(OfficeConversion.IsInstalled(OfficeApp.Word) ? "Word, Excel and PowerPoint files are converted with your installed Microsoft Office."
                        : "Microsoft Office is not installed: only images can be converted.") } },
            6 => Rows(("Format", _imageFormat), ("Resolution (DPI)", _dpi)),
            8 => Rows(("Output name", _mergeName)),
            _ => null,
        };

    BatchOperation? Operation(out string? problem)
    {
        problem = null;
        switch (_action.SelectedIndex)
        {
            case 0: return new CompressOperation(_preset.SelectedIndex switch { 1 => OptimizeOptions.HighQuality, 2 => OptimizeOptions.Smallest, 3 => OptimizeOptions.Lossless, _ => OptimizeOptions.Balanced });
            case 1:
                if (_ownerPw.Password.Length < 6) { problem = "The permissions password needs at least 6 characters."; return null; }
                if (_openPw.Password.Length is > 0 and < 4) { problem = "The open password needs at least 4 characters."; return null; }
                return new ProtectOperation(new ProtectionOptions(_openPw.Password.Length == 0 ? null : _openPw.Password, _ownerPw.Password, new PdfPermissions(), EncryptionStrength.Aes256));
            case 2: return new RemoveHiddenInfoOperation(new SanitizeOptions());
            case 3:
                if (_ocrLanguage.SelectedItem is null) { problem = "Install an OCR language in Windows Settings first."; return null; }
                return new OcrOperation(new OcrOptions(((Lang)_ocrLanguage.SelectedItem).Tag));
            case 4: return new RotateOperation(_rotate.SelectedIndex switch { 1 => 180, 2 => -90, _ => 90 });
            case 5: return new ConvertToPdfOperation(_imagePage.SelectedIndex switch { 1 => PageSizeMode.Letter, 2 => PageSizeMode.A4, _ => PageSizeMode.MatchImage });
            case 6: return new ExportImagesOperation(_imageFormat.SelectedIndex switch { 1 => ImageFileFormat.Jpeg, 2 => ImageFileFormat.Tiff, _ => ImageFileFormat.Png }, double.Parse((string)_dpi.SelectedItem));
            case 7: return new RenameOperation();
            default:
                if (_files.Count < 2) { problem = "Add at least two PDFs to merge."; return null; }
                return new MergeOperation(string.IsNullOrWhiteSpace(_mergeName.Text) ? "Combined" : _mergeName.Text.Trim());
        }
    }

    async Task RunAsync()
    {
        if (_files.Count == 0) { _status.Text = "Add files first."; return; }
        if (string.IsNullOrWhiteSpace(_output.Text)) { _status.Text = "Choose an output folder."; return; }
        if (Operation(out var problem) is not { } operation) { _status.Text = problem; return; }

        _cancel = new CancellationTokenSource();
        _run.IsEnabled = false;
        _close.Content = "Cancel";
        _progress.Visibility = Visibility.Visible;
        _results.Visibility = Visibility.Visible;
        _results.Items.Clear();
        var progress = new Progress<BatchProgress>(p =>
        {
            _progress.Maximum = Math.Max(1, p.Total);
            _progress.Value = p.Done;
            _status.Text = p.Current is null ? "Finishing…" : $"{p.Done + 1} of {p.Total}: {p.Current}";
        });
        try
        {
            var processor = new BatchProcessor(operation, _output.Text.Trim(), _template.Text);
            var results = await processor.RunAsync(_files.ToList(), progress, _cancel.Token);
            foreach (var r in results)
                _results.Items.Add(r.Success
                    ? $"✓ {Path.GetFileName(r.Input)} → {string.Join(", ", r.Outputs.Select(Path.GetFileName).Take(3))}{(r.Outputs.Count > 3 ? $" (+{r.Outputs.Count - 3})" : "")}"
                    : $"✗ {Path.GetFileName(r.Input)}: {r.Message}");
            int ok = results.Count(r => r.Success);
            _status.Text = $"Done: {ok} of {results.Count} succeeded. Results are in {processor.OutputFolder}.";
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled. Files finished before cancelling were kept."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status.Text = "The batch could not run: " + ex.Message;
        }
        finally
        {
            _cancel = null;
            _run.IsEnabled = true;
            _close.Content = "Close";
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true, Title = "Add files",
            Filter = "Supported files|*.pdf;*.docx;*.doc;*.rtf;*.odt;*.txt;*.xlsx;*.xls;*.csv;*.pptx;*.ppt;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif|PDF documents|*.pdf|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var f in dialog.FileNames) if (!_files.Contains(f)) _files.Add(f);
        if (string.IsNullOrWhiteSpace(_output.Text)) _output.Text = Path.Combine(Path.GetDirectoryName(dialog.FileNames[0])!, "Processed");
    }

    void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add all PDFs in a folder" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var f in Directory.EnumerateFiles(dialog.FolderName, "*.pdf").Order()) if (!_files.Contains(f)) _files.Add(f);
        if (string.IsNullOrWhiteSpace(_output.Text)) _output.Text = Path.Combine(dialog.FolderName, "Processed");
    }

    void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save results to" };
        if (dialog.ShowDialog(this) == true) _output.Text = dialog.FolderName;
    }

    static Button Small(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };

    static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) };

    static Grid Rows(params (string Label, FrameworkElement Control)[] rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(label, i);
            rows[i].Control.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(rows[i].Control, i);
            Grid.SetColumn(rows[i].Control, 1);
            grid.Children.Add(label);
            grid.Children.Add(rows[i].Control);
        }
        return grid;
    }
}
