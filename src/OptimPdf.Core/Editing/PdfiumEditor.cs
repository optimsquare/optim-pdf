using System.Runtime.InteropServices;
using OptimPdf.Core.Rendering;
using PDFiumCore;

namespace OptimPdf.Core.Editing;

public enum PageObjectKind { Unknown = 0, Text = 1, Path = 2, Image = 3, Shading = 4, Form = 5 }

/// <summary>A top-level page object as PDFium sees it. Bounds are in user space and ignore clipping.</summary>
public sealed record PageObjectInfo(int Index, PageObjectKind Kind, PdfRect Bounds);

/// <summary>
/// Object-level page editing through PDFium: list, remove and insert page objects, regenerate the
/// content stream, then save a full (non-incremental) copy. Used where content must really be removed
/// (redaction, replacing existing text) rather than covered.
/// </summary>
/// <remarks>
/// Use only inside <see cref="Run"/>, which holds the global PDFium lock for the whole edit.
/// Saved output is always decrypted; callers re-apply protection (see DocumentSession).
/// </remarks>
public sealed class PdfiumEditor : IDisposable
{
    const int FPDF_ERR_PASSWORD = 4;
    const int FPDFBitmap_BGR = 2;  // fpdfview.h: Gray = 1, BGR = 2, BGRx = 3, BGRA = 4.
    const int FPDFBitmap_BGRx = 3;
    const ulong FPDF_REMOVE_SECURITY = 3;
    const int FPDF_FILLMODE_WINDING = 2;

    readonly FpdfDocumentT _doc;
    readonly IntPtr _buffer;
    readonly List<EditorPage> _pages = [];

    PdfiumEditor(FpdfDocumentT doc, IntPtr buffer) { _doc = doc; _buffer = buffer; }

    /// <summary>Opens <paramref name="data"/>, runs <paramref name="edit"/> under the PDFium lock, and disposes.</summary>
    public static T Run<T>(byte[] data, string? password, Func<PdfiumEditor, T> edit)
    {
        PdfiumLibrary.EnsureInitialized();
        lock (PdfiumLibrary.Sync)
        {
            using var editor = Open(data, password);
            return edit(editor);
        }
    }

    static PdfiumEditor Open(byte[] data, string? password)
    {
        var buffer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, buffer, data.Length);
        var doc = fpdfview.FPDF_LoadMemDocument64(buffer, (ulong)data.Length, password);
        if (doc is null || doc.__Instance == IntPtr.Zero)
        {
            int error = (int)fpdfview.FPDF_GetLastError();
            Marshal.FreeHGlobal(buffer);
            throw error == FPDF_ERR_PASSWORD
                ? new PdfPasswordRequiredException("This document is password protected.")
                : new PdfOpenException($"The file could not be opened as a PDF (PDFium error {error}).");
        }
        return new PdfiumEditor(doc, buffer);
    }

    public int PageCount => fpdfview.FPDF_GetPageCount(_doc);

    /// <summary>Loads a page for editing; it stays open until the editor is disposed.</summary>
    public EditorPage Page(int index)
    {
        if ((uint)index >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        var existing = _pages.FirstOrDefault(p => p.Index == index);
        if (existing is not null) return existing;
        var handle = fpdfview.FPDF_LoadPage(_doc, index);
        if (handle is null || handle.__Instance == IntPtr.Zero) throw new PdfOpenException($"Page {index + 1} could not be loaded.");
        var page = new EditorPage(this, index, handle);
        _pages.Add(page);
        return page;
    }

    /// <summary>Writes the edited document (unencrypted). Pages with changes must have been committed first.</summary>
    public byte[] Save()
    {
        foreach (var page in _pages) page.CommitIfChanged();
        using var output = new MemoryStream();
        // Keep the delegate reachable for the duration of the native call.
        PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_ulong write = (_, data, size) =>
        {
            int count = checked((int)(size & 0xFFFFFFFF)); // C "unsigned long" is 32-bit on Windows.
            if (count == 0) return 1;
            var chunk = new byte[count];
            Marshal.Copy(data, chunk, 0, count);
            output.Write(chunk);
            return 1;
        };
        using var fileWrite = new FPDF_FILEWRITE_ { Version = 1, WriteBlock = write };
        if (fpdf_save.FPDF_SaveAsCopy(_doc, fileWrite, FPDF_REMOVE_SECURITY) == 0)
            throw new InvalidOperationException("PDFium could not save the edited document.");
        GC.KeepAlive(write);
        return output.ToArray();
    }

    internal FpdfDocumentT Handle => _doc;

    public void Dispose()
    {
        foreach (var page in _pages) fpdfview.FPDF_ClosePage(page.Handle);
        _pages.Clear();
        fpdfview.FPDF_CloseDocument(_doc);
        Marshal.FreeHGlobal(_buffer);
    }

    /// <summary>One page open for editing.</summary>
    public sealed class EditorPage
    {
        readonly PdfiumEditor _owner;
        bool _changed;

        internal EditorPage(PdfiumEditor owner, int index, FpdfPageT handle) { _owner = owner; Index = index; Handle = handle; }

        public int Index { get; }
        internal FpdfPageT Handle { get; }

        public double Width => fpdfview.FPDF_GetPageWidthF(Handle);
        public double Height => fpdfview.FPDF_GetPageHeightF(Handle);

        public IReadOnlyList<PageObjectInfo> Objects()
        {
            int n = fpdf_edit.FPDFPageCountObjects(Handle);
            var list = new List<PageObjectInfo>(n);
            for (int i = 0; i < n; i++)
            {
                var obj = fpdf_edit.FPDFPageGetObject(Handle, i);
                list.Add(new PageObjectInfo(i, (PageObjectKind)fpdf_edit.FPDFPageObjGetType(obj), BoundsOf(obj)));
            }
            return list;
        }

        internal FpdfPageobjectT ObjectAt(int index) => fpdf_edit.FPDFPageGetObject(Handle, index);

        internal int IndexOf(FpdfPageobjectT obj)
        {
            int n = fpdf_edit.FPDFPageCountObjects(Handle);
            for (int i = 0; i < n; i++)
                if (fpdf_edit.FPDFPageGetObject(Handle, i).__Instance == obj.__Instance) return i;
            return -1;
        }

        internal static PdfRect BoundsOf(FpdfPageobjectT obj)
        {
            float l = 0, b = 0, r = 0, t = 0;
            fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t);
            return PdfRect.FromPoints(l, b, r, t);
        }

        /// <summary>Removes the objects at the given indices (as returned by <see cref="Objects"/>).</summary>
        public void RemoveObjects(IEnumerable<int> indices)
        {
            var handles = indices.Distinct().Select(ObjectAt).ToList();
            foreach (var obj in handles)
            {
                if (fpdf_edit.FPDFPageRemoveObject(Handle, obj) == 0)
                    throw new InvalidOperationException("A page object could not be removed.");
                fpdf_edit.FPDFPageObjDestroy(obj);
            }
            if (handles.Count > 0) _changed = true;
        }

        /// <summary>Renders the page as it is now (no annotations or form widgets) at <paramref name="scale"/> px/pt.</summary>
        public Raster Render(double scale)
        {
            int w = Math.Max(1, (int)Math.Round(Width * scale)), h = Math.Max(1, (int)Math.Round(Height * scale));
            var pixels = new byte[w * h * 4];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bitmap = fpdfview.FPDFBitmapCreateEx(w, h, FPDFBitmap_BGRx, pin.AddrOfPinnedObject(), w * 4);
                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                    fpdfview.FPDF_RenderPageBitmap(bitmap, Handle, 0, 0, w, h, 0, 0);
                }
                finally { fpdfview.FPDFBitmapDestroy(bitmap); }
            }
            finally { pin.Free(); }
            return new Raster(this, w, h, pixels);
        }

        /// <summary>Adds an opaque image covering the device-pixel rectangle of <paramref name="raster"/>.</summary>
        public void InsertImage(Raster raster, int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            // Image space maps the unit square onto the image with row 0 at the top, so take the three
            // corners in device space and map them back to user space (correct for any /Rotate).
            var p00 = raster.ToPage(x, y + height);
            var p10 = raster.ToPage(x + width, y + height);
            var p01 = raster.ToPage(x, y);

            // The image object keeps a reference to the bitmap and reads it when saving, so PDFium must own
            // the memory (an external buffer would be gone by then). 24-bit BGR maps directly to DeviceRGB.
            var image = fpdf_edit.FPDFPageObjNewImageObj(_owner.Handle);
            var bitmap = fpdfview.FPDFBitmapCreateEx(width, height, FPDFBitmap_BGR, IntPtr.Zero, 0);
            try
            {
                var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                var row = new byte[width * 3];
                for (int r = 0; r < height; r++)
                {
                    for (int c = 0; c < width; c++)
                    {
                        int s = ((y + r) * raster.Width + x + c) * 4;
                        row[c * 3] = raster.Pixels[s]; row[c * 3 + 1] = raster.Pixels[s + 1]; row[c * 3 + 2] = raster.Pixels[s + 2];
                    }
                    Marshal.Copy(row, 0, buffer + r * stride, row.Length);
                }
                if (fpdf_edit.FPDFImageObjSetBitmap(null!, 0, image, bitmap) == 0)
                    throw new InvalidOperationException("PDFium could not create an image.");
            }
            finally { fpdfview.FPDFBitmapDestroy(bitmap); }
            fpdf_edit.FPDFImageObjSetMatrix(image, p10.X - p00.X, p10.Y - p00.Y, p01.X - p00.X, p01.Y - p00.Y, p00.X, p00.Y);
            fpdf_edit.FPDFPageInsertObject(Handle, image);
            _changed = true;
        }

        /// <summary>Adds a filled rectangle (user space).</summary>
        public void InsertRect(PdfRect rect, byte r, byte g, byte b)
        {
            var path = fpdf_edit.FPDFPageObjCreateNewRect((float)rect.Left, (float)rect.Bottom, (float)rect.Width, (float)rect.Height);
            fpdf_edit.FPDFPageObjSetFillColor(path, r, g, b, 255);
            fpdf_edit.FPDFPathSetDrawMode(path, FPDF_FILLMODE_WINDING, 0);
            fpdf_edit.FPDFPageInsertObject(Handle, path);
            _changed = true;
        }

        internal void MarkChanged() => _changed = true;

        internal void CommitIfChanged()
        {
            if (!_changed) return;
            if (fpdf_edit.FPDFPageGenerateContent(Handle) == 0)
                throw new InvalidOperationException($"PDFium could not rewrite the content of page {Index + 1}.");
            _changed = false;
        }

        (double X, double Y) DeviceToPage(int w, int h, int x, int y)
        {
            double px = 0, py = 0;
            fpdfview.FPDF_DeviceToPage(Handle, 0, 0, w, h, 0, x, y, ref px, ref py);
            return (px, py);
        }

        (int X, int Y) PageToDevice(int w, int h, double x, double y)
        {
            int dx = 0, dy = 0;
            fpdfview.FPDF_PageToDevice(Handle, 0, 0, w, h, 0, x, y, ref dx, ref dy);
            return (dx, dy);
        }

        /// <summary>A rendering of the page with the mapping between its pixels and user space.</summary>
        public sealed class Raster(EditorPage page, int width, int height, byte[] pixels)
        {
            public int Width { get; } = width;
            public int Height { get; } = height;
            public byte[] Pixels { get; } = pixels;

            public (double X, double Y) ToPage(int x, int y) => page.DeviceToPage(Width, Height, x, y);

            /// <summary>The device-pixel rectangle covering a user-space rectangle, grown by <paramref name="margin"/> px and clipped.</summary>
            public (int X, int Y, int Width, int Height) ToDevice(PdfRect rect, int margin = 1)
            {
                var a = page.PageToDevice(Width, Height, rect.Left, rect.Bottom);
                var b = page.PageToDevice(Width, Height, rect.Right, rect.Top);
                int x1 = Math.Clamp(Math.Min(a.X, b.X) - margin, 0, Width), x2 = Math.Clamp(Math.Max(a.X, b.X) + margin, 0, Width);
                int y1 = Math.Clamp(Math.Min(a.Y, b.Y) - margin, 0, Height), y2 = Math.Clamp(Math.Max(a.Y, b.Y) + margin, 0, Height);
                return (x1, y1, x2 - x1, y2 - y1);
            }

            /// <summary>Paints a user-space rectangle with a solid color in this raster.</summary>
            public void Fill(PdfRect rect, byte r, byte g, byte b, int margin = 1)
            {
                var (x, y, w, h) = ToDevice(rect, margin);
                for (int row = y; row < y + h; row++)
                    for (int col = x; col < x + w; col++)
                    {
                        int o = (row * Width + col) * 4;
                        Pixels[o] = b; Pixels[o + 1] = g; Pixels[o + 2] = r; Pixels[o + 3] = 255;
                    }
            }
        }
    }
}
