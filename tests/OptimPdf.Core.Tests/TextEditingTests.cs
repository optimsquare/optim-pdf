using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;

namespace OptimPdf.Core.Tests;

public class TextEditingTests
{
    static TextLine Find(DocumentSession s, double x, double y) =>
        ExistingTextEditor.FindLine(s.CurrentBytes, s.Password, 0, x, y) ?? throw new Xunit.Sdk.XunitException($"No text at {x},{y}");

    [Fact]
    public void Finds_a_line_with_its_font_size_position_and_family()
    {
        using var s = DocumentSession.FromBytes(TestPdf.Create(1));
        var line = Find(s, TestPdf.TitleX + 20, TestPdf.TitleY + 8);

        Assert.Equal(TestPdf.Title(1), line.Text);
        Assert.Equal(TestPdf.TitleSize, line.FontSize, 1);
        Assert.Equal(TestPdf.TitleX, line.Matrix.E, 1);
        Assert.Equal(TestPdf.TitleY, line.Matrix.F, 1);
        Assert.Equal("Arial", line.Family); // Helvetica is matched to its installed equivalent.
        Assert.False(line.Bold);
    }

    [Fact]
    public void Replacing_a_line_changes_only_that_line()
    {
        using var s = DocumentSession.FromBytes(TestPdf.Create(1));
        var line = Find(s, TestPdf.TitleX + 20, TestPdf.TitleY + 8);

        ExistingTextEditor.Replace(s, line, "Quarterly report — €1,250");

        var text = s.Renderer.GetPageText(0);
        Assert.Contains("Quarterly report — €1,250", text);
        Assert.DoesNotContain("Optim test", text);
        Assert.Contains("The quick brown fox jumps over the lazy dog.", text);
        // Same baseline, start and size.
        var edited = Find(s, TestPdf.TitleX + 20, TestPdf.TitleY + 8);
        Assert.Equal("Quarterly report — €1,250", edited.Text);
        Assert.Equal(TestPdf.TitleSize, edited.FontSize, 1);
        Assert.Equal(TestPdf.TitleX, edited.Matrix.E, 1);
        Assert.Equal(TestPdf.TitleY, edited.Matrix.F, 1);
        // The old text is gone from the file, not hidden.
        Assert.DoesNotContain("Optim test", TestPdf.RawContent(s.CurrentBytes));

        s.Undo();
        Assert.Contains(TestPdf.Title(1), s.Renderer.GetPageText(0));
    }

    [Fact]
    public void Separate_runs_on_one_baseline_form_one_line()
    {
        double hello = ContentBuilder.MeasureText("Hello ", StandardFont.Helvetica, 18);
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            PdfObjects.AppendPageContent(doc.Pages[0], new ContentBuilder().FillColor(PdfColor.Black)
                .Text(100, 500, StandardFont.Helvetica, 18, "Hello")
                .Text(100 + hello, 500, StandardFont.Helvetica, 18, "World")
                .Text(100, 450, StandardFont.Helvetica, 18, "Next line")));
        using var s = DocumentSession.FromBytes(data);

        var line = Find(s, 105, 505);
        Assert.Equal("Hello World", line.Text);
        Assert.Equal(2, line.ObjectIndices.Count);

        ExistingTextEditor.Replace(s, line, "Goodbye");
        var text = s.Renderer.GetPageText(0);
        Assert.Contains("Goodbye", text);
        Assert.DoesNotContain("World", text);
        Assert.Contains("Next line", text);
    }

    [Fact]
    public void Color_bold_and_installed_font_are_kept()
    {
        var red = new PdfColor(0.8, 0, 0);
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddText(doc, 0, 100, 600, "Invoice total", new TextStyle("Times New Roman", 20, red, Bold: true)));
        using var s = DocumentSession.FromBytes(data);

        var line = Find(s, 120, 585);
        Assert.Equal("Invoice total", line.Text);
        Assert.Equal("Times New Roman", line.Family);
        Assert.True(line.FontInstalled);
        Assert.True(line.Bold);
        Assert.Equal(0.8, line.Color.R, 1);

        ExistingTextEditor.Replace(s, line, "Invoice total: $480");
        var edited = Find(s, 120, 585);
        Assert.Equal("Invoice total: $480", edited.Text);
        Assert.Equal("Times New Roman", edited.Family);
        Assert.True(edited.Bold);
        Assert.Equal(0.8, edited.Color.R, 1);
        Assert.Equal(0, edited.Color.G, 1);
    }

    [Fact]
    public void Text_on_a_rotated_page_is_redrawn_in_place()
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            doc.Pages[0].Rotate = 90;
            ContentEditor.AddText(doc, 0, 300, 200, "Sideways label", new TextStyle("Arial", 16));
        });
        using var s = DocumentSession.FromBytes(data);
        int at = s.Renderer.GetPageText(0).IndexOf("Sideways", StringComparison.Ordinal);
        var box = s.Renderer.GetTextRects(0, at, 1)[0];
        var line = Find(s, (box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2);
        Assert.Equal("Sideways label", line.Text);

        ExistingTextEditor.Replace(s, line, "Turned label");

        var text = s.Renderer.GetPageText(0);
        Assert.Contains("Turned label", text);
        var newBox = s.Renderer.GetTextRects(0, text.IndexOf("Turned", StringComparison.Ordinal), 1)[0];
        // Same orientation (glyph box taller than wide along the same axis) and start position.
        Assert.Equal(box.Width > box.Height, newBox.Width > newBox.Height);
        Assert.InRange(Math.Abs(newBox.Left - box.Left) + Math.Abs(newBox.Top - box.Top), 0, 4);
    }

    [Fact]
    public void Empty_text_deletes_the_line()
    {
        using var s = DocumentSession.FromBytes(TestPdf.Create(1));
        ExistingTextEditor.Replace(s, Find(s, TestPdf.TitleX + 20, TestPdf.TitleY + 8), "");
        Assert.DoesNotContain("Optim test", s.Renderer.GetPageText(0));
        Assert.Contains("quick brown fox", s.Renderer.GetPageText(0));
    }

    [Fact]
    public void No_line_where_there_is_no_text()
    {
        using var s = DocumentSession.FromBytes(TestPdf.Create(1));
        Assert.Null(ExistingTextEditor.FindLine(s.CurrentBytes, null, 0, 400, 100));
    }
}
