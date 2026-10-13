using OptimPdf.Core.Documents;
using OptimPdf.Core.Pages;
using OptimPdf.Core.Rendering;

namespace OptimPdf.Core.Tests;

public class PageOperationTests
{
    static byte[] Edit(byte[] data, Action<PdfSharp.Pdf.PdfDocument> edit) => DocumentSession.Transform(data, null, edit);

    static string[] Titles(params int[] pageNumbers) => pageNumbers.Select(TestPdf.Title).ToArray();

    [Fact]
    public void Rotates_pages_and_normalizes_angle()
    {
        var rotated = Edit(TestPdf.Create(2), doc => PageOperations.Rotate(doc, [0], 90));
        using (var r = PdfiumDocument.Open(rotated))
        {
            Assert.Equal(792, r.GetPageSize(0).Width, 1);
            Assert.Equal(612, r.GetPageSize(1).Width, 1);
        }
        var back = Edit(rotated, doc =>
        {
            PageOperations.Rotate(doc, [0], -90);
            Assert.Equal(0, doc.Pages[0].Rotate);
            PageOperations.Rotate(doc, [1], 450);
            Assert.Equal(90, doc.Pages[1].Rotate);
        });
        Assert.Throws<ArgumentException>(() => Edit(back, doc => PageOperations.Rotate(doc, [0], 45)));
    }

    [Fact]
    public void Deletes_pages_but_never_all()
    {
        var result = Edit(TestPdf.Create(5), doc => PageOperations.Delete(doc, [1, 3, 3]));
        Assert.Equal(Titles(1, 3, 5), TestPdf.PageTitles(result));
        Assert.Throws<InvalidOperationException>(() => Edit(TestPdf.Create(2), doc => PageOperations.Delete(doc, [0, 1])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Edit(TestPdf.Create(2), doc => PageOperations.Delete(doc, [2])));
    }

    [Fact]
    public void Reorders_and_moves_pages()
    {
        var reordered = Edit(TestPdf.Create(4), doc => PageOperations.Reorder(doc, [3, 1, 0, 2]));
        Assert.Equal(Titles(4, 2, 1, 3), TestPdf.PageTitles(reordered));

        var moved = Edit(TestPdf.Create(4), doc => PageOperations.Move(doc, 0, 3));
        Assert.Equal(Titles(2, 3, 4, 1), TestPdf.PageTitles(moved));

        Assert.Throws<ArgumentException>(() => Edit(TestPdf.Create(3), doc => PageOperations.Reorder(doc, [0, 0, 1])));
    }

    [Fact]
    public void Inserts_pages_from_another_document()
    {
        var other = TestPdf.Create(2);
        var result = Edit(TestPdf.Create(3), doc =>
        {
            using var source = PageOperations.OpenForImport(other);
            Assert.Equal(1, PageOperations.InsertFrom(doc, source, 1, [1]));
            PageOperations.InsertFrom(doc, source, doc.PageCount);
        });
        Assert.Equal(Titles(1, 2, 2, 3, 1, 2), TestPdf.PageTitles(result));
    }

    [Fact]
    public void Inserts_blank_page()
    {
        var result = Edit(TestPdf.Create(2), doc => PageOperations.InsertBlank(doc, 1, 595, 842));
        using var r = PdfiumDocument.Open(result);
        Assert.Equal(3, r.PageCount);
        Assert.Equal("", r.GetPageText(1).Trim());
        Assert.Equal(595, r.GetPageSize(1).Width, 1);
    }

    [Fact]
    public void Crops_pages_and_clamps_to_media_box()
    {
        var result = Edit(TestPdf.Create(1), doc => PageOperations.Crop(doc, [0], new PdfRect(50, 100, 700, 750)));
        using var r = PdfiumDocument.Open(result);
        Assert.Equal(612 - 50, r.GetPageSize(0).Width, 1);
        Assert.Equal(650, r.GetPageSize(0).Height, 1);
        Assert.Throws<ArgumentException>(() => Edit(TestPdf.Create(1), doc => PageOperations.Crop(doc, [0], new PdfRect(700, 0, 900, 100))));
    }

    [Fact]
    public void Merges_documents_in_order()
    {
        using var a = PageOperations.OpenForImport(TestPdf.Create(2));
        using var b = PageOperations.OpenForImport(TestPdf.Create(3));
        using var merged = PageOperations.Merge([a, b]);
        Assert.Equal(Titles(1, 2, 1, 2, 3), TestPdf.PageTitles(DocumentSession.Serialize(merged)));
    }

    [Fact]
    public void Extracts_and_splits()
    {
        using var source = PageOperations.OpenForImport(TestPdf.Create(5, title: "Source"));

        using var extracted = PageOperations.Extract(source, [4, 0]);
        Assert.Equal(Titles(5, 1), TestPdf.PageTitles(DocumentSession.Serialize(extracted)));
        Assert.Equal("Source", extracted.Info.Title);

        var parts = PageOperations.SplitEvery(source, 2);
        Assert.Equal([2, 2, 1], parts.Select(p => p.PageCount));
        Assert.Equal(Titles(5), TestPdf.PageTitles(DocumentSession.Serialize(parts[2])));

        var ranges = PageOperations.SplitByRanges(source, PageRange.Parse("1-2, 4-", 5));
        Assert.Equal(Titles(4, 5), TestPdf.PageTitles(DocumentSession.Serialize(ranges[1])));
    }

    [Theory]
    [InlineData("1", 10, "0-0")]
    [InlineData("1-3, 5", 10, "0-2;4-4")]
    [InlineData(" 8- ", 10, "7-9")]
    [InlineData("-2;9", 10, "0-1;8-8")]
    public void Parses_page_ranges(string text, int pageCount, string expected)
    {
        var ranges = PageRange.Parse(text, pageCount);
        Assert.Equal(expected, string.Join(";", ranges.Select(r => $"{r.First}-{r.Last}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("5-2")]
    [InlineData("a-b")]
    [InlineData("1-2-3")]
    public void Rejects_invalid_page_ranges(string text)
    {
        Assert.Throws<FormatException>(() => PageRange.Parse(text, 10));
    }
}
