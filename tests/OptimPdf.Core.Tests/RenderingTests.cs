using OptimPdf.Core.Rendering;

namespace OptimPdf.Core.Tests;

public class RenderingTests
{
    [Fact]
    public void Opens_document_and_reports_pages_and_size()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(3));
        Assert.Equal(3, doc.PageCount);
        var size = doc.GetPageSize(0);
        Assert.Equal(612, size.Width, 1);
        Assert.Equal(792, size.Height, 1);
    }

    [Fact]
    public void Renders_at_requested_scale_and_draws_text()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(1));
        var page = doc.Render(0, 2.0);
        Assert.Equal(1224, page.PixelWidth);
        Assert.Equal(1584, page.PixelHeight);
        Assert.Equal(page.PixelWidth * 4, page.Stride);
        Assert.True(TestPdf.DarkPixelCount(page) > 500, "Expected visible text pixels.");
    }

    [Fact]
    public void Blank_page_renders_white()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Blank());
        Assert.Equal(0, TestPdf.DarkPixelCount(doc.Render(0, 0.5), threshold: 250));
    }

    [Fact]
    public void View_rotation_swaps_dimensions()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(1));
        var page = doc.Render(0, 1.0, viewRotation: 1);
        Assert.Equal(792, page.PixelWidth);
        Assert.Equal(612, page.PixelHeight);
    }

    [Fact]
    public void Extreme_zoom_is_clamped()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Blank());
        var page = doc.Render(0, 100.0);
        Assert.True(Math.Max(page.PixelWidth, page.PixelHeight) <= PdfiumDocument.MaxBitmapSide);
    }

    [Fact]
    public void Extracts_page_text()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(3));
        Assert.Contains(TestPdf.Title(3), doc.GetPageText(2));
        Assert.Contains("lazy dog", doc.GetPageText(0));
    }

    [Fact]
    public void Search_finds_hits_on_every_page_with_bounds_inside_page()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(4));
        var hits = doc.Search("optim");
        Assert.Equal(4, hits.Count);
        Assert.Equal([0, 1, 2, 3], hits.Select(h => h.PageIndex));
        foreach (var hit in hits)
        {
            var r = Assert.Single(hit.Rects);
            Assert.InRange(r.Left, TestPdf.TitleX, 612);
            Assert.InRange(r.Bottom, TestPdf.TitleY - 10, TestPdf.TitleY + TestPdf.TitleSize);
            Assert.Equal(5, hit.CharCount);
        }
    }

    [Fact]
    public void Search_respects_match_case_and_whole_word()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(1));
        Assert.Empty(doc.Search("OPTIM", matchCase: true));
        Assert.Single(doc.Search("Optim", matchCase: true));
        Assert.Empty(doc.Search("Opt", wholeWord: true));
        Assert.Empty(doc.Search(""));
    }

    [Fact]
    public void Hit_tests_characters_for_text_selection()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(1));
        // Inside the first glyph ("P") of the title line.
        int first = doc.GetCharIndexAt(0, TestPdf.TitleX + 3, TestPdf.TitleY + 6);
        Assert.Equal(0, first);
        Assert.Equal(-1, doc.GetCharIndexAt(0, 500, 100, tolerance: 2));

        var hit = doc.Search("lazy").Single();
        Assert.Equal("lazy", doc.GetText(0, hit.CharIndex, hit.CharCount));
        var rect = Assert.Single(doc.GetTextRects(0, hit.CharIndex, hit.CharCount));
        Assert.Equal(hit.Rects[0], rect);
    }

    [Fact]
    public void Reads_outline()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(3, withOutline: true));
        var outline = doc.GetOutline();
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], outline.Select(o => o.Title));
        Assert.Equal([0, 1, 2], outline.Select(o => o.PageIndex));
    }

    [Fact]
    public void Reads_metadata()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(1, title: "Quarterly Report"));
        Assert.Equal("Quarterly Report", doc.GetMetadata("Title"));
        Assert.Null(doc.GetMetadata("Keywords"));
        Assert.Equal(-1, doc.SecurityRevision);
    }

    [Fact]
    public void Device_and_page_coordinates_round_trip()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(1));
        for (int rotation = 0; rotation < 4; rotation++)
        {
            int w = rotation % 2 == 0 ? 612 : 792, h = rotation % 2 == 0 ? 792 : 612;
            var (dx, dy) = doc.PageToDevice(0, w, h, rotation, 100, 200);
            var (px, py) = doc.DeviceToPage(0, w, h, rotation, dx, dy);
            Assert.Equal(100, px, 1.5);
            Assert.Equal(200, py, 1.5);
        }
    }

    [Fact]
    public void Rejects_non_pdf_data()
    {
        Assert.Throws<PdfOpenException>(() => PdfiumDocument.Open("this is not a pdf"u8.ToArray()));
    }

    [Fact]
    public void Disposed_document_throws()
    {
        var doc = PdfiumDocument.Open(TestPdf.Create(1));
        doc.Dispose();
        doc.Dispose(); // Idempotent.
        Assert.Throws<ObjectDisposedException>(() => doc.Render(0, 1));
    }

    [Fact]
    public async Task Concurrent_rendering_is_safe()
    {
        using var doc = PdfiumDocument.Open(TestPdf.Create(6));
        var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(() => doc.Render(i % 6, 0.5)));
        var pages = await Task.WhenAll(tasks);
        Assert.All(pages, p => Assert.True(TestPdf.DarkPixelCount(p) > 0));
    }
}
