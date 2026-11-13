using System.Security.Cryptography;
using System.Text;

namespace Traceline.Core.Notifications;

/// <summary>
/// Encrypts secrets (passwords, tokens, webhook URLs) before they are stored. On Windows this is DPAPI in
/// machine scope, so both an interactive run and the background service can read them but they are useless
/// on another computer. On other systems a random key kept in the data folder is used instead.
/// </summary>
public static class SecretProtector
{
    private const string Dpapi = "dpapi:", Aes = "aes:";
    private static readonly byte[] Entropy = "Traceline/v1/secret"u8.ToArray();

    /// <summary>Where the key file lives on systems without DPAPI. Set once at start-up.</summary>
    public static string KeyFolder { get; set; } = AppContext.BaseDirectory;

    /// <summary>Tests switch this on to exercise, on Windows, the path that other systems always take.</summary>
    internal static bool UseKeyFile { get; set; }

    public static bool IsProtected(string? value) =>
        value is not null && (value.StartsWith(Dpapi, StringComparison.Ordinal) || value.StartsWith(Aes, StringComparison.Ordinal));

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var data = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows() && !UseKeyFile)
            return Dpapi + Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key(), 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Aes + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        // Values typed by hand are accepted as plaintext.
        if (!IsProtected(stored)) return stored;
        try
        {
            if (stored.StartsWith(Dpapi, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return string.Empty;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Dpapi.Length..]), Entropy, DataProtectionScope.LocalMachine));
            }
            var raw = Convert.FromBase64String(stored[Aes.Length..]);
            var plain = new byte[raw.Length - 28];
            using var aes = new AesGcm(Key(), 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // Stored on another machine or with another key: the secret has to be entered again.
            return string.Empty;
        }
    }

    private static byte[] Key()
    {
        var path = Path.Combine(KeyFolder, "secret.key");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}
