using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using OptimPdf.App.Services;
using OptimPdf.Core.Signatures;

namespace OptimPdf.App.Dialogs;

/// <summary>Create a signature by drawing, typing or choosing an image. Saved signatures are stored encrypted on this PC.</summary>
public sealed class SignatureDialog : DialogBase
{
    readonly TabControl _tabs = new();
    readonly InkCanvas _ink = new() { Height = 160, Background = Brushes.White };
    readonly TextBox _typed = new() { FontSize = 16 };
    readonly ComboBox _font = new();
    readonly TextBlock _preview = new() { FontSize = 40, Height = 70, Foreground = new SolidColorBrush(Color.FromRgb(13, 31, 115)), Background = Brushes.White, Padding = new Thickness(8, 4, 8, 4) };
    readonly TextBlock _imageInfo = new() { Text = "No image chosen.", Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox _name = new() { Text = "My signature" };
    byte[]? _image;
    SignatureMark? _result;

    SignatureDialog(Window? owner) : base(owner, "Create signature", 560, "Save signature")
    {
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Color.FromRgb(13, 31, 115), Width = 2.6, Height = 2.6, FitToCurve = true };
        var clear = new Button { Content = "Clear", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), MinWidth = 80 };
        clear.Click += (_, _) => _ink.Strokes.Clear();
        var draw = new StackPanel();
        draw.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _ink });
        draw.Children.Add(clear);
        _tabs.Items.Add(new TabItem { Header = "Draw", Content = draw });

        var installed = new HashSet<string>(Core.Editing.ContentEditor.InstalledFamilies, StringComparer.OrdinalIgnoreCase);
        _font.ItemsSource = VisualSignatureService.ScriptFonts.Where(installed.Contains).DefaultIfEmpty("Segoe UI").ToList();
        _font.SelectedIndex = 0;
        _typed.TextChanged += (_, _) => UpdatePreview();
        _font.SelectionChanged += (_, _) => UpdatePreview();
        var type = new StackPanel();
        type.Children.Add(LabeledRows(("Your name", _typed), ("Style", _font)));
        type.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _preview, Margin = new Thickness(0, 8, 0, 0) });
        _tabs.Items.Add(new TabItem { Header = "Type", Content = type });

        var browse = new Button { Content = "Choose image…", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 120 };
        browse.Click += (_, _) => ChooseImage();
        var image = new StackPanel();
        image.Children.Add(browse);
        image.Children.Add(_imageInfo);
        image.Children.Add(Note("A PNG with a transparent background looks best."));
        _tabs.Items.Add(new TabItem { Header = "Image", Content = image });

        var body = new StackPanel();
        body.Children.Add(_tabs);
        body.Children.Add(LabeledRows(("Name", _name)));
        body.Children.Add(Note("This is an electronic signature (a visible mark). It does not certify the document cryptographically."));
        SetBody(body);
        UpdatePreview();
    }

    void UpdatePreview()
    {
        _preview.Text = string.IsNullOrWhiteSpace(_typed.Text) ? "Your name" : _typed.Text;
        _preview.FontFamily = new FontFamily(_font.SelectedItem as string ?? "Segoe Script");
    }

    void ChooseImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _image = File.ReadAllBytes(dialog.FileName);
            using var bmp = Core.Editing.ImageCodec.Decode(_image);
            _imageInfo.Text = $"{Path.GetFileName(dialog.FileName)} — {bmp.Width} × {bmp.Height} px";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _image = null;
            _imageInfo.Text = "That image could not be read.";
        }
    }

    protected override string? OnAccept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) return "Give the signature a name.";
        switch (_tabs.SelectedIndex)
        {
            case 0:
                if (_ink.Strokes.Count == 0) return "Draw your signature in the box.";
                var strokes = _ink.Strokes.Select(s => (IReadOnlyList<SignaturePoint>)s.GetBezierStylusPoints()
                    .Select(p => new SignaturePoint(p.X, p.Y)).ToList()).ToList();
                _result = DrawnSignature.FromStrokes(strokes);
                break;
            case 1:
                if (string.IsNullOrWhiteSpace(_typed.Text)) return "Type your name.";
                _result = new TypedSignature(_typed.Text.Trim(), _font.SelectedItem as string ?? "Segoe Script");
                break;
            default:
                if (_image is null) return "Choose an image.";
                _result = ImageSignature.FromImage(_image);
                break;
        }
        return null;
    }

    /// <returns>The new saved signature, or null when cancelled.</returns>
    public static SavedSignature? Show(Window? owner)
    {
        var d = new SignatureDialog(owner);
        if (d.ShowDialog() != true || d._result is null) return null;
        return SignatureStore.Add(d._name.Text.Trim(), d._result);
    }
}
