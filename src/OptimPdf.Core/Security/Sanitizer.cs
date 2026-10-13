using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace OptimPdf.Core.Security;

public sealed record SanitizeOptions(
    bool DocumentInfo = true,
    bool XmpMetadata = true,
    bool EmbeddedFiles = true,
    bool JavaScriptAndActions = true,
    bool PrivateAppData = true,
    bool Thumbnails = true,
    bool Comments = false);

public sealed record SanitizeReport(int InfoFields, int XmpStreams, int EmbeddedFiles, int Scripts, int PrivateData, int Thumbnails, int Comments)
{
    public int Total => InfoFields + XmpStreams + EmbeddedFiles + Scripts + PrivateData + Thumbnails + Comments;
}

/// <summary>
/// Removes hidden information: document properties, XMP metadata, attachments, scripts and
/// automatic actions, application-private data (/PieceInfo), embedded thumbnails and, optionally, comments.
/// Unreferenced objects are dropped by the writer, so removed data is not left in the saved file.
/// </summary>
public static class Sanitizer
{
    static readonly string[] InfoKeys = ["/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/Producer", "/CreationDate", "/ModDate", "/Trapped"];

    public static SanitizeReport Sanitize(PdfDocument doc, SanitizeOptions options)
    {
        int info = 0, xmp = 0, files = 0, scripts = 0, priv = 0, thumbs = 0, comments = 0;
        var catalog = doc.Internals.Catalog;

        if (options.DocumentInfo)
        {
            // Standard and custom properties alike.
            foreach (var key in doc.Info.Elements.Keys.ToList())
            {
                doc.Info.Elements.Remove(key);
                info++;
            }
        }

        if (options.XmpMetadata)
        {
            if (catalog.Elements.Remove("/Metadata")) xmp++;
            foreach (var page in doc.Pages)
                if (page.Elements.Remove("/Metadata")) xmp++;
            // Metadata on images and forms.
            foreach (var dict in AllDictionaries(doc))
                if (dict.Elements.ContainsKey("/Subtype") && dict.Elements.Remove("/Metadata")) xmp++;
        }

        var names = catalog.Elements.GetDictionary("/Names");
        if (options.EmbeddedFiles)
        {
            if (names?.Elements.Remove("/EmbeddedFiles") == true) files++;
            if (catalog.Elements.Remove("/AF")) files++;
            foreach (var page in doc.Pages)
            {
                var annots = page.Annotations;
                for (int i = annots.Count - 1; i >= 0; i--)
                    if (annots[i].Elements.GetName("/Subtype") == "/FileAttachment") { annots.Remove(annots[i]); files++; }
            }
        }

        if (options.JavaScriptAndActions)
        {
            if (names?.Elements.Remove("/JavaScript") == true) scripts++;
            if (catalog.Elements.Remove("/OpenAction")) scripts++;
            if (catalog.Elements.Remove("/AA")) scripts++;
            foreach (var page in doc.Pages)
                if (page.Elements.Remove("/AA")) scripts++;
            foreach (var dict in AllDictionaries(doc))
            {
                // Annotation and field actions that run scripts or launch programs.
                if (dict.Elements.GetDictionary("/A") is { } action && action.Elements.GetName("/S") is "/JavaScript" or "/Launch" or "/SubmitForm" or "/ImportData")
                {
                    dict.Elements.Remove("/A");
                    scripts++;
                }
                if (dict.Elements.ContainsKey("/AA") && dict.Elements.Remove("/AA")) scripts++;
            }
        }

        if (options.PrivateAppData)
        {
            if (catalog.Elements.Remove("/PieceInfo")) priv++;
            foreach (var page in doc.Pages)
                if (page.Elements.Remove("/PieceInfo")) priv++;
            foreach (var dict in AllDictionaries(doc))
                if (dict.Elements.ContainsKey("/PieceInfo") && dict.Elements.Remove("/PieceInfo")) priv++;
        }

        if (options.Thumbnails)
            foreach (var page in doc.Pages)
                if (page.Elements.Remove("/Thumb")) thumbs++;

        if (options.Comments)
        {
            foreach (var page in doc.Pages)
            {
                var annots = page.Annotations;
                for (int i = annots.Count - 1; i >= 0; i--)
                {
                    var subtype = annots[i].Elements.GetName("/Subtype");
                    if (subtype is "/Link" or "/Widget") continue; // Links and form fields are content, not comments.
                    annots.Remove(annots[i]);
                    comments++;
                }
            }
        }

        return new SanitizeReport(info, xmp, files, scripts, priv, thumbs, comments);
    }

    /// <summary>Lists the hidden data a sanitize pass would remove, without changing the document.</summary>
    public static IReadOnlyList<string> Inspect(PdfDocument doc)
    {
        var findings = new List<string>();
        foreach (var key in InfoKeys)
            if (doc.Info.Elements.GetString(key) is { Length: > 0 } value) findings.Add($"Property {key.TrimStart('/')}: {value}");
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.ContainsKey("/Metadata")) findings.Add("XMP metadata");
        var names = catalog.Elements.GetDictionary("/Names");
        if (names?.Elements.ContainsKey("/EmbeddedFiles") == true) findings.Add("Attached files");
        if (names?.Elements.ContainsKey("/JavaScript") == true || catalog.Elements.ContainsKey("/OpenAction")) findings.Add("Scripts or open actions");
        if (catalog.Elements.ContainsKey("/PieceInfo") || doc.Pages.Cast<PdfPage>().Any(p => p.Elements.ContainsKey("/PieceInfo")))
            findings.Add("Application-private data");
        int thumbs = doc.Pages.Cast<PdfPage>().Count(p => p.Elements.ContainsKey("/Thumb"));
        if (thumbs > 0) findings.Add($"{thumbs} embedded page thumbnail(s)");
        return findings;
    }

    static IEnumerable<PdfDictionary> AllDictionaries(PdfDocument doc) =>
        doc.Internals.GetAllObjects().OfType<PdfDictionary>().ToList();
}
