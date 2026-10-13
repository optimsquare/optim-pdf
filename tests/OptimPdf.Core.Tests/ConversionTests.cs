using System.Drawing;
using OptimPdf.Core.Conversion;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;

namespace OptimPdf.Core.Tests;

public class ConversionTests
{
    [Fact]
    public void Images_become_pages_sized_to_the_image()
    {
        var portrait = TestImages.Solid(192, 288, Color.Green);   // 2 x 3 inches at 96 DPI
        var landscape = TestImages.Solid(288, 96, Color.Blue);
        using var doc = ImageConversion.ImagesToPdf([portrait, landscape]);
        using var r = PdfiumDocument.Open(DocumentSession.Serialize(doc));
        Assert.Equal(2, r.PageCount);
        Assert.Equal(144, r.GetPageSize(0).Width, 1);
        Assert.Equal(216, r.GetPageSize(0).Height, 1);
        Assert.Equal(216, r.GetPageSize(1).Width, 1);
        var (red, green, blue) = TestPdf.AverageColor(r, 0, new PdfRect(10, 10, 130, 200));
        Assert.True(green > 100 && red < 40 && blue < 40);
    }

    [Theory]
    [InlineData(ImageFileFormat.Png)]
    [InlineData(ImageFileFormat.Tiff)]
    public void Page_size_uses_the_image_dpi(ImageFileFormat format)
    {
        // A 300 DPI letter-size scan must become a letter-size page (not 96 DPI-sized: 3× too large).
        using var bmp = new Bitmap(2550, 3300);
        bmp.SetResolution(300, 300);
        using var doc = ImageConversion.ImagesToPdf([ImageCodec.Encode(bmp, format)]);
        using var r = PdfiumDocument.Open(DocumentSession.Serialize(doc));
        Assert.Equal(612, r.GetPageSize(0).Width, 1);
        Assert.Equal(792, r.GetPageSize(0).Height, 1);
    }

    [Fact]
    public void Fits_images_on_a4_and_turns_page_for_landscape()
    {
        var landscape = TestImages.Solid(3000, 1000, Color.Red);
        using var doc = ImageConversion.ImagesToPdf([landscape], PageSizeMode.A4);
        using var r = PdfiumDocument.Open(DocumentSession.Serialize(doc));
        Assert.Equal(841.89, r.GetPageSize(0).Width, 1);
        Assert.Equal(595.28, r.GetPageSize(0).Height, 1);
        // Margins stay white.
        var (wr, _, _) = TestPdf.AverageColor(r, 0, new PdfRect(2, 2, 30, 30));
        Assert.True(wr > 250);
    }

    [Fact]
    public void Multi_page_tiff_becomes_one_page_per_frame()
    {
        var frames = new[] { Color.Red, Color.Green, Color.Blue }.Select(c =>
        {
            var b = new Bitmap(50, 50);
            using (var g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }).ToList();
        var tiff = ImageCodec.EncodeMultiPageTiff(frames);
        frames.ForEach(f => f.Dispose());

        using var doc = ImageConversion.ImagesToPdf([tiff]);
        Assert.Equal(3, doc.PageCount);
    }

    [Theory]
    [InlineData(ImageFileFormat.Png)]
    [InlineData(ImageFileFormat.Jpeg)]
    [InlineData(ImageFileFormat.Tiff)]
    public void Exports_pages_as_images_at_requested_dpi(ImageFileFormat format)
    {
        var dir = TestPdf.TempDir();
        using var r = PdfiumDocument.Open(TestPdf.Create(3));
        var files = ImageConversion.PdfToImages(r, [0, 2], 144, format, i => Path.Combine(dir, $"page{i + 1}.{format}"));
        Assert.Equal(2, files.Count);
        using var img = Image.FromFile(files[1]);
        Assert.Equal(1224, img.Width);   // 8.5 in × 144 DPI
        Assert.Equal(1584, img.Height);
        Assert.Equal(144, img.HorizontalResolution, 0.5);
    }

    [Fact]
    public void Exports_multi_page_tiff()
    {
        using var r = PdfiumDocument.Open(TestPdf.Create(3));
        var tiff = ImageConversion.PdfToMultiPageTiff(r, [0, 1, 2], 50);
        using var img = Image.FromStream(new MemoryStream(tiff));
        Assert.Equal(3, img.GetFrameCount(System.Drawing.Imaging.FrameDimension.Page));
    }

    [Fact]
    public void Round_trip_pdf_to_image_to_pdf_keeps_content_visible()
    {
        using var r = PdfiumDocument.Open(TestPdf.Create(1));
        using var bmp = ImageConversion.RenderPage(r, 0, 96);
        var png = ImageCodec.Encode(bmp, ImageFileFormat.Png);
        using var doc = ImageConversion.ImagesToPdf([png]);
        using var back = PdfiumDocument.Open(DocumentSession.Serialize(doc));
        Assert.Equal(612, back.GetPageSize(0).Width, 1);
        Assert.True(TestPdf.DarkPixelCount(back.Render(0, 1)) > 300);
    }
}
