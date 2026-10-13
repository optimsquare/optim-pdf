using PdfSharp.Pdf;

namespace OptimPdf.Core.Documents;

public sealed record DocumentProperties(
    string? Title, string? Author, string? Subject, string? Keywords,
    string? Creator, string? Producer, DateTime? Created, DateTime? Modified,
    string PdfVersion, int PageCount, bool Encrypted, long FileSize)
{
    public static DocumentProperties Read(DocumentSession session) => session.Query(doc =>
    {
        var info = doc.Info;
        return new DocumentProperties(
            Empty(info.Title), Empty(info.Author), Empty(info.Subject), Empty(info.Keywords),
            Empty(info.Creator), Empty(info.Elements.GetString("/Producer")),
            Date(info, "/CreationDate", () => info.CreationDate), Date(info, "/ModDate", () => info.ModificationDate),
            $"{doc.Version / 10}.{doc.Version % 10}", doc.PageCount,
            session.Renderer.SecurityRevision >= 0, session.CurrentBytes.LongLength);
    });

    /// <summary>Updates the editable fields of the Info dictionary (null leaves a field unchanged).</summary>
    public static void Write(PdfDocument doc, string? title, string? author, string? subject, string? keywords)
    {
        if (title is not null) doc.Info.Title = title;
        if (author is not null) doc.Info.Author = author;
        if (subject is not null) doc.Info.Subject = subject;
        if (keywords is not null) doc.Info.Keywords = keywords;
    }

    static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static DateTime? Date(PdfDocumentInformation info, string key, Func<DateTime> get)
    {
        if (!info.Elements.ContainsKey(key)) return null;
        try { return get(); } catch (Exception) { return null; } // Malformed dates are common.
    }
}
