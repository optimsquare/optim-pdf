using System.Drawing;
using OptimPdf.Core.Annotations;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Signatures;
using PdfSharp.Pdf.IO;

namespace OptimPdf.Core.Tests;

public class ObjectEditingTests
{
    static PdfRect TextBox(PdfiumDocument doc, string word)
    {
        var text = doc.GetPageText(0);
        int at = text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{word}' not on the page");
        return doc.GetTextRects(0, at, word.Length)[0];
    }

    [Fact]
    public void Added_multiline_text_is_selected_and_moved_as_one_item()
    {
        int id = ContentEditor.NewItemId();
        var data = DocumentSession.Transform(TestPdf.Create(1), null, doc =>
            ContentEditor.AddText(doc, 0, 100, 500, "First line\nSecond line", new TextStyle("Arial", 16), itemId: id));
        using var session = DocumentSession.FromBytes(data);

        var before = TextBox(session.Renderer, "Second");
        var item = PageObjectEditor.FindAt(session.CurrentBytes, null, 0, before.Left + 2, (before.Bottom + before.Top) / 2);
        Assert.NotNull(item);
        Assert.Equal(id, item.ItemId);
        Assert.True(item.ObjectIndices.Count >= 2, "Both lines belong to the item");

        PageObjectEditor.Move(session, item, 150, -200);

        var a = TextBox(session.Renderer, "First");
        var b = TextBox(session.Renderer, "Second");
        Assert.Equal(before.Left + 150, b.Left, 1);
        Assert.Equal(before.Bottom - 200, b.Bottom, 1);
        Assert.True(a.Bottom > b.Bottom); // Lines kept their arrangement.
        Assert.Contains(TestPdf.Title(1), session.Renderer.GetPageText(0)); // Other content untouched.
        // The tag survives the edit, so the item can be moved again.
        Assert.NotNull(PageObjectEditor.FindItem(session.CurrentBytes, null, 0, id));
        session.Undo();
        Assert.Equal(before.Left, TextBox(session.Renderer, "Second").Left, 1);
    }

    [Fact]
    public void Drawn_signature_moves_and_resizes_as_one_item()
    {
        int id = ContentEditor.NewItemId();
        var mark = DrawnSignature.FromStrokes([[new(0, 0), new(40, 20), new(80, 0)], [new(10, 30), new(70, 30)]]);
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
            VisualSignatureService.Place(doc, 0, 100, 300, 160, 60, mark, itemId: id));
        using var session = DocumentSession.FromBytes(data);

        var item = PageObjectEditor.FindItem(session.CurrentBytes, null, 0, id)!;
        Assert.Equal(2, item.ObjectIndices.Count); // Two strokes, one signature.
        var b0 = item.Bounds;

        PageObjectEditor.Move(session, item, 200, 100);
        var moved = PageObjectEditor.FindItem(session.CurrentBytes, null, 0, id)!;
        Assert.Equal(b0.Left + 200, moved.Bounds.Left, 1);
        Assert.Equal(b0.Top + 100, moved.Bounds.Top, 1);

        PageObjectEditor.Resize(session, moved, 2, (moved.Bounds.Left, moved.Bounds.Top));
        var resized = PageObjectEditor.FindItem(session.CurrentBytes, null, 0, id)!;
        Assert.Equal(moved.Bounds.Width * 2, resized.Bounds.Width, 0);
        Assert.Equal(moved.Bounds.Left, resized.Bounds.Left, 1); // Anchor stays put.
        Assert.Equal(moved.Bounds.Top, resized.Bounds.Top, 1);

        PageObjectEditor.Delete(session, resized);
        Assert.Null(PageObjectEditor.FindItem(session.CurrentBytes, null, 0, id));
        Assert.Equal(0, TestPdf.DarkPixelCount(session.Renderer.Render(0, 1)));
    }

    [Fact]
    public void Images_and_existing_text_can_be_selected_too()
    {
        var data = DocumentSession.Transform(TestPdf.Create(1), null, doc =>
            ContentEditor.AddImage(doc, 0, 300, 400, 100, 80, TestImages.Solid(50, 40, Color.Red)));
        using var session = DocumentSession.FromBytes(data);

        var image = PageObjectEditor.FindAt(session.CurrentBytes, null, 0, 350, 360)!;
        Assert.Equal(PageObjectKind.Image, image.Kind);
        Assert.Equal(100, image.Bounds.Width, 1);

        // Text that Optim PDF did not add: a single text run is selectable.
        var title = TextBox(session.Renderer, "Optim");
        var run = PageObjectEditor.FindAt(session.CurrentBytes, null, 0, title.Left + 2, (title.Bottom + title.Top) / 2)!;
        Assert.Equal(PageObjectKind.Text, run.Kind);
        Assert.Equal(0, run.ItemId);
        PageObjectEditor.Move(session, run, 0, -100);
        Assert.Equal(title.Bottom - 100, TextBox(session.Renderer, "Optim").Bottom, 1);
    }

    [Fact]
    public void Empty_area_and_page_backgrounds_are_not_selected()
    {
        var data = DocumentSession.Transform(TestPdf.Create(1), null, doc =>
            Writing.PdfObjects.AppendPageContent(doc.Pages[0], new Writing.ContentBuilder().FillColor(new Writing.PdfColor(0.95, 0.95, 0.9)).Rect(0, 0, 612, 792).Fill()));
        Assert.Null(PageObjectEditor.FindAt(data, null, 0, 400, 100));
    }

    [Fact]
    public void Stale_selection_is_refused()
    {
        int id = ContentEditor.NewItemId();
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc => ContentEditor.AddText(doc, 0, 100, 500, "Item", new TextStyle(), itemId: id));
        using var session = DocumentSession.FromBytes(data);
        var item = PageObjectEditor.FindItem(session.CurrentBytes, null, 0, id)!;
        PageObjectEditor.Delete(session, item);
        Assert.Throws<InvalidOperationException>(() => PageObjectEditor.Move(session, item, 10, 10));
    }

    [Fact]
    public void Comments_and_stamps_move_and_resize()
    {
        var data = DocumentSession.Transform(TestPdf.Blank(), null, doc =>
        {
            AnnotationService.AddStamp(doc, 0, new PdfRect(100, 600, 270, 656), "APPROVED");
            AnnotationService.AddInk(doc, 0, [[(300, 300), (350, 350)]], Writing.PdfColor.Blue);
        });
        var moved = DocumentSession.Transform(data, null, doc =>
        {
            AnnotationService.Move(doc, 0, 0, 50, -100);
            AnnotationService.Resize(doc, 0, 1, 2, (300, 300));
        });
        using var model = PdfReader.Open(new MemoryStream(moved), PdfDocumentOpenMode.Import);
        var list = AnnotationService.List(model);
        var stamp = list.Single(a => a.Subtype == "Stamp").Rect;
        Assert.Equal(150, stamp.Left, 1);
        Assert.Equal(500, stamp.Bottom, 1);
        var ink = model.Pages[0].Annotations[1].Elements.GetArray("/InkList")!.Elements[0] as PdfSharp.Pdf.PdfArray;
        Assert.Equal(400, ink!.Elements.GetReal(2), 1); // (350,350) scaled ×2 about (300,300).
        using var r = PdfiumDocument.Open(moved);
        // The stamp is drawn at its new place (its appearance follows the rectangle).
        var at = TestPdf.AverageColor(r, 0, new PdfRect(152, 502, 318, 554));
        Assert.True(at.R < 245 || at.G < 245 || at.B < 245, $"Stamp not drawn at its new place: {at}");
        var old = TestPdf.AverageColor(r, 0, new PdfRect(102, 602, 148, 654));
        Assert.True(old.R > 250 && old.G > 250 && old.B > 250, "Stamp still drawn at its old place");
    }
}
