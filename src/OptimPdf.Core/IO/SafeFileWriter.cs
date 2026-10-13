namespace OptimPdf.Core.IO;

/// <summary>
/// Writes files so a crash or full disk can never leave a half-written PDF in place of the original:
/// data goes to a temporary file in the target folder and then atomically replaces the target.
/// The previous version is copied to a backup folder first.
/// </summary>
public static class SafeFileWriter
{
    public static string DefaultBackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Optim Square", "Optim PDF", "Backups");

    /// <summary>Number of backups kept per file name.</summary>
    public const int BackupsPerFile = 5;

    /// <returns>The backup path, or null when there was no previous file or backups are off.</returns>
    public static string? Write(string path, ReadOnlySpan<byte> data, bool keepBackup = true, string? backupDirectory = null)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        string? backup = null;
        if (keepBackup && File.Exists(path))
            backup = Backup(path, backupDirectory ?? DefaultBackupDirectory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return backup;
    }

    static string Backup(string path, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var backup = Path.Combine(backupDirectory, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}");
        File.Copy(path, backup, overwrite: true);

        // Keep only the newest backups of this file name.
        var old = Directory.GetFiles(backupDirectory, $"{name}.????????-??????-???{ext}")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(BackupsPerFile);
        foreach (var f in old)
        {
            try { File.Delete(f); } catch (IOException) { /* In use; try again next time. */ }
        }
        return backup;
    }
}
