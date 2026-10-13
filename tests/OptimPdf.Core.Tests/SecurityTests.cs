using System.Text;
using OptimPdf.Core.Documents;
using OptimPdf.Core.Pages;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Security;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace OptimPdf.Core.Tests;

public class SecurityTests
{
    const string Open = "open-Secret1", Owner = "owner-Secret2";

    static byte[] Protected(EncryptionStrength strength = EncryptionStrength.Aes256, string? open = Open, PdfPermissions? permissions = null) =>
        DocumentSession.Transform(TestPdf.Create(2), null, doc =>
            SecurityService.Protect(doc, new ProtectionOptions(open, Owner, permissions ?? new PdfPermissions(), strength)));

    [Fact]
    public void Aes256_open_password_is_required_and_works()
    {
        var data = Protected();
        Assert.Throws<PdfPasswordRequiredException>(() => PdfiumDocument.Open(data));
        Assert.Throws<PdfPasswordRequiredException>(() => PdfiumDocument.Open(data, "wrong"));

        using var user = PdfiumDocument.Open(data, Open);
        Assert.Equal(6, user.SecurityRevision); // AES-256 (ISO 32000-2)
        Assert.Contains(TestPdf.Title(2), user.GetPageText(1));
        using var owner = PdfiumDocument.Open(data, Owner);
        Assert.True(SecurityService.IsOwnerAccess(owner.PermissionFlags));
    }

    [Fact]
    public void Aes128_uses_revision_4()
    {
        using var r = PdfiumDocument.Open(Protected(EncryptionStrength.Aes128), Open);
        Assert.Equal(4, r.SecurityRevision);
    }

    [Fact]
    public void Text_is_not_readable_in_the_encrypted_file()
    {
        var data = Protected();
        Assert.DoesNotContain("Optim test", Encoding.Latin1.GetString(data));
    }

    [Fact]
    public void Permissions_are_enforced_flags_for_open_without_password()
    {
        var data = Protected(open: null, permissions: new PdfPermissions(Print: false, CopyContent: false, Modify: false, Annotate: true));
        using var r = PdfiumDocument.Open(data); // No open password.
        var p = PdfPermissions.FromFlags(r.PermissionFlags);
        Assert.False(p.Print);
        Assert.False(p.CopyContent);
        Assert.False(p.Modify);
        Assert.True(p.Annotate);
        Assert.False(SecurityService.IsOwnerAccess(r.PermissionFlags));
    }

    [Fact]
    public void Passwords_must_differ_and_owner_is_required()
    {
        Assert.Throws<ArgumentException>(() => DocumentSession.Transform(TestPdf.Create(1), null, d =>
            SecurityService.Protect(d, new ProtectionOptions("same", "same", PdfPermissions.All))));
        Assert.Throws<ArgumentException>(() => DocumentSession.Transform(TestPdf.Create(1), null, d =>
            SecurityService.Protect(d, new ProtectionOptions("x", "", PdfPermissions.All))));
    }

    [Fact]
    public void Editing_a_protected_document_keeps_it_encrypted_with_its_permissions()
    {
        using var session = DocumentSession.FromBytes(TestPdf.Create(3));
        session.Protect(new ProtectionOptions(Open, Owner, new PdfPermissions(Print: false, CopyContent: false)));
        Assert.True(session.IsEncrypted);
        Assert.Equal(Owner, session.Password);

        session.Apply("Rotate", d => PageOperations.Rotate(d, [0], 90));
        session.Apply("Delete", d => PageOperations.Delete(d, [2]));

        Assert.Throws<PdfPasswordRequiredException>(() => PdfiumDocument.Open(session.CurrentBytes));
        using var check = PdfiumDocument.Open(session.CurrentBytes, Open);
        Assert.Equal(6, check.SecurityRevision);
        Assert.Equal(2, check.PageCount);
        Assert.Equal(792, check.GetPageSize(0).Width, 1);
        var p = PdfPermissions.FromFlags(check.PermissionFlags);
        Assert.False(p.Print);
        Assert.False(p.CopyContent);
        Assert.True(p.Annotate);
    }

    [Fact]
    public void Undo_of_protect_restores_the_unencrypted_version()
    {
        using var session = DocumentSession.FromBytes(TestPdf.Create(1));
        session.Protect(new ProtectionOptions(Open, Owner, new PdfPermissions()));
        session.Undo();
        Assert.Null(session.Password);
        Assert.Null(session.Protection);
        Assert.False(session.IsEncrypted);
        session.Redo();
        Assert.Equal(Owner, session.Password);
        Assert.True(session.IsEncrypted);
        session.Undo();
        // Edits after undoing protection are saved unencrypted.
        session.Apply("Rotate", d => PageOperations.Rotate(d, [0], 90));
        Assert.Equal(-1, PdfiumDocument.Open(session.CurrentBytes).SecurityRevision);
    }

    [Fact]
    public void Open_password_only_gives_read_only_access()
    {
        using var session = DocumentSession.FromBytes(Protected(), password: Open);
        Assert.True(session.IsReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => session.Apply("Rotate", d => PageOperations.Rotate(d, [0], 90)));
        Assert.Throws<UnauthorizedAccessException>(() => session.RemoveProtection());
        Assert.Contains("Optim test", session.Renderer.GetPageText(0)); // Viewing still works.
    }

    [Fact]
    public void Owner_access_with_unknown_open_password_asks_for_it_before_editing()
    {
        using var session = DocumentSession.FromBytes(Protected(permissions: new PdfPermissions(Print: false)), password: Owner);
        Assert.False(session.IsReadOnly);
        Assert.True(session.NeedsOpenPassword);
        Assert.Throws<InvalidOperationException>(() => session.Apply("Rotate", d => PageOperations.Rotate(d, [0], 90)));

        Assert.Throws<UnauthorizedAccessException>(() => session.ProvideOpenPassword("wrong"));
        session.ProvideOpenPassword(Open);
        session.Apply("Rotate", d => PageOperations.Rotate(d, [0], 90));

        Assert.Throws<PdfPasswordRequiredException>(() => PdfiumDocument.Open(session.CurrentBytes));
        using var user = PdfiumDocument.Open(session.CurrentBytes, Open);
        Assert.False(PdfPermissions.FromFlags(user.PermissionFlags).Print); // Restrictions kept.
        using var owner = PdfiumDocument.Open(session.CurrentBytes, Owner);
        Assert.True(SecurityService.IsOwnerAccess(owner.PermissionFlags));
    }

    [Fact]
    public void Restrictions_without_open_password_survive_edits()
    {
        var data = Protected(open: null, permissions: new PdfPermissions(Print: false));
        using var session = DocumentSession.FromBytes(data, password: Owner);
        Assert.False(session.NeedsOpenPassword);
        session.Apply("Rotate", d => PageOperations.Rotate(d, [0], 90));
        using var anyone = PdfiumDocument.Open(session.CurrentBytes);
        Assert.False(PdfPermissions.FromFlags(anyone.PermissionFlags).Print);
    }

    [Fact]
    public void Restricted_document_opened_without_password_is_read_only()
    {
        using var session = DocumentSession.FromBytes(Protected(open: null));
        Assert.True(session.IsReadOnly);
    }

    [Fact]
    public void Remove_protection_with_owner_password()
    {
        using var session = DocumentSession.FromBytes(Protected(), password: Owner);
        Assert.True(SecurityService.IsOwnerAccess(session.Renderer.PermissionFlags));
        session.RemoveProtection();
        using var r = PdfiumDocument.Open(session.CurrentBytes);
        Assert.Equal(-1, r.SecurityRevision);
        Assert.Contains("Optim test", r.GetPageText(0));
        Assert.Null(session.Protection);
    }

    [Fact]
    public void User_password_does_not_grant_owner_access()
    {
        using var r = PdfiumDocument.Open(Protected(), Open);
        Assert.False(SecurityService.IsOwnerAccess(r.PermissionFlags));
    }
}

public class SanitizeTests
{
    const string Secret = "SECRET-AUTHOR-4711";

    static byte[] DocumentWithHiddenData() => DocumentSession.Transform(TestPdf.Create(2), null, doc =>
    {
        doc.Info.Author = Secret;
        doc.Info.Title = "Board minutes";
        doc.Info.Elements.SetString("/Department", "Finance");
        var catalog = doc.Internals.Catalog;

        var xmp = new PdfDictionary(doc);
        xmp.Elements.SetName("/Type", "/Metadata");
        xmp.Elements.SetName("/Subtype", "/XML");
        xmp.CreateStream(Encoding.UTF8.GetBytes($"<x:xmpmeta xmlns:x='adobe:ns:meta/'><creator>{Secret}-XMP</creator></x:xmpmeta>"));
        doc.Internals.AddObject(xmp);
        catalog.Elements["/Metadata"] = xmp.Reference;

        var js = new PdfDictionary(doc);
        js.Elements.SetName("/S", "/JavaScript");
        js.Elements.SetString("/JS", "app.alert('hi')");
        catalog.Elements["/OpenAction"] = js;

        var piece = new PdfDictionary(doc);
        piece.Elements.SetString("/Private", Secret + "-PIECE");
        doc.Pages[0].Elements["/PieceInfo"] = piece;

        using var attachment = new MemoryStream(Encoding.ASCII.GetBytes(Secret + "-ATTACHMENT"));
        doc.AddEmbeddedFile("notes.txt", attachment);
    });

    [Fact]
    public void Inspect_reports_hidden_data()
    {
        var findings = DocumentSession.FromBytes(DocumentWithHiddenData()).Query(Sanitizer.Inspect);
        Assert.Contains(findings, f => f.Contains(Secret));
        Assert.Contains("XMP metadata", findings);
        Assert.Contains("Attached files", findings);
        Assert.Contains("Scripts or open actions", findings);
        Assert.Contains("Application-private data", findings);
    }

    [Fact]
    public void Sanitize_removes_everything_from_the_saved_bytes()
    {
        SanitizeReport? report = null;
        var clean = DocumentSession.Transform(DocumentWithHiddenData(), null, doc => report = Sanitizer.Sanitize(doc, new SanitizeOptions()));
        Assert.NotNull(report);
        Assert.True(report!.InfoFields >= 3);
        Assert.Equal(1, report.XmpStreams);
        Assert.True(report.EmbeddedFiles >= 1);
        Assert.True(report.Scripts >= 1);
        Assert.True(report.PrivateData >= 1);

        // Nothing of the secret survives anywhere in the file, compressed or not.
        var text = Encoding.Latin1.GetString(clean);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("Board minutes", text);
        Assert.DoesNotContain("/JavaScript", text);
        var decoded = DocumentSession.FromBytes(clean).Query(d => string.Join("\n",
            d.Internals.GetAllObjects().OfType<PdfDictionary>().Where(o => o.Stream is not null)
                .Select(o => Encoding.Latin1.GetString(o.Stream.UnfilteredValue))));
        Assert.DoesNotContain(Secret, decoded);

        // Content is untouched.
        using var r = PdfiumDocument.Open(clean);
        Assert.Contains(TestPdf.Title(2), r.GetPageText(1));
    }

    [Fact]
    public void Sanitized_file_carries_no_writer_metadata_either()
    {
        var clean = DocumentSession.Transform(DocumentWithHiddenData(), null, doc => Sanitizer.Sanitize(doc, new SanitizeOptions()));
        using (var r = PdfiumDocument.Open(clean))
            foreach (var tag in new[] { "Title", "Author", "Creator", "Producer", "CreationDate", "ModDate" })
                Assert.Null(r.GetMetadata(tag));
        Assert.DoesNotContain("PDFsharp", Encoding.Latin1.GetString(clean));
        Assert.Empty(DocumentSession.FromBytes(clean).Query(Sanitizer.Inspect));

        // Stays clean through later edits.
        var edited = DocumentSession.Transform(clean, null, d => Core.Pages.PageOperations.Rotate(d, [0], 90));
        Assert.Empty(DocumentSession.FromBytes(edited).Query(Sanitizer.Inspect));
        Assert.DoesNotContain("PDFsharp", Encoding.Latin1.GetString(edited));
    }

    [Fact]
    public void Scrubbing_keeps_the_cross_reference_table_exact()
    {
        var clean = DocumentSession.Transform(DocumentWithHiddenData(), null, doc => Sanitizer.Sanitize(doc, new SanitizeOptions()));
        // Every xref entry must still point exactly at "N G obj".
        var text = Encoding.Latin1.GetString(clean);
        int xref = text.LastIndexOf("\nxref", StringComparison.Ordinal) + 1;
        var lines = text[xref..].Split('\n');
        var header = lines[1].Trim().Split(' ');
        int first = int.Parse(header[0]), count = int.Parse(header[1]);
        for (int i = 0; i < count; i++)
        {
            var entry = lines[2 + i];
            if (entry[17] != 'n') continue;
            int offset = int.Parse(entry[..10]);
            Assert.StartsWith($"{first + i} ", text[offset..(offset + 12)]);
        }
        // A strict reader opens it too.
        using var strict = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(clean), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(2, strict.PageCount);
    }

    [Fact]
    public void Documents_keep_their_own_metadata()
    {
        var data = DocumentSession.Transform(TestPdf.Create(1, title: "Annual report"), null, d => Core.Pages.PageOperations.Rotate(d, [0], 90));
        using var r = PdfiumDocument.Open(data);
        Assert.Equal("Annual report", r.GetMetadata("Title"));
    }

    [Fact]
    public void New_documents_are_written_without_tool_fingerprints()
    {
        using var r = PdfiumDocument.Open(TestPdf.Create(1));
        Assert.Null(r.GetMetadata("Producer"));
        Assert.Null(r.GetMetadata("Creator"));
    }

    [Fact]
    public void Sanitizing_an_encrypted_document_keeps_it_encrypted_and_clean()
    {
        using var session = DocumentSession.FromBytes(DocumentWithHiddenData());
        session.Protect(new ProtectionOptions("open-pw", "owner-pw", new PdfPermissions()));
        session.Apply("Sanitize", d => Sanitizer.Sanitize(d, new SanitizeOptions()));
        Assert.Throws<PdfPasswordRequiredException>(() => PdfiumDocument.Open(session.CurrentBytes));
        using var r = PdfiumDocument.Open(session.CurrentBytes, "open-pw");
        Assert.Null(r.GetMetadata("Producer"));
        Assert.Null(r.GetMetadata("Author"));
    }

    [Fact]
    public void Optionally_removes_comments_but_keeps_links()
    {
        var data = DocumentSession.Transform(TestPdf.Create(1), null, doc =>
            Core.Annotations.AnnotationService.AddNote(doc, 0, 100, 100, "remove me"));
        var clean = DocumentSession.Transform(data, null, doc => Sanitizer.Sanitize(doc, new SanitizeOptions(Comments: true)));
        Assert.Empty(DocumentSession.FromBytes(clean).Query(Core.Annotations.AnnotationService.List));
    }
}
