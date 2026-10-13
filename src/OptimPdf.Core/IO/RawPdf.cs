using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OptimPdf.Core.IO;

// A minimal, byte-exact PDF object model and reader (ISO 32000-2 §7.3, §7.5). It exists for incremental
// updates (digital signatures), where existing objects must be re-written exactly as they are in the file;
// the general-purpose PDF writer normalizes documents on load and cannot do that.

public abstract class RawObject;

public sealed class RawName(string value) : RawObject
{
    public string Value { get; } = value; // Without the leading slash, #xx escapes decoded.
    public override string ToString() => "/" + Value;
}

public sealed class RawNumber(string text) : RawObject
{
    public string Text { get; } = text; // Kept as written for an exact round trip.
    public double Value => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
    public int IntValue => (int)Value;
    public static RawNumber Of(double v) => new(v.ToString("0.####", CultureInfo.InvariantCulture));
    public override string ToString() => Text;
}

public sealed class RawString(byte[] bytes, bool hex = false) : RawObject
{
    public byte[] Bytes { get; } = bytes;
    public bool Hex { get; } = hex;

    /// <summary>A text string: PDFDocEncoding-compatible ASCII as-is, anything else as UTF-16BE with BOM.</summary>
    public static RawString Text(string s) =>
        s.All(c => c is >= ' ' and < (char)127)
            ? new(Encoding.ASCII.GetBytes(s))
            : new([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(s)], hex: true);

    public string Decode() =>
        Bytes is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2)
        : Bytes is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(Bytes, 3, Bytes.Length - 3)
        : Encoding.Latin1.GetString(Bytes);
}

public sealed class RawBool(bool value) : RawObject { public bool Value { get; } = value; }

public sealed class RawNull : RawObject { public static readonly RawNull Instance = new(); }

public sealed class RawRef(int number, int generation) : RawObject
{
    public int Number { get; } = number;
    public int Generation { get; } = generation;
}

public sealed class RawArray(List<RawObject>? items = null) : RawObject
{
    public List<RawObject> Items { get; } = items ?? [];
}

public sealed class RawDict(List<KeyValuePair<string, RawObject>>? entries = null) : RawObject
{
    public List<KeyValuePair<string, RawObject>> Entries { get; } = entries ?? [];

    public RawObject? this[string key]
    {
        get { foreach (var e in Entries) if (e.Key == key) return e.Value; return null; }
        set
        {
            int i = Entries.FindIndex(e => e.Key == key);
            if (value is null) { if (i >= 0) Entries.RemoveAt(i); return; }
            if (i >= 0) Entries[i] = new(key, value); else Entries.Add(new(key, value));
        }
    }

    public RawDict Copy() => new([.. Entries]);
}

public sealed class RawStream(RawDict dict, byte[] data) : RawObject
{
    public RawDict Dict { get; } = dict;
    public byte[] Data { get; } = data; // As stored (still encoded).
}

/// <summary>A parsed PDF file: cross-reference sections (classic, stream and hybrid), trailer and object access.</summary>
public sealed class RawPdf
{
    readonly byte[] _data;
    readonly Dictionary<int, XrefEntry> _xref = [];
    readonly Dictionary<int, RawObject> _cache = [];
    readonly Dictionary<int, (byte[] Data, int First, List<(int Num, int Offset)> Index)> _objectStreams = [];

    public byte[] Data => _data;
    public RawDict Trailer { get; }
    public RawRef? Root => Trailer["Root"] as RawRef;
    /// <summary>Offset of the newest cross-reference section (the /Prev of an update).</summary>
    public long StartXref { get; }
    /// <summary>True when the newest section is a cross-reference stream (updates must use one too).</summary>
    public bool UsesXrefStream { get; }
    public int Size => Math.Max(Trailer["Size"] is RawNumber n ? n.IntValue : 0, _xref.Count == 0 ? 0 : _xref.Keys.Max() + 1);

    readonly record struct XrefEntry(int Type, long Offset, int Generation); // 1 = in file, 2 = in object stream (Offset = stream, Generation = index).

    public RawPdf(byte[] data)
    {
        _data = data;
        StartXref = FindStartXref();
        RawDict? newest = null;
        var visited = new HashSet<long>();
        long offset = StartXref;
        bool first = true;
        while (offset >= 0 && visited.Add(offset))
        {
            var (trailer, isStream) = ReadXrefSection(offset);
            if (first) { UsesXrefStream = isStream; first = false; }
            newest ??= trailer;
            if (trailer["XRefStm"] is RawNumber hybrid) ReadXrefSection(hybrid.IntValue);
            offset = trailer["Prev"] is RawNumber prev ? (long)prev.Value : -1;
        }
        Trailer = newest ?? throw new InvalidDataException("The file has no trailer.");
    }

    public RawObject Resolve(RawObject? obj) => obj is RawRef r ? Get(r.Number) : obj ?? RawNull.Instance;

    public RawDict? ResolveDict(RawObject? obj) => Resolve(obj) switch { RawDict d => d, RawStream s => s.Dict, _ => null };

    public RawObject Get(int number)
    {
        if (_cache.TryGetValue(number, out var cached)) return cached;
        if (!_xref.TryGetValue(number, out var entry)) return RawNull.Instance;
        RawObject value;
        if (entry.Type == 1)
        {
            var p = new Parser(_data, (int)entry.Offset, this);
            p.ReadToken(); p.ReadToken(); // "N G"
            if (p.ReadToken() is not Token.Keyword("obj")) throw new InvalidDataException($"Object {number} is not where the cross-reference table says.");
            value = p.ReadObjectBody();
        }
        else
        {
            var (data, firstOffset, index) = ObjectStream((int)entry.Offset);
            int at = index.FindIndex(i => i.Num == number);
            if (at < 0) return RawNull.Instance;
            value = new Parser(data, firstOffset + index[at].Offset, this).ReadValue();
        }
        _cache[number] = value;
        return value;
    }

    public int GenerationOf(int number) => _xref.TryGetValue(number, out var e) && e.Type == 1 ? e.Generation : 0;

    /// <summary>Object numbers of the pages, in order.</summary>
    public List<int> PageObjectNumbers()
    {
        var pages = new List<int>();
        var root = ResolveDict(Trailer["Root"]) ?? throw new InvalidDataException("The document has no catalog.");
        Walk(root["Pages"], 0);
        return pages;

        void Walk(RawObject? node, int depth)
        {
            if (depth > 64 || node is not RawRef r) return;
            var d = ResolveDict(r);
            if (d is null) return;
            if ((d["Type"] as RawName)?.Value == "Page" || d["Kids"] is null) { pages.Add(r.Number); return; }
            if (Resolve(d["Kids"]) is RawArray kids) foreach (var k in kids.Items) Walk(k, depth + 1);
        }
    }

    (byte[] Data, int First, List<(int Num, int Offset)> Index) ObjectStream(int number)
    {
        if (_objectStreams.TryGetValue(number, out var cached)) return cached;
        if (Get(number) is not RawStream s) throw new InvalidDataException($"Object stream {number} is missing.");
        var data = Filters.Decode(s, this);
        int n = ((RawNumber)Resolve(s.Dict["N"])).IntValue, first = ((RawNumber)Resolve(s.Dict["First"])).IntValue;
        var p = new Parser(data, 0, this);
        var index = new List<(int, int)>(n);
        for (int i = 0; i < n; i++)
            index.Add((((Token.Number)p.ReadToken()).AsInt, ((Token.Number)p.ReadToken()).AsInt));
        return _objectStreams[number] = (data, first, index);
    }

    long FindStartXref()
    {
        var tail = Encoding.Latin1.GetString(_data, Math.Max(0, _data.Length - 2048), Math.Min(2048, _data.Length));
        int at = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (at < 0) throw new InvalidDataException("The file has no startxref.");
        var p = new Parser(Encoding.Latin1.GetBytes(tail), at + 9, this);
        return (long)((Token.Number)p.ReadToken()).Value;
    }

    (RawDict Trailer, bool IsStream) ReadXrefSection(long offset)
    {
        var p = new Parser(_data, (int)offset, this);
        var token = p.ReadToken();
        if (token is Token.Keyword("xref"))
        {
            while (true)
            {
                var t = p.ReadToken();
                if (t is Token.Keyword("trailer")) break;
                int start = ((Token.Number)t).AsInt, count = ((Token.Number)p.ReadToken()).AsInt;
                for (int i = 0; i < count; i++)
                {
                    long off = (long)((Token.Number)p.ReadToken()).Value;
                    int gen = ((Token.Number)p.ReadToken()).AsInt;
                    var kind = (Token.Keyword)p.ReadToken();
                    if (kind.Value == "n") _xref.TryAdd(start + i, new XrefEntry(1, off, gen));
                    else _xref.TryAdd(start + i, new XrefEntry(0, 0, gen));
                }
            }
            return ((RawDict)p.ReadValue(), false);
        }

        // Cross-reference stream: "N G obj << /Type /XRef … >> stream".
        p = new Parser(_data, (int)offset, this);
        p.ReadToken(); p.ReadToken();
        if (p.ReadToken() is not Token.Keyword("obj") || p.ReadObjectBody() is not RawStream xs)
            throw new InvalidDataException("The cross-reference section is damaged.");
        var w = ((RawArray)xs.Dict["W"]!).Items.Select(i => ((RawNumber)i).IntValue).ToArray();
        int size = ((RawNumber)xs.Dict["Size"]!).IntValue;
        var ranges = xs.Dict["Index"] is RawArray idx ? idx.Items.Select(i => ((RawNumber)i).IntValue).ToArray() : [0, size];
        var rows = Filters.Decode(xs, this);
        int rowLength = w.Sum(), pos = 0;
        for (int r = 0; r + 1 < ranges.Length; r += 2)
            for (int i = 0; i < ranges[r + 1] && pos + rowLength <= rows.Length; i++, pos += rowLength)
            {
                long Field(int f, long dflt)
                {
                    if (w[f] == 0) return dflt;
                    int start = pos + w.Take(f).Sum();
                    long v = 0;
                    for (int b = 0; b < w[f]; b++) v = (v << 8) | rows[start + b];
                    return v;
                }
                int type = (int)Field(0, 1);
                long f2 = Field(1, 0), f3 = Field(2, 0);
                int num = ranges[r] + i;
                _xref.TryAdd(num, type switch { 1 => new XrefEntry(1, f2, (int)f3), 2 => new XrefEntry(2, f2, (int)f3), _ => new XrefEntry(0, 0, 0) });
            }
        return (xs.Dict, true);
    }

    // ---- Lexer / parser ----

    abstract record Token
    {
        public sealed record Number(string Text) : Token
        {
            public double Value => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            public int AsInt => (int)Value;
            public bool IsInteger => !Text.Contains('.');
        }
        public sealed record Name(string Value) : Token;
        public sealed record Str(byte[] Bytes, bool Hex) : Token;
        public sealed record Keyword(string Value) : Token;
        public sealed record Delim(string Value) : Token;
        public sealed record End : Token;
    }

    sealed class Parser(byte[] data, int position, RawPdf owner)
    {
        int _pos = position;

        static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        void SkipWhite()
        {
            while (_pos < data.Length)
            {
                if (IsWhite(data[_pos])) _pos++;
                else if (data[_pos] == '%') { while (_pos < data.Length && data[_pos] is not (10 or 13)) _pos++; }
                else break;
            }
        }

        public Token ReadToken()
        {
            SkipWhite();
            if (_pos >= data.Length) return new Token.End();
            byte c = data[_pos];
            switch (c)
            {
                case (byte)'/':
                {
                    int start = ++_pos;
                    while (_pos < data.Length && !IsWhite(data[_pos]) && !IsDelim(data[_pos])) _pos++;
                    return new Token.Name(DecodeName(data.AsSpan(start, _pos - start)));
                }
                case (byte)'(':
                    return new Token.Str(ReadLiteral(), false);
                case (byte)'<':
                    if (_pos + 1 < data.Length && data[_pos + 1] == '<') { _pos += 2; return new Token.Delim("<<"); }
                    return new Token.Str(ReadHex(), true);
                case (byte)'>':
                    if (_pos + 1 < data.Length && data[_pos + 1] == '>') { _pos += 2; return new Token.Delim(">>"); }
                    _pos++; return new Token.Delim(">");
                case (byte)'[': _pos++; return new Token.Delim("[");
                case (byte)']': _pos++; return new Token.Delim("]");
            }
            int s = _pos;
            while (_pos < data.Length && !IsWhite(data[_pos]) && !IsDelim(data[_pos])) _pos++;
            if (_pos == s) { _pos++; return new Token.Delim(((char)c).ToString()); }
            var word = Encoding.Latin1.GetString(data, s, _pos - s);
            return word.Length > 0 && (char.IsDigit(word[0]) || word[0] is '-' or '+' or '.') ? new Token.Number(word) : new Token.Keyword(word);
        }

        public RawObject ReadValue() => ToObject(ReadToken());

        RawObject ToObject(Token t)
        {
            switch (t)
            {
                case Token.Number n when n.IsInteger:
                {
                    // "N G R" is a reference.
                    int save = _pos;
                    if (ReadToken() is Token.Number { IsInteger: true } g && ReadToken() is Token.Keyword("R"))
                        return new RawRef(n.AsInt, g.AsInt);
                    _pos = save;
                    return new RawNumber(n.Text);
                }
                case Token.Number n: return new RawNumber(n.Text);
                case Token.Name n: return new RawName(n.Value);
                case Token.Str s: return new RawString(s.Bytes, s.Hex);
                case Token.Keyword("true"): return new RawBool(true);
                case Token.Keyword("false"): return new RawBool(false);
                case Token.Keyword("null"): return RawNull.Instance;
                case Token.Delim("["):
                {
                    var items = new List<RawObject>();
                    while (true)
                    {
                        var next = ReadToken();
                        if (next is Token.Delim("]") or Token.End) break;
                        items.Add(ToObject(next));
                    }
                    return new RawArray(items);
                }
                case Token.Delim("<<"):
                {
                    var entries = new List<KeyValuePair<string, RawObject>>();
                    while (true)
                    {
                        var key = ReadToken();
                        if (key is Token.Delim(">>") or Token.End) break;
                        if (key is not Token.Name name) continue; // Skip garbage.
                        entries.Add(new(name.Value, ReadValue()));
                    }
                    return new RawDict(entries);
                }
                default: return RawNull.Instance;
            }
        }

        /// <summary>After "N G obj": the value, and its stream data when it is a stream.</summary>
        public RawObject ReadObjectBody()
        {
            var value = ReadValue();
            if (value is not RawDict dict) return value;
            int save = _pos;
            if (ReadToken() is not Token.Keyword("stream")) { _pos = save; return value; }
            if (_pos < data.Length && data[_pos] == 13) _pos++;
            if (_pos < data.Length && data[_pos] == 10) _pos++;
            int start = _pos;
            int length = owner.Resolve(dict["Length"]) is RawNumber l ? l.IntValue : -1;
            if (length < 0 || start + length > data.Length || !IsEndStream(start + length))
                length = FindEndStream(start) - start; // Wrong /Length: fall back to the keyword.
            return new RawStream(dict, data.AsSpan(start, length).ToArray());
        }

        bool IsEndStream(int at)
        {
            while (at < data.Length && IsWhite(data[at])) at++;
            return data.AsSpan(at).StartsWith("endstream"u8);
        }

        int FindEndStream(int from)
        {
            int at = data.AsSpan(from).IndexOf("endstream"u8);
            if (at < 0) throw new InvalidDataException("A stream has no end.");
            int end = from + at;
            if (end > from && data[end - 1] == 10) end--;
            if (end > from && data[end - 1] == 13) end--;
            return end;
        }

        byte[] ReadLiteral()
        {
            var bytes = new List<byte>();
            _pos++; // '('
            int depth = 1;
            while (_pos < data.Length)
            {
                byte b = data[_pos++];
                if (b == '\\')
                {
                    if (_pos >= data.Length) break;
                    byte e = data[_pos++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add(10); break;
                        case (byte)'r': bytes.Add(13); break;
                        case (byte)'t': bytes.Add(9); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case 13: if (_pos < data.Length && data[_pos] == 10) _pos++; break; // Line continuation.
                        case 10: break;
                        case >= (byte)'0' and <= (byte)'7':
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && _pos < data.Length && data[_pos] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + (data[_pos++] - '0');
                            bytes.Add((byte)v);
                            break;
                        }
                        default: bytes.Add(e); break;
                    }
                }
                else if (b == '(') { depth++; bytes.Add(b); }
                else if (b == ')') { if (--depth == 0) break; bytes.Add(b); }
                else bytes.Add(b);
            }
            return [.. bytes];
        }

        byte[] ReadHex()
        {
            _pos++; // '<'
            var hex = new StringBuilder();
            while (_pos < data.Length && data[_pos] != '>')
            {
                if (Uri.IsHexDigit((char)data[_pos])) hex.Append((char)data[_pos]);
                _pos++;
            }
            _pos++; // '>'
            if (hex.Length % 2 == 1) hex.Append('0');
            return Convert.FromHexString(hex.ToString());
        }

        static string DecodeName(ReadOnlySpan<byte> raw)
        {
            var bytes = new List<byte>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '#' && i + 2 < raw.Length && Uri.IsHexDigit((char)raw[i + 1]) && Uri.IsHexDigit((char)raw[i + 2]))
                {
                    bytes.Add(Convert.ToByte(Encoding.ASCII.GetString(raw.Slice(i + 1, 2)), 16));
                    i += 2;
                }
                else bytes.Add(raw[i]);
            }
            return Encoding.Latin1.GetString(bytes.ToArray());
        }
    }
}

/// <summary>Stream decoding needed to read cross-reference and object streams: Flate with PNG/TIFF predictors.</summary>
public static class Filters
{
    public static byte[] Decode(RawStream stream, RawPdf? owner = null)
    {
        var filter = owner?.Resolve(stream.Dict["Filter"]) ?? stream.Dict["Filter"];
        var names = filter switch
        {
            RawName n => [n.Value],
            RawArray a => a.Items.Select(i => (i as RawName)?.Value ?? "").ToList(),
            _ => new List<string>(),
        };
        var parms = owner?.Resolve(stream.Dict["DecodeParms"]) ?? stream.Dict["DecodeParms"];
        var data = stream.Data;
        for (int i = 0; i < names.Count; i++)
        {
            var p = parms switch { RawDict d => d, RawArray a when i < a.Items.Count => a.Items[i] as RawDict, _ => null };
            data = names[i] switch
            {
                "FlateDecode" or "Fl" => Predict(Inflate(data), p),
                _ => throw new NotSupportedException($"Stream filter {names[i]} is not supported here."),
            };
        }
        return data;
    }

    public static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        try { z.CopyTo(ms); }
        catch (InvalidDataException) when (ms.Length > 0) { } // Truncated tail: keep what decoded.
        return ms.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    static byte[] Predict(byte[] data, RawDict? p)
    {
        int predictor = p?["Predictor"] is RawNumber pr ? pr.IntValue : 1;
        if (predictor < 10) return data; // 1 = none; TIFF predictor 2 is not used for xref data.
        int colors = p?["Colors"] is RawNumber c ? c.IntValue : 1;
        int bpc = p?["BitsPerComponent"] is RawNumber b ? b.IntValue : 8;
        int columns = p?["Columns"] is RawNumber col ? col.IntValue : 1;
        int bpp = Math.Max(1, colors * bpc / 8), rowLength = (colors * bpc * columns + 7) / 8;
        var output = new List<byte>(data.Length);
        var prev = new byte[rowLength];
        for (int pos = 0; pos + 1 + rowLength <= data.Length; pos += rowLength + 1)
        {
            int type = data[pos];
            var row = data.AsSpan(pos + 1, rowLength).ToArray();
            for (int i = 0; i < rowLength; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0, up = prev[i], upLeft = i >= bpp ? prev[i - bpp] : 0;
                row[i] = type switch
                {
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + (left + up) / 2),
                    4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                    _ => row[i],
                };
            }
            output.AddRange(row);
            prev = row;
        }
        return [.. output];
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

/// <summary>Serializes raw objects.</summary>
public static class RawWriter
{
    public static void Write(StringBuilder sb, RawObject obj)
    {
        switch (obj)
        {
            case RawName n: sb.Append('/').Append(EncodeName(n.Value)); break;
            case RawNumber n: sb.Append(n.Text); break;
            case RawBool b: sb.Append(b.Value ? "true" : "false"); break;
            case RawNull: sb.Append("null"); break;
            case RawRef r: sb.Append(r.Number).Append(' ').Append(r.Generation).Append(" R"); break;
            case RawString s:
                if (s.Hex || s.Bytes.Any(b => b < 32 || b > 126)) sb.Append('<').Append(Convert.ToHexString(s.Bytes)).Append('>');
                else
                {
                    sb.Append('(');
                    foreach (byte b in s.Bytes)
                    {
                        if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\');
                        sb.Append((char)b);
                    }
                    sb.Append(')');
                }
                break;
            case RawArray a:
                sb.Append('[');
                for (int i = 0; i < a.Items.Count; i++) { if (i > 0) sb.Append(' '); Write(sb, a.Items[i]); }
                sb.Append(']');
                break;
            case RawDict d:
                sb.Append("<<");
                foreach (var (key, value) in d.Entries) { sb.Append('/').Append(EncodeName(key)).Append(' '); Write(sb, value); sb.Append(' '); }
                sb.Append(">>");
                break;
            case RawStream:
                throw new InvalidOperationException("Streams are written as indirect objects only.");
        }
    }

    static string EncodeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (c < 33 || c > 126 || c is '#' or '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%')
                sb.Append('#').Append(((int)c).ToString("X2"));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Builds an incremental update (ISO 32000-2 §7.5.6): new and changed objects plus a new cross-reference section
/// appended after the original bytes, which stay untouched.
/// </summary>
public sealed class IncrementalUpdate(RawPdf source)
{
    readonly SortedDictionary<int, RawObject> _objects = [];
    int _next = source.Size;

    public RawPdf Source => source;

    public RawRef Add(RawObject obj)
    {
        int n = _next++;
        _objects[n] = obj;
        return new RawRef(n, 0);
    }

    /// <summary>Writes a new version of an existing object (same number).</summary>
    public void Replace(int number, RawObject obj) => _objects[number] = obj;

    /// <summary>The bytes of the updated file, and the offset where each written object starts.</summary>
    public (byte[] Data, Dictionary<int, long> Offsets) Build()
    {
        var output = new MemoryStream();
        output.Write(source.Data);
        if (source.Data.Length > 0 && source.Data[^1] is not (10 or 13)) output.WriteByte(10);
        var offsets = new Dictionary<int, long>();
        foreach (var (number, obj) in _objects)
        {
            offsets[number] = output.Position;
            int gen = number < source.Size ? source.GenerationOf(number) : 0;
            WriteObject(output, number, gen, obj);
        }

        var trailer = new RawDict();
        int size = Math.Max(source.Size, _next);
        if (source.Root is { } root) trailer["Root"] = root;
        if (source.Trailer["Info"] is { } info) trailer["Info"] = info;
        if (source.Trailer["ID"] is { } id) trailer["ID"] = id;
        trailer["Prev"] = new RawNumber(source.StartXref.ToString(CultureInfo.InvariantCulture));

        long xrefOffset = output.Position;
        if (source.UsesXrefStream)
        {
            // The update must be a cross-reference stream too; it lists itself.
            int self = size++;
            offsets[self] = xrefOffset;
            trailer["Type"] = new RawName("XRef");
            trailer["Size"] = new RawNumber(size.ToString(CultureInfo.InvariantCulture));
            var numbers = offsets.Keys.Order().ToList();
            var index = new RawArray();
            var rows = new MemoryStream();
            foreach (var run in Runs(numbers))
            {
                index.Items.Add(new RawNumber(run[0].ToString(CultureInfo.InvariantCulture)));
                index.Items.Add(new RawNumber(run.Count.ToString(CultureInfo.InvariantCulture)));
                foreach (int n in run)
                {
                    long off = offsets[n];
                    rows.WriteByte(1);
                    for (int b = 3; b >= 0; b--) rows.WriteByte((byte)(off >> (8 * b)));
                    int gen = n < source.Size ? source.GenerationOf(n) : 0;
                    rows.WriteByte((byte)(gen >> 8)); rows.WriteByte((byte)gen);
                }
            }
            trailer["Index"] = index;
            trailer["W"] = new RawArray([new RawNumber("1"), new RawNumber("4"), new RawNumber("2")]);
            WriteObject(output, self, 0, new RawStream(trailer, rows.ToArray()));
        }
        else
        {
            trailer["Size"] = new RawNumber(size.ToString(CultureInfo.InvariantCulture));
            var sb = new StringBuilder("xref\n");
            foreach (var run in Runs(offsets.Keys.Order().ToList()))
            {
                sb.Append(run[0]).Append(' ').Append(run.Count).Append('\n');
                foreach (int n in run)
                    sb.Append(offsets[n].ToString("D10")).Append(' ').Append((n < source.Size ? source.GenerationOf(n) : 0).ToString("D5")).Append(" n\r\n");
            }
            sb.Append("trailer\n");
            RawWriter.Write(sb, trailer);
            sb.Append('\n');
            output.Write(Encoding.Latin1.GetBytes(sb.ToString()));
        }
        output.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n"));
        return (output.ToArray(), offsets);
    }

    static void WriteObject(Stream output, int number, int gen, RawObject obj)
    {
        var sb = new StringBuilder().Append(number).Append(' ').Append(gen).Append(" obj\n");
        if (obj is RawStream s)
        {
            var dict = s.Dict.Copy();
            dict["Length"] = new RawNumber(s.Data.Length.ToString(CultureInfo.InvariantCulture));
            RawWriter.Write(sb, dict);
            sb.Append("\nstream\n");
            output.Write(Encoding.Latin1.GetBytes(sb.ToString()));
            output.Write(s.Data);
            output.Write("\nendstream\nendobj\n"u8);
            return;
        }
        RawWriter.Write(sb, obj);
        sb.Append("\nendobj\n");
        output.Write(Encoding.Latin1.GetBytes(sb.ToString()));
    }

    static IEnumerable<List<int>> Runs(List<int> sorted)
    {
        var run = new List<int>();
        foreach (int n in sorted)
        {
            if (run.Count > 0 && n != run[^1] + 1) { yield return run; run = []; }
            run.Add(n);
        }
        if (run.Count > 0) yield return run;
    }
}
