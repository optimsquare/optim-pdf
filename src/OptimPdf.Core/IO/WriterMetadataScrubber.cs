using System.Text;
using System.Text.RegularExpressions;

namespace OptimPdf.Core.IO;

/// <summary>
/// The PDF writer always stamps an Info dictionary (Creator, Producer, dates) and an XMP metadata stream
/// into every file it saves, with no option to turn this off. For documents that should carry no such
/// metadata (new documents, or ones the user sanitized) this removes them from the saved bytes.
/// Edits are same-length byte replacements, so every cross-reference offset stays valid: references are
/// blanked out and the objects' bodies are replaced by an empty dictionary padded with spaces.
/// Only classic cross-reference tables (what the writer produces) are handled; anything else is left unchanged.
/// </summary>
public static class WriterMetadataScrubber
{
    public static byte[] Scrub(byte[] pdf, bool removeInfo, bool removeXmp, bool inPlace = false)
    {
        var latin = Encoding.Latin1.GetString(pdf); // 1 char per byte, so indices are byte offsets.

        int trailerAt = latin.LastIndexOf("trailer", StringComparison.Ordinal);
        int xrefAt = latin.LastIndexOf("\nxref", StringComparison.Ordinal);
        if (trailerAt < 0 || xrefAt < 0 || xrefAt > trailerAt) return pdf; // Not a classic xref layout.
        int trailerEnd = latin.IndexOf("startxref", trailerAt, StringComparison.Ordinal);
        if (trailerEnd < 0) return pdf;
        var output = inPlace ? pdf : (byte[])pdf.Clone(); // Same-length edits only.

        // The writer appends a "% Created with …" comment after the trailer; comments are insignificant.
        var comment = Regex.Match(latin[trailerAt..trailerEnd], @"%[^\r\n]*");
        if (comment.Success) Blank(output, trailerAt + comment.Index, comment.Length);
        if (!removeInfo && !removeXmp) return output;
        var offsets = ParseXref(latin, xrefAt + 1);

        if (removeInfo)
        {
            var info = Regex.Match(latin[trailerAt..trailerEnd], @"/Info\s+(\d+)\s+(\d+)\s+R");
            if (info.Success)
            {
                Blank(output, trailerAt + info.Index, info.Length);
                EmptyObject(output, latin, offsets, int.Parse(info.Groups[1].Value));
            }
        }

        if (removeXmp)
        {
            var root = Regex.Match(latin[trailerAt..trailerEnd], @"/Root\s+(\d+)\s+\d+\s+R");
            if (root.Success && offsets.TryGetValue(int.Parse(root.Groups[1].Value), out int rootAt))
            {
                int rootEnd = latin.IndexOf("endobj", rootAt, StringComparison.Ordinal);
                var meta = Regex.Match(latin[rootAt..rootEnd], @"/Metadata\s+(\d+)\s+\d+\s+R");
                if (meta.Success)
                {
                    Blank(output, rootAt + meta.Index, meta.Length);
                    EmptyObject(output, latin, offsets, int.Parse(meta.Groups[1].Value));
                }
            }
        }
        return output;
    }

    /// <summary>Object number → byte offset, from the classic xref table starting at <paramref name="at"/>.</summary>
    static Dictionary<int, int> ParseXref(string pdf, int at)
    {
        var offsets = new Dictionary<int, int>();
        int pos = at + 4; // "xref"
        while (true)
        {
            var header = Regex.Match(pdf[pos..Math.Min(pdf.Length, pos + 64)], @"^\s*(\d+)\s+(\d+)\s*\r?\n");
            if (!header.Success) break;
            int first = int.Parse(header.Groups[1].Value), count = int.Parse(header.Groups[2].Value);
            pos += header.Length;
            for (int i = 0; i < count; i++)
            {
                // Each entry is exactly 20 bytes: "oooooooooo ggggg n\r\n".
                var entry = pdf.Substring(pos, 20);
                if (entry[17] == 'n') offsets[first + i] = int.Parse(entry[..10]);
                pos += 20;
            }
        }
        return offsets;
    }

    static void EmptyObject(byte[] output, string pdf, Dictionary<int, int> offsets, int number)
    {
        if (!offsets.TryGetValue(number, out int start)) return;
        int body = pdf.IndexOf("obj", start, StringComparison.Ordinal) + 3;
        int end = pdf.IndexOf("endobj", body, StringComparison.Ordinal);
        if (body < 3 || end < 0) return;
        Blank(output, body, end - body);
        "\n<<>>\n"u8.CopyTo(output.AsSpan(body, Math.Min(6, end - body)));
    }

    static void Blank(byte[] output, int start, int length) => output.AsSpan(start, length).Fill((byte)' ');
}
