using System.Text;
using System.Text.RegularExpressions;
using OptimPdf.Core.Editing;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace OptimPdf.Core.Security;

/// <summary>An area to redact, in PDF user space.</summary>
public sealed record RedactionArea(int PageIndex, PdfRect Rect);

public sealed record RedactionReport(int Areas, int ObjectsRemoved, int RegionsRasterized, int AnnotationsRemoved, int FieldsRemoved);

/// <summary>
/// True redaction: content under the marked areas is removed from the file, not just covered.
/// </summary>
/// <remarks>
/// Every page object (text run, image, vector path, form XObject) that touches a marked area is deleted.
/// Where such an object also extends outside the area, the visible remainder is kept as an image rendered
/// from the original page with the marked areas painted over first, so the image holds no redacted pixels.
/// Annotations and form fields touching an area are deleted, and resources the page no longer uses are
/// dropped so removed images and fonts are not saved. The trade-off: text that shared a run with redacted
/// words stays visible but becomes part of an image (not searchable).
/// Redaction does not touch bookmarks, metadata, attachments or other pages; combine with
/// <see cref="Sanitizer"/> for those.
/// </remarks>
public static class Redactor
{
    /// <summary>Resolution of the image kept for the unredacted remainder of affected objects.</summary>
    public const double PatchDpi = 300;

    /// <summary>Redacts the areas as one undoable edit of the session.</summary>
    public static RedactionReport Redact(Documents.DocumentSession session, IReadOnlyList<RedactionArea> areas, PdfColor? fill = null)
    {
        if (areas.Count == 0) throw new ArgumentException("Mark at least one area to redact.", nameof(areas));
        int removed = 0, rasterized = 0, annotations = 0, fields = 0;
        session.Apply(areas.Count == 1 ? "Redact area" : $"Redact {areas.Count} areas",
            (data, password) => RemoveContent(data, password, areas, fill ?? PdfColor.Black, out removed, out rasterized),
            doc => (annotations, fields) = FinishRedaction(doc, areas));
        return new RedactionReport(areas.Count, removed, rasterized, annotations, fields);
    }

    /// <summary>
    /// Stage 1 (PDFium): removes and rasterizes page content and draws the redaction boxes.
    /// Returns the decrypted, edited document; then run <see cref="FinishRedaction"/> on it.
    /// </summary>
    public static byte[] RemoveContent(byte[] data, string? password, IReadOnlyList<RedactionArea> areas, PdfColor fill,
        out int objectsRemoved, out int regionsRasterized)
    {
        int removed = 0, rasterized = 0;
        var (r, g, b) = ((byte)Math.Round(fill.R * 255), (byte)Math.Round(fill.G * 255), (byte)Math.Round(fill.B * 255));
        var result = PdfiumEditor.Run(data, password, editor =>
        {
            foreach (var group in areas.GroupBy(a => a.PageIndex))
            {
                var page = editor.Page(group.Key);
                var rects = group.Select(a => a.Rect).Where(a => a.Width > 0 && a.Height > 0).ToList();
                if (rects.Count == 0) continue;

                var objects = page.Objects();
                var hit = objects.Where(o => rects.Any(a => Intersects(o.Bounds, a))).ToList();
                // Objects entirely inside one area need no remainder image.
                var partial = Merge(hit.Where(o => !rects.Any(a => Contains(a, o.Bounds))).Select(o => o.Bounds).ToList());

                PdfiumEditor.EditorPage.Raster? raster = null;
                if (partial.Count > 0)
                {
                    double scale = Math.Min(PatchDpi / 72, PdfiumDocument.MaxBitmapSide / Math.Max(page.Width, page.Height));
                    raster = page.Render(scale);           // Rendered before removal…
                    foreach (var a in rects) raster.Fill(a, r, g, b); // …with the redacted pixels destroyed.
                }

                page.RemoveObjects(hit.Select(o => o.Index));
                removed += hit.Count;

                if (raster is not null)
                    foreach (var area in partial)
                    {
                        var (x, y, w, h) = raster.ToDevice(area);
                        page.InsertImage(raster, x, y, w, h);
                        rasterized++;
                    }
                foreach (var a in rects) page.InsertRect(a, r, g, b);
            }
            return editor.Save();
        });
        objectsRemoved = removed;
        regionsRasterized = rasterized;
        return result;
    }

    /// <summary>
    /// Stage 2 (PDF writer): removes annotations and fields touching the areas, page thumbnails, and
    /// resources the redacted pages no longer use; compresses the new remainder images.
    /// </summary>
    public static (int Annotations, int Fields) FinishRedaction(PdfDocument doc, IReadOnlyList<RedactionArea> areas)
    {
        int annotations = 0, fields = 0;
        var removedWidgets = new HashSet<PdfObjectID>();
        foreach (var group in areas.GroupBy(a => a.PageIndex))
        {
            var page = doc.Pages[group.Key];
            var rects = group.Select(a => a.Rect).ToList();
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");

            if (page.Elements.GetArray("/Annots") is { } annots)
            {
                var removedHere = new HashSet<PdfObjectID>();
                for (int pass = 0; pass < 2; pass++) // Second pass: popups whose parent was removed.
                    for (int i = annots.Elements.Count - 1; i >= 0; i--)
                    {
                        if ((annots.Elements[i] as PdfReference) is not { Value: PdfDictionary annot } reference) continue;
                        bool remove = pass == 0
                            ? annot.Elements.GetRectangle("/Rect") is var ar && rects.Any(a => Intersects(PdfRect.FromPoints(ar.X1, ar.Y1, ar.X2, ar.Y2), a))
                            : (annot.Elements["/Parent"] as PdfReference) is { } parent && removedHere.Contains(parent.ObjectID)
                              && annot.Elements.GetName("/Subtype") == "/Popup";
                        if (!remove) continue;
                        annots.Elements.RemoveAt(i);
                        removedHere.Add(reference.ObjectID);
                        annotations++;
                        if (annot.Elements.GetName("/Subtype") == "/Widget") removedWidgets.Add(reference.ObjectID);
                    }
            }

            PruneResources(doc, page);
            CompressImages(page);
        }
        if (removedWidgets.Count > 0) fields = RemoveFields(doc, removedWidgets);
        return (annotations, fields);
    }

    /// <summary>Drops /XObject and /Font entries the page's content no longer refers to.</summary>
    static void PruneResources(PdfDocument doc, PdfPage page)
    {
        if (page.Elements.GetDictionary("/Resources") is not { } resources) return;
        var content = new StringBuilder();
        foreach (var part in page.Contents.Elements)
            if ((part as PdfReference)?.Value is PdfDictionary { Stream: { } stream })
                content.Append(Encoding.Latin1.GetString(stream.UnfilteredValue)).Append('\n');
        var text = content.ToString();
        var usedXObjects = Regex.Matches(text, @"/([^\s/\[\]()<>{}%]+)\s+Do\b").Select(m => "/" + m.Groups[1].Value).ToHashSet();
        var usedFonts = Regex.Matches(text, @"/([^\s/\[\]()<>{}%]+)\s+[-+.\d]+\s+Tf\b").Select(m => "/" + m.Groups[1].Value).ToHashSet();

        // Resources may be shared with other pages, so give this page its own filtered copies.
        var copy = new PdfDictionary(doc);
        foreach (var key in resources.Elements.Keys) copy.Elements[key] = resources.Elements[key]!;
        Filter(doc, copy, "/XObject", usedXObjects);
        Filter(doc, copy, "/Font", usedFonts);
        page.Elements["/Resources"] = copy;
    }

    static void Filter(PdfDocument doc, PdfDictionary resources, string key, HashSet<string> used)
    {
        if (resources.Elements.GetDictionary(key) is not { } dict) return;
        var filtered = new PdfDictionary(doc);
        foreach (var name in dict.Elements.Keys)
            if (used.Contains(name)) filtered.Elements[name] = dict.Elements[name]!;
        resources.Elements[key] = filtered;
    }

    static void CompressImages(PdfPage page)
    {
        var xobjects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
        if (xobjects is null) return;
        foreach (var key in xobjects.Elements.Keys)
            if ((xobjects.Elements[key] as PdfReference)?.Value is PdfDictionary { Stream: { Length: > 64 } } image
                && image.Elements.GetName("/Subtype") == "/Image" && !image.Elements.ContainsKey("/Filter"))
                image.Stream.Zip();
    }

    /// <summary>Removes fields whose widgets were deleted; parents left without kids are removed too.</summary>
    static int RemoveFields(PdfDocument doc, HashSet<PdfObjectID> widgets)
    {
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is not { } fields) return 0;
        return Prune(fields, widgets);
    }

    /// <returns>The number of fields removed.</returns>
    static int Prune(PdfArray fields, HashSet<PdfObjectID> widgets)
    {
        int count = 0;
        for (int i = fields.Elements.Count - 1; i >= 0; i--)
        {
            if (fields.Elements[i] is not PdfReference { Value: PdfDictionary node } reference) continue;
            if (widgets.Contains(reference.ObjectID)) { fields.Elements.RemoveAt(i); count++; continue; } // Field and widget in one.
            if (node.Elements.GetArray("/Kids") is not { Elements.Count: > 0 } kids) continue;

            bool kidsAreWidgets = kids.Elements.All(k => (k as PdfReference)?.Value is PdfDictionary d && !d.Elements.ContainsKey("/T"));
            if (kidsAreWidgets)
            {
                for (int k = kids.Elements.Count - 1; k >= 0; k--)
                    if (kids.Elements[k] is PdfReference kr && widgets.Contains(kr.ObjectID)) kids.Elements.RemoveAt(k);
            }
            else count += Prune(kids, widgets);

            if (kids.Elements.Count == 0)
            {
                fields.Elements.RemoveAt(i);
                if (kidsAreWidgets) count++;
            }
        }
        return count;
    }

    static bool Intersects(PdfRect a, PdfRect b) =>
        a.Left <= b.Right && b.Left <= a.Right && a.Bottom <= b.Top && b.Bottom <= a.Top;

    static bool Contains(PdfRect outer, PdfRect inner) =>
        inner.Left >= outer.Left && inner.Right <= outer.Right && inner.Bottom >= outer.Bottom && inner.Top <= outer.Top;

    /// <summary>Merges overlapping rectangles so overlapping objects get one remainder image.</summary>
    static List<PdfRect> Merge(List<PdfRect> rects)
    {
        bool mergedAny = true;
        while (mergedAny)
        {
            mergedAny = false;
            for (int i = 0; i < rects.Count && !mergedAny; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    if (!Intersects(rects[i], rects[j])) continue;
                    var (a, b) = (rects[i], rects[j]);
                    rects[i] = new PdfRect(Math.Min(a.Left, b.Left), Math.Min(a.Bottom, b.Bottom), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top));
                    rects.RemoveAt(j);
                    mergedAny = true;
                    break;
                }
        }
        return rects;
    }
}
