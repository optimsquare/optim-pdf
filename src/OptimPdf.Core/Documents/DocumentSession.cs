using OptimPdf.Core.IO;
using OptimPdf.Core.Rendering;
using OptimPdf.Core.Security;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OptimPdf.Core.Documents;

/// <summary>
/// One open document. The session holds the current PDF bytes; every edit is applied through PDFsharp,
/// re-serialized, and re-opened in PDFium for display. Keeping each version's bytes gives exact undo/redo.
/// The file on disk is only touched by <see cref="Save"/>/<see cref="SaveAs"/>.
/// </summary>
/// <remarks>
/// Encrypted documents: the PDF writer decrypts on load and cannot preserve the original encryption,
/// so the session keeps the document's <see cref="Protection"/> and re-applies it after every edit.
/// Editing requires the owner (permissions) password. When a document was opened with the owner
/// password but also has an open password, that open password cannot be recovered from the file;
/// <see cref="NeedsOpenPassword"/> is then true and edits are refused until <see cref="ProvideOpenPassword"/> is called.
/// </remarks>
public sealed class DocumentSession : IDisposable
{
    /// <summary>Total bytes of undo history kept before the oldest steps are dropped.</summary>
    public const long MaxHistoryBytes = 512L * 1024 * 1024;

    readonly LinkedList<HistoryEntry> _undo = new();
    readonly Stack<HistoryEntry> _redo = new();
    long _historyBytes;
    byte[] _savedBytes;

    DocumentSession(string? path, byte[] data, PdfiumDocument renderer, string? password)
    {
        FilePath = path;
        CurrentBytes = data;
        _savedBytes = data;
        Renderer = renderer;
        Password = password;
        InspectProtection();
    }

    public string? FilePath { get; private set; }
    public string DisplayName => FilePath is null ? "Untitled.pdf" : Path.GetFileName(FilePath);

    /// <summary>The password that opens the current version (null when not encrypted or none was needed).</summary>
    public string? Password { get; private set; }

    /// <summary>How the document is protected; re-applied on every edit. Null when not encrypted.</summary>
    public ProtectionOptions? Protection { get; private set; }

    /// <summary>True when the document is encrypted and was opened without the owner password.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>The document has an open password that must be re-entered before it can be edited.</summary>
    public bool NeedsOpenPassword { get; private set; }

    /// <summary>Stable identifier of this session, used for autosave snapshots.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Incremented on every content change; lets autosave detect new versions cheaply.</summary>
    public long ContentVersion { get; private set; }

    public bool IsEncrypted => Renderer.SecurityRevision >= 0;
    public byte[] CurrentBytes { get; private set; }
    public PdfiumDocument Renderer { get; private set; }
    public int PageCount => Renderer.PageCount;
    public bool IsDirty => !ReferenceEquals(CurrentBytes, _savedBytes);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.TryPeek(out var e) ? e.Description : null;

    /// <summary>Raised after any change of document content (edit, undo, redo) or save state.</summary>
    public event EventHandler? Changed;

    public static DocumentSession Open(string path, string? password = null)
    {
        var data = File.ReadAllBytes(path);
        var renderer = PdfiumDocument.Open(data, password);
        return new DocumentSession(Path.GetFullPath(path), data, renderer, password);
    }

    public static DocumentSession FromBytes(byte[] data, string? suggestedPath = null, string? password = null)
    {
        var renderer = PdfiumDocument.Open(data, password);
        var session = new DocumentSession(suggestedPath, data, renderer, password);
        session._savedBytes = []; // Never saved: always dirty.
        return session;
    }

    /// <summary>Applies an edit. If the edit throws, the document is left unchanged.</summary>
    public void Apply(string description, Action<PdfDocument> edit) => Commit(description, null, edit, Protection);

    /// <summary>
    /// Applies a two-stage edit: <paramref name="prepare"/> receives the current bytes and password and returns
    /// new, unencrypted bytes (for edits done with PDFium); <paramref name="edit"/> then runs on the result.
    /// Protection is re-applied afterwards as for any edit.
    /// </summary>
    public void Apply(string description, Func<byte[], string?, byte[]> prepare, Action<PdfDocument> edit) =>
        Commit(description, prepare, edit, Protection);

    /// <summary>
    /// Applies an edit that produces the new file bytes itself and must not be re-serialized, such as an
    /// incremental update adding a digital signature.
    /// </summary>
    public void ApplyRaw(string description, Func<byte[], byte[]> produce)
    {
        if (IsReadOnly)
            throw new UnauthorizedAccessException("This document is protected. Open it with its permissions password to make changes.");
        var updated = produce(CurrentBytes);
        var renderer = PdfiumDocument.Open(updated, Password);
        PushUndo(Snapshot(description));
        _redo.Clear();
        Swap(updated, renderer);
    }

    /// <summary>
    /// Number of digital signatures. Any edit other than adding another signature rewrites the file and
    /// invalidates them, so the app asks before such edits.
    /// </summary>
    public int SignatureCount => Renderer.GetSignatures().Count;

    /// <summary>Encrypts the document (or changes its passwords and permissions).</summary>
    public void Protect(ProtectionOptions options) => Commit("Protect document", null, _ => { }, options);

    /// <summary>Removes encryption. Requires owner access, like any edit.</summary>
    public void RemoveProtection() => Commit("Remove security", null, _ => { }, protection: null);

    /// <summary>
    /// Supplies the open password of a document opened with its owner password, so its protection can be
    /// re-applied after edits. Pass null to drop the open password (anyone can then open the file).
    /// </summary>
    public void ProvideOpenPassword(string? openPassword)
    {
        if (Protection is null || !NeedsOpenPassword) return;
        if (openPassword is { Length: > 0 })
        {
            try { using var check = PdfiumDocument.Open(CurrentBytes, openPassword); }
            catch (PdfPasswordRequiredException) { throw new UnauthorizedAccessException("That is not this document's open password."); }
        }
        Protection = Protection with { OpenPassword = string.IsNullOrEmpty(openPassword) ? null : openPassword };
        NeedsOpenPassword = false;
    }

    /// <summary>
    /// False when the current version certainly has no annotations and no form, found by reading just the page
    /// dictionaries (no stream data). Lets callers skip loading the full object model for scans and plain documents.
    /// </summary>
    public bool MayHaveAnnotationsOrForm()
    {
        try
        {
            var raw = new RawPdf(CurrentBytes);
            if (raw.ResolveDict(raw.Root)?["AcroForm"] is not null) return true;
            foreach (int n in raw.PageObjectNumbers())
                if (raw.ResolveDict(new RawRef(n, 0))?["Annots"] is { } annots && raw.Resolve(annots) is RawArray { Items.Count: > 0 }) return true;
            return false;
        }
        catch (Exception) { return true; } // Encrypted object streams, damaged files: assume yes.
    }

    /// <summary>Runs a read-only query against the PDFsharp object model of the current version.</summary>
    public T Query<T>(Func<PdfDocument, T> query)
    {
        using var doc = OpenModel(CurrentBytes, Password, PdfDocumentOpenMode.Import);
        return query(doc);
    }

    public void Undo()
    {
        if (_undo.Last is not { } node) return;
        _undo.RemoveLast();
        _historyBytes -= node.Value.Bytes.Length;
        _redo.Push(Snapshot(node.Value.Description));
        Restore(node.Value);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var entry)) return;
        PushUndo(Snapshot(entry.Description));
        Restore(entry);
    }

    /// <returns>The backup file path, if a previous version was backed up.</returns>
    public string? Save()
    {
        if (FilePath is null) throw new InvalidOperationException("The document has no file name yet; use Save As.");
        var backup = SafeFileWriter.Write(FilePath, CurrentBytes);
        _savedBytes = CurrentBytes;
        Changed?.Invoke(this, EventArgs.Empty);
        return backup;
    }

    public string? SaveAs(string path)
    {
        var backup = SafeFileWriter.Write(path, CurrentBytes);
        FilePath = Path.GetFullPath(path);
        _savedBytes = CurrentBytes;
        Changed?.Invoke(this, EventArgs.Empty);
        return backup;
    }

    /// <summary>Writes a copy of the current version without changing this session's file or saved state.</summary>
    public void SaveCopy(string path) => SafeFileWriter.Write(path, CurrentBytes, keepBackup: true);

    /// <summary>Opens, edits and re-serializes a document; <paramref name="protection"/> is applied before saving.</summary>
    public static byte[] Transform(byte[] data, string? password, Action<PdfDocument> edit, ProtectionOptions? protection = null)
    {
        using var doc = OpenModel(data, password, PdfDocumentOpenMode.Modify);
        edit(doc);
        // The writer always saves decrypted, so protection is applied afresh each time.
        if (protection is not null) SecurityService.Protect(doc, protection);
        return Serialize(doc);
    }

    /// <summary>
    /// Saves to bytes. Metadata the writer stamps on its own is removed when the document has none:
    /// an Info dictionary only if it had properties, XMP only if it had an XMP stream. Documents keep
    /// their own metadata (and get updated dates) as before.
    /// </summary>
    public static byte[] Serialize(PdfDocument doc)
    {
        // Real properties: anything except dates and the writer's own Creator/Producer stamps.
        bool hadInfo = doc.Info.Elements.Keys.Any(k => k is not ("/CreationDate" or "/ModDate")
            && !(doc.Info.Elements.GetString(k)?.StartsWith("PDFsharp", StringComparison.Ordinal) ?? false));
        bool hadXmp = doc.Internals.Catalog.Elements.ContainsKey("/Metadata");
        using var ms = new MemoryStream(capacity: Math.Max(64 * 1024, (int)Math.Min(int.MaxValue / 2, doc.FileSize + doc.FileSize / 8)));
        doc.Save(ms, closeStream: false);
        // The array is ours, so the scrubber may edit it in place (no extra copy of a large file).
        return WriterMetadataScrubber.Scrub(ms.ToArray(), removeInfo: !hadInfo, removeXmp: !hadXmp, inPlace: true);
    }

    void Commit(string description, Func<byte[], string?, byte[]>? prepare, Action<PdfDocument> edit, ProtectionOptions? protection)
    {
        if (IsReadOnly)
            throw new UnauthorizedAccessException("This document is protected. Open it with its permissions password to make changes.");
        if (NeedsOpenPassword && protection is not null && ReferenceEquals(protection, Protection))
            throw new InvalidOperationException("Enter the document's open password first, so it stays protected after editing.");

        var updated = prepare is null
            ? Transform(CurrentBytes, Password, edit, protection)
            : Transform(prepare(CurrentBytes, Password), null, edit, protection);
        // The owner password keeps full access to the new version.
        string? password = protection?.OwnerPassword;
        var renderer = PdfiumDocument.Open(updated, password); // Validate before committing.

        PushUndo(Snapshot(description));
        _redo.Clear();
        Password = password;
        if (!ReferenceEquals(protection, Protection)) NeedsOpenPassword = false;
        Protection = protection;
        Swap(updated, renderer);
    }

    HistoryEntry Snapshot(string description) => new(description, CurrentBytes, Password, Protection, NeedsOpenPassword);

    void Restore(HistoryEntry entry)
    {
        Password = entry.Password;
        Protection = entry.Protection;
        NeedsOpenPassword = entry.NeedsOpenPassword;
        Swap(entry.Bytes, PdfiumDocument.Open(entry.Bytes, Password));
    }

    /// <summary>Works out editability and the protection to re-apply for an encrypted document.</summary>
    void InspectProtection()
    {
        if (!IsEncrypted) return;
        try
        {
            using var _ = OpenModel(CurrentBytes, Password, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException)
        {
            IsReadOnly = true; // Opened with the open (user) password only.
            return;
        }

        var permissions = PdfPermissions.FromFlags(Renderer.UserPermissionFlags);
        var strength = Renderer.SecurityRevision >= 5 ? EncryptionStrength.Aes256 : EncryptionStrength.Aes128;
        bool opensWithoutPassword;
        try { using var probe = PdfiumDocument.Open(CurrentBytes); opensWithoutPassword = true; }
        catch (PdfPasswordRequiredException) { opensWithoutPassword = false; }

        Protection = new ProtectionOptions(null, Password ?? "", permissions, strength);
        NeedsOpenPassword = !opensWithoutPassword;
    }

    static PdfDocument OpenModel(byte[] data, string? password, PdfDocumentOpenMode mode)
    {
        var stream = new MemoryStream(data, writable: false);
        return password is null ? PdfReader.Open(stream, mode) : PdfReader.Open(stream, password, mode);
    }

    void PushUndo(HistoryEntry entry)
    {
        _undo.AddLast(entry);
        _historyBytes += entry.Bytes.Length;
        // Always keep at least one undo step, even for a very large file.
        while (_historyBytes > MaxHistoryBytes && _undo.Count > 1)
        {
            _historyBytes -= _undo.First!.Value.Bytes.Length;
            _undo.RemoveFirst();
        }
    }

    void Swap(byte[] bytes, PdfiumDocument renderer)
    {
        var old = Renderer;
        CurrentBytes = bytes;
        Renderer = renderer;
        ContentVersion++;
        old.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Renderer.Dispose();

    sealed record HistoryEntry(string Description, byte[] Bytes, string? Password, ProtectionOptions? Protection, bool NeedsOpenPassword);
}
