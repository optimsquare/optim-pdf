using PdfSharp.Pdf;

namespace OptimPdf.Core.Security;

public enum EncryptionStrength
{
    /// <summary>AES-256 (PDF 2.0, security handler revision 6). Recommended.</summary>
    Aes256,
    /// <summary>AES-128 for older readers (PDF 1.6, revision 4).</summary>
    Aes128,
}

/// <summary>What people who open the file with the open password may do. Ignored when the owner password is used.</summary>
public sealed record PdfPermissions(
    bool Print = true,
    bool HighQualityPrint = true,
    bool Modify = false,
    bool CopyContent = true,
    bool Annotate = true,
    bool FillForms = true,
    bool Assemble = false)
{
    public static readonly PdfPermissions All = new(true, true, true, true, true, true, true);
    public static readonly PdfPermissions ReadOnly = new(Print: true, Modify: false, CopyContent: false, Annotate: false, FillForms: false);

    /// <summary>Decodes the /P value of an encryption dictionary (ISO 32000-2 Table 22).</summary>
    public static PdfPermissions FromFlags(ulong p) => new(
        Print: (p & (1 << 2)) != 0,
        HighQualityPrint: (p & (1 << 11)) != 0,
        Modify: (p & (1 << 3)) != 0,
        CopyContent: (p & (1 << 4)) != 0,
        Annotate: (p & (1 << 5)) != 0,
        FillForms: (p & (1 << 8)) != 0,
        Assemble: (p & (1 << 10)) != 0);
}

public sealed record ProtectionOptions(
    string? OpenPassword,
    string OwnerPassword,
    PdfPermissions Permissions,
    EncryptionStrength Strength = EncryptionStrength.Aes256);

public static class SecurityService
{
    /// <summary>
    /// Encrypts the document. With an open password, nobody can read the file without it.
    /// Without one, anyone can open the file, but permission restrictions apply unless the owner
    /// password is given. Note: permissions are honored by conforming readers; they are not a
    /// cryptographic guarantee against a determined user who can open the file.
    /// </summary>
    public static void Protect(PdfDocument doc, ProtectionOptions options)
    {
        if (string.IsNullOrEmpty(options.OwnerPassword))
            throw new ArgumentException("A permissions (owner) password is required.", nameof(options));
        if (options.OpenPassword is { Length: > 0 } open && open == options.OwnerPassword)
            throw new ArgumentException("The open password and the permissions password must differ.", nameof(options));

        var settings = doc.SecuritySettings;
        var handler = doc.SecurityHandler;
        handler.SetEncryptionToNoneAndResetPasswords();
        if (options.Strength == EncryptionStrength.Aes256) handler.SetEncryptionToV5(encryptMetadata: true);
        else handler.SetEncryptionToV4UsingAES(encryptMetadata: true);

        settings.UserPassword = options.OpenPassword ?? "";
        settings.OwnerPassword = options.OwnerPassword;
        var p = options.Permissions;
        settings.PermitPrint = p.Print;
        settings.PermitFullQualityPrint = p.HighQualityPrint;
        settings.PermitModifyDocument = p.Modify;
        settings.PermitExtractContent = p.CopyContent;
        settings.PermitAnnotations = p.Annotate;
        settings.PermitFormsFill = p.FillForms;
        settings.PermitAssembleDocument = p.Assemble;
    }

    /// <summary>
    /// Removes encryption and passwords. Callers must have opened the document with the owner password
    /// (check <see cref="IsOwnerAccess"/>): removing security with only the open password would bypass the
    /// author's restrictions.
    /// </summary>
    public static void RemoveProtection(PdfDocument doc) => doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();

    /// <summary>
    /// True when the permission flags PDFium reports for the current password are unrestricted (owner access,
    /// or a document whose author allowed everything). Bits 1–2 are reserved and always 0.
    /// </summary>
    public static bool IsOwnerAccess(ulong permissionFlags) => ((uint)permissionFlags | 3) == 0xFFFFFFFF;
}