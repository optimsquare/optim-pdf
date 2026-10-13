using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OptimPdf.Core.Signatures;

namespace OptimPdf.App.Services;

public sealed record SavedSignature(string Id, string Name, SignatureMark Mark);

/// <summary>
/// The user's saved signatures. Stored encrypted with Windows DPAPI (current user scope), so other
/// accounts on the PC cannot read them, and kept on this device only.
/// </summary>
public static class SignatureStore
{
    static string FilePath => Path.Combine(App.DataDirectory, "signatures.bin");
    static readonly byte[] Entropy = "Optim PDF signatures v1"u8.ToArray();

    public static List<SavedSignature> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException)
        {
            App.LogError(ex);
            return [];
        }
    }

    public static void Save(List<SavedSignature> signatures)
    {
        Directory.CreateDirectory(App.DataDirectory);
        var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(signatures), Entropy, DataProtectionScope.CurrentUser);
        Core.IO.SafeFileWriter.Write(FilePath, data, keepBackup: false);
    }

    public static SavedSignature Add(string name, SignatureMark mark)
    {
        var all = Load();
        var saved = new SavedSignature(Guid.NewGuid().ToString("N"), name, mark);
        all.Insert(0, saved);
        Save(all);
        return saved;
    }

    public static void Remove(string id)
    {
        var all = Load();
        all.RemoveAll(s => s.Id == id);
        Save(all);
    }
}
