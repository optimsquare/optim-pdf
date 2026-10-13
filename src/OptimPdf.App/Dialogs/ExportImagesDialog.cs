using System.Windows;
using System.Windows.Controls;
using OptimPdf.Core.Editing;
using OptimPdf.Core.IO;
using OptimPdf.Core.Pages;

namespace OptimPdf.App.Dialogs;

public sealed record ExportImagesOptions(ImageFileFormat Format, bool MultiPageTiff, int Dpi, int JpegQuality, IReadOnlyList<int> Pages);

public sealed class ExportImagesDialog : DialogBase
{
    readonly ComboBox _format = new() { ItemsSource = new[] { "PNG (lossless)", "JPEG (smaller files)", "TIFF (one file per page)", "TIFF (single multi-page file)" }, SelectedIndex = 0 };
    readonly ComboBox _dpi = new() { ItemsSource = new[] { 72, 96, 150, 200, 300, 600 }, SelectedItem = 150 };
    readonly Slider _quality = new() { Minimum = 40, Maximum = 100, Value = 90, TickFrequency = 5, IsSnapToTickEnabled = true };
    readonly TextBox _pages = new();
    readonly int _pageCount;
    IReadOnlyList<int> _parsed = [];

    ExportImagesDialog(Window owner, int pageCount, string initialPages) : base(owner, "Export pages as images", 460, "Export")
    {
        _pageCount = pageCount;
        _pages.Text = initialPages;
        var qualityLabel = new TextBlock();
        _quality.ValueChanged += (_, _) => qualityLabel.Text = $"JPEG quality {_quality.Value:0}";
        qualityLabel.Text = $"JPEG quality {_quality.Value:0}";
        var qualityPanel = new StackPanel();
        qualityPanel.Children.Add(qualityLabel);
        qualityPanel.Children.Add(_quality);
        _format.SelectionChanged += (_, _) => qualityPanel.IsEnabled = _format.SelectedIndex == 1;
        qualityPanel.IsEnabled = false;

        var body = new StackPanel();
        body.Children.Add(LabeledRows(("Format", _format), ("Resolution (DPI)", _dpi), ($"Pages (1â€“{pageCount})", _pages)));
        body.Children.Add(qualityPanel);
        body.Children.Add(Note("300 DPI suits printing; 150 DPI suits screens and email."));
        SetBody(body);
    }

    protected override string? OnAccept()
    {
        try { _parsed = Core.Pages.PageRange.Parse(_pages.Text, _pageCount).SelectMany(r => r.Indices()).ToList(); }
        catch (FormatException ex) { return ex.Message; }
        return null;
    }

    ExportImagesOptions Result => new(
        _format.SelectedIndex switch { 1 => ImageFileFormat.Jpeg, 2 or 3 => ImageFileFormat.Tiff, _ => ImageFileFormat.Png },
        _format.SelectedIndex == 3, (int)_dpi.SelectedItem, (int)_quality.Value, _parsed);

    public static ExportImagesOptions? Show(Window owner, int pageCount, string initialPages)
    {
        var d = new ExportImagesDialog(owner, pageCount, initialPages);
        return d.ShowDialog() == true ? d.Result : null;
    }
}

/// <summary>Offers to restore documents autosaved before the app closed unexpectedly.</summary>
public sealed class RecoveryDialog : DialogBase
{
    readonly List<(CheckBox Box, RecoveryEntry Entry)> _items = [];

    RecoveryDialog(Window owner, IReadOnlyList<RecoveryEntry> entries) : base(owner, "Recover unsaved work", 520, "Restore selected")
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "Optim PDF closed before these changes were saved. Restored documents open as unsaved copies; your original files were not changed.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        foreach (var e in entries)
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = e.DisplayName, FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = $"Autosaved {e.SavedAt:g}" + (e.OriginalPath is { } p ? $" Â· {p}" : ""), FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            var box = new CheckBox { Content = label, IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
            _items.Add((box, e));
            body.Children.Add(box);
        }
        body.Children.Add(Note("Entries you don't restore are deleted."));
        SetBody(body);
    }

    /// <returns>The entries to restore (empty: discard all), or null to decide later.</returns>
    public static IReadOnlyList<RecoveryEntry>? Show(Window owner, IReadOnlyList<RecoveryEntry> entries)
    {
        var d = new RecoveryDialog(owner, entries);
        return d.ShowDialog() == true ? d._items.Where(i => i.Box.IsChecked == true).Select(i => i.Entry).ToList() : null;
    }
}
