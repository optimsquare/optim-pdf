using OptimPdf.Core.Documents;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PdfSharp.Pdf;

namespace OptimPdf.Core.Tests;

/// <summary>Builds test PDFs at runtime so the suite has no binary fixtures.</summary>
static class TestPdf
{
    public const double TitleX = 72, TitleY = 700, TitleSize = 24;

    public static string Title(int pageNumber) => $"Page {pageNumber} of Optim test";

    /// <summary>Letter-size pages; page n shows "Page n of Optim test" and a second line of text.</summary>
    public static byte[] Create(int pages, bool withOutline = false, string? title = null)
    {
        using var doc = new PdfDocument();
        if (title is not null) doc.Info.Title = title;
        for (int i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
            var content = new ContentBuilder()
                .FillColor(PdfColor.Black)
                .Text(TitleX, TitleY, StandardFont.Helvetica, TitleSize, Title(i))
                .Text(72, 650, StandardFont.Helvetica, 14, "The quick brown fox jumps over the lazy dog.");
            PdfObjects.AppendPageContent(page, content);
            if (withOutline) doc.Outlines.Add($"Chapter {i}", page);
        }
        return DocumentSession.Serialize(doc);
    }

    public static byte[] Blank(int pages = 1)
    {
        using var doc = new PdfDocument();
        for (int i = 0; i < pages; i++) doc.AddPage();
        return DocumentSession.Serialize(doc);
    }

    /// <summary>Texts of all pages, first line only, as PDFium extracts them.</summary>
    public static List<string> PageTitles(PdfiumDocument doc) =>
        Enumerable.Range(0, doc.PageCount).Select(i => FirstLine(doc.GetPageText(i))).ToList();

    public static List<string> PageTitles(byte[] data)
    {
        using var doc = PdfiumDocument.Open(data);
        return PageTitles(doc);
    }

    static string FirstLine(string text) => text.Split('\n', '\r')[0].Trim();

    /// <summary>Average BGRA color of a PDF-space rectangle rendered at 1 px/pt with no view rotation.</summary>
    public static (double R, double G, double B) AverageColor(PdfiumDocument doc, int pageIndex, PdfRect rect)
    {
        var bmp = doc.Render(pageIndex, 1.0);
        var (x1, y1) = doc.PageToDevice(pageIndex, bmp.PixelWidth, bmp.PixelHeight, 0, rect.Left, rect.Top);
        var (x2, y2) = doc.PageToDevice(pageIndex, bmp.PixelWidth, bmp.PixelHeight, 0, rect.Right, rect.Bottom);
        int left = Math.Clamp(Math.Min(x1, x2), 0, bmp.PixelWidth - 1), right = Math.Clamp(Math.Max(x1, x2), 0, bmp.PixelWidth - 1);
        int top = Math.Clamp(Math.Min(y1, y2), 0, bmp.PixelHeight - 1), bottom = Math.Clamp(Math.Max(y1, y2), 0, bmp.PixelHeight - 1);

        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                int o = y * bmp.Stride + x * 4;
                b += bmp.Pixels[o]; g += bmp.Pixels[o + 1]; r += bmp.Pixels[o + 2];
                n++;
            }
        return (r / n, g / n, b / n);
    }

    public static int DarkPixelCount(RenderedPage page, int threshold = 128)
    {
        int count = 0;
        for (int i = 0; i < page.Pixels.Length; i += 4)
            if (page.Pixels[i] < threshold && page.Pixels[i + 1] < threshold && page.Pixels[i + 2] < threshold) count++;
        return count;
    }

    /// <summary>
    /// Every byte of the file as Latin-1 text, with each Flate stream also inflated: an independent check of
    /// what the file really contains, without going through either PDF library.
    /// </summary>
    public static string RawContent(byte[] pdf)
    {
        var text = new System.Text.StringBuilder(System.Text.Encoding.Latin1.GetString(pdf));
        int pos = 0;
        while ((pos = IndexOf(pdf, "stream"u8, pos)) >= 0)
        {
            int start = pos + 6;
            if (start < pdf.Length && pdf[start] == (byte)13) start++;
            if (start < pdf.Length && pdf[start] == (byte)10) start++;
            int end = IndexOf(pdf, "endstream"u8, start);
            if (end < 0) break;
            try
            {
                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, end - start), System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                text.Append((char)10).Append(System.Text.Encoding.Latin1.GetString(ms.ToArray()));
            }
            catch (InvalidDataException) { } // Not Flate.
            pos = end + 9;
        }
        return text.ToString();
    }

    static int IndexOf(byte[] data, ReadOnlySpan<byte> needle, int from)
    {
        int i = data.AsSpan(from).IndexOf(needle);
        return i < 0 ? -1 : from + i;
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OptimPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
