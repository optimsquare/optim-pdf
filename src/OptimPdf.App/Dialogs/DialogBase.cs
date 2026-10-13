using System.Windows;
using System.Windows.Controls;

namespace OptimPdf.App.Dialogs;

/// <summary>A modal dialog with a content area and OK/Cancel buttons; subclasses validate in <see cref="OnAccept"/>.</summary>
public abstract class DialogBase : Window
{
    readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    protected Button OkButton { get; }

    protected DialogBase(Window? owner, string title, double width = 460, string okText = "OK")
    {
        Title = title;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = width;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        OkButton = new Button { Content = okText, IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), Style = TryFindResource("AccentButtonStyle") as Style };
        OkButton.Click += (_, _) =>
        {
            var problem = OnAccept();
            if (problem is null) DialogResult = true;
            else
            {
                _error.Text = problem;
                _error.Visibility = Visibility.Visible;
            }
        };
    }

    /// <summary>Call once the content is built.</summary>
    protected void SetBody(UIElement body)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 });
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(body);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <returns>An error message to show, or null to close with OK.</returns>
    protected virtual string? OnAccept() => null;

    protected static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };

    protected static TextBlock Note(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0),
    };

    protected static Grid LabeledRows(params (string Label, FrameworkElement Control)[] rows)
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

/// <summary>Pick one option from a short list.</summary>
public sealed class ChoiceDialog : DialogBase
{
    readonly List<RadioButton> _options = [];

    ChoiceDialog(Window? owner, string title, string prompt, IReadOnlyList<(string Label, string Detail)> options, int selected) : base(owner, title)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        for (int i = 0; i < options.Count; i++)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = options[i].Label, FontWeight = FontWeights.SemiBold });
            if (options[i].Detail.Length > 0) panel.Children.Add(new TextBlock { Text = options[i].Detail, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            var rb = new RadioButton { Content = panel, IsChecked = i == selected, Margin = new Thickness(0, 4, 0, 4), GroupName = "choice" };
            _options.Add(rb);
            body.Children.Add(rb);
        }
        SetBody(body);
    }

    /// <returns>The chosen index, or -1 when cancelled.</returns>
    public static int Show(Window? owner, string title, string prompt, IReadOnlyList<(string Label, string Detail)> options, int selected = 0)
    {
        var d = new ChoiceDialog(owner, title, prompt, options, selected);
        return d.ShowDialog() == true ? d._options.FindIndex(o => o.IsChecked == true) : -1;
    }
}
