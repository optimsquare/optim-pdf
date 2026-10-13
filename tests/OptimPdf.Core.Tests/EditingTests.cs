using System.Drawing;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Pages;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;

namespace OptimPdf.Core.Tests;

public class EditingTests
{
    [Fact]
    public void Adds_text_at_the_requested_position()
    {
        var result = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            var bounds = ContentEditor.AddText(doc, 0, 72, 500, "Invoice approved", new TextStyle("Arial", 18));
            Assert.Equal(72, bounds.Left, 1);
            Assert.Equal(500, bounds.Top, 1);
        });
        using var r = PdfiumDocument.Open(result);
        Assert.Contains("Invoice approved", r.GetPageText(0));
        var hit = r.Search("Invoice").Single();
        var rect = hit.Rects[0];
        // Glyphs hang below the anchor (top-left) by roughly one line.
        Assert.InRange(rect.Left, 72, 80);
        Assert.InRange(rect.Top, 480, 500.5);
        Assert.InRange(rect.Bottom, 470, 500);
    }

    [Theory]
    [InlineData("Café Ñandú – 25 €")]
    [InlineData("Größe ÄÖÜ ß")]
    [InlineData("Привет мир")]
    [InlineData("Γειά σου")]
    [InlineData("你好，世界")]
    [InlineData("こんにちは")]
    [InlineData("안녕하세요")]
    public void Adds_unicode_text_that_extracts_back_exactly(string text)
    {
        var result = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddText(doc, 0, 72, 700, text, new TextStyle(Size: 16)));
        using var r = PdfiumDocument.Open(result);
        Assert.Contains(text, r.GetPageText(0).Replace("\r", "").Replace("\n", ""));
    }

    [Fact]
    public void Rejects_scripts_that_need_a_shaping_engine_instead_of_writing_them_wrongly()
    {
        // Arabic and Hebrew are shaped since phase 3 (see RtlTextTests); Indic scripts are still refused.
        Assert.Throws<NotSupportedException>(() => DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddText(doc, 0, 72, 700, "নমস্কার", new TextStyle())));
        Assert.Throws<NotSupportedException>(() => DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddText(doc, 0, 72, 700, "வணக்கம்", new TextStyle())));
    }

    [Fact]
    public void Wraps_text_to_a_maximum_width()
    {
        var result = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            var b = ContentEditor.AddText(doc, 0, 72, 700, "one two three four five six seven eight nine ten", new TextStyle("Arial", 12), maxWidth: 100);
            Assert.True(b.Width <= 100.5);
            Assert.True(b.Height > 40, "Expected several lines.");
        });
        using var r = PdfiumDocument.Open(result);
        Assert.Contains("ten", r.GetPageText(0));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Text_on_rotated_pages_reads_upright_on_screen(int rotate)
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc => PageOperations.Rotate(doc, [0], rotate));
        using (var before = PdfiumDocument.Open(data))
        {
            // Anchor: 100 pt from the displayed left edge, 120 pt from the displayed top.
            var bmp = before.Render(0, 1);
            var (ax, ay) = before.DeviceToPage(0, bmp.PixelWidth, bmp.PixelHeight, 0, 100, 120);
            data = DocumentSession.Transform(data, null, doc => ContentEditor.AddText(doc, 0, ax, ay, "Upright text", new TextStyle("Arial", 20)));
        }
        using var r = PdfiumDocument.Open(data);
        var page = r.Render(0, 1);
        var rect = r.Search("Upright").Single().Rects[0];
        var a = r.PageToDevice(0, page.PixelWidth, page.PixelHeight, 0, rect.Left, rect.Top);
        var b = r.PageToDevice(0, page.PixelWidth, page.PixelHeight, 0, rect.Right, rect.Bottom);
        double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        Assert.True(dx > dy * 2, $"Text should run horizontally on screen (dx={dx}, dy={dy}).");
        // It starts near the anchor on screen.
        Assert.InRange(Math.Min(a.X, b.X), 95, 110);
        Assert.InRange(Math.Min(a.Y, b.Y), 115, 150);
    }

    [Fact]
    public void Adds_png_image_scaled_to_box()
    {
        var png = TestImages.Solid(40, 20, Color.Red);
        var result = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddImage(doc, 0, 100, 700, 200, 100, png));
        using var r = PdfiumDocument.Open(result);
        var (red, green, blue) = TestPdf.AverageColor(r, 0, new PdfRect(105, 605, 295, 695));
        Assert.True(red > 200 && green < 60 && blue < 60, $"Expected red, got {red:0},{green:0},{blue:0}");
        var (wr, wg, wb) = TestPdf.AverageColor(r, 0, new PdfRect(320, 605, 400, 695));
        Assert.True(wr > 250 && wg > 250 && wb > 250, "Area beside the image stays white.");
    }

    [Fact]
    public void Embeds_jpeg_without_recompressing()
    {
        var jpeg = TestImages.PhotoBytes(300, 200, ImageFileFormat.Jpeg, 80);
        var result = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddImage(doc, 0, 50, 750, 300, 200, jpeg));
        // The exact JPEG bytes appear in the file: no generation loss.
        Assert.True(IndexOf(result, jpeg.AsSpan(0, 64)) >= 0);
    }

    [Fact]
    public void Image_on_rotated_page_lands_in_returned_bounds()
    {
        var png = TestImages.Solid(10, 10, Color.Blue);
        PdfRect bounds = default;
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            PageOperations.Rotate(doc, [0], 90);
            bounds = ContentEditor.AddImage(doc, 0, 200, 300, 150, 80, png);
        });
        Assert.Equal(150, Math.Max(bounds.Width, bounds.Height), 1);
        using var r = PdfiumDocument.Open(data);
        var inner = new PdfRect(bounds.Left + 5, bounds.Bottom + 5, bounds.Right - 5, bounds.Top - 5);
        var (red, _, blue) = TestPdf.AverageColor(r, 0, inner);
        Assert.True(blue > 200 && red < 60);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Upright_box_inverts_placement_bounds(int rotate)
    {
        var png = TestImages.Solid(10, 10, Color.Green);
        DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            PageOperations.Rotate(doc, [0], rotate);
            var bounds = ContentEditor.AddImage(doc, 0, 200, 400, 120, 60, png);
            var (x, y, w, h) = ContentEditor.UprightBox(bounds, rotate);
            Assert.Equal((200.0, 400.0, 120.0, 60.0), (Math.Round(x, 3), Math.Round(y, 3), Math.Round(w, 3), Math.Round(h, 3)));
        });
    }

    [Fact]
    public void Lists_installed_fonts_with_display_names()
    {
        var families = ContentEditor.InstalledFamilies.ToList();
        Assert.Contains("Arial", families);
        Assert.Contains("Segoe UI", families);
    }

    [Fact]
    public void Rejects_undecodable_image_data()
    {
        Assert.Throws<InvalidDataException>(() => DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddImage(doc, 0, 100, 700, 100, 100, "not an image"u8.ToArray())));
    }

    [Fact]
    public void Picks_fonts_that_really_contain_the_glyphs()
    {
        Assert.Equal("Microsoft YaHei", ContentEditor.FontFor("中文", "Segoe UI"));
        Assert.Equal("Yu Gothic", ContentEditor.FontFor("ひらがな", "Segoe UI"));
        Assert.Equal("Segoe UI", ContentEditor.FontFor("Hello", "Segoe UI"));
        Assert.Equal("Arial", ContentEditor.FontFor("Hello", "Arial"));
        // Arial has no check mark; a font that does is chosen instead of drawing an empty box.
        var family = ContentEditor.FontFor("Done ✓", "Arial");
        Assert.NotEqual("Arial", family);
        Assert.Equal(6, ContentEditor.CoveredCount(family, "Done ✓"));
    }

    [Fact]
    public void Common_windows_fonts_allow_embedding()
    {
        foreach (var family in new[] { "Arial", "Segoe UI", "Segoe Script", "Microsoft YaHei" })
            Assert.True(ContentEditor.IsEmbeddable(family), family);
        Assert.False(ContentEditor.IsEmbeddable("No Such Font Family"));
    }

    [Fact]
    public void Reads_font_character_maps()
    {
        Assert.True(ContentEditor.CoveredCount("Arial", "ABCxyz123éü") == 11);
        Assert.True(ContentEditor.CoveredCount("Arial", "中") == 0);
        Assert.Equal(-1, ContentEditor.CoveredCount("No Such Font Family", "A"));
    }

    static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
}
