using System.Text;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace OptimPdf.Core.Editing;

public sealed record TextStyle(string FontFamily = "Segoe UI", double Size = 12, PdfColor? Color = null, bool Bold = false, bool Italic = false)
{
    public PdfColor EffectiveColor => Color ?? PdfColor.Black;
}

/// <summary>
/// Adds new page content (text and images) with embedded, subset Unicode fonts.
/// Positions are given as the item's top-left corner in PDF user space, and items are laid out
/// upright as the page is displayed (honoring the page's /Rotate).
/// </summary>
public static class ContentEditor
{
    /// <summary>Fonts are configured by <see cref="CoreSetup"/> when the assembly loads; kept as an explicit hook.</summary>
    public static void EnsureFonts() => RuntimeHelpers.RunModuleConstructor(typeof(ContentEditor).Module.ModuleHandle);

    /// <summary>Fonts shipped with Windows 10/11 that together cover most scripts and symbols.</summary>
    static readonly string[] FallbackFamilies =
        ["Segoe UI", "Segoe UI Symbol", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Nirmala UI", "Leelawadee UI",
         "Ebrima", "Gadugi", "Segoe UI Historic", "Arial", "Segoe UI Emoji"];

    /// <summary>
    /// The requested font if it has glyphs for every character of <paramref name="text"/>; otherwise the
    /// installed fallback font covering the most characters (checked against each font's cmap), so text
    /// never silently turns into empty boxes. Japanese kana prefers a Japanese font for correct glyph shapes.
    /// </summary>
    public static string FontFor(string text, string requested)
    {
        var fonts = CoreSetup.Fonts;
        int total = text.EnumerateRunes().Count();
        bool requestedOk = fonts.IsEmbeddable(requested);
        if (requestedOk && fonts.CoveredCount(requested, text) == total) return requested;

        var candidates = Regex.IsMatch(text, @"[぀-ヿ]") ? FallbackFamilies.Prepend("Yu Gothic") : FallbackFamilies;
        string best = requestedOk ? requested : "Arial";
        int bestCount = requestedOk ? fonts.CoveredCount(requested, text) : -1;
        foreach (var family in candidates)
        {
            if (!fonts.IsEmbeddable(family)) continue; // Never embed a font whose license forbids it.
            int count = fonts.CoveredCount(family, text);
            if (count == total) return family;
            if (count > bestCount) (best, bestCount) = (family, count);
        }
        return best;
    }

    /// <summary>
    /// True when the text uses a script whose letters must be reordered or combined by a shaping engine
    /// (Devanagari, Bengali, Tamil and other Indic scripts, Myanmar, Khmer, Tibetan, Syriac, Thaana, N'Ko, and
    /// Arabic letters outside the basic Arabic/Persian/Urdu set). The PDF writer cannot lay these out, so they
    /// are refused rather than written incorrectly. Arabic and Hebrew are handled by <see cref="RtlText"/>.
    /// </summary>
    public static bool NeedsComplexShaping(string text) =>
        Regex.IsMatch(text, @"[\u0700-\u08FF\u0900-\u0DFF\u0F00-\u0FFF\u1000-\u109F\u1780-\u17FF\u1900-\u19FF\uA8E0-\uA8FF\uA980-\uA9DF\uAA60-\uAA7F]")
        || text.Any(c => c is >= '\u0600' and <= '\u06FF' && !RtlText.CanShape(c));

    public const string ComplexShapingMessage =
        "This text uses a script that needs advanced shaping (for example Devanagari, Bengali, Tamil, Myanmar, Khmer or Syriac), " +
        "which the PDF writer cannot lay out correctly yet.";

    /// <returns>The bounds of the added text in user space.</returns>
    public static PdfRect AddText(PdfDocument doc, int pageIndex, double x, double y, string text, TextStyle style, double? maxWidth = null, int itemId = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is empty.", nameof(text));
        if (NeedsComplexShaping(text)) throw new NotSupportedException(ComplexShapingMessage);
        EnsureFonts();

        var page = doc.Pages[pageIndex];
        var brush = new XSolidBrush(ToXColor(style.EffectiveColor));
        // Right-to-left text is drawn in visual order with presentation forms; text extraction maps the forms
        // back to letters and restores logical order (verified with PDFium). No /ActualText: PDFium then misorders words.
        TextLayout layout;
        using (var g = OpenUpright(page, x, y))
        {
            layout = Layout(g, text, style, maxWidth);
            Draw(g, layout, brush, 0, 0);
        }
        TagLastContent(page, itemId);
        return Bounds(page, x, y, layout.Width, layout.Height);
    }

    /// <summary>Measures text as <see cref="AddText"/> would lay it out (width, height in points).</summary>
    public static (double Width, double Height) MeasureText(string text, TextStyle style, double? maxWidth = null)
    {
        EnsureFonts();
        var g = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards, null!);
        var layout = Layout(g, text, style, maxWidth);
        return (layout.Width, layout.Height);
    }

    /// <summary>Text ready to draw: one visual-order string per line, with its offset for right-to-left alignment.</summary>
    internal sealed record TextLayout(XFont Font, IReadOnlyList<(string Visual, double Offset)> Lines, double Width, double LineHeight, bool HasRtl)
    {
        public double Height => Lines.Count * LineHeight;
    }

    /// <summary>Wraps (at <paramref name="maxWidth"/>), shapes and orders text; right-to-left paragraphs are right-aligned.</summary>
    internal static TextLayout Layout(XGraphics g, string text, TextStyle style, double? maxWidth)
    {
        bool rtl = RtlText.HasRtl(text);
        var font = CreateFont(FontFor(rtl ? RtlText.Shape(text) : text, style.FontFamily), style);
        var lines = new List<(string Visual, double Width, bool Rtl)>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            bool rtlParagraph = rtl && RtlText.IsRtlParagraph(paragraph);
            string Visual(string s) => rtl ? RtlText.ToVisual(s, rtlParagraph) : s;
            double Measure(string s) => g.MeasureString(Visual(s), font).Width;
            foreach (var line in WrapParagraph(paragraph, maxWidth, Measure))
                lines.Add((Visual(line), Measure(line), rtlParagraph));
        }
        double width = maxWidth is { } m && lines.Any(l => l.Rtl) ? m : lines.Select(l => l.Width).DefaultIfEmpty(0).Max();
        return new TextLayout(font, lines.Select(l => (l.Visual, l.Rtl ? width - l.Width : 0)).ToList(), width, font.GetHeight(), rtl);
    }

    internal static void Draw(XGraphics g, TextLayout layout, XBrush brush, double x, double y)
    {
        for (int i = 0; i < layout.Lines.Count; i++)
            g.DrawString(layout.Lines[i].Visual, layout.Font, brush, x + layout.Lines[i].Offset, y + i * layout.LineHeight, XStringFormats.TopLeft);
    }

    /// <summary>
    /// Places an image with its top-left corner at (x, y). Width and height are in points along the
    /// displayed page axes. Supports any format Windows can decode (JPEG, PNG, BMP, GIF, TIFF).
    /// </summary>
    public static PdfRect AddImage(PdfDocument doc, int pageIndex, double x, double y, double width, double height, byte[] imageData, int itemId = 0)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Image size must be positive.");
        var page = doc.Pages[pageIndex];
        using var image = ImageCodec.ToXImage(imageData);
        using (var g = OpenUpright(page, x, y))
            g.DrawImage(image, 0, 0, width, height);
        TagLastContent(page, itemId);
        return Bounds(page, x, y, width, height);
    }

    /// <summary>
    /// Builds a Form XObject (for annotation and field appearances) by drawing with PDFsharp, so text gets
    /// embedded Unicode fonts. Drawing coordinates are top-down within a width × height box.
    /// </summary>
    public static PdfDictionary FormFromDrawing(PdfDocument doc, double width, double height, Action<XGraphics> draw)
    {
        // Draw on a scratch page, then move its content and resources into a form and drop the page.
        var scratch = doc.AddPage();
        scratch.Width = XUnit.FromPoint(Math.Max(width, 1));
        scratch.Height = XUnit.FromPoint(Math.Max(height, 1));
        using (var g = XGraphics.FromPdfPage(scratch)) draw(g);

        using var content = new MemoryStream();
        foreach (var part in scratch.Contents.Elements)
            if ((part as PdfReference)?.Value is PdfDictionary stream && stream.Stream is not null)
            {
                content.Write(stream.Stream.UnfilteredValue);
                content.WriteByte((byte)'\n');
            }

        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = PdfObjects.Numbers(doc, 0, 0, width, height);
        form.Elements["/Resources"] = scratch.Elements["/Resources"]!;
        form.CreateStream(content.ToArray());
        doc.Internals.AddObject(form);
        doc.Pages.Remove(scratch);
        return form;
    }

    // ---- Coordinate frames ----

    /// <summary>
    /// Opens a graphics context whose origin is (x, y) in user space, with x running to the right and
    /// y running down as the page is displayed. Existing content is wrapped in q/Q first.
    /// </summary>
    internal static XGraphics OpenUpright(PdfPage page, double x, double y)
    {
        var g = OpenUserSpace(page);
        // Build a frame that is upright for the page's display rotation.
        var (right, down) = Axes(page.Rotate);
        g.MultiplyTransform(new XMatrix(right.X, right.Y, down.X, down.Y, x, y));
        return g;
    }

    /// <summary>Marked-content tag of items added by Optim PDF, so a multi-object item can be selected and moved as one.</summary>
    public const string ItemTag = "OptimItem";

    /// <summary>A new random item id.</summary>
    public static int NewItemId() => Random.Shared.Next(1, int.MaxValue);

    /// <summary>
    /// Wraps the content stream just added by a graphics context in <c>/OptimItem &lt;&lt;/Id n&gt;&gt; BDC … EMC</c>.
    /// Marked content does not change how the page looks; viewers that do not know the tag ignore it.
    /// </summary>
    internal static int TagLastContent(PdfPage page, int itemId)
    {
        if (itemId == 0) itemId = NewItemId();
        var elements = page.Contents.Elements;
        if (elements.Count == 0 || elements[^1] is not PdfReference { Value: PdfDictionary { Stream: { } stream } }) return itemId;
        var content = Encoding.Latin1.GetString(stream.UnfilteredValue);
        stream.Value = Encoding.Latin1.GetBytes($"/{ItemTag} <</Id {itemId}>> BDC\n{content}\nEMC\n");
        return itemId;
    }

    /// <summary>A graphics context in raw PDF user space (y up). Existing content is wrapped in q/Q first.</summary>
    internal static XGraphics OpenUserSpace(PdfPage page)
    {
        page.Contents.PrependContent().CreateStream("q\n"u8.ToArray());
        page.Contents.AppendContent().CreateStream("Q\n"u8.ToArray());

        var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        // PDFsharp maps (gx, gy) to user space (gx, H - gy), with H the MediaBox height and no
        // allowance for /Rotate (measured; see docs/03-status.md). Undo that to reach raw user space.
        g.MultiplyTransform(new XMatrix(1, 0, 0, -1, 0, page.MediaBox.Height));
        return g;
    }

    /// <summary>
    /// Draws one line of text whose baseline starts at the origin of <paramref name="m"/>, a text-space-to-user-space
    /// matrix with unit scale (as found on existing text): the line keeps the original position, angle and skew.
    /// </summary>
    public static void DrawTextLine(PdfDocument doc, int pageIndex, string text, TextStyle style,
        (double A, double B, double C, double D, double E, double F) m, int itemId = 0)
    {
        if (NeedsComplexShaping(text)) throw new NotSupportedException(ComplexShapingMessage);
        EnsureFonts();
        var page = doc.Pages[pageIndex];
        var brush = new XSolidBrush(ToXColor(style.EffectiveColor));
        bool rtl = RtlText.HasRtl(text);
        var visual = rtl ? RtlText.ToVisual(text) : text;
        using (var g = OpenUserSpace(page))
        {
            // Text space has y up; the graphics frame has y down.
            g.MultiplyTransform(new XMatrix(m.A, m.B, -m.C, -m.D, m.E, m.F));
            double x = 0;
            foreach (var (segment, family) in FontRuns(visual, style.FontFamily))
            {
                var font = CreateFont(family, style);
                g.DrawString(segment, font, brush, x, 0, XStringFormats.BaseLineLeft);
                x += g.MeasureString(segment, font).Width;
            }
        }
        TagLastContent(page, itemId);
    }

    /// <summary>
    /// Splits text into runs that the requested font can draw and runs that need a fallback font, so one
    /// missing symbol does not change the font of the whole line.
    /// </summary>
    internal static List<(string Text, string Family)> FontRuns(string text, string requested)
    {
        var fonts = CoreSetup.Fonts;
        bool usable = fonts.IsEmbeddable(requested);
        var runs = new List<(string Text, string Family)>();
        var current = new System.Text.StringBuilder();
        bool? currentOwn = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var s = rune.ToString();
            bool own = usable && (Rune.IsWhiteSpace(rune) || fonts.CoveredCount(requested, s) == 1);
            if (currentOwn is { } c && c != own) Flush();
            currentOwn = own;
            current.Append(s);
        }
        Flush();
        return runs;

        void Flush()
        {
            if (current.Length == 0) return;
            var t = current.ToString();
            runs.Add((t, currentOwn == true ? requested : FontFor(t, requested)));
            current.Clear();
        }
    }

    /// <summary>
    /// For a rectangle in user space, the corner that appears top-left on screen and the rectangle's width and
    /// height along the displayed axes — the arguments <see cref="AddText"/>/<see cref="AddImage"/> expect.
    /// </summary>
    public static (double X, double Y, double Width, double Height) UprightBox(PdfRect rect, int pageRotate) =>
        (((pageRotate % 360) + 360) % 360) switch
        {
            90 => (rect.Left, rect.Bottom, rect.Height, rect.Width),
            180 => (rect.Right, rect.Bottom, rect.Width, rect.Height),
            270 => (rect.Right, rect.Top, rect.Height, rect.Width),
            _ => (rect.Left, rect.Top, rect.Width, rect.Height),
        };

    /// <summary>Characters of <paramref name="text"/> the family has glyphs for (-1 when the family is not installed).</summary>
    public static int CoveredCount(string family, string text) => CoreSetup.Fonts.CoveredCount(family, text);

    /// <summary>Whether the font's license allows embedding it in a PDF.</summary>
    public static bool IsEmbeddable(string family) => CoreSetup.Fonts.IsEmbeddable(family);

    /// <summary>Font families installed in Windows whose licenses allow embedding, for font pickers.</summary>
    public static IEnumerable<string> InstalledFamilies => CoreSetup.Fonts.Families.Where(CoreSetup.Fonts.IsEmbeddable);

    /// <summary>User-space directions of the displayed page's "right" and "down".</summary>
    internal static ((double X, double Y) Right, (double X, double Y) Down) Axes(int rotate) =>
        (((rotate % 360) + 360) % 360) switch
        {
            90 => ((0, 1), (1, 0)),
            180 => ((-1, 0), (0, 1)),
            270 => ((0, -1), (-1, 0)),
            _ => ((1, 0), (0, -1)),
        };

    static PdfRect Bounds(PdfPage page, double x, double y, double width, double height)
    {
        var (r, d) = Axes(page.Rotate);
        double x2 = x + r.X * width + d.X * height, y2 = y + r.Y * width + d.Y * height;
        return PdfRect.FromPoints(x, y, x2, y2);
    }

    internal static XFont CreateFont(string family, TextStyle style)
    {
        var flags = (style.Bold ? XFontStyleEx.Bold : 0) | (style.Italic ? XFontStyleEx.Italic : 0);
        return new XFont(family, style.Size, flags, new XPdfFontOptions(PdfFontEncoding.Unicode));
    }

    internal static XColor ToXColor(PdfColor c) =>
        XColor.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255));

    static IEnumerable<string> WrapParagraph(string paragraph, double? maxWidth, Func<string, double> measure)
    {
        if (maxWidth is not { } max || paragraph.Length == 0) { yield return paragraph; yield break; }
        var line = "";
        foreach (var word in paragraph.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && measure(candidate) > max)
            {
                yield return line;
                line = word;
            }
            else line = candidate;
        }
        yield return line;
    }
}
