using System.Globalization;
using System.Text.RegularExpressions;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Writing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;

namespace OptimPdf.Core.Forms;

/// <summary>
/// Generates normal appearances (/AP /N) for text-like field values so every viewer shows the value
/// exactly, in any script, without relying on /NeedAppearances. Honors the field's font size and color
/// from /DA, alignment (/Q), multi-line and password flags.
/// </summary>
static class FieldAppearance
{
    const double Padding = 2;

    public static void WriteText(PdfDocument doc, PdfDictionary field, string value)
    {
        var da = Inherited(field, "/DA") ?? doc.AcroForm?.Elements.GetString("/DA") ?? "/Helv 0 Tf 0 g";
        double requestedSize = ParseFontSize(da);
        var color = ParseColor(da);
        int align = field.Elements.GetInteger("/Q");
        var flags = (PdfAcroFieldFlags)field.Elements.GetInteger("/Ff");
        bool multiline = (flags & PdfAcroFieldFlags.Multiline) != 0;
        bool password = (flags & PdfAcroFieldFlags.Password) != 0;
        string shown = password ? new string('•', value.Length) : value;
        // Right-to-left values are shaped and drawn in visual order, line by line.
        bool rtl = RtlText.HasRtl(shown);
        string Visual(string line) => rtl ? RtlText.ToVisual(line) : line;
        string family = ContentEditor.FontFor(rtl ? RtlText.Shape(shown) : shown, "Arial");

        foreach (var widget in FormService.Widgets(field))
        {
            var r = widget.Elements.GetRectangle("/Rect");
            double w = Math.Abs(r.X2 - r.X1), h = Math.Abs(r.Y2 - r.Y1);
            if (w < 1 || h < 1) continue;

            double size = requestedSize > 0 ? requestedSize
                : multiline ? 10 : Math.Clamp((h - 2 * Padding) * 0.72, 4, 14); // 0 means auto size.
            var (background, border) = Colors(widget);
            var form = ContentEditor.FormFromDrawing(doc, w, h, g =>
            {
                // Background and border from the widget's /MK, so the field is visible even when empty.
                if (background is { } bg) g.DrawRectangle(new XSolidBrush(ContentEditor.ToXColor(bg)), 0, 0, w, h);
                if (border is { } bc) g.DrawRectangle(new XPen(ContentEditor.ToXColor(bc), 1), 0.5, 0.5, w - 1, h - 1);
                if (shown.Length == 0) return;
                var font = ContentEditor.CreateFont(family, new TextStyle(family, size));
                var brush = new XSolidBrush(ContentEditor.ToXColor(color));
                if (multiline)
                {
                    var lines = Wrap(g, shown, font, w - 2 * Padding);
                    double lh = font.GetHeight();
                    double y = Padding;
                    foreach (var line in lines)
                    {
                        if (y + lh > h + 0.5) break;
                        g.DrawString(Visual(line), font, brush, new XRect(Padding, y, w - 2 * Padding, lh), Format(rtl && align == 0 ? 2 : align, top: true));
                        y += lh;
                    }
                }
                else
                {
                    var line = Visual(shown);
                    // Auto-size shrinks long single-line values to fit the width.
                    if (requestedSize <= 0)
                    {
                        double width = g.MeasureString(line, font).Width;
                        if (width > w - 2 * Padding)
                            font = ContentEditor.CreateFont(family, new TextStyle(family, Math.Max(4, size * (w - 2 * Padding) / width)));
                    }
                    g.DrawString(line, font, brush, new XRect(Padding, 0, w - 2 * Padding, h), Format(rtl && align == 0 ? 2 : align, top: false));
                }
            });

            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = form.Reference;
            widget.Elements["/AP"] = ap;
        }
    }

    /// <summary>/MK /BG and /BC as RGB or gray colors (other color spaces are ignored).</summary>
    static (PdfColor? Background, PdfColor? Border) Colors(PdfDictionary widget)
    {
        var mk = widget.Elements.GetDictionary("/MK");
        static PdfColor? Read(PdfArray? a) => a?.Elements.Count switch
        {
            1 => new PdfColor(a.Elements.GetReal(0), a.Elements.GetReal(0), a.Elements.GetReal(0)),
            3 => new PdfColor(a.Elements.GetReal(0), a.Elements.GetReal(1), a.Elements.GetReal(2)),
            _ => null,
        };
        return (Read(mk?.Elements.GetArray("/BG")), Read(mk?.Elements.GetArray("/BC")));
    }

    /// <summary>The normal appearance stream a widget currently shows (resolving /AS for buttons).</summary>
    public static PdfDictionary? NormalAppearance(PdfDictionary widget)
    {
        var n = widget.Elements.GetDictionary("/AP")?.Elements["/N"];
        var value = n is PdfReference r ? r.Value : n;
        if (value is PdfDictionary d && d.Stream is not null) return d;
        if (value is PdfDictionary states)
        {
            var state = widget.Elements.GetName("/AS");
            if (string.IsNullOrEmpty(state)) return null;
            var chosen = states.Elements[state];
            return (chosen is PdfReference cr ? cr.Value : chosen) as PdfDictionary;
        }
        return null;
    }

    static string? Inherited(PdfDictionary field, string key)
    {
        for (var d = field; d is not null; d = (d.Elements["/Parent"] as PdfReference)?.Value as PdfDictionary)
            if (d.Elements.GetString(key) is { Length: > 0 } s) return s;
        return null;
    }

    static double ParseFontSize(string da)
    {
        var m = Regex.Match(da, @"([\d.]+)\s+Tf");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    static PdfColor ParseColor(string da)
    {
        var rgb = Regex.Match(da, @"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg");
        if (rgb.Success) return new PdfColor(D(rgb.Groups[1].Value), D(rgb.Groups[2].Value), D(rgb.Groups[3].Value));
        var gray = Regex.Match(da, @"([\d.]+)\s+g\b");
        if (gray.Success) { double v = D(gray.Groups[1].Value); return new PdfColor(v, v, v); }
        return PdfColor.Black;
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    }

    static XStringFormat Format(int quadding, bool top) => new()
    {
        Alignment = quadding switch { 1 => XStringAlignment.Center, 2 => XStringAlignment.Far, _ => XStringAlignment.Near },
        LineAlignment = top ? XLineAlignment.Near : XLineAlignment.Center,
    };

    static List<string> Wrap(XGraphics g, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && g.MeasureString(candidate, font).Width > maxWidth) { lines.Add(line); line = word; }
                else line = candidate;
            }
            lines.Add(line);
        }
        return lines;
    }
}
