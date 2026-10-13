using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Win32;
using PdfSharp.Fonts;

namespace OptimPdf.Core.Editing;

/// <summary>
/// Resolves font families to the fonts installed in Windows (machine-wide and per-user), using the
/// same registry the system font picker uses. Faces inside TrueType collections (.ttc) are extracted
/// into standalone fonts because the PDF writer embeds single fonts only.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    public const string FallbackFamily = "Arial";

    readonly Dictionary<(string Family, bool Bold, bool Italic), string> _faces = new();
    readonly ConcurrentDictionary<string, byte[]> _cache = new();
    readonly SortedSet<string> _familyNames = new(StringComparer.OrdinalIgnoreCase);

    public WindowsFontResolver()
    {
        var systemFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        Load(Registry.LocalMachine, systemFonts);
        Load(Registry.CurrentUser, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"));
    }

    /// <summary>Installed family names as Windows displays them.</summary>
    public IEnumerable<string> Families => _familyNames;

    public bool HasFamily(string family) => _familyNames.Contains(family);

    void Load(RegistryKey root, string defaultDirectory)
    {
        using var key = root.OpenSubKey(FontsKey);
        if (key is null) return;
        foreach (var valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is not string file) continue;
            if (!file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.IsPathRooted(file) ? file : Path.Combine(defaultDirectory, file);

            // "Segoe UI Bold Italic (TrueType)" or, for collections, "Microsoft YaHei & Microsoft YaHei UI (TrueType)".
            var name = valueName;
            int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) name = name[..paren];
            var names = name.Split(" & ", StringSplitOptions.TrimEntries);
            for (int i = 0; i < names.Length; i++)
            {
                var (family, bold, italic) = ParseStyle(names[i]);
                _faces.TryAdd((family.ToLowerInvariant(), bold, italic), $"{path}#{i}");
                _familyNames.Add(family);
            }
        }
    }

    static (string Family, bool Bold, bool Italic) ParseStyle(string name)
    {
        bool bold = false, italic = false;
        while (true)
        {
            if (name.EndsWith(" Italic", StringComparison.OrdinalIgnoreCase)) { italic = true; name = name[..^7]; }
            else if (name.EndsWith(" Oblique", StringComparison.OrdinalIgnoreCase)) { italic = true; name = name[..^8]; }
            else if (name.EndsWith(" Bold", StringComparison.OrdinalIgnoreCase)) { bold = true; name = name[..^5]; }
            else if (name.EndsWith(" Regular", StringComparison.OrdinalIgnoreCase)) name = name[..^8];
            else break;
        }
        return (name, bold, italic);
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var family = familyName.ToLowerInvariant();
        if (_faces.TryGetValue((family, bold, italic), out var face)) return new FontResolverInfo(face);
        // Missing style: simulate it from the closest available face.
        if (_faces.TryGetValue((family, bold, false), out face)) return new FontResolverInfo(face, false, italic);
        if (_faces.TryGetValue((family, false, italic), out face)) return new FontResolverInfo(face, bold, false);
        if (_faces.TryGetValue((family, false, false), out face)) return new FontResolverInfo(face, bold, italic);
        return family == FallbackFamily.ToLowerInvariant() ? null : ResolveTypeface(FallbackFamily, bold, italic);
    }

    public byte[]? GetFont(string faceName) => _cache.GetOrAdd(faceName, Load);

    readonly ConcurrentDictionary<string, HashSet<int>?> _coverage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many of the text's characters the family's regular face has glyphs for.</summary>
    public int CoveredCount(string family, string text)
    {
        var map = _coverage.GetOrAdd(family, f =>
        {
            if (!_faces.TryGetValue((f.ToLowerInvariant(), false, false), out var face) &&
                !_faces.TryGetValue((f.ToLowerInvariant(), true, false), out face)) return null;
            try { return ReadCmap(GetFont(face)!); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException) { return null; }
        });
        if (map is null) return -1;
        int covered = 0;
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value <= 0x20 || map.Contains(rune.Value)) covered++;
        return covered;
    }

    readonly ConcurrentDictionary<string, bool> _embeddable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// False when the font's license forbids embedding it in documents (OS/2 fsType "restricted license",
    /// ISO/IEC 14496-22 §5.2.8). Such fonts are never written into a PDF.
    /// </summary>
    public bool IsEmbeddable(string family) => _embeddable.GetOrAdd(family, f =>
    {
        if (!_faces.TryGetValue((f.ToLowerInvariant(), false, false), out var face) &&
            !_faces.TryGetValue((f.ToLowerInvariant(), true, false), out face)) return false;
        try
        {
            var font = GetFont(face)!;
            var span = font.AsSpan();
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
            for (int t = 0; t < numTables; t++)
            {
                var rec = span[(12 + 16 * t)..];
                if (BinaryPrimitives.ReadUInt32BigEndian(rec) != 0x4F532F32) continue; // 'OS/2'
                int os2 = (int)BinaryPrimitives.ReadUInt32BigEndian(rec[8..]);
                int fsType = BinaryPrimitives.ReadUInt16BigEndian(span[(os2 + 8)..]);
                return (fsType & 0x000F) != 0x0002;
            }
            return true; // No OS/2 table: no restriction declared.
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException) { return false; }
    });

    /// <summary>Unicode code points mapped by a TrueType/OpenType font's cmap (formats 4 and 12).</summary>
    internal static HashSet<int> ReadCmap(byte[] font)
    {
        var span = font.AsSpan();
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        int cmap = -1;
        for (int t = 0; t < numTables; t++)
        {
            var rec = span[(12 + 16 * t)..];
            if (BinaryPrimitives.ReadUInt32BigEndian(rec) == 0x636D6170) cmap = (int)BinaryPrimitives.ReadUInt32BigEndian(rec[8..]); // 'cmap'
        }
        if (cmap < 0) throw new InvalidDataException("Font has no cmap table.");

        var result = new HashSet<int>();
        int subtables = BinaryPrimitives.ReadUInt16BigEndian(span[(cmap + 2)..]);
        int best = -1, bestFormat = 0;
        for (int i = 0; i < subtables; i++)
        {
            var rec = span[(cmap + 4 + 8 * i)..];
            int platform = BinaryPrimitives.ReadUInt16BigEndian(rec), encoding = BinaryPrimitives.ReadUInt16BigEndian(rec[2..]);
            int offset = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(rec[4..]);
            int format = BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);
            bool unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (unicode && (format == 12 || (format == 4 && bestFormat != 12))) { best = offset; bestFormat = format; }
        }
        if (best < 0) throw new InvalidDataException("Font has no Unicode cmap.");

        if (bestFormat == 12)
        {
            int groups = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(best + 12)..]);
            for (int g = 0; g < groups; g++)
            {
                var grp = span[(best + 16 + 12 * g)..];
                uint start = BinaryPrimitives.ReadUInt32BigEndian(grp), end = BinaryPrimitives.ReadUInt32BigEndian(grp[4..]);
                for (uint c = start; c <= end && c <= 0x10FFFF; c++) result.Add((int)c);
            }
        }
        else
        {
            int segX2 = BinaryPrimitives.ReadUInt16BigEndian(span[(best + 6)..]);
            int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, rangeOffsets = deltas + segX2;
            for (int s = 0; s < segX2 / 2; s++)
            {
                int end = BinaryPrimitives.ReadUInt16BigEndian(span[(ends + 2 * s)..]);
                int start = BinaryPrimitives.ReadUInt16BigEndian(span[(starts + 2 * s)..]);
                int delta = BinaryPrimitives.ReadInt16BigEndian(span[(deltas + 2 * s)..]);
                int ro = BinaryPrimitives.ReadUInt16BigEndian(span[(rangeOffsets + 2 * s)..]);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (ro == 0) glyph = (c + delta) & 0xFFFF;
                    else
                    {
                        int at = rangeOffsets + 2 * s + ro + 2 * (c - start);
                        glyph = BinaryPrimitives.ReadUInt16BigEndian(span[at..]);
                        if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    }
                    if (glyph != 0) result.Add(c);
                }
            }
        }
        return result;
    }

    static byte[] Load(string faceName)
    {
        int hash = faceName.LastIndexOf('#');
        var path = faceName[..hash];
        int index = int.Parse(faceName[(hash + 1)..]);
        var data = File.ReadAllBytes(path);
        return data.Length > 4 && data.AsSpan(0, 4).SequenceEqual("ttcf"u8) ? ExtractFromCollection(data, index) : data;
    }

    /// <summary>Rebuilds face <paramref name="index"/> of a TrueType collection as a standalone font file.</summary>
    internal static byte[] ExtractFromCollection(byte[] ttc, int index)
    {
        int fontCount = (int)BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(8));
        if ((uint)index >= (uint)fontCount) throw new InvalidDataException("Font index outside collection.");
        int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(ttc.AsSpan(12 + 4 * index));

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(ttc.AsSpan(offset + 4));
        int headerSize = 12 + 16 * numTables;
        var tables = new List<(uint Tag, uint Checksum, int Offset, int Length)>();
        for (int t = 0; t < numTables; t++)
        {
            var rec = ttc.AsSpan(offset + 12 + 16 * t);
            tables.Add((BinaryPrimitives.ReadUInt32BigEndian(rec), BinaryPrimitives.ReadUInt32BigEndian(rec[4..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(rec[8..]), (int)BinaryPrimitives.ReadUInt32BigEndian(rec[12..])));
        }

        int total = headerSize + tables.Sum(t => (t.Length + 3) & ~3);
        var output = new byte[total];
        ttc.AsSpan(offset, 12).CopyTo(output); // sfnt version + table counts
        int cursor = headerSize;
        for (int t = 0; t < tables.Count; t++)
        {
            var (tag, checksum, srcOffset, length) = tables[t];
            var rec = output.AsSpan(12 + 16 * t);
            BinaryPrimitives.WriteUInt32BigEndian(rec, tag);
            BinaryPrimitives.WriteUInt32BigEndian(rec[4..], checksum);
            BinaryPrimitives.WriteUInt32BigEndian(rec[8..], (uint)cursor);
            BinaryPrimitives.WriteUInt32BigEndian(rec[12..], (uint)length);
            ttc.AsSpan(srcOffset, length).CopyTo(output.AsSpan(cursor));
            cursor += (length + 3) & ~3;
        }
        return output;
    }
}
