using OptimPdf.Core.Conversion;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Ocr;
using OptimPdf.Core.Rendering;

namespace OptimPdf.Core.Tests;

public class OcrTests
{
    /// <summary>A "scan": the test page rendered to a 200 DPI image, with no text layer.</summary>
    static byte[] Scanned(int viewRotation = 0, int pageRotate = 0)
    {
        using var source = PdfiumDocument.Open(TestPdf.Create(1));
        using var bitmap = ImageCodec.FromRendered(source.Render(0, 200 / 72.0, viewRotation), 200);
        var png = ImageCodec.Encode(bitmap, ImageFileFormat.Png);
        return DocumentSession.Transform(DocumentSession.Serialize(ImageConversion.ImagesToPdf([png])), null, doc =>
        {
            if (pageRotate != 0) doc.Pages[0].Rotate = pageRotate;
        });
    }

    static async Task<(DocumentSession Session, int Words)> Ocr(byte[] data)
    {
        var session = DocumentSession.FromBytes(data);
        var pages = await OcrService.RecognizeAsync(session.Renderer, [0], new OcrOptions("en-US"));
        int words = 0;
        session.Apply("Recognize text", doc => words = OcrService.AddTextLayer(doc, pages));
        return (session, words);
    }

    [Fact]
    public async Task Scanned_page_becomes_searchable_and_looks_unchanged()
    {
        var scan = Scanned();
        using var before = PdfiumDocument.Open(scan);
        Assert.Equal("", before.GetPageText(0).Trim());

        var (session, words) = await Ocr(scan);
        using (session)
        {
            Assert.True(words >= 12, $"Only {words} words recognized");
            var text = session.Renderer.GetPageText(0);
            Assert.Contains("quick brown fox jumps over the lazy dog", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Optim test", text);

            // Invisible: the page renders as before.
            Assert.True(MeanDifference(before.Render(0, 1), session.Renderer.Render(0, 1)) < 0.5);

            // The hit lies over the word on the image: compare with the word in the source page, scaled to the scan's page size.
            using var source = PdfiumDocument.Open(TestPdf.Create(1));
            var expected = source.Search("brown").Single().Rects[0];
            double k = session.Renderer.GetPageSize(0).Width / source.GetPageSize(0).Width;
            var box = session.Renderer.Search("brown").Single().Rects[0];
            Assert.InRange(box.Left, expected.Left * k - 3 * k, expected.Left * k + 3 * k);
            Assert.InRange(box.Right, expected.Right * k - 3 * k, expected.Right * k + 3 * k);
            Assert.InRange((box.Bottom + box.Top) / 2, (expected.Bottom + expected.Top) / 2 * k - 4 * k, (expected.Bottom + expected.Top) / 2 * k + 4 * k);
        }
    }

    [Fact]
    public async Task Text_layer_follows_page_rotation()
    {
        // The image is stored sideways and the page's /Rotate turns it upright, as scanners often do.
        var (upright, _) = await Ocr(Scanned());
        var (rotated, words) = await Ocr(Scanned(viewRotation: 3, pageRotate: 90));
        using (upright)
        using (rotated)
        {
            Assert.True(words >= 12);
            Assert.Contains("lazy dog", rotated.Renderer.GetPageText(0));
            // Same word, same place on the displayed page.
            var a = DeviceCenter(upright.Renderer, upright.Renderer.Search("brown").Single().Rects[0]);
            var b = DeviceCenter(rotated.Renderer, rotated.Renderer.Search("brown").Single().Rects[0]);
            Assert.InRange(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y), 0, 6);
        }
    }

    [Fact]
    public async Task Pages_that_already_have_text_are_skipped()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(2));
        var pages = await OcrService.RecognizeAsync(doc, [0, 1], new OcrOptions("en-US"));
        Assert.All(pages, p => Assert.True(p.Skipped));
    }

    [Fact]
    public void English_ocr_is_available_on_this_machine()
    {
        Assert.Contains(OcrService.AvailableLanguages, l => l.Tag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
    }

    static (int X, int Y) DeviceCenter(PdfiumDocument doc, PdfRect r)
    {
        var bmp = doc.Render(0, 1);
        return doc.PageToDevice(0, bmp.PixelWidth, bmp.PixelHeight, 0, (r.Left + r.Right) / 2, (r.Bottom + r.Top) / 2);
    }

    static double MeanDifference(RenderedPage a, RenderedPage b)
    {
        Assert.Equal(a.Pixels.Length, b.Pixels.Length);
        long sum = 0;
        for (int i = 0; i < a.Pixels.Length; i++) sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        return sum / (double)a.Pixels.Length;
    }
}
