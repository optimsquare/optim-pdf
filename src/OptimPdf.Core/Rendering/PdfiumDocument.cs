using System.Runtime.InteropServices;
using PDFiumCore;

namespace OptimPdf.Core.Rendering;

/// <summary>Thrown when a document needs a password, or the password given was wrong.</summary>
public sealed class PdfPasswordRequiredException(string message) : Exception(message);

/// <summary>Thrown when PDFium cannot open or parse a document.</summary>
public sealed class PdfOpenException(string message) : Exception(message);

public readonly record struct PageSize(double Width, double Height);

/// <summary>A rectangle in PDF user space (points, origin bottom-left).</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;

    public static PdfRect FromPoints(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
}

/// <summary>A rendered page in 32-bit BGRA (premultiplied, opaque white background).</summary>
public sealed record RenderedPage(int PixelWidth, int PixelHeight, int Stride, byte[] Pixels);

public sealed record SearchHit(int PageIndex, int CharIndex, int CharCount, IReadOnlyList<PdfRect> Rects);

public sealed record OutlineItem(string Title, int PageIndex, IReadOnlyList<OutlineItem> Children);

/// <summary>A signature dictionary's raw values: /Contents bytes, /ByteRange, /SubFilter, /M and /Reason.</summary>
public sealed record PdfSignatureData(byte[] Contents, int[] ByteRange, string? SubFilter, string? Time, string? Reason);

/// <summary>
/// Read-only view of a PDF backed by PDFium. All PDFium calls are serialized through
/// <see cref="PdfiumLibrary.Sync"/> because PDFium is not thread-safe.
/// </summary>
public sealed class PdfiumDocument : IDisposable
{
    const int FPDF_ERR_PASSWORD = 4;
    const int FPDFBitmap_BGRA = 4;
    const int FPDF_ANNOT = 0x01;
    const int FPDF_MATCHCASE = 0x01;
    const int FPDF_MATCHWHOLEWORD = 0x02;

    // Upper bound on rendered bitmap side length, to keep extreme zoom levels from exhausting memory.
    public const int MaxBitmapSide = 12000;

    readonly FpdfDocumentT _doc;
    // PDFium reads the document lazily, so the bytes must stay put for the document's lifetime. The caller's array is
    // pinned rather than copied (document arrays are never modified), which halves memory for large files.
    GCHandle _pin;
    // Form widgets (fields) are only drawn through a form-fill environment; the info block must outlive it.
    readonly FPDF_FORMFILLINFO? _formInfo;
    readonly FpdfFormHandleT? _form;
    bool _disposed;

    PdfiumDocument(FpdfDocumentT doc, GCHandle pin)
    {
        _doc = doc;
        _pin = pin;
        _formInfo = new FPDF_FORMFILLINFO { Version = 1 }; // No callbacks: display only, no interaction or scripts.
        var form = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(doc, _formInfo);
        _form = form is not null && form.__Instance != IntPtr.Zero ? form : null;
    }

    /// <summary>True when the document has an interactive form that PDFium draws.</summary>
    public bool HasFormEnvironment => _form is not null;

    public static PdfiumDocument Open(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        PdfiumLibrary.EnsureInitialized();

        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);

        lock (PdfiumLibrary.Sync)
        {
            var doc = fpdfview.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (ulong)data.Length, password);
            if (doc is null || doc.__Instance == IntPtr.Zero)
            {
                var error = (int)fpdfview.FPDF_GetLastError();
                pin.Free();
                if (error == FPDF_ERR_PASSWORD)
                    throw new PdfPasswordRequiredException(password is null
                        ? "This document is password protected."
                        : "The password is incorrect.");
                throw new PdfOpenException($"The file could not be opened as a PDF (PDFium error {error}).");
            }
            return new PdfiumDocument(doc, pin);
        }
    }

    public int PageCount
    {
        get { lock (PdfiumLibrary.Sync) { ThrowIfDisposed(); return fpdfview.FPDF_GetPageCount(_doc); } }
    }

    /// <summary>Displayed page size in points, after the page's /Rotate is applied.</summary>
    public PageSize GetPageSize(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if ((uint)pageIndex >= (uint)fpdfview.FPDF_GetPageCount(_doc)) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            // Reads the page dictionary only (rotation included), without parsing the page's content.
            using var size = new FS_SIZEF_();
            if (fpdfview.FPDF_GetPageSizeByIndexF(_doc, pageIndex, size) != 0) return new PageSize(size.Width, size.Height);
            using var page = LoadPage(pageIndex);
            return new PageSize(fpdfview.FPDF_GetPageWidthF(page.Handle), fpdfview.FPDF_GetPageHeightF(page.Handle));
        }
    }

    /// <summary>The page's /Rotate in degrees clockwise (0, 90, 180 or 270).</summary>
    public int GetPageRotation(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            return fpdf_edit.FPDFPageGetRotation(page.Handle) * 90;
        }
    }

    /// <summary>Renders a page. <paramref name="scale"/> is pixels per point (1.0 = 72 DPI).</summary>
    /// <param name="viewRotation">Extra clockwise view rotation in quarter turns (0–3), on top of /Rotate.</param>
    public RenderedPage Render(int pageIndex, double scale, int viewRotation = 0, bool drawAnnotations = true)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        viewRotation = ((viewRotation % 4) + 4) % 4;

        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            double w = fpdfview.FPDF_GetPageWidthF(page.Handle) * scale;
            double h = fpdfview.FPDF_GetPageHeightF(page.Handle) * scale;
            if (viewRotation % 2 == 1) (w, h) = (h, w);

            // Keep the aspect ratio when clamping.
            double clamp = Math.Min(1.0, MaxBitmapSide / Math.Max(w, h));
            int pw = Math.Max(1, (int)Math.Round(w * clamp));
            int ph = Math.Max(1, (int)Math.Round(h * clamp));
            int stride = pw * 4;
            var pixels = new byte[stride * ph];

            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bitmap = fpdfview.FPDFBitmapCreateEx(pw, ph, FPDFBitmap_BGRA, pin.AddrOfPinnedObject(), stride);
                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pw, ph, 0xFFFFFFFF);
                    fpdfview.FPDF_RenderPageBitmap(bitmap, page.Handle, 0, 0, pw, ph, viewRotation,
                        drawAnnotations ? FPDF_ANNOT : 0);
                    if (drawAnnotations && _form is not null)
                    {
                        fpdf_formfill.FORM_OnAfterLoadPage(page.Handle, _form);
                        fpdf_formfill.FPDF_FFLDraw(_form, bitmap, page.Handle, 0, 0, pw, ph, viewRotation, FPDF_ANNOT);
                        fpdf_formfill.FORM_OnBeforeClosePage(page.Handle, _form);
                    }
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                pin.Free();
            }
            return new RenderedPage(pw, ph, stride, pixels);
        }
    }

    /// <summary>Maps a device pixel in a rendered bitmap of the given size to PDF user space.</summary>
    public (double X, double Y) DeviceToPage(int pageIndex, int bitmapWidth, int bitmapHeight, int viewRotation, int deviceX, int deviceY)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            double x = 0, y = 0;
            fpdfview.FPDF_DeviceToPage(page.Handle, 0, 0, bitmapWidth, bitmapHeight, ((viewRotation % 4) + 4) % 4, deviceX, deviceY, ref x, ref y);
            return (x, y);
        }
    }

    /// <summary>Maps a PDF user-space point to a device pixel in a rendered bitmap of the given size.</summary>
    public (int X, int Y) PageToDevice(int pageIndex, int bitmapWidth, int bitmapHeight, int viewRotation, double pageX, double pageY)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            int x = 0, y = 0;
            fpdfview.FPDF_PageToDevice(page.Handle, 0, 0, bitmapWidth, bitmapHeight, ((viewRotation % 4) + 4) % 4, pageX, pageY, ref x, ref y);
            return (x, y);
        }
    }

    public string GetPageText(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            var text = fpdf_text.FPDFTextLoadPage(page.Handle);
            try
            {
                int count = fpdf_text.FPDFTextCountChars(text);
                if (count <= 0) return string.Empty;
                var buffer = new ushort[count + 1];
                int written = fpdf_text.FPDFTextGetText(text, 0, count, ref buffer[0]);
                return Utf16ToString(buffer, Math.Max(0, written - 1));
            }
            finally
            {
                fpdf_text.FPDFTextClosePage(text);
            }
        }
    }

    /// <summary>Index of the character at a PDF-space point (within the tolerance, in points), or -1.</summary>
    public int GetCharIndexAt(int pageIndex, double x, double y, double tolerance = 6)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            var text = fpdf_text.FPDFTextLoadPage(page.Handle);
            try { return fpdf_text.FPDFTextGetCharIndexAtPos(text, x, y, tolerance, tolerance); }
            finally { fpdf_text.FPDFTextClosePage(text); }
        }
    }

    /// <summary>Text of the characters in [charIndex, charIndex + count) on a page.</summary>
    public string GetText(int pageIndex, int charIndex, int count)
    {
        if (count <= 0) return string.Empty;
        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            var text = fpdf_text.FPDFTextLoadPage(page.Handle);
            try
            {
                var buffer = new ushort[count + 1];
                int written = fpdf_text.FPDFTextGetText(text, charIndex, count, ref buffer[0]);
                return Utf16ToString(buffer, Math.Max(0, written - 1));
            }
            finally
            {
                fpdf_text.FPDFTextClosePage(text);
            }
        }
    }

    /// <summary>
    /// Returns the bounding rectangles of the characters in [charIndex, charIndex + count) on a page,
    /// merged per text line by PDFium. Used to turn a text selection into markup quads.
    /// </summary>
    public IReadOnlyList<PdfRect> GetTextRects(int pageIndex, int charIndex, int count)
    {
        lock (PdfiumLibrary.Sync)
        {
            using var page = LoadPage(pageIndex);
            var text = fpdf_text.FPDFTextLoadPage(page.Handle);
            try { return ReadRects(text, charIndex, count); }
            finally { fpdf_text.FPDFTextClosePage(text); }
        }
    }

    public IReadOnlyList<SearchHit> Search(string query, bool matchCase = false, bool wholeWord = false,
        CancellationToken cancellationToken = default)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrEmpty(query)) return hits;

        var needle = new ushort[query.Length + 1];
        for (int i = 0; i < query.Length; i++) needle[i] = query[i];
        ulong flags = (matchCase ? (ulong)FPDF_MATCHCASE : 0) | (wholeWord ? (ulong)FPDF_MATCHWHOLEWORD : 0);

        int pages = PageCount;
        for (int p = 0; p < pages; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Lock per page so rendering can interleave with a long search.
            lock (PdfiumLibrary.Sync)
            {
                using var page = LoadPage(p);
                var text = fpdf_text.FPDFTextLoadPage(page.Handle);
                try
                {
                    var handle = fpdf_text.FPDFTextFindStart(text, ref needle[0], flags, 0);
                    try
                    {
                        while (fpdf_text.FPDFTextFindNext(handle) != 0)
                        {
                            int start = fpdf_text.FPDFTextGetSchResultIndex(handle);
                            int len = fpdf_text.FPDFTextGetSchCount(handle);
                            hits.Add(new SearchHit(p, start, len, ReadRects(text, start, len)));
                        }
                    }
                    finally
                    {
                        fpdf_text.FPDFTextFindClose(handle);
                    }
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(text);
                }
            }
        }
        return hits;
    }

    public IReadOnlyList<OutlineItem> GetOutline()
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            var visited = new HashSet<IntPtr>();
            return ReadOutlineLevel(null, visited, depth: 0);
        }
    }

    public string? GetMetadata(string tag)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            ulong bytes = fpdf_doc.FPDF_GetMetaText(_doc, tag, IntPtr.Zero, 0);
            if (bytes <= 2) return null;
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                fpdf_doc.FPDF_GetMetaText(_doc, tag, buffer, bytes);
                return Marshal.PtrToStringUni(buffer, (int)bytes / 2 - 1);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>The /P permission flags in effect for the password used to open the document (all bits set when unrestricted).</summary>
    public ulong PermissionFlags
    {
        get { lock (PdfiumLibrary.Sync) { ThrowIfDisposed(); return fpdfview.FPDF_GetDocPermissions(_doc); } }
    }

    /// <summary>The document's /P flags as granted to the open password, regardless of the password used.</summary>
    public ulong UserPermissionFlags
    {
        get { lock (PdfiumLibrary.Sync) { ThrowIfDisposed(); return fpdfview.FPDF_GetDocUserPermissions(_doc); } }
    }

    /// <summary>Security handler revision, or -1 when the document is not encrypted.</summary>
    public int SecurityRevision
    {
        get { lock (PdfiumLibrary.Sync) { ThrowIfDisposed(); return fpdfview.FPDF_GetSecurityHandlerRevision(_doc); } }
    }

    /// <summary>The document's signature dictionaries as PDFium reads them (no validation).</summary>
    public unsafe IReadOnlyList<PdfSignatureData> GetSignatures()
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            var list = new List<PdfSignatureData>();
            int count = fpdf_signature.FPDF_GetSignatureCount(_doc);
            for (int i = 0; i < count; i++)
            {
                var sig = fpdf_signature.FPDF_GetSignatureObject(_doc, i);
                if (sig is null || sig.__Instance == IntPtr.Zero) continue;

                ulong n = fpdf_signature.FPDFSignatureObjGetContents(sig, IntPtr.Zero, 0);
                var contents = new byte[n];
                fixed (byte* p = contents) fpdf_signature.FPDFSignatureObjGetContents(sig, (IntPtr)p, n);

                int dummy = 0;
                ulong rn = fpdf_signature.FPDFSignatureObjGetByteRange(sig, ref dummy, 0);
                // PDFium also lists signature fields nobody has signed yet; those are not signatures.
                if (n == 0 && rn == 0) continue;
                var range = new int[rn];
                if (rn > 0) fpdf_signature.FPDFSignatureObjGetByteRange(sig, ref range[0], rn);

                list.Add(new PdfSignatureData(contents, range,
                    AsciiString((b, l) => fpdf_signature.FPDFSignatureObjGetSubFilter(sig, b, l)),
                    AsciiString((b, l) => fpdf_signature.FPDFSignatureObjGetTime(sig, b, l)),
                    Utf16String((b, l) => fpdf_signature.FPDFSignatureObjGetReason(sig, b, l))));
            }
            return list;
        }
    }

    unsafe delegate ulong AsciiGetter(sbyte* buffer, ulong length);

    static unsafe string? AsciiString(AsciiGetter get)
    {
        ulong n = get(null, 0);
        if (n <= 1) return null;
        var buffer = new sbyte[n];
        fixed (sbyte* p = buffer) { get(p, n); return new string(p, 0, (int)n - 1); }
    }

    static string? Utf16String(Func<IntPtr, ulong, ulong> get)
    {
        ulong n = get(IntPtr.Zero, 0);
        if (n <= 2) return null;
        var buffer = Marshal.AllocHGlobal((int)n);
        try { get(buffer, n); return Marshal.PtrToStringUni(buffer, (int)n / 2 - 1); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    List<OutlineItem> ReadOutlineLevel(FpdfBookmarkT? parent, HashSet<IntPtr> visited, int depth)
    {
        var items = new List<OutlineItem>();
        if (depth > 64) return items; // Malformed outlines can be deeply or cyclically nested.

        var node = fpdf_doc.FPDFBookmarkGetFirstChild(_doc, parent!);
        while (node is not null && node.__Instance != IntPtr.Zero && visited.Add(node.__Instance))
        {
            string title = ReadBookmarkTitle(node);
            int pageIndex = -1;
            var dest = fpdf_doc.FPDFBookmarkGetDest(_doc, node);
            if (dest is not null && dest.__Instance != IntPtr.Zero)
                pageIndex = fpdf_doc.FPDFDestGetDestPageIndex(_doc, dest);

            items.Add(new OutlineItem(title, pageIndex, ReadOutlineLevel(node, visited, depth + 1)));
            node = fpdf_doc.FPDFBookmarkGetNextSibling(_doc, node);
        }
        return items;
    }

    static string ReadBookmarkTitle(FpdfBookmarkT node)
    {
        ulong bytes = fpdf_doc.FPDFBookmarkGetTitle(node, IntPtr.Zero, 0);
        if (bytes <= 2) return string.Empty;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(node, buffer, bytes);
            return Marshal.PtrToStringUni(buffer, (int)bytes / 2 - 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static List<PdfRect> ReadRects(FpdfTextpageT text, int start, int count)
    {
        int n = fpdf_text.FPDFTextCountRects(text, start, count);
        var rects = new List<PdfRect>(Math.Max(0, n));
        for (int i = 0; i < n; i++)
        {
            double l = 0, t = 0, r = 0, b = 0;
            if (fpdf_text.FPDFTextGetRect(text, i, ref l, ref t, ref r, ref b) != 0)
                rects.Add(PdfRect.FromPoints(l, b, r, t));
        }
        return rects;
    }

    static string Utf16ToString(ushort[] buffer, int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = (char)buffer[i];
        return new string(chars);
    }

    PageHandle LoadPage(int pageIndex)
    {
        ThrowIfDisposed();
        int count = fpdfview.FPDF_GetPageCount(_doc);
        if ((uint)pageIndex >= (uint)count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
            throw new PdfOpenException($"Page {pageIndex + 1} could not be loaded.");
        return new PageHandle(page);
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_form is not null) fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(_form);
            fpdfview.FPDF_CloseDocument(_doc);
            _formInfo?.Dispose();
        }
        if (_pin.IsAllocated) _pin.Free();
    }

    readonly struct PageHandle(FpdfPageT handle) : IDisposable
    {
        public FpdfPageT Handle { get; } = handle;
        public void Dispose() => fpdfview.FPDF_ClosePage(Handle);
    }
}

public static class PdfiumLibrary
{
    /// <summary>Global lock for every PDFium call.</summary>
    public static readonly object Sync = new();
    static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (_initialized) return;
            fpdfview.FPDF_InitLibrary();
            _initialized = true;
        }
    }
}
