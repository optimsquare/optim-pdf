using System.Globalization;
using System.Text;
using OptimPdf.Core.Rendering;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace OptimPdf.Core.Writing;

/// <summary>An RGB color with components in 0–1.</summary>
public readonly record struct PdfColor(double R, double G, double B)
{
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor Yellow = new(1, 0.86, 0);
    public static readonly PdfColor Red = new(0.86, 0.12, 0.12);
    public static readonly PdfColor Blue = new(0.1, 0.35, 0.85);
    public static readonly PdfColor Green = new(0.13, 0.6, 0.27);

    public static PdfColor FromBytes(byte r, byte g, byte b) => new(r / 255.0, g / 255.0, b / 255.0);
}

/// <summary>Standard 14 fonts that every conforming PDF reader provides; no embedding needed.</summary>
public enum StandardFont { Helvetica, HelveticaBold }

/// <summary>
/// Builds PDF content-stream operators. Text uses standard 14 fonts with WinAnsi encoding,
/// so characters outside Latin-1 are written as '?'. Unicode text needs an embedded font (phase 2).
/// </summary>
public sealed class ContentBuilder
{
    readonly StringBuilder _sb = new();

    public static string FontResourceName(StandardFont font) => font == StandardFont.HelveticaBold ? "HelvB" : "Helv";

    static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    ContentBuilder Op(string s) { _sb.Append(s).Append('\n'); return this; }

    public ContentBuilder Save() => Op("q");
    public ContentBuilder Restore() => Op("Q");
    public ContentBuilder FillColor(PdfColor c) => Op($"{Num(c.R)} {Num(c.G)} {Num(c.B)} rg");
    public ContentBuilder StrokeColor(PdfColor c) => Op($"{Num(c.R)} {Num(c.G)} {Num(c.B)} RG");
    public ContentBuilder LineWidth(double w) => Op($"{Num(w)} w");
    public ContentBuilder RoundCaps() => Op("1 J 1 j");
    public ContentBuilder GraphicsState(string name) => Op($"/{name} gs");
    public ContentBuilder Rect(double x, double y, double w, double h) => Op($"{Num(x)} {Num(y)} {Num(w)} {Num(h)} re");
    public ContentBuilder MoveTo(double x, double y) => Op($"{Num(x)} {Num(y)} m");
    public ContentBuilder LineTo(double x, double y) => Op($"{Num(x)} {Num(y)} l");
    public ContentBuilder Fill() => Op("f");
    public ContentBuilder Stroke() => Op("S");
    public ContentBuilder FillStroke() => Op("B");

    /// <summary>Rounded rectangle path approximated with cubic Béziers.</summary>
    public ContentBuilder RoundedRect(double x, double y, double w, double h, double r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        const double k = 0.5523; // Bézier circle constant.
        double c = r * k;
        MoveTo(x + r, y);
        LineTo(x + w - r, y);
        Op($"{Num(x + w - r + c)} {Num(y)} {Num(x + w)} {Num(y + r - c)} {Num(x + w)} {Num(y + r)} c");
        LineTo(x + w, y + h - r);
        Op($"{Num(x + w)} {Num(y + h - r + c)} {Num(x + w - r + c)} {Num(y + h)} {Num(x + w - r)} {Num(y + h)} c");
        LineTo(x + r, y + h);
        Op($"{Num(x + r - c)} {Num(y + h)} {Num(x)} {Num(y + h - r + c)} {Num(x)} {Num(y + h - r)} c");
        LineTo(x, y + r);
        Op($"{Num(x)} {Num(y + r - c)} {Num(x + r - c)} {Num(y)} {Num(x + r)} {Num(y)} c");
        return Op("h");
    }

    public ContentBuilder Text(double x, double y, StandardFont font, double size, string text) =>
        Op($"BT /{FontResourceName(font)} {Num(size)} Tf {Num(x)} {Num(y)} Td ({EscapeText(text)}) Tj ET");

    /// <summary>Approximate advance width of text in a standard font (good enough for layout).</summary>
    public static double MeasureText(string text, StandardFont font, double size)
    {
        // Average Helvetica glyph widths per 1000 em: digits/lowercase ≈ 556, uppercase ≈ 667, space 278.
        double units = 0;
        foreach (char ch in text)
            units += ch == ' ' ? 278 : char.IsUpper(ch) ? (font == StandardFont.HelveticaBold ? 722 : 667) : 556;
        return units * size / 1000.0;
    }

    public static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            char c = ch < 128 || (ch >= 160 && ch <= 255) ? ch : '?';
            if (c is '(' or ')' or '\\') sb.Append('\\');
            if (c is '\r' or '\n') c = ' ';
            sb.Append(c);
        }
        return sb.ToString();
    }

    public byte[] ToBytes() => Encoding.Latin1.GetBytes(_sb.ToString());

    public override string ToString() => _sb.ToString();
}

/// <summary>Helpers for creating low-level PDF objects with PDFsharp.</summary>
public static class PdfObjects
{
    public static PdfArray Numbers(PdfDocument doc, params double[] values)
    {
        var array = new PdfArray(doc);
        foreach (var v in values) array.Elements.Add(new PdfReal(v));
        return array;
    }

    public static PdfDictionary StandardFontDictionary(PdfDocument doc, StandardFont font)
    {
        var dict = new PdfDictionary(doc);
        dict.Elements.SetName("/Type", "/Font");
        dict.Elements.SetName("/Subtype", "/Type1");
        dict.Elements.SetName("/BaseFont", font == StandardFont.HelveticaBold ? "/Helvetica-Bold" : "/Helvetica");
        dict.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        return dict;
    }

    public static PdfDictionary FontResources(PdfDocument doc)
    {
        var fonts = new PdfDictionary(doc);
        fonts.Elements[$"/{ContentBuilder.FontResourceName(StandardFont.Helvetica)}"] = StandardFontDictionary(doc, StandardFont.Helvetica);
        fonts.Elements[$"/{ContentBuilder.FontResourceName(StandardFont.HelveticaBold)}"] = StandardFontDictionary(doc, StandardFont.HelveticaBold);
        return fonts;
    }

    /// <summary>
    /// Creates a Form XObject (registered as an indirect object) for use as an annotation appearance.
    /// </summary>
    public static PdfDictionary FormXObject(PdfDocument doc, PdfRect bbox, ContentBuilder content,
        PdfDictionary? extGStates = null, bool withFonts = false)
    {
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Numbers(doc, bbox.Left, bbox.Bottom, bbox.Right, bbox.Top);

        var resources = new PdfDictionary(doc);
        if (withFonts) resources.Elements["/Font"] = FontResources(doc);
        if (extGStates is not null) resources.Elements["/ExtGState"] = extGStates;
        form.Elements["/Resources"] = resources;

        form.CreateStream(content.ToBytes());
        doc.Internals.AddObject(form);
        return form;
    }

    /// <summary>Appends a content stream to a page, wrapped in q/Q so it cannot disturb existing state.</summary>
    public static void AppendPageContent(PdfPage page, ContentBuilder content, bool withFonts = true)
    {
        if (withFonts)
        {
            var resources = page.Resources;
            var fonts = resources.Elements.GetDictionary("/Font");
            if (fonts is null)
            {
                fonts = new PdfDictionary(page.Owner);
                resources.Elements["/Font"] = fonts;
            }
            foreach (var font in new[] { StandardFont.Helvetica, StandardFont.HelveticaBold })
            {
                var key = $"/{ContentBuilder.FontResourceName(font)}";
                if (!fonts.Elements.ContainsKey(key))
                    fonts.Elements[key] = StandardFontDictionary(page.Owner, font);
            }
        }

        // Existing content may leave the graphics state altered (e.g. an unbalanced cm), so the
        // original streams are wrapped in q … Q before the new content draws in a clean state.
        page.Contents.PrependContent().CreateStream("q\n"u8.ToArray());
        var bytes = "Q\nq\n"u8.ToArray().Concat(content.ToBytes()).Concat("Q\n"u8.ToArray()).ToArray();
        page.Contents.AppendContent().CreateStream(bytes);
    }
}
