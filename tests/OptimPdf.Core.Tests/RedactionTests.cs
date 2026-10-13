using System.Drawing;
using OptimPdf.Core.Annotations;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Forms;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Security;
using OptimPdf.Core.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OptimPdf.Core.Tests;

public class RedactionTests
{
    const double Size = 14;

    /// <summary>A letter page with one text run per line: (y, text).</summary>
    static byte[] Lines(int rotate, params (double Y, string Text)[] lines) => DocumentSession.Transform(TestPdf.Blank(), null, doc =>
    {
        var page = doc.Pages[0];
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
        var content = new ContentBuilder().FillColor(PdfColor.Black);
        foreach (var (y, text) in lines) content.Text(72, y, StandardFont.Helvetica, Size, text);
        PdfObjects.AppendPageContent(page, content);
        if (rotate != 0) page.Rotate = rotate;
    });

    /// <summary>The user-space box of <paramref name="part"/> within a Helvetica line drawn at (72, y).</summary>
    static PdfRect Box(string line, string part, double y)
    {
        int at = line.IndexOf(part, StringComparison.Ordinal);
        double x1 = 72 + ContentBuilder.MeasureText(line[..at], StandardFont.Helvetica, Size);
        double x2 = 72 + ContentBuilder.MeasureText(line[..(at + part.Length)], StandardFont.Helvetica, Size);
        return new PdfRect(x1 - 1, y - 4, x2 + 1, y + Size);
    }

    const string NameLine = "Name: Jane Doe";
    const string AccountLine = "Account 1234-5678";
    const string PublicLine = "This public line stays searchable.";

    static DocumentSession Sample(int rotate = 0) =>
        DocumentSession.FromBytes(Lines(rotate, (700, NameLine), (650, AccountLine), (600, PublicLine)));

    [Fact]
    public void Redacted_text_is_gone_from_the_file_and_the_text_layer()
    {
        using var session = Sample();
        var areas = new[]
        {
            new RedactionArea(0, Box(NameLine, "Jane Doe", 700)),
            new RedactionArea(0, Box(AccountLine, AccountLine, 650)),
        };
        var report = Redactor.Redact(session, areas);

        Assert.Equal(2, report.ObjectsRemoved);
        Assert.Equal(1, report.RegionsRasterized); // Only the name line extends past its area.

        var text = session.Renderer.GetPageText(0);
        Assert.DoesNotContain("Jane", text);
        Assert.DoesNotContain("1234", text);
        Assert.Contains(PublicLine, text); // Untouched lines keep real text.

        // Independent check of the bytes, including inside every compressed stream.
        var raw = TestPdf.RawContent(session.CurrentBytes);
        Assert.DoesNotContain("Jane", raw);
        Assert.DoesNotContain("Doe", raw);
        Assert.DoesNotContain("1234", raw);
    }

    [Fact]
    public void Redaction_draws_boxes_and_keeps_the_visible_remainder()
    {
        using var session = Sample();
        using var before = PdfiumDocument.Open(session.CurrentBytes);
        var nameArea = Box(NameLine, "Jane Doe", 700);
        var label = Box(NameLine, "Name:", 700);
        double labelInkBefore = Ink(before, label);

        Redactor.Redact(session, [new RedactionArea(0, nameArea)]);

        var (r, g, b) = TestPdf.AverageColor(session.Renderer, 0, Inset(nameArea, 2));
        Assert.True(r < 10 && g < 10 && b < 10, $"Redaction box not black: {r:F0},{g:F0},{b:F0}");
        // "Name:" was part of the removed run; it must still look the same (now as an image).
        double labelInkAfter = Ink(session.Renderer, label);
        Assert.InRange(labelInkAfter, labelInkBefore * 0.8, labelInkBefore * 1.25);
        Assert.DoesNotContain("Name:", session.Renderer.GetPageText(0));
    }

    [Fact]
    public void Redaction_on_rotated_page_keeps_remainder_in_place()
    {
        using var session = Sample(rotate: 90);
        using var before = PdfiumDocument.Open(session.CurrentBytes);
        var label = Box(NameLine, "Name:", 700);
        double inkBefore = Ink(before, label);
        Assert.True(inkBefore > 0.05);

        Redactor.Redact(session, [new RedactionArea(0, Box(NameLine, "Jane Doe", 700))]);

        Assert.DoesNotContain("Jane", TestPdf.RawContent(session.CurrentBytes));
        double inkAfter = Ink(session.Renderer, label);
        var dbg = Environment.GetEnvironmentVariable("OPTIM_DEBUG_DIR");
        if (dbg is not null)
        {
            File.WriteAllBytes(Path.Combine(dbg, "rot-before.png"), ImageCodec.Encode(ImageCodec.FromRendered(before.Render(0, 1), 72), ImageFileFormat.Png, 90));
            File.WriteAllBytes(Path.Combine(dbg, "rot-after.png"), ImageCodec.Encode(ImageCodec.FromRendered(session.Renderer.Render(0, 1), 72), ImageFileFormat.Png, 90));
        }
        Assert.True(inkAfter > inkBefore * 0.8 && inkAfter < inkBefore * 1.25, $"before {inkBefore:F3}, after {inkAfter:F3}");
    }

    [Fact]
    public void Image_pixels_under_the_area_are_removed()
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            ContentEditor.AddImage(doc, 0, 100, 500, 200, 200, TestImages.Solid(200, 200, Color.FromArgb(0, 160, 0))));
        using var session = DocumentSession.FromBytes(data);
        var area = new PdfRect(180, 380, 220, 420); // Center of the image.

        var report = Redactor.Redact(session, [new RedactionArea(0, area)]);
        Assert.Equal(1, report.RegionsRasterized);

        // The original 200Ã—200 image is no longer in the file; the remainder is a new rendering.
        using var model = PdfReader.Open(new MemoryStream(session.CurrentBytes), PdfDocumentOpenMode.Import);
        var images = model.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Elements.GetName("/Subtype") == "/Image").ToList();
        Assert.DoesNotContain(images, i => i.Elements.GetInteger("/Width") == 200 && i.Elements.GetInteger("/Height") == 200);

        var center = TestPdf.AverageColor(session.Renderer, 0, Inset(area, 2));
        Assert.True(center.R < 10 && center.G < 10 && center.B < 10);
        var corner = TestPdf.AverageColor(session.Renderer, 0, new PdfRect(110, 310, 140, 340));
        Assert.True(corner.G > 130 && corner.R < 40, $"Image remainder lost: {corner}");
    }

    [Fact]
    public void Annotations_and_fields_in_the_area_are_removed()
    {
        var data = DocumentSession.Transform(Lines(0, (700, NameLine)), null, doc =>
        {
            AnnotationService.AddNote(doc, 0, 300, 705, "Jane's home address is 12 Elm St");
            AnnotationService.AddNote(doc, 0, 500, 300, "Unrelated note");
            FormBuilder.AddTextField(doc, 0, "ssn", new PdfRect(300, 640, 450, 660), "987-65-4320");
            FormBuilder.AddTextField(doc, 0, "city", new PdfRect(300, 200, 450, 220), "Springfield");
        });
        using var session = DocumentSession.FromBytes(data);

        var report = Redactor.Redact(session, [new RedactionArea(0, new PdfRect(290, 630, 460, 720))]);

        Assert.Equal(2, report.AnnotationsRemoved);
        Assert.Equal(1, report.FieldsRemoved);
        var raw = TestPdf.RawContent(session.CurrentBytes);
        Assert.DoesNotContain("Elm St", raw);
        Assert.DoesNotContain("987-65", raw);
        Assert.Contains("Unrelated note", raw);
        var fields = session.Query(FormService.ListFields);
        Assert.Equal(["city"], fields.Select(f => f.Name));
    }

    [Fact]
    public void Encrypted_document_stays_encrypted_and_undo_restores()
    {
        const string owner = "own3r", open = "op3n";
        var data = DocumentSession.Transform(Lines(0, (700, NameLine), (600, PublicLine)), null, doc =>
            SecurityService.Protect(doc, new ProtectionOptions(open, owner, new PdfPermissions(), EncryptionStrength.Aes256)));
        using var session = DocumentSession.FromBytes(data, password: owner);
        session.ProvideOpenPassword(open);

        Redactor.Redact(session, [new RedactionArea(0, Box(NameLine, "Jane Doe", 700))]);

        Assert.Throws<PdfPasswordRequiredException>(() => PdfiumDocument.Open(session.CurrentBytes));
        using (var reopened = PdfiumDocument.Open(session.CurrentBytes, open))
        {
            Assert.Equal(6, reopened.SecurityRevision);
            Assert.DoesNotContain("Jane", reopened.GetPageText(0));
            Assert.Contains(PublicLine, reopened.GetPageText(0));
        }

        session.Undo();
        Assert.Contains("Jane", session.Renderer.GetPageText(0));
    }

    static PdfRect Inset(PdfRect r, double by) => new(r.Left + by, r.Bottom + by, r.Right - by, r.Top - by);

    /// <summary>Darkness (0 = white, 1 = black) of a user-space area as displayed.</summary>
    static double Ink(PdfiumDocument doc, PdfRect rect)
    {
        var (r, g, b) = TestPdf.AverageColor(doc, 0, rect);
        return 1 - (r + g + b) / (3 * 255.0);
    }
}

