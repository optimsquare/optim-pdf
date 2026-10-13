using OptimPdf.Core.Documents;
using OptimPdf.Core.Forms;
using OptimPdf.Core.Rendering;
using PdfSharp.Pdf.IO;

namespace OptimPdf.Core.Tests;

public class FormBuilderTests
{
    static IReadOnlyList<FormFieldInfo> Fields(byte[] data)
    {
        using var doc = PdfReader.Open(new MemoryStream(data), PdfDocumentOpenMode.Import);
        return FormService.ListFields(doc);
    }

    [Fact]
    public void Creates_every_field_kind_and_they_can_be_filled()
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            FormBuilder.AddTextField(doc, 0, "Name", new PdfRect(72, 700, 300, 720));
            FormBuilder.AddCheckBox(doc, 0, "Agree", new PdfRect(72, 660, 86, 674));
            FormBuilder.AddComboBox(doc, 0, "Country", new PdfRect(72, 620, 250, 640), ["UAE", "USA", "France"]);
            FormBuilder.AddSignatureField(doc, 0, "Sign here", new PdfRect(72, 520, 280, 580));
        });
        var fields = Fields(data);
        Assert.Equal([FormFieldKind.Text, FormFieldKind.CheckBox, FormFieldKind.ComboBox, FormFieldKind.Signature], fields.Select(f => f.Kind));
        Assert.Equal("UAE", fields[2].Value);
        Assert.Equal(["UAE", "USA", "France"], fields[2].Options);

        var filled = DocumentSession.Transform(data, null, doc =>
        {
            FormService.SetValue(doc, "Name", "Jamie Rivera");
            FormService.SetValue(doc, "Agree", "true");
            FormService.SetValue(doc, "Country", "France");
        });
        var after = Fields(filled);
        Assert.Equal("Jamie Rivera", after[0].Value);
        Assert.Equal("true", after[1].Value);
        Assert.Equal("France", after[2].Value);
        using var r = PdfiumDocument.Open(filled);
        Assert.True(TestPdf.DarkPixelCount(r.Render(0, 1)) > 100); // Appearances are drawn.
    }

    [Fact]
    public void Empty_new_fields_are_visible_on_the_page()
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            FormBuilder.AddTextField(doc, 0, "Empty", new PdfRect(100, 600, 300, 624));
            FormBuilder.AddComboBox(doc, 0, "Pick", new PdfRect(100, 500, 300, 524), ["A", "B"]);
        });
        using var r = PdfiumDocument.Open(data);
        var inside = TestPdf.AverageColor(r, 0, new PdfRect(110, 604, 290, 620));
        Assert.True(inside.B > inside.R + 5, $"Field background not drawn: {inside}"); // Light blue tint.
        var edge = TestPdf.AverageColor(r, 0, new PdfRect(100, 600, 101, 624));
        Assert.True(edge.R < 200, $"Field border not drawn: {edge}");
    }

    [Fact]
    public void Removes_a_field_and_its_widget()
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            FormBuilder.AddTextField(doc, 0, "Keep", new PdfRect(72, 700, 300, 720));
            FormBuilder.AddTextField(doc, 0, "Drop", new PdfRect(72, 600, 300, 620), "secret value");
        });
        var removed = DocumentSession.Transform(data, null, doc => Assert.True(FormBuilder.RemoveField(doc, "Drop")));
        Assert.Equal(["Keep"], Fields(removed).Select(f => f.Name));
        Assert.DoesNotContain("secret value", TestPdf.RawContent(removed));
        using var doc2 = PdfReader.Open(new MemoryStream(removed), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc2.Pages[0].Elements.GetArray("/Annots")!.Elements.Count);
    }
}
