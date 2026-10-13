using System.Text.Json;

namespace OptimPdf.Core.IO;

public sealed record RecoveryEntry(string Id, string? OriginalPath, string DisplayName, DateTime SavedAt, string DataPath, bool Encrypted, int OwnerProcessId = 0)
{
    /// <summary>True when the process that wrote the snapshot is still running (its document is still open there).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OwnerIsRunning
    {
        get
        {
            if (OwnerProcessId == 0) return false;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(OwnerProcessId);
                return !p.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
}

/// <summary>
/// Autosave snapshots of unsaved documents, kept until the document is saved or closed normally.
/// After a crash, the next start finds them with <see cref="List"/> and offers to restore them.
/// Snapshots stay inside the user's profile; encrypted documents are stored still encrypted
/// (the password itself is never written).
/// </summary>
public sealed class RecoveryStore(string directory)
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Optim Square", "Optim PDF", "Recovery");

    public string Directory { get; } = directory;

    public RecoveryEntry Save(string id, ReadOnlySpan<byte> data, string? originalPath, string displayName, bool encrypted)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var dataPath = Path.Combine(Directory, $"{id}.pdf");
        SafeFileWriter.Write(dataPath, data, keepBackup: false);
        var entry = new RecoveryEntry(id, originalPath, displayName, DateTime.Now, dataPath, encrypted, Environment.ProcessId);
        SafeFileWriter.Write(Path.Combine(Directory, $"{id}.json"), JsonSerializer.SerializeToUtf8Bytes(entry), keepBackup: false);
        return entry;
    }

    /// <summary>Snapshots left behind by sessions that are no longer running, i.e. candidates for recovery.</summary>
    public IReadOnlyList<RecoveryEntry> ListOrphaned() => List().Where(e => !e.OwnerIsRunning).ToList();

    public IReadOnlyList<RecoveryEntry> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var entries = new List<RecoveryEntry>();
        foreach (var meta in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllBytes(meta));
                if (entry is not null && File.Exists(entry.DataPath)) entries.Add(entry);
                else File.Delete(meta); // Orphaned metadata.
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* Skip unreadable entries. */ }
        }
        return entries.OrderByDescending(e => e.SavedAt).ToList();
    }

    public void Delete(string id)
    {
        foreach (var ext in new[] { ".pdf", ".json" })
        {
            var path = Path.Combine(Directory, id + ext);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* In use; it will be cleaned up next time. */ }
        }
    }
}
