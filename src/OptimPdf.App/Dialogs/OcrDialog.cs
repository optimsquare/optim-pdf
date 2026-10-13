using System.Windows;
using System.Windows.Controls;
using OptimPdf.Core.Ocr;

namespace OptimPdf.App.Dialogs;

public sealed record OcrChoice(string Language, IReadOnlyList<int> Pages, bool SkipPagesWithText);

/// <summary>Language and pages for text recognition.</summary>
public sealed class OcrDialog : DialogBase
{
    readonly ComboBox _language = new() { DisplayMemberPath = "Name" };
    readonly RadioButton _all = new() { Content = "All pages", IsChecked = true, GroupName = "pages" };
    readonly RadioButton _current = new() { GroupName = "pages" };
    readonly RadioButton _range = new() { Content = "Pages:", GroupName = "pages" };
    readonly TextBox _rangeText = new() { Width = 160, Margin = new Thickness(8, 0, 0, 0) };
    readonly CheckBox _skip = new() { Content = "Skip pages that already have text", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    readonly int _pageCount, _currentPage;

    sealed record Lang(string Tag, string Name)
    {
        public override string ToString() => Name; // Read by screen readers.
    }

    OcrDialog(Window owner, int pageCount, int currentPage) : base(owner, "Recognize text (OCR)", 480, "Recognize")
    {
        _pageCount = pageCount;
        _currentPage = currentPage;
        _current.Content = $"Current page ({currentPage + 1})";
        var languages = OcrService.AvailableLanguages.Select(l => new Lang(l.Tag, l.Name)).ToList();
        _language.ItemsSource = languages;
        var ui = System.Globalization.CultureInfo.CurrentUICulture.Name;
        _language.SelectedItem = languages.FirstOrDefault(l => l.Tag.Equals(ui, StringComparison.OrdinalIgnoreCase))
                                 ?? languages.FirstOrDefault(l => l.Tag.StartsWith(ui.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                                 ?? languages.FirstOrDefault();

        var body = new StackPanel();
        body.Children.Add(Heading("Document language"));
        body.Children.Add(_language);
        body.Children.Add(Note(languages.Count == 0
            ? "No OCR language is installed. Add one in Windows Settings › Time & language › Language & region (language options › Optical character recognition)."
            : "Uses the OCR built into Windows; runs offline. More languages can be added in Windows Settings › Time & language › Language & region."));
        body.Children.Add(Heading("Pages"));
        body.Children.Add(_all);
        body.Children.Add(_current);
        var range = new StackPanel { Orientation = Orientation.Horizontal };
        range.Children.Add(_range);
        range.Children.Add(_rangeText);
        _rangeText.GotFocus += (_, _) => _range.IsChecked = true;
        body.Children.Add(range);
        body.Children.Add(_skip);
        body.Children.Add(Note("The recognized text is added invisibly behind each word: the pages look the same, and text can be searched, selected and copied."));
        SetBody(body);
    }

    IReadOnlyList<int> Pages()
    {
        if (_current.IsChecked == true) return [_currentPage];
        if (_range.IsChecked == true) return Core.Pages.PageRange.Parse(_rangeText.Text, _pageCount).SelectMany(r => r.Indices()).Distinct().Order().ToList();
        return Enumerable.Range(0, _pageCount).ToList();
    }

    protected override string? OnAccept()
    {
        if (_language.SelectedItem is null) return "Install an OCR language in Windows Settings first.";
        try { return Pages().Count == 0 ? "Choose at least one page." : null; }
        catch (FormatException ex) { return ex.Message; }
    }

    public static OcrChoice? Show(Window owner, int pageCount, int currentPage)
    {
        var d = new OcrDialog(owner, pageCount, currentPage);
        return d.ShowDialog() == true ? new OcrChoice(((Lang)d._language.SelectedItem).Tag, d.Pages(), d._skip.IsChecked == true) : null;
    }
}
