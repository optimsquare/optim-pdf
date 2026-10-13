using System.Runtime.CompilerServices;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Rendering;
using PDFiumCore;

namespace OptimPdf.Core.Editing;

/// <summary>
/// Page content that can be selected and moved: an item added by Optim PDF (text box, image or signature,
/// possibly made of many objects, kept together by its <see cref="ContentEditor.ItemTag"/> id), or a single
/// object of any other page content (a text run, an image, a drawing).
/// </summary>
public sealed record PageItem(int PageIndex, IReadOnlyList<int> ObjectIndices, PdfRect Bounds, PageObjectKind Kind, int ItemId)
{
    public bool IsOptimItem => ItemId != 0;
}

/// <summary>Selects, moves, resizes and deletes page content through PDFium's object model.</summary>
public static class PageObjectEditor
{
    /// <summary>The item under a point (user space), or null. Topmost wins; large background shapes are skipped.</summary>
    public static PageItem? FindAt(byte[] data, string? password, int pageIndex, double x, double y, double tolerance = 2) =>
        PdfiumEditor.Run(data, password, editor =>
        {
            var page = editor.Page(pageIndex);
            var objects = page.Objects();
            double pageArea = page.Width * page.Height;
            var ids = objects.Select(o => ItemIdOf(page.ObjectAt(o.Index))).ToList();

            PageObjectInfo? hit = null;
            for (int i = objects.Count - 1; i >= 0; i--)
            {
                var o = objects[i];
                var b = o.Bounds;
                if (x < b.Left - tolerance || x > b.Right + tolerance || y < b.Bottom - tolerance || y > b.Top + tolerance) continue;
                // A shape covering most of the page is a background: selecting it by accident is worse than not selecting.
                if (ids[i] == 0 && o.Kind is PageObjectKind.Path or PageObjectKind.Shading && b.Width * b.Height > pageArea * 0.25) continue;
                hit = o;
                break;
            }
            if (hit is null) return null;

            int id = ids[hit.Index];
            var members = id == 0 ? [hit] : objects.Where(o => ids[o.Index] == id).ToList();
            var bounds = members.Select(m => m.Bounds).Aggregate((p, q) =>
                new PdfRect(Math.Min(p.Left, q.Left), Math.Min(p.Bottom, q.Bottom), Math.Max(p.Right, q.Right), Math.Max(p.Top, q.Top)));
            var kind = members.Select(m => m.Kind).Distinct().Count() == 1 ? hit.Kind : PageObjectKind.Form;
            return new PageItem(pageIndex, members.Select(m => m.Index).ToList(), bounds, kind, id);
        });

    /// <summary>The bounds of an Optim PDF item on a page (e.g. right after adding it), or null.</summary>
    public static PageItem? FindItem(byte[] data, string? password, int pageIndex, int itemId) =>
        PdfiumEditor.Run(data, password, editor =>
        {
            var page = editor.Page(pageIndex);
            var members = page.Objects().Where(o => ItemIdOf(page.ObjectAt(o.Index)) == itemId).ToList();
            if (members.Count == 0) return null;
            var bounds = members.Select(m => m.Bounds).Aggregate((p, q) =>
                new PdfRect(Math.Min(p.Left, q.Left), Math.Min(p.Bottom, q.Bottom), Math.Max(p.Right, q.Right), Math.Max(p.Top, q.Top)));
            return new PageItem(pageIndex, members.Select(m => m.Index).ToList(), bounds, members[0].Kind, itemId);
        });

    /// <summary>Moves an item by (dx, dy) points in user space, as one undoable edit.</summary>
    public static void Move(DocumentSession session, PageItem item, double dx, double dy) =>
        Transform(session, item, "Move", (1, 0, 0, 1, dx, dy));

    /// <summary>Scales an item uniformly about a fixed point (user space), e.g. the opposite corner of a resize handle.</summary>
    public static void Resize(DocumentSession session, PageItem item, double factor, (double X, double Y) anchor)
    {
        if (!(factor > 0.02 && factor < 50)) throw new ArgumentOutOfRangeException(nameof(factor), "That size is not possible.");
        Transform(session, item, "Resize", (factor, 0, 0, factor, anchor.X - factor * anchor.X, anchor.Y - factor * anchor.Y));
    }

    public static void Delete(DocumentSession session, PageItem item) =>
        session.Apply("Delete", (data, password) => PdfiumEditor.Run(data, password, editor =>
        {
            var page = editor.Page(item.PageIndex);
            Verify(page, item);
            page.RemoveObjects(item.ObjectIndices);
            return editor.Save();
        }), _ => { });

    static void Transform(DocumentSession session, PageItem item, string description, (double A, double B, double C, double D, double E, double F) m) =>
        session.Apply(description, (data, password) => PdfiumEditor.Run(data, password, editor =>
        {
            var page = editor.Page(item.PageIndex);
            Verify(page, item);
            foreach (int i in item.ObjectIndices)
                fpdf_edit.FPDFPageObjTransform(page.ObjectAt(i), m.A, m.B, m.C, m.D, m.E, m.F);
            page.MarkChanged();
            return editor.Save();
        }), _ => { });

    /// <summary>Refuses to act on a stale selection (the page changed since the item was found).</summary>
    static void Verify(PdfiumEditor.EditorPage page, PageItem item)
    {
        int count = fpdf_edit.FPDFPageCountObjects(page.Handle);
        foreach (int i in item.ObjectIndices)
            if (i >= count || (item.ItemId != 0 && ItemIdOf(page.ObjectAt(i)) != item.ItemId))
                throw new InvalidOperationException("The page changed; select the item again.");
    }

    /// <summary>The Optim item id from the object's marked content, or 0.</summary>
    internal static unsafe int ItemIdOf(FpdfPageobjectT obj)
    {
        int marks = fpdf_edit.FPDFPageObjCountMarks(obj);
        for (int m = 0; m < marks; m++)
        {
            var mark = fpdf_edit.FPDFPageObjGetMark(obj, (ulong)m);
            if (mark is null || mark.__Instance == IntPtr.Zero) continue;
            var name = new ushort[64];
            ulong length = 0;
            if (fpdf_edit.FPDFPageObjMarkGetName(mark, ref name[0], (ulong)(name.Length * 2), ref length) == 0) continue;
            var text = new string(name.Take((int)Math.Max(0, (long)length / 2 - 1)).Select(c => (char)c).ToArray());
            if (text != ContentEditor.ItemTag) continue;
            int id = 0;
            if (fpdf_edit.FPDFPageObjMarkGetParamIntValue(mark, "Id", ref id) != 0) return id;
        }
        return 0;
    }
}
