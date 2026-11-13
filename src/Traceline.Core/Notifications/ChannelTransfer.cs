using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Traceline.Core.Notifications;

/// <summary>What an export file says about itself before the password is entered.</summary>
public sealed record ChannelExportInfo(int Count, string Machine, DateTimeOffset Exported);

/// <summary>What an import did. <see cref="NeedSignIn"/> names the channels that used a sign-in, which Traceline cannot send through.</summary>
public sealed record ChannelImportResult(int Added, int Replaced, List<string> NeedSignIn);

/// <summary>
/// Channel export files (*.smchannels), in the format of the author's Service Monitor: the channels with their
/// passwords, tokens and webhook addresses, so the same channels can be set up on another computer, after a reinstall,
/// or in the other program. The channel types are the same code in both programs, and a file written by either is read
/// by both. Stored secrets are encrypted for this computer only (<see cref="SecretProtector"/>), so the file carries
/// them encrypted with a password instead: AES-256-GCM with a key derived by PBKDF2-SHA256. Sign-ins (Google,
/// Microsoft) are not in the file.
/// </summary>
public static class ChannelTransfer
{
    public const string FileExtension = ".smchannels";
    public const int MinimumPasswordLength = 8;

    private const string Format = "ServiceMonitor.Channels";
    private const int Iterations = 600_000;
    private const int MaxIterations = 10_000_000;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(Format + "/1");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes an export file: every channel, with its secrets decrypted and then encrypted with <paramref name="password"/>.</summary>
    /// <exception cref="ArgumentException">The password is shorter than <see cref="MinimumPasswordLength"/>.</exception>
    public static string Export(IEnumerable<ChannelConfig> channels, string password, DateTimeOffset now)
    {
        if (password.Length < MinimumPasswordLength) throw new ArgumentException($"Use at least {MinimumPasswordLength} characters.");
        var plain = channels.Select(c =>
        {
            var copy = JsonSerializer.Deserialize<ChannelConfig>(JsonSerializer.Serialize(c, Json), Json)!;
            copy.TransformSecrets(SecretProtector.Unprotect);
            return copy;
        }).ToList();
        var data = JsonSerializer.SerializeToUtf8Bytes(plain, Json);

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[data.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32), tag.Length))
            aes.Encrypt(nonce, data, cipher, tag, AssociatedData);
        CryptographicOperations.ZeroMemory(data);

        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["product"] = $"{AppInfo.ProductName} {AppInfo.Version}",
            ["machine"] = AppInfo.MachineName,
            ["exported"] = now,
            ["channels"] = plain.Count,
            ["encryption"] = new JsonObject
            {
                ["cipher"] = "AES-256-GCM",
                ["kdf"] = "PBKDF2-SHA256",
                ["iterations"] = Iterations,
                ["salt"] = Convert.ToBase64String(salt),
                ["nonce"] = Convert.ToBase64String(nonce),
                ["tag"] = Convert.ToBase64String(tag),
            },
            ["data"] = Convert.ToBase64String(cipher),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Checks that <paramref name="fileText"/> is a channel export, without the password.</summary>
    /// <exception cref="InvalidDataException">It is not one.</exception>
    public static ChannelExportInfo ReadInfo(string fileText)
    {
        var file = Parse(fileText);
        try
        {
            return new ChannelExportInfo(
                file["channels"]?.GetValue<int>() ?? 0,
                file["machine"]?.GetValue<string>() ?? "",
                file["exported"]?.GetValue<DateTimeOffset>() ?? default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The file is damaged.", ex);
        }
    }

    /// <summary>Decrypts an export and encrypts its secrets for this computer.</summary>
    /// <exception cref="InvalidDataException">Not a channel export, a wrong password or a damaged file.</exception>
    public static List<ChannelConfig> Read(string fileText, string password)
    {
        var file = Parse(fileText);
        byte[] data;
        try
        {
            var e = file["encryption"]!;
            var iterations = e["iterations"]!.GetValue<int>();
            if (iterations is < 1 or > MaxIterations) throw new FormatException("iterations");
            var salt = Convert.FromBase64String(e["salt"]!.GetValue<string>());
            var nonce = Convert.FromBase64String(e["nonce"]!.GetValue<string>());
            var tag = Convert.FromBase64String(e["tag"]!.GetValue<string>());
            var cipher = Convert.FromBase64String(file["data"]!.GetValue<string>());
            data = new byte[cipher.Length];
            using var aes = new AesGcm(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32), tag.Length);
            aes.Decrypt(nonce, cipher, tag, data, AssociatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("That password is not the one the file was exported with.");
        }
        catch (Exception ex) when (ex is FormatException or NullReferenceException or InvalidOperationException or ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("The file is damaged.", ex);
        }

        try
        {
            var channels = JsonSerializer.Deserialize<List<ChannelConfig>>(data, Json) ?? [];
            foreach (var c in channels) c.TransformSecrets(SecretProtector.Protect);
            return channels;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The file holds channels this version does not know.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Adds imported channels to <paramref name="channels"/>. One with the same name as an existing channel replaces it
    /// in place and keeps its id, so alerts that send to that channel keep doing so.
    /// </summary>
    public static ChannelImportResult Merge(List<ChannelConfig> channels, IEnumerable<ChannelConfig> imported)
    {
        int added = 0, replaced = 0;
        var needSignIn = new List<string>();
        foreach (var channel in imported)
        {
            if (channel.UsesOAuth) needSignIn.Add(channel.Name);
            var index = channels.FindIndex(c => c.Name.Equals(channel.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                channel.Id = channels[index].Id;
                channels[index] = channel;
                replaced++;
            }
            else
            {
                if (channels.Any(c => c.Id == channel.Id)) channel.Id = Guid.NewGuid().ToString("N");
                channels.Add(channel);
                added++;
            }
        }
        return new ChannelImportResult(added, replaced, needSignIn);
    }

    private static JsonNode Parse(string fileText)
    {
        try
        {
            var file = JsonNode.Parse(fileText);
            if (file?["format"]?.GetValue<string>() == Format && file["version"]?.GetValue<int>() == 1) return file;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }
        throw new InvalidDataException("This is not a channel export file.");
    }
}
