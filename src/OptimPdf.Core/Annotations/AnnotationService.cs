using System.Globalization;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;

namespace OptimPdf.Core.Annotations;

public enum TextMarkupKind { Highlight, Underline, StrikeOut }

public sealed record AnnotationInfo(int PageIndex, int IndexOnPage, string Subtype, PdfRect Rect,
    string? Contents, string? Author, string? Id);

/// <summary>Generic annotation backed by a raw dictionary; PDFsharp's own subclasses cover few subtypes.</summary>
sealed class RawAnnotation(PdfDocument document) : PdfAnnotation(document);

/// <summary>
/// Creates and lists standard PDF annotations (ISO 32000-2 §12.5). Every annotation gets its own
/// appearance stream (/AP) so it renders identically in all conforming viewers.
/// </summary>
public static class AnnotationService
{
    public const string DefaultAuthor = "Optim PDF";

    public static PdfAnnotation AddTextMarkup(PdfDocument doc, int pageIndex, TextMarkupKind kind,
        IReadOnlyList<PdfRect> lineRects, PdfColor color, string? author = null, string? comment = null)
    {
        if (lineRects.Count == 0) throw new ArgumentException("At least one rectangle is required.", nameof(lineRects));
        var page = doc.Pages[pageIndex];

        var bounds = Union(lineRects);
        var annot = CreateBase(doc, kind switch
        {
            TextMarkupKind.Highlight => "/Highlight",
            TextMarkupKind.Underline => "/Underline",
            _ => "/StrikeOut",
        }, bounds, color, author, comment);

        // QuadPoints order per spec practice (and as common viewers and PDFium read them): TL, TR, BL, BR.
        var quads = new List<double>();
        foreach (var r in lineRects)
            quads.AddRange([r.Left, r.Top, r.Right, r.Top, r.Left, r.Bottom, r.Right, r.Bottom]);
        annot.Elements["/QuadPoints"] = PdfObjects.Numbers(doc, [.. quads]);

        var content = new ContentBuilder();
        PdfDictionary? gs = null;
        if (kind == TextMarkupKind.Highlight)
        {
            gs = MultiplyBlendState(doc);
            content.GraphicsState("GS0").FillColor(color);
            foreach (var r in lineRects) content.Rect(r.Left, r.Bottom, r.Width, r.Height);
            content.Fill();
        }
        else
        {
            content.StrokeColor(color);
            foreach (var r in lineRects)
            {
                double width = Math.Max(0.75, r.Height / 14);
                double y = kind == TextMarkupKind.Underline ? r.Bottom + width : r.Bottom + r.Height * 0.45;
                content.LineWidth(width).MoveTo(r.Left, y).LineTo(r.Right, y).Stroke();
            }
        }
        SetAppearance(doc, annot, bounds, content, gs);
        page.Annotations.Add(annot);
        return annot;
    }

    /// <summary>Sticky note anchored with its top-left corner at (x, y).</summary>
    public static PdfAnnotation AddNote(PdfDocument doc, int pageIndex, double x, double y, string text,
        PdfColor? color = null, string? author = null)
    {
        var c = color ?? PdfColor.Yellow;
        var rect = new PdfRect(x, y - 22, x + 20, y);
        var annot = CreateBase(doc, "/Text", rect, c, author, text);
        annot.Elements.SetName("/Name", "/Comment");
        annot.Elements["/Open"] = new PdfBoolean(false);
        // Notes keep a fixed on-screen size when zooming, like other viewers.
        annot.Elements.SetInteger("/F", (int)(PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom | PdfAnnotationFlags.NoRotate));

        var content = new ContentBuilder()
            .FillColor(c).StrokeColor(new PdfColor(c.R * 0.6, c.G * 0.6, c.B * 0.6)).LineWidth(1)
            .RoundedRect(rect.Left + 1, rect.Bottom + 5, 18, 16, 2.5).FillStroke()
            // Speech-bubble tail.
            .MoveTo(rect.Left + 5, rect.Bottom + 5).LineTo(rect.Left + 4, rect.Bottom + 1).LineTo(rect.Left + 10, rect.Bottom + 5).FillStroke()
            .StrokeColor(new PdfColor(0.25, 0.25, 0.25)).LineWidth(0.9);
        for (int i = 0; i < 3; i++)
        {
            double ly = rect.Bottom + 16 - i * 4;
            content.MoveTo(rect.Left + 5, ly).LineTo(rect.Left + 15, ly).Stroke();
        }
        SetAppearance(doc, annot, rect, content);
        doc.Pages[pageIndex].Annotations.Add(annot);
        return annot;
    }

    public static PdfAnnotation AddFreeText(PdfDocument doc, int pageIndex, PdfRect rect, string text,
        double fontSize = 12, PdfColor? textColor = null, string? author = null)
    {
        if (ContentEditor.NeedsComplexShaping(text)) throw new NotSupportedException(ContentEditor.ComplexShapingMessage);
        var c = textColor ?? PdfColor.Black;
        var annot = CreateBase(doc, "/FreeText", rect, c, author, text);
        annot.Elements.Remove("/C"); // For FreeText, /C is the background; leave it transparent.
        annot.Elements.SetString("/DA",
            $"/Helv {fontSize.ToString(CultureInfo.InvariantCulture)} Tf {Fmt(c.R)} {Fmt(c.G)} {Fmt(c.B)} rg");

        // Drawn with an embedded font so any script supported by the installed fonts shows correctly.
        var form = ContentEditor.FormFromDrawing(doc, rect.Width, rect.Height, g =>
        {
            // Wrapped to the box; right-to-left paragraphs are shaped and right-aligned.
            var layout = ContentEditor.Layout(g, text, new TextStyle("Segoe UI", fontSize), rect.Width - 4);
            int fit = Math.Max(1, (int)((rect.Height - 2 + 0.5) / layout.LineHeight));
            ContentEditor.Draw(g, layout with { Lines = layout.Lines.Take(fit).ToList() }, new XSolidBrush(ContentEditor.ToXColor(c)), 2, 2);
        });
        SetAppearance(annot, form);
        doc.Pages[pageIndex].Annotations.Add(annot);
        return annot;
    }

    public static PdfAnnotation AddInk(PdfDocument doc, int pageIndex,
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes, PdfColor color, double lineWidth = 2,
        string? author = null)
    {
        var points = strokes.SelectMany(s => s).ToList();
        if (points.Count == 0) throw new ArgumentException("Ink needs at least one point.", nameof(strokes));

        double pad = lineWidth;
        var rect = new PdfRect(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad,
            points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad);
        var annot = CreateBase(doc, "/Ink", rect, color, author, null);

        var inkList = new PdfArray(doc);
        foreach (var stroke in strokes.Where(s => s.Count > 0))
            inkList.Elements.Add(PdfObjects.Numbers(doc, [.. stroke.SelectMany(p => new[] { p.X, p.Y })]));
        annot.Elements["/InkList"] = inkList;
        annot.Elements["/BS"] = BorderStyle(doc, lineWidth);

        var content = new ContentBuilder().StrokeColor(color).LineWidth(lineWidth).RoundCaps();
        foreach (var stroke in strokes.Where(s => s.Count > 0))
        {
            content.MoveTo(stroke[0].X, stroke[0].Y);
            // A single tap still leaves a visible dot.
            if (stroke.Count == 1) content.LineTo(stroke[0].X + 0.01, stroke[0].Y);
            for (int i = 1; i < stroke.Count; i++) content.LineTo(stroke[i].X, stroke[i].Y);
            content.Stroke();
        }
        SetAppearance(doc, annot, rect, content);
        doc.Pages[pageIndex].Annotations.Add(annot);
        return annot;
    }

    /// <summary>Rubber stamp with an original Optim PDF appearance: a rounded label with a date line.</summary>
    public static PdfAnnotation AddStamp(PdfDocument doc, int pageIndex, PdfRect rect, string label,
        PdfColor? color = null, string? author = null, DateTime? date = null)
    {
        var c = color ?? PdfColor.Blue;
        var annot = CreateBase(doc, "/Stamp", rect, c, author, label);
        annot.Elements.SetName("/Name", "/" + new string(label.Where(char.IsLetterOrDigit).ToArray()));

        if (ContentEditor.NeedsComplexShaping(label)) throw new NotSupportedException(ContentEditor.ComplexShapingMessage);
        var shown = RtlText.HasRtl(label) ? RtlText.ToVisual(label) : label; // Shaped, in visual order.
        string dateLine = $"{author ?? DefaultAuthor} · {(date ?? DateTime.Now):yyyy-MM-dd HH:mm}";
        double w = rect.Width, h = rect.Height;
        string family = ContentEditor.FontFor(shown, "Segoe UI");

        var form = ContentEditor.FormFromDrawing(doc, w, h, g =>
        {
            var ink = ContentEditor.ToXColor(c);
            g.DrawRoundedRectangle(new XPen(ink, 2.2), 1.5, 1.5, w - 3, h - 3, h * 0.36, h * 0.36);
            // Fit the label to the stamp's width and upper part.
            var probe = ContentEditor.CreateFont(family, new TextStyle(family, 100, Bold: true));
            double labelSize = Math.Min(h * 0.42, 100 * (w - 16) / Math.Max(1, g.MeasureString(shown, probe).Width));
            var labelFont = ContentEditor.CreateFont(family, new TextStyle(family, labelSize, Bold: true));
            var dateFont = ContentEditor.CreateFont("Segoe UI", new TextStyle("Segoe UI", Math.Max(5, Math.Min(h * 0.16, labelSize * 0.4))));
            var brush = new XSolidBrush(ink);
            g.DrawString(shown, labelFont, brush, new XRect(0, h * 0.08, w, h * 0.55), XStringFormats.Center);
            g.DrawString(dateLine, dateFont, brush, new XRect(4, h * 0.62, w - 8, h * 0.28), XStringFormats.Center);
        });
        SetAppearance(annot, form);
        doc.Pages[pageIndex].Annotations.Add(annot);
        return annot;
    }

    public static IReadOnlyList<AnnotationInfo> List(PdfDocument doc)
    {
        var result = new List<AnnotationInfo>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            var annots = doc.Pages[p].Annotations;
            for (int i = 0; i < annots.Count; i++)
            {
                var a = annots[i];
                string subtype = a.Elements.GetName("/Subtype").TrimStart('/');
                if (subtype is "Popup" or "Link" or "Widget") continue; // Not user comments.
                var r = a.Elements.GetRectangle("/Rect");
                result.Add(new AnnotationInfo(p, i, subtype, PdfRect.FromPoints(r.X1, r.Y1, r.X2, r.Y2),
                    NullIfEmpty(a.Elements.GetString("/Contents")), NullIfEmpty(a.Elements.GetString("/T")),
                    NullIfEmpty(a.Elements.GetString("/NM"))));
            }
        }
        return result;
    }

    public static bool Remove(PdfDocument doc, int pageIndex, int indexOnPage)
    {
        var annots = doc.Pages[pageIndex].Annotations;
        if ((uint)indexOnPage >= (uint)annots.Count) return false;
        annots.Remove(annots[indexOnPage]);
        return true;
    }

    /// <summary>Moves an annotation (and its popup) by (dx, dy) in user space.</summary>
    public static void Move(PdfDocument doc, int pageIndex, int indexOnPage, double dx, double dy) =>
        Transform(doc, pageIndex, indexOnPage, p => (p.X + dx, p.Y + dy));

    /// <summary>Scales an annotation about a fixed point; its appearance stretches with its rectangle.</summary>
    public static void Resize(PdfDocument doc, int pageIndex, int indexOnPage, double factor, (double X, double Y) anchor)
    {
        if (!(factor > 0.02 && factor < 50)) throw new ArgumentOutOfRangeException(nameof(factor), "That size is not possible.");
        Transform(doc, pageIndex, indexOnPage, p => (anchor.X + (p.X - anchor.X) * factor, anchor.Y + (p.Y - anchor.Y) * factor), movePopup: false);
    }

    /// <summary>Applies a point mapping to every coordinate an annotation stores (ISO 32000-2 §12.5).</summary>
    static void Transform(PdfDocument doc, int pageIndex, int indexOnPage, Func<(double X, double Y), (double X, double Y)> map, bool movePopup = true)
    {
        var annots = doc.Pages[pageIndex].Annotations;
        if ((uint)indexOnPage >= (uint)annots.Count) throw new ArgumentOutOfRangeException(nameof(indexOnPage));
        var a = annots[indexOnPage];
        MapRect(a, map);
        foreach (var key in new[] { "/QuadPoints", "/Vertices", "/L", "/CL" })
            if (a.Elements.GetArray(key) is { } arr) MapPoints(arr, map);
        if (a.Elements.GetArray("/InkList") is { } ink)
            foreach (var stroke in ink.Elements)
                if ((stroke is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : stroke) is PdfArray points) MapPoints(points, map);
        if (movePopup && (a.Elements["/Popup"] as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfDictionary popup) MapRect(popup, map);
    }

    static void MapRect(PdfDictionary d, Func<(double X, double Y), (double X, double Y)> map)
    {
        var r = d.Elements.GetRectangle("/Rect");
        var p1 = map((r.X1, r.Y1));
        var p2 = map((r.X2, r.Y2));
        d.Elements["/Rect"] = PdfObjects.Numbers(d.Owner, Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));
    }

    static void MapPoints(PdfArray arr, Func<(double X, double Y), (double X, double Y)> map)
    {
        for (int i = 0; i + 1 < arr.Elements.Count; i += 2)
        {
            var (x, y) = map((arr.Elements.GetReal(i), arr.Elements.GetReal(i + 1)));
            arr.Elements[i] = new PdfReal(x);
            arr.Elements[i + 1] = new PdfReal(y);
        }
    }

    static RawAnnotation CreateBase(PdfDocument doc, string subtype, PdfRect rect, PdfColor color,
        string? author, string? contents)
    {
        var annot = new RawAnnotation(doc);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", subtype);
        annot.Rectangle = new PdfRectangle(new XPoint(rect.Left, rect.Bottom), new XPoint(rect.Right, rect.Top));
        annot.Elements["/C"] = PdfObjects.Numbers(doc, color.R, color.G, color.B);
        annot.Elements.SetString("/T", author ?? DefaultAuthor);
        if (!string.IsNullOrEmpty(contents)) annot.Elements.SetString("/Contents", contents);
        annot.Elements.SetString("/NM", Guid.NewGuid().ToString("N"));
        annot.Elements.SetString("/M", PdfDate(DateTime.Now));
        annot.Elements.SetString("/CreationDate", PdfDate(DateTime.Now));
        annot.Elements.SetInteger("/F", (int)PdfAnnotationFlags.Print);
        return annot;
    }

    static void SetAppearance(PdfDocument doc, PdfAnnotation annot, PdfRect bbox, ContentBuilder content,
        PdfDictionary? extGStates = null, bool withFonts = false)
    {
        var form = PdfObjects.FormXObject(doc, bbox, content, extGStates, withFonts);
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form.Reference;
        annot.Elements["/AP"] = ap;
    }

    static void SetAppearance(PdfAnnotation annot, PdfDictionary form)
    {
        var ap = new PdfDictionary(annot.Owner);
        ap.Elements["/N"] = form.Reference;
        annot.Elements["/AP"] = ap;
    }

    static PdfDictionary MultiplyBlendState(PdfDocument doc)
    {
        var gs = new PdfDictionary(doc);
        gs.Elements.SetName("/Type", "/ExtGState");
        gs.Elements.SetName("/BM", "/Multiply");
        var states = new PdfDictionary(doc);
        states.Elements["/GS0"] = gs;
        return states;
    }

    static PdfDictionary BorderStyle(PdfDocument doc, double width)
    {
        var bs = new PdfDictionary(doc);
        bs.Elements.SetName("/Type", "/Border");
        bs.Elements["/W"] = new PdfReal(width);
        bs.Elements.SetName("/S", "/S");
        return bs;
    }

    internal static IEnumerable<string> WrapText(string text, double maxWidth, double fontSize)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && ContentBuilder.MeasureText(candidate, StandardFont.Helvetica, fontSize) > maxWidth)
                {
                    yield return line;
                    line = word;
                }
                else line = candidate;
            }
            yield return line;
        }
    }

    static PdfRect Union(IReadOnlyList<PdfRect> rects) => new(
        rects.Min(r => r.Left), rects.Min(r => r.Bottom), rects.Max(r => r.Right), rects.Max(r => r.Top));

    static string PdfDate(DateTime t)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(t);
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        return $"D:{t:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
    }

    static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
