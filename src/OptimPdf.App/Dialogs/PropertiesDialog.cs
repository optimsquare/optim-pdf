using System.Windows;
using System.Windows.Controls;
using OptimPdf.Core.Documents;

namespace OptimPdf.App.Dialogs;

/// <summary>Shows document properties; Title/Author/Subject/Keywords are editable.</summary>
public sealed class PropertiesDialog : Window
{
    readonly TextBox _title, _author, _subject, _keywords;

    PropertiesDialog(Window owner, DocumentProperties p, string? path)
    {
        Title = "Document properties";
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        int row = 0;
        TextBox Editable(string label, string? value)
        {
            var box = new TextBox { Text = value ?? "", Margin = new Thickness(0, 3, 0, 3) };
            AddRow(label, box);
            return box;
        }
        void ReadOnly(string label, string? value) =>
            AddRow(label, new TextBlock { Text = string.IsNullOrEmpty(value) ? "—" : value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        void AddRow(string label, FrameworkElement element)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
            Grid.SetRow(text, row);
            Grid.SetRow(element, row);
            Grid.SetColumn(element, 1);
            grid.Children.Add(text);
            grid.Children.Add(element);
            row++;
        }

        _title = Editable("Title", p.Title);
        _author = Editable("Author", p.Author);
        _subject = Editable("Subject", p.Subject);
        _keywords = Editable("Keywords", p.Keywords);
        ReadOnly("File", path);
        ReadOnly("File size", FormatSize(p.FileSize));
        ReadOnly("Pages", p.PageCount.ToString());
        ReadOnly("PDF version", p.PdfVersion);
        ReadOnly("Created", p.Created?.ToString("g"));
        ReadOnly("Modified", p.Modified?.ToString("g"));
        ReadOnly("Application", p.Creator);
        ReadOnly("PDF producer", p.Producer);
        ReadOnly("Security", p.Encrypted ? "Encrypted" : "None");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, row);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }

    static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1 << 10 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} bytes";

    /// <returns>The edited fields, or null when cancelled or nothing changed.</returns>
    public static (string Title, string Author, string Subject, string Keywords)? Show(Window owner, DocumentProperties p, string? path)
    {
        var d = new PropertiesDialog(owner, p, path);
        if (d.ShowDialog() != true) return null;
        var result = (d._title.Text, d._author.Text, d._subject.Text, d._keywords.Text);
        return result == (p.Title ?? "", p.Author ?? "", p.Subject ?? "", p.Keywords ?? "") ? null : result;
    }
}
