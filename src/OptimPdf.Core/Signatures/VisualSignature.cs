using System.Text.Json.Serialization;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OptimPdf.Core.Signatures;

/// <summary>
/// An electronic (visual) signature: a drawn, typed or image mark. This is not a cryptographic
/// digital signature; it does not prove who signed or protect the document from changes.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DrawnSignature), "drawn")]
[JsonDerivedType(typeof(TypedSignature), "typed")]
[JsonDerivedType(typeof(ImageSignature), "image")]
public abstract record SignatureMark
{
    /// <summary>Width divided by height, used to keep proportions when placing.</summary>
    public abstract double AspectRatio { get; }
}

/// <summary>Strokes in a unit box: x in [0, AspectRatio], y in [0, 1], y pointing down.</summary>
public sealed record DrawnSignature(IReadOnlyList<IReadOnlyList<SignaturePoint>> Strokes, double Aspect) : SignatureMark
{
    public override double AspectRatio => Aspect;

    /// <summary>Normalizes raw strokes (any units, y down) into the unit box with a small margin.</summary>
    public static DrawnSignature FromStrokes(IReadOnlyList<IReadOnlyList<SignaturePoint>> strokes)
    {
        var pts = strokes.SelectMany(s => s).ToList();
        if (pts.Count == 0) throw new ArgumentException("The signature is empty.", nameof(strokes));
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double h = Math.Max(maxY - minY, 1), w = Math.Max(maxX - minX, 1);
        double pad = 0.06 * h;
        double scale = 1 / (h + 2 * pad);
        var normalized = strokes.Where(s => s.Count > 0)
            .Select(s => (IReadOnlyList<SignaturePoint>)s.Select(p => new SignaturePoint((p.X - minX + pad) * scale, (p.Y - minY + pad) * scale)).ToList())
            .ToList();
        return new DrawnSignature(normalized, (w + 2 * pad) * scale);
    }
}

public readonly record struct SignaturePoint(double X, double Y);

public sealed record TypedSignature(string Text, string FontFamily = "Segoe Script") : SignatureMark
{
    public override double AspectRatio => Math.Max(1.5, Text.Length * 0.55);
}

public sealed record ImageSignature(byte[] ImageData, double Aspect) : SignatureMark
{
    public override double AspectRatio => Aspect;

    public static ImageSignature FromImage(byte[] data)
    {
        using var bitmap = ImageCodec.Decode(data);
        return new ImageSignature(data, bitmap.Width / (double)bitmap.Height);
    }
}

public static class VisualSignatureService
{
    /// <summary>Handwriting-style fonts that ship with Windows, in order of preference.</summary>
    public static readonly string[] ScriptFonts = ["Segoe Script", "Ink Free", "Lucida Handwriting", "Segoe Print"];

    /// <summary>
    /// Places a signature as permanent page content, with its top-left corner at (x, y) in user space,
    /// sized to fit width × height (points, along the displayed page axes) keeping proportions.
    /// </summary>
    public static PdfRect Place(PdfDocument doc, int pageIndex, double x, double y, double width, double height,
        SignatureMark mark, PdfColor? color = null, int itemId = 0)
    {
        ContentEditor.EnsureFonts();
        var page = doc.Pages[pageIndex];
        var ink = ContentEditor.ToXColor(color ?? new PdfColor(0.05, 0.12, 0.45)); // Dark blue ink.

        // Fit the mark's aspect ratio inside the box.
        double w = width, h = width / mark.AspectRatio;
        if (h > height) { h = height; w = height * mark.AspectRatio; }

        using (var g = ContentEditor.OpenUpright(page, x, y))
        {
            switch (mark)
            {
                case DrawnSignature drawn:
                    var pen = new XPen(ink, Math.Max(0.8, h * 0.035)) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
                    foreach (var stroke in drawn.Strokes)
                    {
                        var pts = stroke.Select(p => new XPoint(p.X * h, p.Y * h)).ToArray();
                        if (pts.Length == 1) pts = [pts[0], new XPoint(pts[0].X + 0.01, pts[0].Y)];
                        g.DrawLines(pen, pts);
                    }
                    break;

                case TypedSignature typed:
                    var family = string.IsNullOrWhiteSpace(typed.FontFamily) ? ScriptFonts[0] : typed.FontFamily;
                    var probe = ContentEditor.CreateFont(family, new TextStyle(family, 100));
                    var size = g.MeasureString(typed.Text, probe);
                    double fontSize = Math.Min(100 * w / size.Width, 100 * h / size.Height) * 0.95;
                    var font = ContentEditor.CreateFont(family, new TextStyle(family, fontSize));
                    g.DrawString(typed.Text, font, new XSolidBrush(ink), new XRect(0, 0, w, h), XStringFormats.Center);
                    break;

                case ImageSignature image:
                    using (var xi = ImageCodec.ToXImage(image.ImageData))
                        g.DrawImage(xi, 0, 0, w, h);
                    break;
            }
        }

        ContentEditor.TagLastContent(page, itemId);
        var (r, d) = ContentEditor.Axes(page.Rotate);
        return PdfRect.FromPoints(x, y, x + r.X * w + d.X * h, y + r.Y * w + d.Y * h);
    }
}
