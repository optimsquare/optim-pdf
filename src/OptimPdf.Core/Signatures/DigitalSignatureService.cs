using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Editing;
using OptimPdf.Core.IO;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Writing;
using PdfSharp.Pdf;

namespace OptimPdf.Core.Signatures;

/// <param name="Rect">Where the signature appears (user space); null for an invisible signature.</param>
/// <param name="FieldName">An existing, unsigned signature field to sign; or the name for a new field.</param>
/// <param name="Mark">An optional handwritten/typed/image mark shown next to the signer's details.</param>
/// <param name="TimestampServer">An RFC 3161 time-stamp authority; adds a trusted signing time (PAdES B-T). Needs network access.</param>
public sealed record DigitalSignOptions(
    X509Certificate2 Certificate,
    int PageIndex = 0,
    PdfRect? Rect = null,
    string? Reason = null,
    string? Location = null,
    string? ContactInfo = null,
    string? FieldName = null,
    SignatureMark? Mark = null,
    Uri? TimestampServer = null,
    DateTimeOffset? SigningTime = null);

/// <summary>
/// Certificate-based digital signatures in the PAdES baseline format (ETSI EN 319 142-1, SubFilter
/// ETSI.CAdES.detached; B-B, or B-T with a time-stamp server). The signature is added as an incremental
/// update, so the signed bytes are the original file plus the new objects and earlier signatures stay valid.
/// </summary>
public static class DigitalSignatureService
{

    const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>Certificates usable for signing: they have a private key and allow digital signatures.</summary>
    public static bool CanSign(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey) return false;
        if (DateTime.Now > cert.NotAfter || DateTime.Now < cert.NotBefore) return false;
        var usage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is null || (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) != 0;
    }

    public static byte[] Sign(byte[] pdf, DigitalSignOptions options, HttpClient? http = null) =>
        SignAsync(pdf, options, http).GetAwaiter().GetResult();

    public static async Task<byte[]> SignAsync(byte[] pdf, DigitalSignOptions options, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var cert = options.Certificate;
        if (!cert.HasPrivateKey) throw new ArgumentException("This certificate has no private key, so it cannot sign.", nameof(options));
        DateTimeOffset when = options.SigningTime ?? DateTimeOffset.Now;

        // Space for the signature: the certificate chain plus the signature itself, and a time-stamp token if
        // requested. If the estimate is short, the file is laid out again with the size actually needed.
        using var chain = new X509Chain { ChainPolicy = { RevocationMode = X509RevocationMode.NoCheck } };
        chain.Build(cert);
        int reserve = chain.ChainElements.Sum(e => e.Certificate.RawData.Length) + 2048 + (options.TimestampServer is null ? 0 : 8192);
        for (int attempt = 0; ; attempt++)
        {
            var (data, contentsStart, contentsEnd) = Layout(pdf, options, reserve, when);
            var signed = new byte[contentsStart + (data.Length - contentsEnd)];
            Buffer.BlockCopy(data, 0, signed, 0, contentsStart);
            Buffer.BlockCopy(data, contentsEnd, signed, contentsStart, data.Length - contentsEnd);
            var cms = await CreateCmsAsync(signed, cert, options.TimestampServer, http, cancellationToken);
            if (cms.Length > reserve)
            {
                if (attempt > 0) throw new InvalidOperationException("The signature is larger than the space reserved for it.");
                reserve = cms.Length + 1024;
                continue;
            }
            Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(data, contentsStart + 1); // After '<'; the rest stays zero padding.
            return data;
        }
    }

    /// <summary>Builds the update with placeholders and fills in the byte range; returns where the /Contents hex string lies.</summary>
    static (byte[] Data, int ContentsStart, int ContentsEnd) Layout(byte[] pdf, DigitalSignOptions options, int reserve, DateTimeOffset when)
    {
        var cert = options.Certificate;
        var source = new RawPdf(pdf);
        if (source.Trailer["Encrypt"] is not null)
            throw new NotSupportedException("Password-protected documents cannot be signed yet. Remove security first, then sign.");

        var update = new IncrementalUpdate(source);
        var catalogRef = source.Root ?? throw new InvalidDataException("The document has no catalog.");
        var catalog = source.ResolveDict(catalogRef)!.Copy();
        var pages = source.PageObjectNumbers();
        string signer = cert.GetNameInfo(X509NameType.SimpleName, false);

        // Form and field list (indirect or direct, existing or new).
        var (form, formRef) = Resolved(source, catalog["AcroForm"]);
        form ??= new RawDict();
        var (fields, fieldsRef) = Resolved<RawArray>(source, form["Fields"]);
        fields ??= new RawArray();

        // The field: an existing empty signature field, or a new one.
        var existing = options.FieldName is { } wanted ? FindField(source, fields, wanted) : null;
        int pageIndex = options.PageIndex;
        RawDict widget;
        int widgetNumber;
        PdfRect? rect = options.Rect;
        if (existing is { } found)
        {
            if (found.Dict["V"] is not null) throw new InvalidOperationException($"\"{options.FieldName}\" is already signed.");
            widget = found.Dict.Copy();
            widgetNumber = found.Number;
            if (widget["Rect"] is RawArray ra && ra.Items.Count == 4)
            {
                var v = ra.Items.Select(i => ((RawNumber)source.Resolve(i)).Value).ToArray();
                var r = PdfRect.FromPoints(v[0], v[1], v[2], v[3]);
                rect = r.Width > 0 && r.Height > 0 ? r : null;
            }
            if (widget["P"] is RawRef pr && pages.IndexOf(pr.Number) is >= 0 and var pi) pageIndex = pi;
        }
        else
        {
            if ((uint)pageIndex >= (uint)pages.Count) throw new ArgumentOutOfRangeException(nameof(options), "The page does not exist.");
            widget = new RawDict();
            widgetNumber = -1;
            widget["Type"] = new RawName("Annot");
            widget["Subtype"] = new RawName("Widget");
            widget["FT"] = new RawName("Sig");
            widget["T"] = RawString.Text(options.FieldName ?? UniqueName(source, fields));
            widget["F"] = new RawNumber("132"); // Print + Locked.
            widget["P"] = new RawRef(pages[pageIndex], source.GenerationOf(pages[pageIndex]));
        }
        var r2 = rect ?? new PdfRect(0, 0, 0, 0);
        widget["Rect"] = new RawArray([RawNumber.Of(r2.Left), RawNumber.Of(r2.Bottom), RawNumber.Of(r2.Right), RawNumber.Of(r2.Top)]);

        // The appearance shown on the page.
        if (rect is { } visible)
        {
            var pageDict = source.ResolveDict(new RawRef(pages[pageIndex], 0))!;
            int rotate = InheritedRotate(source, pageDict);
            var ap = BuildAppearance(source, update, visible, rotate, signer, when, options);
            widget["AP"] = new RawDict([new("N", ap)]);
        }
        else widget["AP"] = null;

        // The signature value, with placeholders filled in after the file is laid out.
        var sig = new RawDict();
        sig["Type"] = new RawName("Sig");
        sig["Filter"] = new RawName("Adobe.PPKLite");
        sig["SubFilter"] = new RawName("ETSI.CAdES.detached");
        sig["ByteRange"] = new RawArray([new RawNumber("0"), new RawNumber(Pad), new RawNumber(Pad), new RawNumber(Pad)]);
        sig["Contents"] = new RawString(new byte[reserve], hex: true);
        sig["M"] = RawString.Text(PdfDate(when));
        sig["Name"] = RawString.Text(signer);
        if (!string.IsNullOrWhiteSpace(options.Reason)) sig["Reason"] = RawString.Text(options.Reason);
        if (!string.IsNullOrWhiteSpace(options.Location)) sig["Location"] = RawString.Text(options.Location);
        if (!string.IsNullOrWhiteSpace(options.ContactInfo)) sig["ContactInfo"] = RawString.Text(options.ContactInfo);
        sig["Prop_Build"] = new RawDict([new("App", new RawDict([new("Name", new RawName("Optim_PDF"))]))]);
        var sigRef = update.Add(sig);
        widget["V"] = sigRef;

        RawRef widgetRef;
        if (widgetNumber >= 0) { update.Replace(widgetNumber, widget); widgetRef = new RawRef(widgetNumber, source.GenerationOf(widgetNumber)); }
        else
        {
            widgetRef = update.Add(widget);
            fields.Items.Add(widgetRef);
            // Add the widget to its page's annotations.
            int pageNumber = pages[pageIndex];
            var page = source.ResolveDict(new RawRef(pageNumber, 0))!.Copy();
            var (annots, annotsRef) = Resolved<RawArray>(source, page["Annots"]);
            annots = new RawArray([.. (annots?.Items ?? [])]);
            annots.Items.Add(widgetRef);
            if (annotsRef is not null) update.Replace(annotsRef.Number, annots);
            else page["Annots"] = annots;
            if (annotsRef is null) update.Replace(pageNumber, page);
        }

        // Write the field list and form back where they live.
        form = form.Copy();
        form["SigFlags"] = new RawNumber("3"); // Signatures exist; append only.
        if (fieldsRef is not null) update.Replace(fieldsRef.Number, fields);
        else form["Fields"] = fields;
        if (formRef is not null) update.Replace(formRef.Number, form);
        else
        {
            catalog["AcroForm"] = form;
            update.Replace(catalogRef.Number, catalog);
        }

        // Lay out the file, then fill in the byte range and the signature.
        var (data, offsets) = update.Build();
        var (contentsStart, contentsEnd, rangeAt, rangeLength) = Locate(data, offsets[sigRef.Number]);
        var range = $"[0 {contentsStart} {contentsEnd} {data.Length - contentsEnd}]";
        if (range.Length > rangeLength) throw new InvalidOperationException("The byte range does not fit its placeholder.");
        // Same length as the placeholder, so no offset moves; the padding spaces are insignificant.
        Encoding.ASCII.GetBytes(range[..^1].PadRight(rangeLength - 1) + "]").CopyTo(data, rangeAt);

        return (data, contentsStart, contentsEnd);
    }

    static readonly string Pad = new('0', 10);

    /// <summary>Finds the /Contents hex string and the /ByteRange array inside the signature dictionary.</summary>
    static (int ContentsStart, int ContentsEnd, int RangeAt, int RangeLength) Locate(byte[] data, long sigOffset)
    {
        var span = data.AsSpan((int)sigOffset);
        int contents = span.IndexOf("/Contents <"u8);
        int range = span.IndexOf("/ByteRange ["u8);
        if (contents < 0 || range < 0) throw new InvalidOperationException("Signature placeholders not found.");
        int cStart = (int)sigOffset + contents + "/Contents ".Length; // At '<'.
        int cEnd = Array.IndexOf(data, (byte)'>', cStart) + 1;         // After '>'.
        int rStart = (int)sigOffset + range + "/ByteRange ".Length;    // At '['.
        int rEnd = Array.IndexOf(data, (byte)']', rStart) + 1;
        return (cStart, cEnd, rStart, rEnd - rStart);
    }

    static async Task<byte[]> CreateCmsAsync(byte[] signedBytes, X509Certificate2 cert, Uri? tsa, HttpClient? http, CancellationToken ct)
    {
        var cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.WholeChain,
        };
        // PAdES: the signing certificate is bound by signing-certificate-v2; signing time lives in /M.
        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, SigningCertificateV2(cert)));
        cms.ComputeSignature(signer, silent: false);

        if (tsa is not null)
        {
            var info = cms.SignerInfos[0];
            var request = Rfc3161TimestampRequest.CreateFromSignerInfo(info, HashAlgorithmName.SHA256, requestSignerCertificates: true,
                nonce: RandomNumberGenerator.GetBytes(8));
            var client = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                using var body = new ByteArrayContent(request.Encode());
                body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
                using var response = await client.PostAsync(tsa, body, ct);
                response.EnsureSuccessStatusCode();
                var token = request.ProcessResponse(await response.Content.ReadAsByteArrayAsync(ct), out _);
                info.AddUnsignedAttribute(new AsnEncodedData(TimestampTokenOid, token.AsSignedCms().Encode()));
            }
            finally { if (http is null) client.Dispose(); }
        }
        return cms.Encode();
    }

    /// <summary>SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 } (RFC 5035), SHA-256, with issuer and serial.</summary>
    static byte[] SigningCertificateV2(X509Certificate2 cert)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
        {
            // hashAlgorithm defaults to SHA-256 and is omitted.
            w.WriteOctetString(SHA256.HashData(cert.RawData));
            using (w.PushSequence()) // IssuerSerial
            {
                using (w.PushSequence()) // GeneralNames
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true))) // directoryName [4]
                    w.WriteEncodedValue(cert.IssuerName.RawData);
                w.WriteInteger(cert.SerialNumberBytes.Span);
            }
        }
        return w.Encode();
    }

    /// <summary>A form XObject showing the mark (if any) and "Digitally signed by … / Date …", upright on the displayed page.</summary>
    static RawRef BuildAppearance(RawPdf source, IncrementalUpdate update, PdfRect rect, int rotate, string signer, DateTimeOffset when, DigitalSignOptions options)
    {
        var (_, _, w, h) = ContentEditor.UprightBox(rect, rotate);
        using var scratch = new PdfDocument();
        var page = scratch.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(w);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(h);
        double textLeft = 2;
        if (options.Mark is { } mark)
        {
            VisualSignatureService.Place(scratch, 0, 2, h - 2, w * 0.45 - 4, h - 4, mark);
            textLeft = w * 0.45 + 2;
        }
        var lines = new List<string> { "Digitally signed by", signer, "Date: " + when.ToString("yyyy.MM.dd HH:mm:ss zzz", CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(options.Reason)) lines.Add("Reason: " + options.Reason);
        if (!string.IsNullOrWhiteSpace(options.Location)) lines.Add("Location: " + options.Location);
        var text = string.Join("\n", lines);
        double size = Math.Min(10, (h - 4) / (lines.Count * 1.2));
        while (size > 3 && ContentEditor.MeasureText(text, new TextStyle("Arial", size)).Width > w - textLeft - 2) size -= 0.5;
        ContentEditor.AddText(scratch, 0, textLeft, h - 2, text, new TextStyle("Arial", size, new PdfColor(0.1, 0.1, 0.1)));

        // Lift the scratch page into a form XObject in the update.
        var raw = new RawPdf(DocumentSession.Serialize(scratch));
        var rawPage = raw.ResolveDict(new RawRef(raw.PageObjectNumbers()[0], 0))!;
        var content = new MemoryStream();
        var parts = raw.Resolve(rawPage["Contents"]) switch { RawArray a => a.Items, var single => [rawPage["Contents"]!] };
        foreach (var part in parts)
            if (raw.Resolve(part) is RawStream s) { content.Write(Filters.Decode(s, raw)); content.WriteByte(10); }

        var map = new Dictionary<int, RawRef>();
        var (right, down) = ContentEditor.Axes(rotate);
        var dict = new RawDict();
        dict["Type"] = new RawName("XObject");
        dict["Subtype"] = new RawName("Form");
        dict["BBox"] = new RawArray([RawNumber.Of(0), RawNumber.Of(0), RawNumber.Of(w), RawNumber.Of(h)]);
        // Form x → displayed right, form y (up) → displayed up, so the appearance reads upright on rotated pages.
        dict["Matrix"] = new RawArray([RawNumber.Of(right.X), RawNumber.Of(right.Y), RawNumber.Of(-down.X), RawNumber.Of(-down.Y), RawNumber.Of(0), RawNumber.Of(0)]);
        dict["Resources"] = Copy(raw, update, raw.Resolve(rawPage["Resources"]), map);
        dict["Filter"] = new RawName("FlateDecode");
        return update.Add(new RawStream(dict, Filters.Deflate(content.ToArray())));
    }

    /// <summary>Deep-copies an object graph from another file into the update, renumbering indirect objects.</summary>
    static RawObject Copy(RawPdf from, IncrementalUpdate to, RawObject obj, Dictionary<int, RawRef> map)
    {
        switch (obj)
        {
            case RawRef r:
                if (map.TryGetValue(r.Number, out var done)) return done;
                var placeholder = to.Add(RawNull.Instance);
                map[r.Number] = placeholder;
                to.Replace(placeholder.Number, Copy(from, to, from.Get(r.Number), map));
                return placeholder;
            case RawArray a:
                return new RawArray(a.Items.Select(i => Copy(from, to, i, map)).ToList());
            case RawDict d:
                return new RawDict(d.Entries.Select(e => new KeyValuePair<string, RawObject>(e.Key, Copy(from, to, e.Value, map))).ToList());
            case RawStream s:
                return new RawStream((RawDict)Copy(from, to, s.Dict, map), s.Data);
            default:
                return obj;
        }
    }

    static (RawDict? Dict, RawRef? Ref) Resolved(RawPdf pdf, RawObject? value) => Resolved<RawDict>(pdf, value);

    static (T? Value, RawRef? Ref) Resolved<T>(RawPdf pdf, RawObject? value) where T : RawObject =>
        value is RawRef r ? (pdf.Get(r.Number) as T, r) : (value as T, null);

    static (int Number, RawDict Dict)? FindField(RawPdf pdf, RawArray fields, string name)
    {
        foreach (var item in fields.Items)
            if (item is RawRef r && pdf.ResolveDict(r) is { } d && (d["T"] as RawString)?.Decode() == name
                && (pdf.Resolve(d["FT"]) as RawName)?.Value == "Sig" && d["Kids"] is null)
                return (r.Number, d);
        return null;
    }

    static string UniqueName(RawPdf pdf, RawArray fields)
    {
        var names = fields.Items.Select(i => (pdf.ResolveDict(i)?["T"] as RawString)?.Decode()).ToHashSet();
        for (int i = 1; ; i++) if (!names.Contains($"Signature{i}")) return $"Signature{i}";
    }

    static int InheritedRotate(RawPdf pdf, RawDict page)
    {
        for (var node = page; node is not null; node = pdf.ResolveDict(node["Parent"]))
            if (pdf.Resolve(node["Rotate"]) is RawNumber n) return ((n.IntValue % 360) + 360) % 360;
        return 0;
    }

    /// <summary>A PDF date string: D:YYYYMMDDHHmmSS+HH'mm'.</summary>
    internal static string PdfDate(DateTimeOffset t)
    {
        var offset = t.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(CultureInfo.InvariantCulture, $"D:{t:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }
}
