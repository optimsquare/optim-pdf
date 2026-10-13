using System.Drawing;
using System.Security.Cryptography;
using OptimPdf.Core.Editing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Filters;

namespace OptimPdf.Core.Optimization;

public sealed record OptimizeOptions(
    /// <summary>Images above this resolution are downsampled to it; null keeps resolution.</summary>
    int? MaxImageDpi = 150,
    /// <summary>JPEG quality for recompressed images (1–100); null keeps images lossless.</summary>
    int? JpegQuality = 75,
    bool RemoveThumbnails = true,
    bool RemovePrivateData = true,
    bool MergeDuplicateImages = true)
{
    public static readonly OptimizeOptions HighQuality = new(MaxImageDpi: 300, JpegQuality: 90);
    public static readonly OptimizeOptions Balanced = new(MaxImageDpi: 150, JpegQuality: 75);
    public static readonly OptimizeOptions Smallest = new(MaxImageDpi: 96, JpegQuality: 55);
    public static readonly OptimizeOptions Lossless = new(MaxImageDpi: null, JpegQuality: null);
}

public sealed record OptimizeReport(int ImagesRecompressed, int ImagesDownsampled, int ImagesSkipped, int DuplicatesMerged, int StreamsCompressed);

/// <summary>
/// Reduces file size. Images are recompressed as JPEG (and downsampled above <see cref="OptimizeOptions.MaxImageDpi"/>)
/// only when the result is clearly smaller. Unsupported image types are left untouched and counted as skipped:
/// CMYK/Lab/indexed/spot-color images, 1/2/4/16-bit images, JPEG 2000, JBIG2, image masks, and images with /Decode arrays.
/// </summary>
public static class PdfOptimizer
{
    public static OptimizeReport Optimize(PdfDocument doc, OptimizeOptions options)
    {
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;

        int recompressed = 0, downsampled = 0, skipped = 0, merged = 0, zipped = 0;

        if (options.RemoveThumbnails)
            foreach (var page in doc.Pages) page.Elements.Remove("/Thumb");
        if (options.RemovePrivateData)
        {
            doc.Internals.Catalog.Elements.Remove("/PieceInfo");
            foreach (var page in doc.Pages) page.Elements.Remove("/PieceInfo");
        }

        // Longest page side in inches; an image covering the full page at this size has the lowest possible DPI.
        double pageInches = doc.Pages.Cast<PdfPage>().Select(p => Math.Max(p.MediaBox.Width, p.MediaBox.Height) / 72.0).DefaultIfEmpty(11).Max();

        var objects = doc.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Stream is not null).ToList();
        var smasks = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var d in objects)
            foreach (var key in new[] { "/SMask", "/Mask" })
                if ((d.Elements[key] as PdfReference)?.Value is PdfDictionary m) smasks.Add(m);

        foreach (var dict in objects)
        {
            if (dict.Elements.GetName("/Subtype") != "/Image") continue;
            if (smasks.Contains(dict)) continue; // Soft masks must stay exact; resampling them separately is not worth the risk.
            if (options.JpegQuality is not { } quality) continue;
            switch (TryRecompress(dict, quality, options.MaxImageDpi, pageInches))
            {
                case Outcome.Recompressed: recompressed++; break;
                case Outcome.Downsampled: recompressed++; downsampled++; break;
                case Outcome.Unsupported: skipped++; break;
            }
        }

        if (options.MergeDuplicateImages) merged = MergeDuplicates(doc, objects);

        foreach (var dict in objects)
        {
            // Compress any stream left uncompressed (except XMP metadata, which should stay readable).
            if (dict.Stream is { Length: > 64 } && !dict.Elements.ContainsKey("/Filter") && dict.Elements.GetName("/Type") != "/Metadata")
            {
                dict.Stream.Zip();
                zipped++;
            }
        }
        return new OptimizeReport(recompressed, downsampled, skipped, merged, zipped);
    }

    enum Outcome { Unchanged, Recompressed, Downsampled, Unsupported }

    static Outcome TryRecompress(PdfDictionary image, int quality, int? maxDpi, double pageInches)
    {
        var e = image.Elements;
        if (e.GetBoolean("/ImageMask") || e.ContainsKey("/Decode") || e.GetInteger("/BitsPerComponent") != 8) return Outcome.Unsupported;
        if (e["/Mask"] is PdfArray) return Outcome.Unsupported; // Color-key masking depends on exact sample values.

        int components = Components(e["/ColorSpace"]);
        if (components is not (1 or 3)) return Outcome.Unsupported;
        int width = e.GetInteger("/Width"), height = e.GetInteger("/Height");
        if (width <= 0 || height <= 0) return Outcome.Unsupported;

        string? filter = SingleFilter(e["/Filter"]);
        var original = image.Stream.Value;
        Bitmap bitmap;
        try
        {
            if (filter == "/DCTDecode") bitmap = ImageCodec.Decode(original);
            else if (filter is "/FlateDecode" or null)
            {
                var raw = filter is null ? original : Filtering.Decode(original, e["/Filter"]!, e["/DecodeParms"]);
                if (raw is null) return Outcome.Unsupported;
                bitmap = ImageCodec.FromRawSamples(raw, width, height, components);
            }
            else return Outcome.Unsupported;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotImplementedException)
        {
            return Outcome.Unsupported;
        }

        using (bitmap)
        {
            bool downsample = false;
            Bitmap target = bitmap;
            if (maxDpi is { } dpi)
            {
                // Pixels needed if the image spans the longest page side at the target resolution.
                double needed = dpi * pageInches;
                int longest = Math.Max(bitmap.Width, bitmap.Height);
                if (longest > needed * 1.15)
                {
                    double scale = needed / longest;
                    target = ImageCodec.Resize(bitmap, Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
                    downsample = true;
                }
            }
            try
            {
                var encoded = ImageCodec.Encode(target, ImageFileFormat.Jpeg, quality);
                // Keep the original unless the gain is real (10 %); recompressing a JPEG at similar quality only loses detail.
                if (!downsample && encoded.Length > original.Length * 0.9) return Outcome.Unchanged;
                if (downsample && encoded.Length >= original.Length) return Outcome.Unchanged;

                image.Stream.Value = encoded;
                e.SetName("/Filter", "/DCTDecode");
                e.Remove("/DecodeParms");
                e.SetInteger("/Width", target.Width);
                e.SetInteger("/Height", target.Height);
                e.SetName("/ColorSpace", "/DeviceRGB");
                e.SetInteger("/BitsPerComponent", 8);
                e.SetInteger("/Length", encoded.Length);
                return downsample ? Outcome.Downsampled : Outcome.Recompressed;
            }
            finally
            {
                if (!ReferenceEquals(target, bitmap)) target.Dispose();
            }
        }
    }

    /// <summary>Points every reference to a byte-identical image at a single copy.</summary>
    static int MergeDuplicates(PdfDocument doc, List<PdfDictionary> streams)
    {
        var canonical = new Dictionary<string, PdfDictionary>();
        var replace = new Dictionary<PdfDictionary, PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var dict in streams)
        {
            if (dict.Elements.GetName("/Subtype") != "/Image" || dict.Reference is null) continue;
            var key = Convert.ToHexString(SHA256.HashData(dict.Stream.Value)) + "|" + DictionarySignature(dict);
            if (canonical.TryGetValue(key, out var first)) replace[dict] = first;
            else canonical[key] = dict;
        }
        if (replace.Count == 0) return 0;

        foreach (var container in doc.Internals.GetAllObjects())
        {
            if (container is PdfDictionary d)
                foreach (var k in d.Elements.Keys.ToList())
                    Redirect(d.Elements[k], v => d.Elements[k] = v, replace);
            else if (container is PdfArray a)
                for (int i = 0; i < a.Elements.Count; i++)
                {
                    int index = i;
                    Redirect(a.Elements[index], v => a.Elements[index] = v, replace);
                }
        }
        return replace.Count;
    }

    static void Redirect(PdfItem? item, Action<PdfItem> set, Dictionary<PdfDictionary, PdfDictionary> replace)
    {
        switch (item)
        {
            case PdfReference r when r.Value is PdfDictionary target && replace.TryGetValue(target, out var keep):
                set(keep.Reference!);
                break;
            case PdfDictionary nested when nested.Reference is null: // Direct (inline) dictionary, e.g. /XObject in /Resources.
                foreach (var k in nested.Elements.Keys.ToList())
                    Redirect(nested.Elements[k], v => nested.Elements[k] = v, replace);
                break;
            case PdfArray array when array.Reference is null:
                for (int i = 0; i < array.Elements.Count; i++)
                {
                    int index = i;
                    Redirect(array.Elements[index], v => array.Elements[index] = v, replace);
                }
                break;
        }
    }

    static string DictionarySignature(PdfDictionary d) =>
        string.Join(";", d.Elements.Keys.Where(k => k != "/Length").OrderBy(k => k).Select(k => $"{k}={d.Elements[k]}"));

    static string? SingleFilter(PdfItem? filter) => filter switch
    {
        null => null,
        PdfName n => n.Value,
        PdfArray { Elements.Count: 1 } a => a.Elements.GetName(0),
        PdfArray { Elements.Count: 0 } => null,
        _ => "(chain)",
    };

    static int Components(PdfItem? colorSpace)
    {
        var cs = colorSpace is PdfReference r ? r.Value : colorSpace;
        switch (cs)
        {
            case PdfName n:
                return n.Value switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
            case PdfArray a when a.Elements.Count >= 2 && a.Elements.GetName(0) == "/ICCBased":
                var icc = (a.Elements[1] as PdfReference)?.Value as PdfDictionary;
                return icc?.Elements.GetInteger("/N") ?? 0;
            default:
                return 0;
        }
    }
}
