using System.IO.Compression;
using System.Text;
using OptimPdf.Core.Conversion;
using OptimPdf.Core.Rendering;

namespace OptimPdf.Core.Tests;

/// <summary>A test that needs a Microsoft Office application; reported as skipped where it is not installed.</summary>
public sealed class OfficeFactAttribute : FactAttribute
{
    public OfficeFactAttribute(OfficeApp app)
    {
        if (!OfficeConversion.IsInstalled(app)) Skip = $"Microsoft {app} is not installed.";
    }
}

/// <summary>Runs against the Microsoft Office installed on the test machine; skipped where an app is missing.</summary>
public class OfficeConversionTests
{

    /// <summary>A minimal .docx written by hand (WordprocessingML), so no Office is needed to create it.</summary>
    static void WriteDocx(string path, params string[] paragraphs)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string xml) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(xml); }
        Entry("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
        Entry("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
        var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"));
        Entry("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>""");
    }

    [OfficeFact(OfficeApp.Word)]
    public async Task Word_document_converts_to_pdf()
    {
        var dir = TestPdf.TempDir();
        var docx = Path.Combine(dir, "letter.docx");
        WriteDocx(docx, "Quarterly letter from Optim Square", "Ünïcødé and € work too.");
        var pdf = Path.Combine(dir, "letter.pdf");

        await OfficeConversion.ToPdfAsync(docx, pdf);

        using var doc = PdfiumDocument.Open(File.ReadAllBytes(pdf));
        var text = doc.GetPageText(0);
        Assert.Contains("Quarterly letter from Optim Square", text);
        Assert.Contains("Ünïcødé and € work too.", text);
    }

    [OfficeFact(OfficeApp.Excel)]
    public async Task Spreadsheet_converts_to_pdf()
    {
        var dir = TestPdf.TempDir();
        var csv = Path.Combine(dir, "sales.csv");
        File.WriteAllText(csv, "Region,Revenue\nNorth,1200\nSouth,3400\n");
        var pdf = Path.Combine(dir, "sales.pdf");

        await OfficeConversion.ToPdfAsync(csv, pdf);

        using var doc = PdfiumDocument.Open(File.ReadAllBytes(pdf));
        var text = doc.GetPageText(0);
        Assert.Contains("Revenue", text);
        Assert.Contains("3400", text);
    }

    [OfficeFact(OfficeApp.PowerPoint)]
    public async Task Presentation_converts_to_pdf()
    {
        var dir = TestPdf.TempDir();
        var pptx = Path.Combine(dir, "deck.pptx");
        CreatePresentation(pptx, "Roadmap 2027");
        var pdf = Path.Combine(dir, "deck.pdf");

        await OfficeConversion.ToPdfAsync(pptx, pdf);

        using var doc = PdfiumDocument.Open(File.ReadAllBytes(pdf));
        Assert.Contains("Roadmap 2027", doc.GetPageText(0));
    }

    [OfficeFact(OfficeApp.Word)]
    public async Task Pdf_converts_to_editable_word_document()
    {
        var dir = TestPdf.TempDir();
        var pdf = Path.Combine(dir, "source.pdf");
        File.WriteAllBytes(pdf, TestPdf.Create(1));
        var docx = Path.Combine(dir, "source.docx");

        await OfficeConversion.PdfToWordAsync(pdf, docx);

        using var zip = ZipFile.OpenRead(docx);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = reader.ReadToEnd();
        Assert.Contains("quick brown fox", xml);
    }

    [Fact]
    public async Task Unsupported_files_are_rejected()
    {
        var dir = TestPdf.TempDir();
        var path = Path.Combine(dir, "image.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        await Assert.ThrowsAsync<OfficeConversionException>(() => OfficeConversion.ToPdfAsync(path, Path.Combine(dir, "x.pdf")));
    }

    /// <summary>Creates a one-slide deck with PowerPoint itself (test setup only).</summary>
    static void CreatePresentation(string path, string title)
    {
        var t = new Thread(() =>
        {
            var before = System.Diagnostics.Process.GetProcessesByName("POWERPNT").Length;
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
            dynamic pres = app.Presentations.Add(0);
            dynamic slide = pres.Slides.Add(1, 1 /* ppLayoutTitle */);
            slide.Shapes[1].TextFrame.TextRange.Text = title;
            pres.SaveAs(path);
            pres.Close();
            if (before == 0) app.Quit();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }
}
