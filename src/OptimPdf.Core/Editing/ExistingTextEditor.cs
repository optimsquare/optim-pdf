using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PDFiumCore;

namespace OptimPdf.Core.Editing;

/// <summary>
/// A line of existing page text, made of one or more text objects that share a baseline and font size.
/// <see cref="Matrix"/> maps text space (font size 1, y up) to user space at the start of the line.
/// </summary>
public sealed record TextLine(
    int PageIndex,
    IReadOnlyList<int> ObjectIndices,
    string Text,
    PdfRect Bounds,
    string PdfFontName,
    string Family,
    bool FontInstalled,
    double FontSize,
    PdfColor Color,
    bool Bold,
    bool Italic,
    (double A, double B, double C, double D, double E, double F) Matrix)
{
    /// <summary>The style used to redraw the line.</summary>
    public TextStyle Style => new(Family, FontSize, Color, Bold, Italic);
}

/// <summary>
/// Edits text that is already on a page. The original text objects are removed (PDFium), and the new text is
/// drawn at the same position, size, angle and color with an installed font (the PDF writer). Fonts embedded in
/// PDFs are usually subsets that lack the glyphs for new text, so the line is always redrawn with an installed
/// font: the matching family when it is installed, otherwise a substitute (<see cref="TextLine.FontInstalled"/> is false).
/// </summary>
public static class ExistingTextEditor
{
    /// <summary>The text line at a point in user space, or null when there is no text there.</summary>
    public static TextLine? FindLine(byte[] data, string? password, int pageIndex, double x, double y) =>
        PdfiumEditor.Run(data, password, editor =>
        {
            var page = editor.Page(pageIndex);
            var textPage = fpdf_text.FPDFTextLoadPage(page.Handle);
            try
            {
                int index = fpdf_text.FPDFTextGetCharIndexAtPos(textPage, x, y, 4, 4);
                if (index < 0) return null;
                var hitObject = fpdf_text.FPDFTextGetTextObject(textPage, index);
                if (hitObject is null || hitObject.__Instance == IntPtr.Zero) return null;
                int hitIndex = page.IndexOf(hitObject);
                if (hitIndex < 0) return null; // Text inside a form XObject: not editable at page level.

                var runs = new List<Run>();
                foreach (var info in page.Objects())
                    if (info.Kind == PageObjectKind.Text && Read(page.ObjectAt(info.Index), textPage, info) is { } run)
                        runs.Add(run);
                var hit = runs.FirstOrDefault(r => r.Index == hitIndex);
                return hit is null ? null : BuildLine(pageIndex, hit, runs);
            }
            finally { fpdf_text.FPDFTextClosePage(textPage); }
        });

    /// <summary>Replaces a line's text as one undoable edit. An empty <paramref name="newText"/> deletes the line.</summary>
    public static void Replace(DocumentSession session, TextLine line, string newText, TextStyle? style = null)
    {
        newText = newText.Replace("\r", "").Replace("\n", " ");
        var s = style ?? line.Style;
        if (newText.Trim().Length > 0 && ContentEditor.NeedsComplexShaping(newText))
            throw new NotSupportedException(ContentEditor.ComplexShapingMessage);
        session.Apply(newText.Length == 0 ? "Delete text" : "Edit text",
            (data, password) => PdfiumEditor.Run(data, password, editor =>
            {
                var page = editor.Page(line.PageIndex);
                var objects = page.Objects();
                // Guard against a stale line (the page changed since it was found).
                if (line.ObjectIndices.Any(i => i >= objects.Count || objects[i].Kind != PageObjectKind.Text))
                    throw new InvalidOperationException("The page changed; select the text again.");
                page.RemoveObjects(line.ObjectIndices);
                return editor.Save();
            }),
            doc =>
            {
                if (newText.Trim().Length > 0) ContentEditor.DrawTextLine(doc, line.PageIndex, newText, s, line.Matrix);
            });
    }

    sealed record Run(int Index, string Text, PdfRect Bounds, double Size, (double A, double B, double C, double D, double E, double F) M,
        FpdfFontT Font, PdfColor Color)
    {
        public double Scale => Math.Sqrt(M.C * M.C + M.D * M.D);
        public double EffectiveSize => Size * Scale;
        public (double X, double Y) Direction { get { double n = Math.Sqrt(M.A * M.A + M.B * M.B); return n == 0 ? (1, 0) : (M.A / n, M.B / n); } }
    }

    static Run? Read(FpdfPageobjectT obj, FpdfTextpageT textPage, PageObjectInfo info)
    {
        float size = 0;
        if (fpdf_edit.FPDFTextObjGetFontSize(obj, ref size) == 0 || size <= 0) return null;
        using var m = new FS_MATRIX_();
        if (fpdf_edit.FPDFPageObjGetMatrix(obj, m) == 0) return null;
        uint r = 0, g = 0, b = 0, a = 0;
        fpdf_edit.FPDFPageObjGetFillColor(obj, ref r, ref g, ref b, ref a);
        return new Run(info.Index, ObjectText(obj, textPage), info.Bounds, size, (m.A, m.B, m.C, m.D, m.E, m.F),
            fpdf_edit.FPDFTextObjGetFont(obj), PdfColor.FromBytes((byte)r, (byte)g, (byte)b));
    }

    static string ObjectText(FpdfPageobjectT obj, FpdfTextpageT textPage)
    {
        ulong bytes = fpdf_edit.FPDFTextObjGetText(obj, textPage, ref Unsafe.NullRef<ushort>(), 0);
        if (bytes <= 2) return "";
        var buffer = new ushort[bytes / 2];
        fpdf_edit.FPDFTextObjGetText(obj, textPage, ref buffer[0], bytes);
        var chars = new char[buffer.Length - 1];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)buffer[i];
        return new string(chars);
    }

    /// <summary>Joins the hit run with neighbors on the same baseline, in reading order.</summary>
    static TextLine BuildLine(int pageIndex, Run hit, List<Run> runs)
    {
        var dir = hit.Direction;
        var up = (X: -dir.Y, Y: dir.X);
        double size = hit.EffectiveSize;
        double Along(Run r) => r.M.E * dir.X + r.M.F * dir.Y;
        double Across(Run r) => r.M.E * up.X + r.M.F * up.Y;
        (double Start, double End) Extent(Run r)
        {
            var corners = new[] { (r.Bounds.Left, r.Bounds.Bottom), (r.Bounds.Right, r.Bounds.Bottom), (r.Bounds.Left, r.Bounds.Top), (r.Bounds.Right, r.Bounds.Top) };
            var p = corners.Select(c => c.Item1 * dir.X + c.Item2 * dir.Y).ToList();
            return (p.Min(), p.Max());
        }

        var sameLine = runs
            .Where(r => Math.Abs(r.Direction.X - dir.X) < 0.01 && Math.Abs(r.Direction.Y - dir.Y) < 0.01)
            .Where(r => Math.Abs(Across(r) - Across(hit)) < size * 0.25 && Math.Abs(r.EffectiveSize - size) < size * 0.15)
            .OrderBy(Along).ToList();

        // Grow from the hit run while the gap to the next run is at most about a word space or two.
        int at = sameLine.IndexOf(hit), first = at, last = at;
        double maxGap = size * 1.2;
        while (first > 0 && Extent(sameLine[first]).Start - Extent(sameLine[first - 1]).End < maxGap) first--;
        while (last < sameLine.Count - 1 && Extent(sameLine[last + 1]).Start - Extent(sameLine[last]).End < maxGap) last++;
        var line = sameLine.GetRange(first, last - first + 1);

        var text = new StringBuilder();
        for (int i = 0; i < line.Count; i++)
        {
            if (i > 0)
            {
                double gap = Extent(line[i]).Start - Extent(line[i - 1]).End;
                if (gap > size * 0.15 && !text.ToString().EndsWith(' ') && !line[i].Text.StartsWith(' ')) text.Append(' ');
            }
            text.Append(line[i].Text);
        }

        var bounds = line.Select(r => r.Bounds).Aggregate((p, q) =>
            new PdfRect(Math.Min(p.Left, q.Left), Math.Min(p.Bottom, q.Bottom), Math.Max(p.Right, q.Right), Math.Max(p.Top, q.Top)));
        var start = line[0];
        var (pdfName, family, installed, bold, italic) = MatchFont(start.Font);
        // Normalize the matrix so the font size carries the scale.
        double scale = start.Scale == 0 ? 1 : start.Scale;
        var m = (start.M.A / scale, start.M.B / scale, start.M.C / scale, start.M.D / scale, start.M.E, start.M.F);
        return new TextLine(pageIndex, line.Select(r => r.Index).ToList(), text.ToString().TrimEnd(), bounds, pdfName, family, installed,
            Math.Round(start.Size * scale, 2), start.Color, bold, italic, m);
    }

    // ---- Font matching ----

    static readonly Dictionary<string, string> Standard14 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial", ["Arial"] = "Arial", ["Times"] = "Times New Roman", ["TimesRoman"] = "Times New Roman",
        ["Courier"] = "Courier New", ["Symbol"] = "Symbol", ["ZapfDingbats"] = "Segoe UI Symbol",
    };

    static (string PdfName, string Family, bool Installed, bool Bold, bool Italic) MatchFont(FpdfFontT font)
    {
        string baseName = FontString(font, baseName: true);
        string familyName = FontString(font, baseName: false);
        int weight = fpdf_edit.FPDFFontGetWeight(font);
        int angle = 0;
        fpdf_edit.FPDFFontGetItalicAngle(font, ref angle);

        string name = Regex.Replace(baseName, "^[A-Z]{6}\\+", ""); // Subset tag.
        string style = "";
        int split = name.IndexOfAny(['-', ',']);
        if (split > 0) { style = name[(split + 1)..]; name = name[..split]; }
        bool bold = weight >= 600 || Regex.IsMatch(style + baseName, "Bold|Black|Heavy|Semibold", RegexOptions.IgnoreCase);
        bool italic = angle != 0 || Regex.IsMatch(style, "Italic|Oblique", RegexOptions.IgnoreCase);

        var installed = ContentEditor.InstalledFamilies.ToList();
        foreach (var candidate in Candidates(name, familyName))
        {
            var match = installed.FirstOrDefault(f => Squash(f) == Squash(candidate));
            if (match is not null) return (baseName, match, true, bold, italic);
        }
        // Not installed: a substitute with similar letterforms.
        bool serif = Regex.IsMatch(name, "Times|Serif|Roman|Garamond|Georgia|Cambria|Book|Minion|Palatino", RegexOptions.IgnoreCase)
                     && !Regex.IsMatch(name, "Sans", RegexOptions.IgnoreCase);
        bool mono = Regex.IsMatch(name, "Courier|Mono|Consol", RegexOptions.IgnoreCase);
        return (baseName, mono ? "Courier New" : serif ? "Times New Roman" : "Arial", false, bold, italic);
    }

    static IEnumerable<string> Candidates(string name, string familyName)
    {
        foreach (var raw in new[] { name, familyName })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var n = Regex.Replace(raw, "^[A-Z]{6}\\+", "");
            n = Regex.Replace(n, "(PSMT|PS|MT)$", "");
            if (Standard14.TryGetValue(n, out var mapped)) yield return mapped;
            yield return n;
            yield return Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " "); // TimesNewRoman → Times New Roman
        }
    }

    static string Squash(string s) => Regex.Replace(s, "[^A-Za-z0-9]", "").ToLowerInvariant();

    static unsafe string FontString(FpdfFontT font, bool baseName)
    {
        var buffer = new sbyte[512];
        fixed (sbyte* p = buffer)
        {
            ulong n = baseName ? fpdf_edit.FPDFFontGetBaseFontName(font, p, (ulong)buffer.Length)
                               : fpdf_edit.FPDFFontGetFamilyName(font, p, (ulong)buffer.Length);
            if (n <= 1 || n > (ulong)buffer.Length) return "";
            return Marshal.PtrToStringUTF8((IntPtr)p, (int)n - 1);
        }
    }
}
