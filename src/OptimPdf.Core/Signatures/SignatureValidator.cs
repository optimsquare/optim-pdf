using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using OptimPdf.Core.Rendering;

namespace OptimPdf.Core.Signatures;

public sealed record SignatureStatus(
    int Index,
    string SignerName,
    X509Certificate2? Certificate,
    string SubFilter,
    DateTimeOffset? ClaimedTime,
    DateTimeOffset? TimestampTime,
    string? Reason,
    /// <summary>The signed bytes are exactly what was signed (digest and signature verified).</summary>
    bool IntegrityValid,
    /// <summary>The byte range covers the whole file as it was when signed (only the signature value excluded).</summary>
    bool RangeValid,
    /// <summary>Bytes were appended after this signature (later edits or later signatures).</summary>
    bool ChangedAfterSigning,
    bool CertificateTrusted,
    string TrustDetail,
    string? Error)
{
    public bool IsValid => IntegrityValid && RangeValid && Error is null;
}

/// <summary>
/// Checks digital signatures independently of how they were written: PDFium reads the signature dictionaries,
/// and .NET's CMS implementation verifies the digest and signature over the byte range.
/// </summary>
public static class SignatureValidator
{
    const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    /// <param name="trustedCertificates">Extra trust anchors (e.g. identities the user chose to trust).</param>
    /// <param name="checkRevocationOnline">Contact CRL/OCSP servers. Off by default: Optim PDF works offline.</param>
    public static IReadOnlyList<SignatureStatus> Validate(byte[] pdf, string? password = null,
        IEnumerable<X509Certificate2>? trustedCertificates = null, bool checkRevocationOnline = false)
    {
        using var doc = PdfiumDocument.Open(pdf, password);
        var signatures = doc.GetSignatures();
        var trusted = trustedCertificates?.Select(c => c.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var result = new List<SignatureStatus>();
        for (int i = 0; i < signatures.Count; i++)
            result.Add(Check(i, pdf, signatures[i], trusted, checkRevocationOnline));
        return result;
    }

    static SignatureStatus Check(int index, byte[] pdf, PdfSignatureData sig, HashSet<string> trusted, bool online)
    {
        string subFilter = sig.SubFilter ?? "";
        var claimed = ParsePdfDate(sig.Time);
        var range = sig.ByteRange;

        // The byte range must start at 0 and skip exactly the /Contents hex string.
        bool rangeValid = range.Length == 4 && range[0] == 0 && range[1] > 0 && range[2] > range[1]
                          && (long)range[2] + range[3] <= pdf.Length
                          && pdf[range[1]] == '<' && pdf[range[2] - 1] == '>';
        if (!rangeValid)
            return new(index, "", null, subFilter, claimed, null, sig.Reason, false, false, false, false, "", "The signature's byte range is invalid.");
        bool changedAfter = (long)range[2] + range[3] < pdf.Length && pdf.AsSpan(range[2] + range[3]).Trim("\r\n "u8).Length > 0;

        if (subFilter is not ("ETSI.CAdES.detached" or "adbe.pkcs7.detached"))
            return new(index, "", null, subFilter, claimed, null, sig.Reason, false, true, changedAfter, false, "",
                $"Signatures of type {subFilter} are not supported for verification.");

        var signed = new byte[range[1] + range[3]];
        Buffer.BlockCopy(pdf, 0, signed, 0, range[1]);
        Buffer.BlockCopy(pdf, range[2], signed, range[1], range[3]);

        SignedCms cms;
        try
        {
            // /Contents is zero-padded; decode only the DER value itself.
            AsnDecoder.ReadEncodedValue(sig.Contents, AsnEncodingRules.BER, out _, out _, out int length);
            cms = new SignedCms(new ContentInfo(signed), detached: true);
            cms.Decode(sig.Contents.AsSpan(0, length));
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException)
        {
            return new(index, "", null, subFilter, claimed, null, sig.Reason, false, true, changedAfter, false, "", "The signature value is damaged: " + e.Message);
        }

        var info = cms.SignerInfos[0];
        var cert = info.Certificate;
        string name = cert?.GetNameInfo(X509NameType.SimpleName, false) ?? "(unknown signer)";
        bool integrity;
        string? error = null;
        try { info.CheckSignature(verifySignatureOnly: true); integrity = true; }
        catch (CryptographicException e) { integrity = false; error = "The document was altered after it was signed, or the signature is corrupt (" + e.Message + ")."; }

        DateTimeOffset? stamped = null;
        foreach (var attr in info.UnsignedAttributes)
            if (attr.Oid.Value == TimestampTokenOid && attr.Values.Count > 0
                && Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _)
                && token.VerifySignatureForSignerInfo(info, out _))
                stamped = token.TokenInfo.Timestamp;

        var (isTrusted, detail) = cert is null ? (false, "The signing certificate is missing.") : Trust(cms, cert, stamped ?? claimed, trusted, online);
        return new(index, name, cert, subFilter, claimed, stamped, sig.Reason, integrity, true, changedAfter, isTrusted, detail, error);
    }

    static (bool, string) Trust(SignedCms cms, X509Certificate2 cert, DateTimeOffset? at, HashSet<string> trusted, bool online)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = online ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
        if (at is { } t) chain.ChainPolicy.VerificationTime = t.LocalDateTime;
        bool built = chain.Build(cert);
        var root = chain.ChainElements.Count > 0 ? chain.ChainElements[^1].Certificate : cert;
        if (built) return (true, online ? "Trusted; revocation checked online." : "Trusted (chains to a trusted root; revocation not checked offline).");
        if (trusted.Contains(root.Thumbprint) || trusted.Contains(cert.Thumbprint))
        {
            bool onlyRoot = chain.ChainStatus.All(s => s.Status is X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain
                                                        or X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation);
            if (onlyRoot) return (true, "Trusted (an identity you chose to trust).");
        }
        if (cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData))
            return (false, "Self-signed: the signer's identity is not confirmed by a certificate authority.");
        var reasons = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
        return (false, "Not trusted: " + (reasons.Length > 0 ? reasons : "the certificate chain could not be verified."));
    }

    /// <summary>Parses D:YYYYMMDDHHmmSSOHH'mm (all parts after the year optional).</summary>
    public static DateTimeOffset? ParsePdfDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = Regex.Match(s, @"^(?:D:)?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?([Zz+\-])?(\d{2})?'?(\d{2})?'?");
        if (!m.Success) return null;
        int G(int i, int d) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : d;
        var offset = TimeSpan.Zero;
        if (m.Groups[7].Value is "+" or "-")
        {
            offset = new TimeSpan(G(8, 0), G(9, 0), 0);
            if (m.Groups[7].Value == "-") offset = -offset;
        }
        try { return new DateTimeOffset(G(1, 1), G(2, 1), G(3, 1), G(4, 0), G(5, 0), G(6, 0), offset); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
