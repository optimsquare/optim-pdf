using OptimPdf.Core.Annotations;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;

namespace OptimPdf.Core.Tests;

public class AnnotationTests
{
    static readonly PdfRect EmptyArea = new(300, 300, 400, 360);

    [Fact]
    public void Highlight_from_search_renders_yellow_and_keeps_text_readable()
    {
        var original = TestPdf.Create(1);
        using var before = PdfiumDocument.Open(original);
        var hit = before.Search("lazy dog").Single();

        var annotated = DocumentSession.Transform(original, null, doc =>
            AnnotationService.AddTextMarkup(doc, 0, TextMarkupKind.Highlight, hit.Rects, PdfColor.Yellow, comment: "check"));

        using var after = PdfiumDocument.Open(annotated);
        var rect = hit.Rects[0];
        var (r, g, b) = TestPdf.AverageColor(after, 0, rect);
        Assert.True(r > 150 && g > 120 && b < 120, $"Expected yellow, got R{r:0} G{g:0} B{b:0}");

        // Multiply blending must keep the text dark, so highlighted text still shows dark pixels.
        var page = after.Render(0, 1.0);
        Assert.True(TestPdf.DarkPixelCount(page) >= TestPdf.DarkPixelCount(before.Render(0, 1.0)) * 0.8);
        // The text layer is unchanged.
        Assert.Contains("lazy dog", after.GetPageText(0));
    }

    [Theory]
    [InlineData(TextMarkupKind.Underline)]
    [InlineData(TextMarkupKind.StrikeOut)]
    public void Line_markups_render_in_color(TextMarkupKind kind)
    {
        var annotated = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            AnnotationService.AddTextMarkup(doc, 0, kind, [new PdfRect(100, 400, 300, 420)], PdfColor.Red));
        using var after = PdfiumDocument.Open(annotated);
        var page = after.Render(0, 1.0);
        int red = 0;
        for (int i = 0; i < page.Pixels.Length; i += 4)
            if (page.Pixels[i + 2] > 150 && page.Pixels[i + 1] < 110 && page.Pixels[i] < 110) red++;
        // A 200 pt line is at least ~200 strongly red pixels at 1 px/pt.
        Assert.True(red >= 180, $"Expected a red line, found {red} red pixels.");

        // Underline sits near the bottom of the box, strike-out near the middle.
        var (_, gBottom, _) = TestPdf.AverageColor(after, 0, new PdfRect(100, 400, 300, 404));
        var (_, gMiddle, _) = TestPdf.AverageColor(after, 0, new PdfRect(100, 408, 300, 412));
        if (kind == TextMarkupKind.Underline) Assert.True(gBottom < gMiddle);
        else Assert.True(gMiddle < gBottom);
    }

    [Fact]
    public void Ink_stroke_renders()
    {
        var stroke = Enumerable.Range(0, 50).Select(i => (300.0 + i * 2, 300.0 + i)).ToList();
        var annotated = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            AnnotationService.AddInk(doc, 0, [stroke], PdfColor.Blue, 3));
        using var after = PdfiumDocument.Open(annotated);
        var (r, _, b) = TestPdf.AverageColor(after, 0, new PdfRect(300, 300, 400, 350));
        Assert.True(b > r + 10, "Expected blue ink pixels.");
    }

    [Fact]
    public void Stamp_and_free_text_and_note_render_visible_appearances()
    {
        var annotated = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            AnnotationService.AddStamp(doc, 0, new PdfRect(100, 600, 300, 680), "APPROVED", PdfColor.Green);
            AnnotationService.AddFreeText(doc, 0, new PdfRect(100, 400, 400, 460), "Typed comment that wraps across lines", 14);
            AnnotationService.AddNote(doc, 0, 500, 700, "Please review");
        });
        using var after = PdfiumDocument.Open(annotated);
        var page = after.Render(0, 1.0);
        Assert.True(TestPdf.DarkPixelCount(page, threshold: 200) > 300);

        var (r, g, b) = TestPdf.AverageColor(after, 0, new PdfRect(100, 600, 300, 680));
        Assert.True(g < 250 && g > r, "Stamp should render green.");
        Assert.True(TestPdf.AverageColor(after, 0, new PdfRect(100, 400, 400, 460)).R < 250, "Free text should render.");
        Assert.True(TestPdf.AverageColor(after, 0, new PdfRect(500, 678, 520, 700)).B < 200, "Note icon should render.");
    }

    [Theory]
    [InlineData("漢字かな交じり文 (test) \\")]
    [InlineData("Привет, мир")]
    [InlineData("Ελληνικά")]
    public void Free_text_and_stamps_render_any_script(string text)
    {
        var annotated = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            AnnotationService.AddFreeText(doc, 0, new PdfRect(100, 400, 400, 460), text, 16);
            AnnotationService.AddStamp(doc, 0, new PdfRect(100, 600, 300, 670), text);
        });
        using var after = PdfiumDocument.Open(annotated);
        Assert.True(DarkPixelsIn(after, new PdfRect(100, 400, 400, 460)) > 40, "Text box should show glyphs.");
        Assert.True(TestPdf.AverageColor(after, 0, new PdfRect(100, 600, 300, 670)).R < 240, "Stamp should show glyphs.");
        // The embedded font is a real Unicode font, not the Latin-only standard font.
        var fonts = DocumentSession.FromBytes(annotated).Query(d => d.Internals.GetAllObjects().OfType<PdfSharp.Pdf.PdfDictionary>()
            .Count(o => o.Elements.GetName("/Subtype") == "/Type0"));
        Assert.True(fonts >= 1);
    }

    static int DarkPixelsIn(PdfiumDocument doc, PdfRect rect)
    {
        var bmp = doc.Render(0, 1);
        var (x1, y1) = doc.PageToDevice(0, bmp.PixelWidth, bmp.PixelHeight, 0, rect.Left, rect.Top);
        var (x2, y2) = doc.PageToDevice(0, bmp.PixelWidth, bmp.PixelHeight, 0, rect.Right, rect.Bottom);
        int n = 0;
        for (int y = y1; y < y2; y++)
            for (int x = x1; x < x2; x++)
                if (bmp.Pixels[y * bmp.Stride + x * 4 + 1] < 128) n++;
        return n;
    }

    [Fact]
    public void Free_text_in_an_unsupported_script_is_refused_rather_than_garbled()
    {
        Assert.Throws<NotSupportedException>(() => DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            AnnotationService.AddFreeText(doc, 0, new PdfRect(100, 400, 400, 460), "नमस्ते")));
    }

    [Fact]
    public void Lists_and_removes_annotations()
    {
        var annotated = DocumentSession.Transform(TestPdf.Create(2), null, doc =>
        {
            AnnotationService.AddNote(doc, 0, 50, 50, "first", author: "Reviewer");
            AnnotationService.AddStamp(doc, 1, EmptyArea, "DRAFT");
            AnnotationService.AddTextMarkup(doc, 1, TextMarkupKind.Underline, [EmptyArea], PdfColor.Red);
        });

        var session = DocumentSession.FromBytes(annotated);
        var list = session.Query(AnnotationService.List);
        Assert.Equal(["Text", "Stamp", "Underline"], list.Select(a => a.Subtype));
        Assert.Equal("first", list[0].Contents);
        Assert.Equal("Reviewer", list[0].Author);
        Assert.All(list, a => Assert.NotNull(a.Id));

        session.Apply("Delete comment", doc => Assert.True(AnnotationService.Remove(doc, 1, 0)));
        Assert.Equal(["Text", "Underline"], session.Query(AnnotationService.List).Select(a => a.Subtype));
        session.Dispose();
    }

    [Fact]
    public void Annotations_survive_save_and_reload()
    {
        var dir = TestPdf.TempDir();
        var path = Path.Combine(dir, "doc.pdf");
        File.WriteAllBytes(path, TestPdf.Create(1));

        using (var session = DocumentSession.Open(path))
        {
            session.Apply("Note", doc => AnnotationService.AddNote(doc, 0, 100, 100, "persisted"));
            session.Save();
        }
        using var reopened = DocumentSession.Open(path);
        Assert.Equal("persisted", reopened.Query(AnnotationService.List).Single().Contents);
    }
}
