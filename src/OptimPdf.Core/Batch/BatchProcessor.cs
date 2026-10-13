using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using OptimPdf.Core.Conversion;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.IO;
using OptimPdf.Core.Ocr;
using OptimPdf.Core.Optimization;
using OptimPdf.Core.Pages;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Security;

namespace OptimPdf.Core.Batch;

/// <summary>One thing to do to every file in a batch.</summary>
public abstract record BatchOperation
{
    public abstract string Name { get; }
    /// <summary>Extension of the output (".pdf" unless the operation converts).</summary>
    public virtual string OutputExtension => ".pdf";
    /// <summary>True when the input may be something other than a PDF.</summary>
    public virtual bool AcceptsNonPdf => false;
}

public sealed record CompressOperation(OptimizeOptions Options) : BatchOperation { public override string Name => "Compress"; }
public sealed record ProtectOperation(ProtectionOptions Options) : BatchOperation { public override string Name => "Add password"; }
public sealed record RemoveHiddenInfoOperation(SanitizeOptions Options) : BatchOperation { public override string Name => "Remove hidden information"; }
public sealed record OcrOperation(OcrOptions Options) : BatchOperation { public override string Name => "Recognize text (OCR)"; }
public sealed record RotateOperation(int Degrees) : BatchOperation { public override string Name => "Rotate pages"; }
/// <summary>Copies files under new names (the template decides the name).</summary>
public sealed record RenameOperation : BatchOperation
{
    public override string Name => "Rename";
    public override bool AcceptsNonPdf => true;
}
/// <summary>Office documents and images to PDF.</summary>
public sealed record ConvertToPdfOperation(PageSizeMode ImagePageSize = PageSizeMode.MatchImage) : BatchOperation
{
    public override string Name => "Convert to PDF";
    public override bool AcceptsNonPdf => true;
}
public sealed record ExportImagesOperation(ImageFileFormat Format, double Dpi = 150, int JpegQuality = 90) : BatchOperation
{
    public override string Name => "Export pages as images";
    public override string OutputExtension => Format switch { ImageFileFormat.Jpeg => ".jpg", ImageFileFormat.Tiff => ".tif", ImageFileFormat.Bmp => ".bmp", _ => ".png" };
}
/// <summary>All inputs, in order, become one PDF.</summary>
public sealed record MergeOperation(string OutputName = "Merged") : BatchOperation { public override string Name => "Merge into one PDF"; }

public sealed record BatchItemResult(string Input, IReadOnlyList<string> Outputs, bool Success, string? Message, TimeSpan Duration);

public sealed record BatchProgress(int Done, int Total, string? Current);

/// <summary>
/// Runs one operation over many files. Inputs are never modified: results go to the output folder, named by
/// <see cref="NameTemplate"/>, and an existing file is never overwritten (a number is added instead).
/// A failing file is reported and the batch continues.
/// </summary>
/// <remarks>
/// Name template fields: {name} original name without extension, {n} position in the batch (1-based; {n:000}
/// pads), {date} today (yyyy-MM-dd), {title} the PDF title (or the name when there is none).
/// </remarks>
public sealed class BatchProcessor(BatchOperation operation, string outputFolder, string nameTemplate = "{name}")
{
    public BatchOperation Operation { get; } = operation;
    public string OutputFolder { get; } = Path.GetFullPath(outputFolder);
    public string NameTemplate { get; } = string.IsNullOrWhiteSpace(nameTemplate) ? "{name}" : nameTemplate;

    public static readonly IReadOnlyList<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public async Task<IReadOnlyList<BatchItemResult>> RunAsync(IReadOnlyList<string> inputs, IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(OutputFolder);
        var inputSet = inputs.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<BatchItemResult>();

        if (Operation is MergeOperation merge)
        {
            progress?.Report(new BatchProgress(0, 1, merge.OutputName));
            var sw = Stopwatch.StartNew();
            try
            {
                var docs = inputs.Select(i => PageOperations.OpenForImport(i)).ToList();
                try
                {
                    using var merged = PageOperations.Merge(docs);
                    var path = Reserve(Fill(merge.OutputName, inputs.Count > 0 ? inputs[0] : merge.OutputName, 1, null) + ".pdf", inputSet);
                    int pageCount = merged.PageCount; // The model is sealed once saved.
                    SafeFileWriter.Write(path, DocumentSession.Serialize(merged), keepBackup: false);
                    results.Add(new BatchItemResult(string.Join("; ", inputs.Select(Path.GetFileName)), [path], true, $"{pageCount} pages", sw.Elapsed));
                }
                finally { foreach (var d in docs) d.Dispose(); }
            }
            catch (Exception e) when (IsFileProblem(e))
            {
                results.Add(new BatchItemResult(string.Join("; ", inputs.Select(Path.GetFileName)), [], false, Describe(e), sw.Elapsed));
            }
            progress?.Report(new BatchProgress(1, 1, null));
            return results;
        }

        for (int i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = Path.GetFullPath(inputs[i]);
            progress?.Report(new BatchProgress(i, inputs.Count, Path.GetFileName(input)));
            var sw = Stopwatch.StartNew();
            try
            {
                var outputs = await ProcessAsync(input, i + 1, inputSet, cancellationToken);
                results.Add(new BatchItemResult(input, outputs, true, null, sw.Elapsed));
            }
            catch (Exception e) when (IsFileProblem(e))
            {
                results.Add(new BatchItemResult(input, [], false, Describe(e), sw.Elapsed));
            }
        }
        progress?.Report(new BatchProgress(inputs.Count, inputs.Count, null));
        return results;
    }

    async Task<IReadOnlyList<string>> ProcessAsync(string input, int position, HashSet<string> inputs, CancellationToken ct)
    {
        bool isPdf = Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isPdf && !Operation.AcceptsNonPdf) throw new InvalidDataException("Not a PDF file.");

        switch (Operation)
        {
            case RenameOperation:
            {
                var path = Reserve(Fill(NameTemplate, input, position, isPdf ? TitleOf(input) : null) + Path.GetExtension(input), inputs);
                File.Copy(input, path);
                return [path];
            }
            case ConvertToPdfOperation convert:
            {
                var path = Reserve(Fill(NameTemplate, input, position, null) + ".pdf", inputs);
                var ext = Path.GetExtension(input).ToLowerInvariant();
                if (ImageExtensions.Contains(ext))
                {
                    using var doc = ImageConversion.ImagesToPdf([File.ReadAllBytes(input)], convert.ImagePageSize);
                    SafeFileWriter.Write(path, DocumentSession.Serialize(doc), keepBackup: false);
                }
                else if (OfficeConversion.AppFor(input) is not null) await OfficeConversion.ToPdfAsync(input, path, ct);
                else if (isPdf) File.Copy(input, path);
                else throw new InvalidDataException($"{ext} files cannot be converted to PDF.");
                return [path];
            }
            case ExportImagesOperation export:
            {
                using var doc = PdfiumDocument.Open(File.ReadAllBytes(input));
                var stem = Fill(NameTemplate, input, position, TitleOf(input));
                var pages = Enumerable.Range(0, doc.PageCount).ToList();
                var paths = pages.Select(p => Reserve($"{stem}-{p + 1:000}{export.OutputExtension}", inputs)).ToList();
                return ImageConversion.PdfToImages(doc, pages, export.Dpi, export.Format, p => paths[p], export.JpegQuality, ct);
            }
            default:
            {
                var data = File.ReadAllBytes(input);
                byte[] output = Operation switch
                {
                    CompressOperation c => DocumentSession.Transform(data, null, doc => PdfOptimizer.Optimize(doc, c.Options)),
                    ProtectOperation p => DocumentSession.Transform(data, null, _ => { }, p.Options),
                    RemoveHiddenInfoOperation s => DocumentSession.Transform(data, null, doc => Sanitizer.Sanitize(doc, s.Options)),
                    RotateOperation r => DocumentSession.Transform(data, null, doc => PageOperations.Rotate(doc, Enumerable.Range(0, doc.PageCount), r.Degrees)),
                    OcrOperation o => await OcrAsync(data, o.Options, ct),
                    _ => throw new NotSupportedException(Operation.Name),
                };
                var path = Reserve(Fill(NameTemplate, input, position, TitleOf(input)) + ".pdf", inputs);
                SafeFileWriter.Write(path, output, keepBackup: false);
                return [path];
            }
        }
    }

    static async Task<byte[]> OcrAsync(byte[] data, OcrOptions options, CancellationToken ct)
    {
        IReadOnlyList<OcrPage> pages;
        using (var doc = PdfiumDocument.Open(data))
            pages = await OcrService.RecognizeAsync(doc, Enumerable.Range(0, doc.PageCount), options, null, ct);
        if (pages.All(p => p.Skipped || p.WordCount == 0)) return data; // Nothing to add: keep the file as it is.
        return DocumentSession.Transform(data, null, doc => OcrService.AddTextLayer(doc, pages));
    }

    static string? TitleOf(string path)
    {
        try
        {
            using var doc = PdfiumDocument.Open(File.ReadAllBytes(path));
            return doc.GetMetadata("Title");
        }
        catch (Exception e) when (IsFileProblem(e)) { return null; }
    }

    /// <summary>Expands the name template and removes characters Windows does not allow in file names.</summary>
    public static string Fill(string template, string input, int position, string? title)
    {
        var name = Path.GetFileNameWithoutExtension(input);
        var result = Regex.Replace(template, @"\{(name|n|date|title)(?::(0+))?\}", m => m.Groups[1].Value switch
        {
            "name" => name,
            "n" => position.ToString(m.Groups[2].Success ? new string('0', m.Groups[2].Value.Length) : "0", CultureInfo.InvariantCulture),
            "date" => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => string.IsNullOrWhiteSpace(title) ? name : title,
        });
        foreach (var c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        result = result.Trim().TrimEnd('.');
        return result.Length == 0 ? name : result;
    }

    /// <summary>A free path in the output folder: never an input, never an existing file.</summary>
    string Reserve(string fileName, HashSet<string> inputs)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var path = Path.Combine(OutputFolder, fileName);
        for (int i = 2; File.Exists(path) || inputs.Contains(path) || _reserved.Contains(path); i++)
            path = Path.Combine(OutputFolder, $"{stem} ({i}){ext}");
        _reserved.Add(path);
        return path;
    }

    readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    static bool IsFileProblem(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException
        or PdfOpenException or PdfPasswordRequiredException or PdfSharp.Pdf.IO.PdfReaderException or OfficeConversionException or OcrUnavailableException
        or InvalidOperationException or ArgumentException;

    static string Describe(Exception e) => e switch
    {
        PdfPasswordRequiredException or PdfSharp.Pdf.IO.PdfReaderException when e.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
            => "The file is password protected.",
        PdfPasswordRequiredException => "The file is password protected.",
        PdfOpenException or InvalidDataException => "The file is damaged or is not a supported file: " + e.Message,
        _ => e.Message,
    };
}
