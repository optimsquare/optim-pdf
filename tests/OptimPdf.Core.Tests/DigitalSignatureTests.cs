using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Forms;
using OptimPdf.Core.IO;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Signatures;

namespace OptimPdf.Core.Tests;

public class DigitalSignatureTests
{
    static readonly Lazy<X509Certificate2> Alice = new(() => SelfSigned("Alice Example"));
    static readonly Lazy<X509Certificate2> Bob = new(() => SelfSigned("Bob Example"));

    internal static X509Certificate2 SelfSigned(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}, O=Optim Tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    static readonly PdfRect Box = new(350, 60, 560, 130);

    [Fact]
    public void Signed_document_validates_and_shows_the_signature()
    {
        var original = TestPdf.Create(2);
        var signed = DigitalSignatureService.Sign(original, new DigitalSignOptions(Alice.Value, 0, Box, Reason: "Approved", Location: "Dubai"));

        // An incremental update: the original bytes are untouched at the start of the file.
        Assert.True(signed.AsSpan(0, original.Length).SequenceEqual(original));

        var status = Assert.Single(SignatureValidator.Validate(signed));
        Assert.True(status.IsValid, status.Error);
        Assert.True(status.IntegrityValid);
        Assert.False(status.ChangedAfterSigning);
        Assert.Equal("Alice Example", status.SignerName);
        Assert.Equal("ETSI.CAdES.detached", status.SubFilter);
        Assert.Equal("Approved", status.Reason);
        Assert.NotNull(status.ClaimedTime);
        Assert.False(status.CertificateTrusted); // Self-signed…
        Assert.Contains("Self-signed", status.TrustDetail);
        // …unless the user trusts this identity.
        Assert.True(SignatureValidator.Validate(signed, trustedCertificates: [Alice.Value])[0].CertificateTrusted);

        using var doc = PdfiumDocument.Open(signed);
        Assert.Equal(2, doc.PageCount);
        Assert.Contains(TestPdf.Title(1), doc.GetPageText(0));
        // The appearance is drawn in the box.
        Assert.True(Ink(doc, Box) > 0.02, "No visible signature appearance");
    }

    [Fact]
    public void Signature_space_is_sized_to_the_certificate()
    {
        var original = TestPdf.Create(1);
        var signed = DigitalSignatureService.Sign(original, new DigitalSignOptions(Alice.Value));
        // A self-signed 2048-bit certificate needs a few KB; the reserved space must not balloon the file.
        Assert.InRange(signed.Length - original.Length, 2_000, 12_000);
        Console.WriteLine($"Signature grew the file by {signed.Length - original.Length} bytes.");
        Assert.True(SignatureValidator.Validate(signed)[0].IsValid);
    }

    [Fact]
    public void Cms_follows_pades_baseline()
    {
        var signed = DigitalSignatureService.Sign(TestPdf.Create(1), new DigitalSignOptions(Alice.Value));
        using var doc = PdfiumDocument.Open(signed);
        var sig = doc.GetSignatures().Single();
        AsnDecoder.ReadEncodedValue(sig.Contents, AsnEncodingRules.BER, out _, out _, out int len);
        var cms = new SignedCms();
        cms.Decode(sig.Contents.AsSpan(0, len));
        Assert.Empty(cms.ContentInfo.Content); // Detached: the PDF bytes are not inside the CMS.
        var attrs = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToList();
        Assert.Contains("1.2.840.113549.1.9.3", attrs);          // content-type
        Assert.Contains("1.2.840.113549.1.9.4", attrs);          // message-digest
        Assert.Contains("1.2.840.113549.1.9.16.2.47", attrs);    // signing-certificate-v2
        Assert.DoesNotContain("1.2.840.113549.1.9.5", attrs);    // no signing-time (PAdES uses /M)
        Assert.Equal("2.16.840.1.101.3.4.2.1", cms.SignerInfos[0].DigestAlgorithm.Value); // SHA-256
    }

    [Fact]
    public void Tampering_is_detected()
    {
        var signed = DigitalSignatureService.Sign(TestPdf.Create(1), new DigitalSignOptions(Alice.Value, 0, Box));
        // Change one byte inside the signed range (a key name of an original object).
        var text = Encoding.Latin1.GetString(signed);
        var tampered = (byte[])signed.Clone();
        int at = text.IndexOf("/Producer", StringComparison.Ordinal) is >= 0 and var p ? p : text.IndexOf("/MediaBox", StringComparison.Ordinal);
        tampered[at + 2] ^= 0x01;

        var status = Assert.Single(SignatureValidator.Validate(tampered));
        Assert.False(status.IntegrityValid);
        Assert.False(status.IsValid);
    }

    [Fact]
    public void Second_signature_keeps_the_first_valid()
    {
        var once = DigitalSignatureService.Sign(TestPdf.Create(1), new DigitalSignOptions(Alice.Value, 0, Box, Reason: "Author"));
        var twice = DigitalSignatureService.Sign(once, new DigitalSignOptions(Bob.Value, 0, new PdfRect(60, 60, 270, 130), Reason: "Reviewer"));

        var statuses = SignatureValidator.Validate(twice);
        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, s => Assert.True(s.IsValid, s.Error));
        var first = statuses.Single(s => s.SignerName == "Alice Example");
        var second = statuses.Single(s => s.SignerName == "Bob Example");
        Assert.True(first.ChangedAfterSigning);   // Bob's signature was appended later.
        Assert.False(second.ChangedAfterSigning);
    }

    [Fact]
    public void Empty_signature_fields_are_not_counted_as_signatures()
    {
        var prepared = DocumentSession.Transform(TestPdf.Create(1), null, doc => FormBuilder.AddSignatureField(doc, 0, "ClientSignature", Box));
        Assert.Empty(SignatureValidator.Validate(prepared));
        using var session = DocumentSession.FromBytes(prepared);
        Assert.Equal(0, session.SignatureCount); // No "will invalidate signatures" warning for unsigned fields.
    }

    [Fact]
    public void Signs_an_existing_empty_signature_field()
    {
        var prepared = DocumentSession.Transform(TestPdf.Create(1), null, doc => FormBuilder.AddSignatureField(doc, 0, "ClientSignature", Box));
        var signed = DigitalSignatureService.Sign(prepared, new DigitalSignOptions(Alice.Value, FieldName: "ClientSignature"));

        Assert.True(Assert.Single(SignatureValidator.Validate(signed)).IsValid);
        using var model = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(signed), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var fields = FormService.ListFields(model);
        var field = Assert.Single(fields); // No second field was created.
        Assert.Equal("ClientSignature", field.Name);
        Assert.Equal(FormFieldKind.Signature, field.Kind);
        Assert.Throws<InvalidOperationException>(() =>
            DigitalSignatureService.Sign(signed, new DigitalSignOptions(Bob.Value, FieldName: "ClientSignature")));
    }

    [Fact]
    public void Rewriting_the_file_breaks_the_signature_and_the_session_reports_it()
    {
        var signed = DigitalSignatureService.Sign(TestPdf.Create(1), new DigitalSignOptions(Alice.Value, 0, Box));
        using var session = DocumentSession.FromBytes(signed);
        Assert.Equal(1, session.SignatureCount);
        session.Apply("Rotate", doc => doc.Pages[0].Rotate = 90);
        var after = SignatureValidator.Validate(session.CurrentBytes);
        Assert.True(after.Count == 0 || after.All(s => !s.IsValid));
    }

    [Fact]
    public void Session_signing_is_undoable()
    {
        using var session = DocumentSession.FromBytes(TestPdf.Create(1));
        session.ApplyRaw("Sign", data => DigitalSignatureService.Sign(data, new DigitalSignOptions(Alice.Value, 0, Box)));
        Assert.Equal(1, session.SignatureCount);
        session.Undo();
        Assert.Equal(0, session.SignatureCount);
    }

    [Fact]
    public void Signs_files_that_use_cross_reference_and_object_streams()
    {
        var pdf = HandmadeXrefStreamPdf("Compact file");
        using (var check = PdfiumDocument.Open(pdf)) Assert.Contains("Compact file", check.GetPageText(0));

        var signed = DigitalSignatureService.Sign(pdf, new DigitalSignOptions(Alice.Value, 0, Box));
        Assert.True(Assert.Single(SignatureValidator.Validate(signed)).IsValid);
        // The update is a cross-reference stream too, and the file still opens everywhere.
        Assert.True(new RawPdf(signed).UsesXrefStream);
        using var doc = PdfiumDocument.Open(signed);
        Assert.Contains("Compact file", doc.GetPageText(0));
        using var model = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(signed), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(1, model.PageCount);
    }

    [Fact]
    public void Encrypted_documents_are_refused()
    {
        var data = DocumentSession.Transform(TestPdf.Create(1), null, doc =>
            Security.SecurityService.Protect(doc, new Security.ProtectionOptions(null, "owner", new Security.PdfPermissions(), Security.EncryptionStrength.Aes256)));
        Assert.Throws<NotSupportedException>(() => DigitalSignatureService.Sign(data, new DigitalSignOptions(Alice.Value)));
    }

    [Fact]
    public void Pdf_dates_parse()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 14, 5, 9, TimeSpan.FromHours(4)), SignatureValidator.ParsePdfDate("D:20261012140509+04'00'"));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), SignatureValidator.ParsePdfDate("D:2026"));
    }

    /// <summary>
    /// A PDF 1.5 file written by hand with its page in an object stream and a cross-reference stream
    /// (as many producers write them), so the incremental writer is tested on that layout.
    /// </summary>
    static byte[] HandmadeXrefStreamPdf(string text)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new Dictionary<int, long>();
        W("%PDF-1.5\n%\xE2\xE3\xCF\xD3\n");

        offsets[1] = ms.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var content = $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET";
        offsets[4] = ms.Position; W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        // Object stream 5 holds objects 2 (pages), 3 (page) and 6 (font).
        var o2 = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        var o3 = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> >> >>";
        var o6 = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        var header = $"2 0 3 {o2.Length + 1} 6 {o2.Length + o3.Length + 2} ";
        var body = header + o2 + " " + o3 + " " + o6;
        var packed = Filters.Deflate(Encoding.Latin1.GetBytes(body));
        offsets[5] = ms.Position;
        W($"5 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n");
        ms.Write(packed); W("\nendstream\nendobj\n");

        long xref = ms.Position;
        var rows = new MemoryStream();
        void Row(int type, long f2, int f3) { rows.WriteByte((byte)type); for (int b = 3; b >= 0; b--) rows.WriteByte((byte)(f2 >> (8 * b))); rows.WriteByte((byte)(f3 >> 8)); rows.WriteByte((byte)f3); }
        Row(0, 0, 65535);           // 0
        Row(1, offsets[1], 0);      // 1
        Row(2, 5, 0);               // 2 in objstm 5, index 0
        Row(2, 5, 1);               // 3
        Row(1, offsets[4], 0);      // 4
        Row(1, offsets[5], 0);      // 5
        Row(2, 5, 2);               // 6
        Row(1, xref, 0);            // 7 = this xref stream
        var xdata = Filters.Deflate(rows.ToArray());
        W($"7 0 obj\n<< /Type /XRef /Size 8 /W [1 4 2] /Root 1 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] /Filter /FlateDecode /Length {xdata.Length} >>\nstream\n");
        ms.Write(xdata); W("\nendstream\nendobj\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    static double Ink(PdfiumDocument doc, PdfRect rect)
    {
        var (r, g, b) = TestPdf.AverageColor(doc, 0, rect);
        return 1 - (r + g + b) / (3 * 255.0);
    }
}
