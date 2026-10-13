using OptimPdf.Core.Annotations;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;

namespace OptimPdf.Core.Tests;

public class RtlTextTests
{
    [Fact]
    public void Arabic_letters_take_their_contextual_forms()
    {
        // س (initial) + لا (lam-alef, final) + م (isolated: alef does not join forward).
        Assert.Equal("ﺳﻼﻡ", RtlText.Shape("سلام"));
        // ب ي ت: initial, medial, final.
        Assert.Equal("ﺑﻴﺖ", RtlText.Shape("بيت"));
        // Right-joining letters break the word: د (isolated) ا (isolated)
        Assert.Equal("ﺩﺍ", RtlText.Shape("دا"));
        // Persian گ (initial) + ل (medial) + ه (final)
        Assert.Equal("ﮔﻠﻪ", RtlText.Shape("گله"));
    }

    [Fact]
    public void Bidi_orders_mixed_lines()
    {
        Assert.Equal("world םולש", RtlText.ToVisual("שלום world"));
        Assert.Equal("שלום world".Length, RtlText.ToVisual("שלום world").Length);
        // Numbers keep their left-to-right order inside right-to-left text.
        Assert.Equal("ןבל 250 ריחמ", RtlText.ToVisual("מחיר 250 לבן"));
        // An LTR paragraph with a Hebrew word: only the word is reversed.
        Assert.Equal("Hello םולש!", RtlText.ToVisual("Hello שלום!"));
        // Brackets are mirrored in right-to-left runs.
        Assert.Equal("(ב) א", RtlText.ToVisual("א (ב)"));
    }

    [Fact]
    public void Shaping_limits_are_reported()
    {
        Assert.False(ContentEditor.NeedsComplexShaping("مرحبا بالعالم"));
        Assert.False(ContentEditor.NeedsComplexShaping("שלום עולם"));
        Assert.False(ContentEditor.NeedsComplexShaping("سپاس گزارم")); // Persian
        Assert.True(ContentEditor.NeedsComplexShaping("नमस्ते"));      // Devanagari
        Assert.True(ContentEditor.NeedsComplexShaping("ٹوپی"));       // Urdu letter without presentation forms here
        Assert.Throws<NotSupportedException>(() => DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddText(doc, 0, 72, 700, "नमस्ते", new TextStyle())));
    }

    [Theory]
    [InlineData("مرحبا بالعالم")]
    [InlineData("שלום עולם")]
    [InlineData("الفاتورة رقم 1250 بتاريخ 2026")]
    public void Added_rtl_text_extracts_in_logical_order(string text)
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddText(doc, 0, 72, 700, text, new TextStyle("Arial", 20), maxWidth: 400));
        using var r = PdfiumDocument.Open(data);
        Assert.Equal(text, r.GetPageText(0).Trim());
        Assert.True(TestPdf.DarkPixelCount(r.Render(0, 1)) > 50);
    }

    [Fact]
    public void Rtl_paragraphs_are_right_aligned_in_their_box()
    {
        PdfRect bounds = default;
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            bounds = ContentEditor.AddText(doc, 0, 72, 700, "שלום", new TextStyle("Arial", 20), maxWidth: 400));
        using var r = PdfiumDocument.Open(data);
        Assert.Equal(472, bounds.Right, 1);
        // The ink is at the right end of the 400 pt box, not at its left.
        var right = TestPdf.AverageColor(r, 0, new PdfRect(400, 680, 472, 700));
        var left = TestPdf.AverageColor(r, 0, new PdfRect(72, 680, 300, 700));
        Assert.True(right.R < left.R - 10, $"right {right.R:F0}, left {left.R:F0}");
    }

    [Fact]
    public void Free_text_comment_accepts_arabic()
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            AnnotationService.AddFreeText(doc, 0, new PdfRect(100, 500, 400, 560), "ملاحظة مهمة"));
        using var r = PdfiumDocument.Open(data);
        Assert.True(TestPdf.DarkPixelCount(r.Render(0, 1)) > 30);
    }

    [Fact]
    public void Form_field_values_in_arabic_are_drawn_shaped()
    {
        // Created and filled in separate edits, as in the app (the writer's field list is read once per edit).
        var form = DocumentSession.Transform(TestPdf.Blank(), null, doc => Core.Forms.FormBuilder.AddTextField(doc, 0, "name", new PdfRect(100, 600, 400, 630)));
        var data = DocumentSession.Transform(form, null, doc => Core.Forms.FormService.SetValue(doc, "name", "محمد العلي"));
        using var r = PdfiumDocument.Open(data);
        // The widget appearance draws the value (shaped glyphs come from an Arabic-capable font).
        Assert.True(TestPdf.DarkPixelCount(r.Render(0, 2)) > 80);
        using var model = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(data), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal("محمد العلي", Core.Forms.FormService.ListFields(model)[0].Value);
        // Right-to-left values are right-aligned: ink sits in the right half of the field.
        var right = TestPdf.AverageColor(r, 0, new PdfRect(250, 602, 398, 628));
        var left = TestPdf.AverageColor(r, 0, new PdfRect(102, 602, 200, 628));
        Assert.True(right.R < left.R, $"right {right.R:F0} left {left.R:F0}");
    }

    [Fact]
    public void Existing_line_can_be_replaced_with_hebrew()
    {
        using var s = DocumentSession.FromBytes(TestPdf.Create(1));
        var line = ExistingTextEditor.FindLine(s.CurrentBytes, null, 0, TestPdf.TitleX + 20, TestPdf.TitleY + 8)!;
        ExistingTextEditor.Replace(s, line, "כותרת חדשה");
        Assert.Contains("כותרת חדשה", s.Renderer.GetPageText(0));
    }
}
