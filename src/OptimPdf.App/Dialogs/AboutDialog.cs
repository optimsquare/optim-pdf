using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OptimPdf.App.Dialogs;

/// <summary>Product name, version, copyright and the open-source components with their licenses.</summary>
public sealed class AboutDialog : Window
{
    AboutDialog(Window owner)
    {
        Title = "About Optim PDF";
        Owner = owner;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "";
        var root = new StackPanel { Margin = new Thickness(24) };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/OptimPdf-256.png")), Width = 64, Height = 64,
            Margin = new Thickness(0, 0, 16, 0),
        });
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Optim PDF", FontSize = 24, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = $"Version {version} · 64-bit", Opacity = 0.75 });
        title.Children.Add(new TextBlock { Text = "© 2026 Optim Square LLC · Open source (MIT License)", Opacity = 0.75, FontSize = 12 });
        header.Children.Add(title);
        root.Children.Add(header);

        root.Children.Add(new TextBlock
        {
            Text = "Works offline. Your documents never leave this PC unless you choose a time-stamp server when signing.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0),
        });
        root.Children.Add(new TextBlock { Text = "Open-source components", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        root.Children.Add(new TextBlock
        {
            Text = "PDFium (BSD-3-Clause and bundled permissive licenses) · pdfium-binaries (MIT) · PDFiumCore (Apache-2.0) · " +
                   "PDFsharp (MIT) · .NET and WPF (MIT) · C#/WinRT (MIT)",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var licenses = new Button { Content = "View licenses", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        licenses.Click += (_, _) => OpenLicenses();
        var close = new Button { Content = "Close", MinWidth = 96, IsCancel = true, IsDefault = true, Style = TryFindResource("AccentButtonStyle") as Style };
        close.Click += (_, _) => Close();
        buttons.Children.Add(licenses);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>The installed "licenses" folder, or the notices file when running from a build folder.</summary>
    void OpenLicenses()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "licenses");
        var target = Directory.Exists(folder) ? folder : Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (!Directory.Exists(target) && !File.Exists(target))
        {
            MessageBox.Show(this, "The license files are installed with Optim PDF in its \"licenses\" folder.", "Licenses");
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    public static void Show(Window owner) => new AboutDialog(owner).ShowDialog();
}
