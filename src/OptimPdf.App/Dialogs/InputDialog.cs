using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OptimPdf.App.Dialogs;

/// <summary>Small modal prompt for a line of text, multi-line text, or a password.</summary>
public sealed class InputDialog : Window
{
    readonly TextBox? _text;
    readonly PasswordBox? _password;

    InputDialog(Window? owner, string title, string prompt, string initial, bool multiline, bool password)
    {
        Title = title;
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        Control input;
        if (password)
        {
            input = _password = new PasswordBox();
        }
        else
        {
            input = _text = new TextBox
            {
                Text = initial,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 90 : 0,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            };
        }
        panel.Children.Add(input);
        if (multiline)
            panel.Children.Add(new TextBlock
            {
                Text = "Ctrl+Enter to confirm", FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
                Opacity = 0.7,
            });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = !multiline, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { DialogResult = true; e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            input.Focus();
            _text?.SelectAll();
        };
    }

    /// <returns>The entered text, or null when cancelled.</returns>
    public static string? Show(Window? owner, string title, string prompt, string initial = "", bool multiline = false)
    {
        var dialog = new InputDialog(owner, title, prompt, initial, multiline, password: false);
        return dialog.ShowDialog() == true ? dialog._text!.Text : null;
    }

    public static string? ShowPassword(Window? owner, string title, string prompt)
    {
        var dialog = new InputDialog(owner, title, prompt, "", multiline: false, password: true);
        return dialog.ShowDialog() == true ? dialog._password!.Password : null;
    }
}
