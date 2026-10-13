using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace OptimPdf.App;

public partial class App : Application
{
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Optim Square", "Optim PDF");

    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);

        Settings = AppSettings.Load();
        ApplyTheme(Settings.Theme);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        foreach (var path in e.Args.Where(File.Exists)) window.OpenFile(path);
    }

    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        Settings.Theme = theme;
        Settings.Save();
        ApplyPalette(IsDark(theme));
    }

    static bool _followingSystem;

    /// <summary>True when the dark palette is showing (the setting, or Windows when set to System).</summary>
    public static bool IsDarkActive => IsDark(Settings.Theme);

    static bool IsDark(string theme)
    {
        if (theme == "Dark") return true;
        if (theme == "Light") return false;
        if (!_followingSystem)
        {
            // Re-evaluate when the user switches Windows between light and dark.
            _followingSystem = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && Settings.Theme == "System")
                    Current.Dispatcher.BeginInvoke(() => ApplyPalette(IsDark("System")));
            };
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    /// <summary>
    /// Sets the Optim chrome colors for the effective theme. Values are chosen for WCAG contrast:
    /// text ≥ 4.5:1 and accents ≥ 3:1 against the header surface.
    /// </summary>
    static void ApplyPalette(bool dark)
    {
        var palette = dark
            ? new Dictionary<string, string>
            {
                ["Brand.Accent"] = "#5CC8FF", ["Brand.AccentSubtle"] = "#1D3A5C",
                ["Chrome.Header"] = "#2C2C30", ["Chrome.TabStrip"] = "#1E1E21", ["Chrome.Border"] = "#4A4A52",
                ["Chrome.Text"] = "#F3F3F6", ["Chrome.TextSecondary"] = "#C4C5CC",
                ["Chrome.Hover"] = "#3B3B42", ["Chrome.Pressed"] = "#46464E", ["Chrome.SearchBox"] = "#1F1F23",
            }
            : new Dictionary<string, string>
            {
                ["Brand.Accent"] = "#0A57C2", ["Brand.AccentSubtle"] = "#DCEBFF",
                ["Chrome.Header"] = "#F3F4F8", ["Chrome.TabStrip"] = "#E7E8EE", ["Chrome.Border"] = "#D4D6DE",
                ["Chrome.Text"] = "#1B1B1F", ["Chrome.TextSecondary"] = "#55575F",
                ["Chrome.Hover"] = "#E3E5EC", ["Chrome.Pressed"] = "#D6D9E2", ["Chrome.SearchBox"] = "#FFFFFF",
            };
        foreach (var (key, hex) in palette)
        {
            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Current.Resources[key] = brush;
        }
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show(MainWindow, $"Something went wrong:\n\n{e.Exception.Message}\n\nYour open documents were not changed on disk.",
            "Optim PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // Keep the app (and unsaved work) alive.
    }

    public static void LogError(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (IOException) { }
    }
}

public sealed class AppSettings
{
    static string FilePath => Path.Combine(App.DataDirectory, "settings.json");

    public string Theme { get; set; } = "System";
    public List<string> RecentFiles { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { App.LogError(ex); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex) { App.LogError(ex); }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }
}
