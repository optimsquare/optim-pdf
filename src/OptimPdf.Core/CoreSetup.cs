using System.Runtime.CompilerServices;
using OptimPdf.Core.Editing;
using PdfSharp.Fonts;

namespace OptimPdf.Core;

static class CoreSetup
{
    /// <summary>
    /// PDFsharp lets the font source be chosen only before anything reads it (form fields read it while
    /// loading), so it is set as soon as this assembly is used: all installed Windows fonts are available.
    /// </summary>
#pragma warning disable CA2255 // Deliberate: the setting must be in place before any PDFsharp font use.
    [ModuleInitializer]
    internal static void Initialize() => GlobalFontSettings.FontResolver = Fonts;
#pragma warning restore CA2255

    public static readonly WindowsFontResolver Fonts = new();
}
