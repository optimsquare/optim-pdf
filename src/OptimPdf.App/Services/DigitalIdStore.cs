using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OptimPdf.Core.Signatures;

namespace OptimPdf.App.Services;

/// <summary>
/// Digital IDs (certificates with private keys) in the user's Windows certificate store, and the identities the
/// user trusts. Private keys stay in Windows; Optim PDF never stores them itself.
/// </summary>
public static class DigitalIdStore
{
    const string CreatedByOptim = "Optim PDF digital ID";

    /// <summary>Certificates in Personal (CurrentUser\My) that can sign documents.</summary>
    public static List<X509Certificate2> SigningCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Where(DigitalSignatureService.CanSign).OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, false)).ToList();
    }

    /// <summary>
    /// Identities trusted for validation beyond the Windows trusted roots: your own digital IDs and certificates in
    /// Trusted People (added with "Trust this signer").
    /// </summary>
    public static List<X509Certificate2> TrustAnchors()
    {
        var list = new List<X509Certificate2>();
        try
        {
            using var people = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
            people.Open(OpenFlags.ReadOnly);
            list.AddRange(people.Certificates);
            list.AddRange(SigningCertificates());
        }
        catch (CryptographicException ex) { App.LogError(ex); }
        return list;
    }

    /// <summary>Adds a signer's certificate to Trusted People, so its signatures show as trusted.</summary>
    public static void Trust(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(X509CertificateLoader.LoadCertificate(certificate.RawData)); // Public part only.
    }

    /// <summary>
    /// Creates a self-signed digital ID (RSA 3072, SHA-256, valid 5 years) and stores it in Personal. Good for
    /// signing within a team; others see it as "identity not confirmed" until they trust it.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(string name, string? email, string? organization)
    {
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(name);
        if (!string.IsNullOrWhiteSpace(organization)) dn.AddOrganizationName(organization);
        if (!string.IsNullOrWhiteSpace(email)) dn.AddEmailAddress(email);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(dn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.4") /* email protection */, new Oid("1.3.6.1.4.1.311.10.3.12") /* document signing */], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        if (!string.IsNullOrWhiteSpace(email))
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress(email);
            request.CertificateExtensions.Add(san.Build());
        }
        using var temporary = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now.AddYears(5));

        // Re-import so the private key is persisted in the user's key store (not an ephemeral key).
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var persisted = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx, password), password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        persisted.FriendlyName = CreatedByOptim;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    /// <summary>Loads a .pfx/.p12 digital ID for signing without installing it.</summary>
    public static X509Certificate2 LoadFile(string path, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);

    public static string Describe(X509Certificate2 c)
    {
        var name = c.GetNameInfo(X509NameType.SimpleName, false);
        var issuer = c.GetNameInfo(X509NameType.SimpleName, true);
        var self = c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData) ? "self-signed" : $"issued by {issuer}";
        return $"{name} ({self}, valid until {c.NotAfter:d})";
    }
}
