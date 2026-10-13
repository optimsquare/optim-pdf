using OptimPdf.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OptimPdf.Core.Pages;

/// <summary>
/// Page-level operations. In-place operations act on an open <see cref="PdfDocument"/>;
/// the file-level helpers (merge, split, extract) produce new documents.
/// All page indices are zero-based.
/// </summary>
public static class PageOperations
{
    // ---- In-place operations on an open document ----

    /// <summary>Rotates pages clockwise by a multiple of 90 degrees.</summary>
    public static void Rotate(PdfDocument doc, IEnumerable<int> pageIndices, int degrees)
    {
        if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(degrees));
        foreach (int i in Validate(doc, pageIndices))
        {
            var page = doc.Pages[i];
            page.Rotate = (((page.Rotate + degrees) % 360) + 360) % 360;
        }
    }

    public static void Delete(PdfDocument doc, IEnumerable<int> pageIndices)
    {
        var indices = Validate(doc, pageIndices).Distinct().OrderByDescending(i => i).ToList();
        if (indices.Count >= doc.PageCount) throw new InvalidOperationException("A document must keep at least one page.");
        foreach (int i in indices) doc.Pages.RemoveAt(i);
    }

    /// <summary>Moves one page so that it ends up at <paramref name="newIndex"/>.</summary>
    public static void Move(PdfDocument doc, int oldIndex, int newIndex)
    {
        Validate(doc, [oldIndex, newIndex]).ToList();
        if (oldIndex != newIndex) doc.Pages.MovePage(oldIndex, newIndex);
    }

    /// <summary>Reorders all pages; <paramref name="newOrder"/>[k] is the old index of the page placed at k.</summary>
    public static void Reorder(PdfDocument doc, IReadOnlyList<int> newOrder)
    {
        if (newOrder.Count != doc.PageCount || newOrder.Distinct().Count() != doc.PageCount || newOrder.Any(i => (uint)i >= (uint)doc.PageCount))
            throw new ArgumentException("The new order must be a permutation of all page indices.", nameof(newOrder));

        // Track where each original page currently sits while moving pages into place.
        var current = Enumerable.Range(0, doc.PageCount).ToList();
        for (int target = 0; target < newOrder.Count; target++)
        {
            int from = current.IndexOf(newOrder[target]);
            if (from == target) continue;
            doc.Pages.MovePage(from, target);
            int moved = current[from];
            current.RemoveAt(from);
            current.Insert(target, moved);
        }
    }

    /// <summary>Inserts pages from another PDF before <paramref name="insertIndex"/> (PageCount appends).</summary>
    public static int InsertFrom(PdfDocument doc, PdfDocument source, int insertIndex, IEnumerable<int>? sourcePages = null)
    {
        if ((uint)insertIndex > (uint)doc.PageCount) throw new ArgumentOutOfRangeException(nameof(insertIndex));
        var pages = sourcePages is null ? Enumerable.Range(0, source.PageCount).ToList() : Validate(source, sourcePages).ToList();
        int at = insertIndex;
        foreach (int i in pages) doc.Pages.Insert(at++, source.Pages[i]);
        return pages.Count;
    }

    public static void InsertBlank(PdfDocument doc, int insertIndex, double widthPt = 612, double heightPt = 792)
    {
        if ((uint)insertIndex > (uint)doc.PageCount) throw new ArgumentOutOfRangeException(nameof(insertIndex));
        var page = doc.Pages.Insert(insertIndex);
        page.Width = XUnit.FromPoint(widthPt);
        page.Height = XUnit.FromPoint(heightPt);
    }

    /// <summary>Sets the visible region (CropBox) of pages. The box is in PDF user space.</summary>
    public static void Crop(PdfDocument doc, IEnumerable<int> pageIndices, PdfRect box)
    {
        if (box.Width <= 1 || box.Height <= 1) throw new ArgumentException("The crop box is too small.", nameof(box));
        foreach (int i in Validate(doc, pageIndices))
        {
            var page = doc.Pages[i];
            var media = page.MediaBox;
            // Clamp to the MediaBox, as the spec requires.
            var crop = new PdfRect(Math.Max(box.Left, media.X1), Math.Max(box.Bottom, media.Y1),
                Math.Min(box.Right, media.X2), Math.Min(box.Top, media.Y2));
            if (crop.Width <= 1 || crop.Height <= 1) throw new ArgumentException($"The crop box lies outside page {i + 1}.", nameof(box));
            page.CropBox = new PdfRectangle(new XPoint(crop.Left, crop.Bottom), new XPoint(crop.Right, crop.Top));
        }
    }

    // ---- Document-producing operations ----

    /// <summary>Concatenates documents in order into a new document.</summary>
    public static PdfDocument Merge(IEnumerable<PdfDocument> sources)
    {
        var output = new PdfDocument();
        foreach (var source in sources)
            for (int i = 0; i < source.PageCount; i++)
                output.AddPage(source.Pages[i]);
        if (output.PageCount == 0) throw new ArgumentException("Nothing to merge.", nameof(sources));
        return output;
    }

    /// <summary>Copies the given pages, in the given order, into a new document.</summary>
    public static PdfDocument Extract(PdfDocument source, IEnumerable<int> pageIndices)
    {
        var output = new PdfDocument();
        foreach (int i in Validate(source, pageIndices)) output.AddPage(source.Pages[i]);
        if (output.PageCount == 0) throw new ArgumentException("No pages selected.", nameof(pageIndices));
        CopyInfo(source, output);
        return output;
    }

    /// <summary>Splits into consecutive chunks of <paramref name="pagesPerFile"/> pages.</summary>
    public static IReadOnlyList<PdfDocument> SplitEvery(PdfDocument source, int pagesPerFile)
    {
        if (pagesPerFile < 1) throw new ArgumentOutOfRangeException(nameof(pagesPerFile));
        var ranges = new List<PageRange>();
        for (int start = 0; start < source.PageCount; start += pagesPerFile)
            ranges.Add(new PageRange(start, Math.Min(start + pagesPerFile, source.PageCount) - 1));
        return SplitByRanges(source, ranges);
    }

    public static IReadOnlyList<PdfDocument> SplitByRanges(PdfDocument source, IEnumerable<PageRange> ranges) =>
        ranges.Select(r => Extract(source, Enumerable.Range(r.First, r.Last - r.First + 1))).ToList();

    /// <summary>Opens a PDF for page import (required by PDFsharp for copying pages between documents).</summary>
    public static PdfDocument OpenForImport(string path, string? password = null) =>
        password is null ? PdfReader.Open(path, PdfDocumentOpenMode.Import) : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);

    public static PdfDocument OpenForImport(byte[] data, string? password = null)
    {
        var stream = new MemoryStream(data, writable: false);
        return password is null ? PdfReader.Open(stream, PdfDocumentOpenMode.Import) : PdfReader.Open(stream, password, PdfDocumentOpenMode.Import);
    }

    static void CopyInfo(PdfDocument from, PdfDocument to)
    {
        if (!string.IsNullOrEmpty(from.Info.Title)) to.Info.Title = from.Info.Title;
        if (!string.IsNullOrEmpty(from.Info.Author)) to.Info.Author = from.Info.Author;
    }

    static IEnumerable<int> Validate(PdfDocument doc, IEnumerable<int> indices)
    {
        foreach (int i in indices)
        {
            if ((uint)i >= (uint)doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(indices), $"Page {i + 1} does not exist (document has {doc.PageCount}).");
            yield return i;
        }
    }
}

/// <summary>An inclusive, zero-based page range.</summary>
public readonly record struct PageRange(int First, int Last)
{
    /// <summary>
    /// Parses user input such as "1-3, 5, 8-" (one-based, inclusive; an open end means "to the last page").
    /// </summary>
    public static IReadOnlyList<PageRange> Parse(string text, int pageCount)
    {
        var result = new List<PageRange>();
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('-', StringSplitOptions.TrimEntries);
            int first, last;
            if (parts.Length == 1 && int.TryParse(parts[0], out first)) last = first;
            else if (parts.Length == 2)
            {
                first = parts[0].Length == 0 ? 1 : int.TryParse(parts[0], out var f) ? f : throw Bad(raw);
                last = parts[1].Length == 0 ? pageCount : int.TryParse(parts[1], out var l) ? l : throw Bad(raw);
            }
            else throw Bad(raw);

            if (first < 1 || last > pageCount || first > last)
                throw new FormatException($"\"{raw}\" is outside 1–{pageCount}.");
            result.Add(new PageRange(first - 1, last - 1));
        }
        if (result.Count == 0) throw new FormatException("Enter at least one page or range, e.g. 1-3, 5.");
        return result;
    }

    public IEnumerable<int> Indices() => Enumerable.Range(First, Last - First + 1);

    static FormatException Bad(string raw) => new($"\"{raw}\" is not a valid page or range.");
}
