using System.Drawing;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OptimPdf.Core.Conversion;

public enum PageSizeMode { MatchImage, Letter, A4 }

/// <summary>Image ↔ PDF conversion.</summary>
public static class ImageConversion
{
    /// <summary>
    /// Creates a PDF with one page per image (one per frame for multi-page TIFF).
    /// MatchImage sizes each page to the image at its stored DPI (96 DPI when unknown);
    /// Letter/A4 fit the image inside the page with a margin, rotating the page for landscape images.
    /// </summary>
    public static PdfDocument ImagesToPdf(IEnumerable<byte[]> images, PageSizeMode mode = PageSizeMode.MatchImage, double marginPt = 36)
    {
        var doc = new PdfDocument();
        foreach (var data in images)
        {
            if (ImageCodec.IsJpeg(data))
            {
                // JPEG goes in untouched.
                using var image = XImage.FromStream(new MemoryStream(data, writable: false));
                AddImagePage(doc, image, image.PixelWidth, image.PixelHeight, image.HorizontalResolution, mode, marginPt);
                continue;
            }
            foreach (var frame in ImageCodec.DecodeFrames(data))
            {
                using (frame)
                using (var image = XImage.FromStream(new MemoryStream(ImageCodec.Encode(frame, ImageFileFormat.Png))))
                    AddImagePage(doc, image, frame.Width, frame.Height, frame.HorizontalResolution, mode, marginPt);
            }
        }
        if (doc.PageCount == 0) throw new ArgumentException("No images given.", nameof(images));
        return doc;
    }

    static void AddImagePage(PdfDocument doc, XImage image, int pixelWidth, int pixelHeight, double dpi, PageSizeMode mode, double margin)
    {
        if (dpi is < 10 or > 2400) dpi = 96;
        // PNG stores pixels per meter as an integer, so 96 DPI reads back as 96.012; snap to whole DPI.
        if (Math.Abs(dpi - Math.Round(dpi)) < 0.05) dpi = Math.Round(dpi);
        double imgW = pixelWidth * 72.0 / dpi, imgH = pixelHeight * 72.0 / dpi;

        double pageW, pageH;
        if (mode == PageSizeMode.MatchImage) { pageW = imgW; pageH = imgH; margin = 0; }
        else
        {
            (pageW, pageH) = mode == PageSizeMode.A4 ? (595.28, 841.89) : (612, 792);
            if (imgW > imgH) (pageW, pageH) = (pageH, pageW);
        }

        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(pageW);
        page.Height = XUnit.FromPoint(pageH);

        double scale = mode == PageSizeMode.MatchImage ? 1 : Math.Min(1, Math.Min((pageW - 2 * margin) / imgW, (pageH - 2 * margin) / imgH));
        double w = imgW * scale, h = imgH * scale;
        using var g = XGraphics.FromPdfPage(page);
        g.DrawImage(image, (pageW - w) / 2, (pageH - h) / 2, w, h);
    }

    /// <summary>Renders pages to image files. Returns the paths written.</summary>
    /// <param name="pathForPage">Builds the output path for a zero-based page index.</param>
    public static IReadOnlyList<string> PdfToImages(PdfiumDocument document, IEnumerable<int> pages, double dpi,
        ImageFileFormat format, Func<int, string> pathForPage, int jpegQuality = 90, CancellationToken cancellationToken = default)
    {
        var written = new List<string>();
        foreach (int index in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = RenderPage(document, index, dpi);
            var path = pathForPage(index);
            IO.SafeFileWriter.Write(path, ImageCodec.Encode(bitmap, format, jpegQuality), keepBackup: false);
            written.Add(path);
        }
        return written;
    }

    /// <summary>Renders pages into a single multi-page TIFF.</summary>
    public static byte[] PdfToMultiPageTiff(PdfiumDocument document, IEnumerable<int> pages, double dpi)
    {
        var bitmaps = pages.Select(i => RenderPage(document, i, dpi)).ToList();
        try { return ImageCodec.EncodeMultiPageTiff(bitmaps); }
        finally { foreach (var b in bitmaps) b.Dispose(); }
    }

    public static Bitmap RenderPage(PdfiumDocument document, int index, double dpi)
    {
        if (dpi is < 10 or > 1200) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be between 10 and 1200.");
        return ImageCodec.FromRendered(document.Render(index, dpi / 72.0), dpi);
    }
}
