using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Traceline.Core.Notifications;

namespace Traceline.Core.Tests;

/// <summary>Reading and writing channel export files, which Service Monitor reads and writes too.</summary>
[Collection("Secrets")]
public class ChannelTransferTests
{
    [Fact]
    public void An_export_can_be_read_back_and_holds_no_secret_in_the_clear()
    {
        var channels = new List<ChannelConfig>
        {
            new SmtpChannel { Id = "s1", Name = "Mail", Host = "mail.example.com", Port = 465, Security = SmtpSecurity.SslOnConnect, Username = "user", Password = SecretProtector.Protect("smtp-secret"), Recipients = "ops@example.com", Enabled = false },
            new WebhookChannel { Id = "w1", Name = "Hook", Url = SecretProtector.Protect("https://example.com/hook?key=abc"), HeaderName = "X-Key", HeaderValue = "typed by hand" },
            new GmailChannel { Id = "g1", Name = "Gmail", Account = "sender@example.com", AppPassword = SecretProtector.Protect("abcd efgh ijkl mnop") },
        };
        var stored = channels.OfType<SmtpChannel>().Single().Password;

        var file = ChannelTransfer.Export(channels, Password, new DateTimeOffset(2026, 11, 10, 16, 0, 0, TimeSpan.Zero));

        Assert.DoesNotContain("smtp-secret", file);
        Assert.DoesNotContain("example.com", file);
        Assert.DoesNotContain("dpapi:", file);
        Assert.Equal(stored, channels.OfType<SmtpChannel>().Single().Password);   // the channels in use are left as they were

        var head = JsonNode.Parse(file)!;
        Assert.Equal(("ServiceMonitor.Channels", 1, 3, 600000), (head["format"]!.GetValue<string>(), head["version"]!.GetValue<int>(), head["channels"]!.GetValue<int>(), head["encryption"]!["iterations"]!.GetValue<int>()));
        Assert.StartsWith("Traceline ", head["product"]!.GetValue<string>());
        var info = ChannelTransfer.ReadInfo(file);
        Assert.Equal((3, Environment.MachineName, new DateTimeOffset(2026, 11, 10, 16, 0, 0, TimeSpan.Zero)), (info.Count, info.Machine, info.Exported));

        var back = ChannelTransfer.Read(file, Password);
        var smtp = Assert.IsType<SmtpChannel>(back[0]);
        Assert.Equal(("s1", "Mail", "mail.example.com", 465, SmtpSecurity.SslOnConnect, "user", "ops@example.com", false), (smtp.Id, smtp.Name, smtp.Host, smtp.Port, smtp.Security, smtp.Username, smtp.Recipients, smtp.Enabled));
        Assert.Equal("smtp-secret", SecretProtector.Unprotect(smtp.Password));
        var hook = Assert.IsType<WebhookChannel>(back[1]);
        Assert.Equal(("https://example.com/hook?key=abc", "X-Key", "typed by hand"), (SecretProtector.Unprotect(hook.Url), hook.HeaderName, SecretProtector.Unprotect(hook.HeaderValue)));
        Assert.Equal("abcd efgh ijkl mnop", SecretProtector.Unprotect(Assert.IsType<GmailChannel>(back[2]).AppPassword));

        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => ChannelTransfer.Read(file, "another password")).Message);
    }

    [Fact]
    public void An_export_needs_a_password_of_eight_characters()
    {
        Assert.Contains("at least 8", Assert.Throws<ArgumentException>(() => ChannelTransfer.Export([], "1234567", DateTimeOffset.Now)).Message);
        Assert.Equal(0, ChannelTransfer.ReadInfo(ChannelTransfer.Export([], "12345678", DateTimeOffset.Now)).Count);
    }

    private const string Password = "correct horse";

    private const string Channels = """
        [
          { "type": "gmail", "id": "a1", "name": "Gmail", "enabled": true, "recipients": "ops@example.com", "authMode": "AppPassword", "account": "sender@example.com", "appPassword": "abcd efgh ijkl mnop" },
          { "type": "telegram", "id": "a2", "name": "Telegram", "enabled": true, "botToken": "123:token", "chatId": "42" },
          { "type": "smtp", "id": "a3", "name": "Mail server", "enabled": false, "recipients": "ops@example.com", "provider": "", "host": "mail.example.com", "port": 25, "security": "StartTls", "username": "user", "password": "smtp-secret", "from": "noreply@example.com" },
          { "type": "ntfy", "id": "a4", "name": "Phone push (ntfy)", "enabled": true, "serverUrl": "https://ntfy.sh", "topic": "example-topic", "accessToken": "" },
          { "type": "teams", "id": "a5", "name": "Microsoft Teams", "enabled": true, "webhookUrl": "https://example.com/teams" },
          { "type": "discord", "id": "a6", "name": "Discord", "enabled": true, "webhookUrl": "https://example.com/discord" },
          { "type": "slack", "id": "a7", "name": "Slack", "enabled": true, "webhookUrl": "https://example.com/slack" },
          { "type": "webhook", "id": "a8", "name": "Webhook", "enabled": true, "url": "https://example.com/hook", "headerName": "X-Key", "headerValue": "hook-secret" },
          { "type": "microsoft", "id": "a9", "name": "Office mail", "enabled": true, "recipients": "", "account": "someone@example.com" }
        ]
        """;

    /// <summary>Writes a file the way Service Monitor 1.0 does.</summary>
    private static string Export(string channelsJson, string password, int count = 9)
    {
        var data = Encoding.UTF8.GetBytes(channelsJson);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA256, 32), 16))
            aes.Encrypt(nonce, data, cipher, tag, "ServiceMonitor.Channels/1"u8.ToArray());
        return new JsonObject
        {
            ["format"] = "ServiceMonitor.Channels",
            ["version"] = 1,
            ["product"] = "Service Monitor 1.0.11",
            ["machine"] = "SERVER01",
            ["exported"] = new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.FromHours(-5)),
            ["channels"] = count,
            ["encryption"] = new JsonObject
            {
                ["cipher"] = "AES-256-GCM", ["kdf"] = "PBKDF2-SHA256", ["iterations"] = 1000,
                ["salt"] = Convert.ToBase64String(salt), ["nonce"] = Convert.ToBase64String(nonce), ["tag"] = Convert.ToBase64String(tag),
            },
            ["data"] = Convert.ToBase64String(cipher),
        }.ToJsonString();
    }

    [Fact]
    public void Every_kind_of_channel_comes_across_with_its_secret_encrypted_for_this_computer()
    {
        var channels = ChannelTransfer.Read(Export(Channels, Password), Password);

        Assert.Equal(["gmail", "telegram", "smtp", "ntfy", "teams", "discord", "slack", "webhook", "microsoft"],
            channels.Select(c => c.Kind.ToString().ToLowerInvariant()));

        var gmail = Assert.IsType<GmailChannel>(channels[0]);
        Assert.Equal(("sender@example.com", "ops@example.com", GmailAuthMode.AppPassword), (gmail.Account, gmail.Recipients, gmail.AuthMode));
        Assert.True(SecretProtector.IsProtected(gmail.AppPassword));
        Assert.Equal("abcd efgh ijkl mnop", SecretProtector.Unprotect(gmail.AppPassword));

        var smtp = Assert.IsType<SmtpChannel>(channels[2]);
        Assert.Equal(("mail.example.com", 25, SmtpSecurity.StartTls, "user", "noreply@example.com", false), (smtp.Host, smtp.Port, smtp.Security, smtp.Username, smtp.From, smtp.Enabled));
        Assert.Equal("smtp-secret", SecretProtector.Unprotect(smtp.Password));

        Assert.Equal("123:token", SecretProtector.Unprotect(Assert.IsType<TelegramChannel>(channels[1]).BotToken));
        Assert.Equal("", Assert.IsType<NtfyChannel>(channels[3]).AccessToken);   // nothing to protect stays empty
        Assert.Equal("https://example.com/teams", SecretProtector.Unprotect(Assert.IsType<TeamsChannel>(channels[4]).WebhookUrl));
        Assert.Equal("https://example.com/discord", SecretProtector.Unprotect(Assert.IsType<DiscordChannel>(channels[5]).WebhookUrl));
        Assert.Equal("https://example.com/slack", SecretProtector.Unprotect(Assert.IsType<SlackChannel>(channels[6]).WebhookUrl));
        var hook = Assert.IsType<WebhookChannel>(channels[7]);
        Assert.Equal(("https://example.com/hook", "X-Key", "hook-secret"), (SecretProtector.Unprotect(hook.Url), hook.HeaderName, SecretProtector.Unprotect(hook.HeaderValue)));
    }

    [Fact]
    public void The_file_says_where_it_came_from_before_the_password_is_known()
    {
        var info = ChannelTransfer.ReadInfo(Export(Channels, Password));
        Assert.Equal((9, "SERVER01", new DateTimeOffset(2026, 11, 10, 14, 30, 0, TimeSpan.Zero)), (info.Count, info.Machine, info.Exported.ToUniversalTime()));
    }

    [Fact]
    public void A_wrong_password_another_file_and_a_damaged_file_each_say_what_is_wrong()
    {
        var file = Export(Channels, Password);
        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => ChannelTransfer.Read(file, "something else")).Message);

        Assert.Contains("not a channel export file", Assert.Throws<InvalidDataException>(() => ChannelTransfer.Read("{\"format\":\"Other\"}", Password)).Message);
        Assert.Contains("not a channel export file", Assert.Throws<InvalidDataException>(() => ChannelTransfer.ReadInfo("hello")).Message);

        var damaged = JsonNode.Parse(file)!;
        damaged["encryption"]!["salt"] = "not base64 !";
        Assert.Contains("damaged", Assert.Throws<InvalidDataException>(() => ChannelTransfer.Read(damaged.ToJsonString(), Password)).Message);

        var greedy = JsonNode.Parse(file)!;
        greedy["encryption"]!["iterations"] = int.MaxValue;
        Assert.Contains("damaged", Assert.Throws<InvalidDataException>(() => ChannelTransfer.Read(greedy.ToJsonString(), Password)).Message);

        var unknown = Export("""[{ "type": "pigeon", "name": "Carrier pigeon" }]""", Password, 1);
        Assert.Contains("does not know", Assert.Throws<InvalidDataException>(() => ChannelTransfer.Read(unknown, Password)).Message);
    }

    [Fact]
    public void A_channel_with_a_name_already_in_use_replaces_it_and_keeps_its_id()
    {
        var existing = new List<ChannelConfig>
        {
            new SlackChannel { Id = "keep-me", Name = "slack", WebhookUrl = "old" },
            new NtfyChannel { Id = "a2", Name = "Something else" },   // same id as the imported Telegram channel
        };

        var result = ChannelTransfer.Merge(existing, ChannelTransfer.Read(Export(Channels, Password), Password));

        Assert.Equal((8, 1), (result.Added, result.Replaced));
        Assert.Equal(["Office mail"], result.NeedSignIn);
        Assert.Equal(10, existing.Count);
        var slack = Assert.IsType<SlackChannel>(existing[0]);
        Assert.Equal("keep-me", slack.Id);
        Assert.Equal("https://example.com/slack", SecretProtector.Unprotect(slack.WebhookUrl));
        Assert.Equal(existing.Count, existing.Select(c => c.Id).Distinct().Count());
    }
}
