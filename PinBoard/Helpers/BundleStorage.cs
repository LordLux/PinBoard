using System.Security.Cryptography;
using PinBoard.Models;

namespace PinBoard.Helpers;

/// Handles serializing, DPAPI-encrypting (CurrentUser scope), and persisting
/// FormatBundles. Encrypting protects clipboard payloads at rest; DPAPI keys
/// are tied to the Windows user account and require no password prompts.
internal static class BundleStorage
{
    public static async Task WriteAsync(string path, FormatBundle bundle)
    {
        var raw       = BundleSerializer.Serialize(bundle);
        var encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(path, encrypted);
    }

    public static async Task<FormatBundle?> ReadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path);
            var raw       = ProtectedData.Unprotect(
                                encrypted, null, DataProtectionScope.CurrentUser);
            return BundleSerializer.Deserialize(raw);
        }
        catch (CryptographicException)
        {
            // Key unavailable — different user account or corrupted profile.
            return null;
        }
    }
}
