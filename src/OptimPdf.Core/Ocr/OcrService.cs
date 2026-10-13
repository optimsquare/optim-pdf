using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OptimPdf.Core.Ocr;

/// <summary>A recognized word. Left/Top/Width/Height are in points on the page as displayed (origin top-left).</summary>
public sealed record OcrWord(string Text, double Left, double Top, double Width, double Height);

public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>
/// OCR result for one page. <see cref="Origin"/> is the user-space point shown at the page's top-left corner;
/// together with the page's /Rotate it maps the displayed coordinates of the words back to user space.
/// </summary>
public sealed record OcrPage(int PageIndex, (double X, double Y) Origin, IReadOnlyList<OcrLine> Lines, bool Skipped)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
    public int WordCount => Lines.Sum(l => l.Words.Count);
}

public sealed record OcrOptions(string? Language = null, double Dpi = 300, bool SkipPagesWithText = true);

public sealed class OcrUnavailableException(string message) : Exception(message);

/// <summary>
/// Optical character recognition with the OCR engine built into Windows 10/11 (Windows.Media.Ocr): offline,
/// nothing to bundle, and languages follow the Windows language packs the user has installed. Recognized text
/// is added as an invisible text layer (render mode 3) over each word, so pages become searchable and
/// selectable while looking exactly as before.
/// </summary>
public static class OcrService
{
    /// <summary>Installed OCR languages (BCP-47 tags), e.g. "en-US".</summary>
    public static IReadOnlyList<(string Tag, string Name)> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList();

    /// <summary>A page counts as having text when PDFium extracts at least a few non-space characters.</summary>
    public static bool HasText(PdfiumDocument doc, int pageIndex) =>
        doc.GetPageText(pageIndex).Count(c => !char.IsWhiteSpace(c)) >= 8;

    public static async Task<IReadOnlyList<OcrPage>> RecognizeAsync(PdfiumDocument doc, IEnumerable<int> pageIndices, OcrOptions options,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var engine = CreateEngine(options.Language);
        var pages = pageIndices.ToList();
        var results = new List<OcrPage>();
        for (int i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = pages[i];
            if (options.SkipPagesWithText && HasText(doc, index))
                results.Add(new OcrPage(index, (0, 0), [], Skipped: true));
            else
                results.Add(await RecognizePageAsync(engine, doc, index, options.Dpi));
            progress?.Report((i + 1, pages.Count));
        }
        return results;
    }

    static OcrEngine CreateEngine(string? language)
    {
        OcrEngine? engine;
        if (language is null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
        else
        {
            var lang = new Windows.Globalization.Language(language);
            if (!OcrEngine.IsLanguageSupported(lang))
                throw new OcrUnavailableException($"OCR for {lang.DisplayName} is not installed. Add it in Windows Settings › Time & language › Language (Optical character recognition).");
            engine = OcrEngine.TryCreateFromLanguage(lang);
        }
        return engine ?? throw new OcrUnavailableException(
            "No OCR language is installed. Add a language with Optical character recognition in Windows Settings › Time & language › Language.");
    }

    static async Task<OcrPage> RecognizePageAsync(OcrEngine engine, PdfiumDocument doc, int pageIndex, double dpi)
    {
        var size = doc.GetPageSize(pageIndex);
        double scale = Math.Min(dpi / 72, (OcrEngine.MaxImageDimension - 1) / Math.Max(size.Width, size.Height));
        var page = doc.Render(pageIndex, scale, drawAnnotations: false);
        double sx = size.Width / page.PixelWidth, sy = size.Height / page.PixelHeight; // Points per pixel.

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, page.PixelWidth, page.PixelHeight, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(page.Pixels.AsBuffer());
        var result = await engine.RecognizeAsync(bitmap);

        var lines = result.Lines
            .Select(l => new OcrLine(l.Words
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => new OcrWord(w.Text, w.BoundingRect.X * sx, w.BoundingRect.Y * sy, w.BoundingRect.Width * sx, w.BoundingRect.Height * sy))
                .ToList()))
            .Where(l => l.Words.Count > 0)
            .ToList();
        var origin = doc.DeviceToPage(pageIndex, page.PixelWidth, page.PixelHeight, 0, 0, 0);
        return new OcrPage(pageIndex, origin, lines, Skipped: false);
    }

    /// <summary>Adds the recognized words as invisible text. Returns the number of words added.</summary>
    public static int AddTextLayer(PdfDocument doc, IEnumerable<OcrPage> pages)
    {
        ContentEditor.EnsureFonts();
        int words = 0;
        foreach (var result in pages.Where(p => !p.Skipped && p.WordCount > 0))
        {
            var page = doc.Pages[result.PageIndex];
            string family = ContentEditor.FontFor(result.Text, "Arial");
            const double em = 10;
            var font = new XFont(family, em, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            var metrics = font.Metrics;
            double ascent = metrics.Ascent / (double)metrics.UnitsPerEm, height = (metrics.Ascent + metrics.Descent) / (double)metrics.UnitsPerEm;

            int before = page.Contents.Elements.Count;
            using (var g = ContentEditor.OpenUpright(page, result.Origin.X, result.Origin.Y))
            {
                foreach (var word in result.Lines.SelectMany(l => l.Words))
                {
                    // Scale the glyphs so each word's text box matches the word on the image exactly.
                    double natural = g.MeasureString(word.Text, font).Width;
                    if (natural <= 0) continue;
                    double sy = word.Height / (height * em), sx = word.Width / natural;
                    var state = g.Save();
                    g.TranslateTransform(word.Left, word.Top + ascent * em * sy);
                    g.ScaleTransform(sx, sy);
                    g.DrawString(word.Text, font, XBrushes.Black, 0, 0, XStringFormats.BaseLineLeft);
                    g.Restore(state);
                    words++;
                }
            }
            MakeTextInvisible(page, before);
        }
        return words;
    }

    /// <summary>Sets text render mode 3 (neither fill nor stroke) in the content streams added since <paramref name="from"/>.</summary>
    static void MakeTextInvisible(PdfPage page, int from)
    {
        for (int i = from; i < page.Contents.Elements.Count; i++)
        {
            if (page.Contents.Elements[i] is not PdfReference { Value: PdfDictionary { Stream: { } stream } }) continue;
            var text = Encoding.Latin1.GetString(stream.UnfilteredValue);
            if (!text.Contains("BT")) continue;
            stream.Value = Encoding.Latin1.GetBytes(Regex.Replace(text, @"(?m)^BT\s*$", "BT\n3 Tr"));
        }
    }
}
