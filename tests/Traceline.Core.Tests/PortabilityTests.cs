using System.Net;
using System.Text.Json.Nodes;
using Traceline.Core;
using Traceline.Core.Notifications;

namespace Traceline.Core.Tests;

/// <summary>Code paths that the Windows build rarely or never takes: IPv6 replies, other systems' ping and secret storage.</summary>
// Secret protection has program-wide settings that one test changes, so the tests that use it take turns.
[Collection("Secrets")]
public class PortabilityTests
{
    /// <summary>An ICMPv6 error carrying the start of the packet that caused it.</summary>
    private static byte[] Icmp6(byte type, byte code, byte nextHeader, ushort sourcePort, ushort destinationPort, ushort udpLength, bool withOuterHeader = false)
    {
        var inner = new byte[48];
        inner[0] = 0x60; inner[6] = nextHeader;
        inner[40] = (byte)(sourcePort >> 8); inner[41] = (byte)sourcePort;
        inner[42] = (byte)(destinationPort >> 8); inner[43] = (byte)destinationPort;
        inner[44] = (byte)(udpLength >> 8); inner[45] = (byte)udpLength;
        byte[] message = [type, code, 0, 0, 0, 0, 0, 0, .. inner];
        if (!withOuterHeader) return message;
        var outer = new byte[40];
        outer[0] = 0x60; outer[6] = 58;
        return [.. outer, .. message];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_ipv6_hop_limit_reply_is_matched_to_the_udp_packet_that_caused_it(bool withOuterHeader)
    {
        var packet = Icmp6(3, 0, 17, 41234, 33434, 45, withOuterHeader);
        var parsed = IcmpListener.Parse6(packet, packet.Length);
        Assert.NotNull(parsed);
        Assert.Equal(((byte)17, (ushort)41234, (ushort)33434, (ushort)45), parsed.Value.Key);
        Assert.Equal(11, parsed.Value.Type);   // reported the IPv4 way: ran out of hops
    }

    [Fact]
    public void Ipv6_unreachable_and_tcp_replies_are_read_and_other_messages_ignored()
    {
        var unreachable = Icmp6(1, 4, 17, 41234, 33434, 37);
        Assert.Equal((3, 4), (IcmpListener.Parse6(unreachable, unreachable.Length)!.Value.Type, IcmpListener.Parse6(unreachable, unreachable.Length)!.Value.Code));
        var tcp = Icmp6(3, 0, 6, 50001, 443, 999);
        Assert.Equal(((byte)6, (ushort)50001, (ushort)443, (ushort)0), IcmpListener.Parse6(tcp, tcp.Length)!.Value.Key);   // no length for TCP
        var echoReply = Icmp6(129, 0, 17, 1, 2, 3);
        Assert.Null(IcmpListener.Parse6(echoReply, echoReply.Length));
        Assert.Null(IcmpListener.Parse6(new byte[20], 20));
    }

    [Fact]
    public async Task The_ping_used_on_other_systems_reaches_this_computer()
    {
        var result = await IcmpProber.ManagedAsync(IPAddress.Loopback, 64, 28, 2000, default);
        Assert.True(result.Reached);
        Assert.InRange(result.RttMs, 0, 1000);
    }

    [Fact]
    public void Secrets_can_be_kept_with_a_key_file_where_there_is_no_windows_protection()
    {
        var (folder, other) = (Directory.CreateTempSubdirectory("traceline-key").FullName, Directory.CreateTempSubdirectory("traceline-key").FullName);
        var before = SecretProtector.KeyFolder;
        try
        {
            SecretProtector.KeyFolder = folder;
            SecretProtector.UseKeyFile = true;
            var stored = SecretProtector.Protect("s3cret webhook");
            Assert.StartsWith("aes:", stored);
            Assert.DoesNotContain("s3cret", stored);
            Assert.True(SecretProtector.IsProtected(stored));
            Assert.Equal("s3cret webhook", SecretProtector.Unprotect(stored));
            Assert.NotEqual(stored, SecretProtector.Protect("s3cret webhook"));   // a fresh nonce every time
            Assert.Equal(32, File.ReadAllBytes(Path.Combine(folder, "secret.key")).Length);

            SecretProtector.KeyFolder = other;   // another computer: a different key
            Assert.Equal("", SecretProtector.Unprotect(stored));
            Assert.Equal("typed by hand", SecretProtector.Unprotect("typed by hand"));
        }
        finally
        {
            SecretProtector.UseKeyFile = false;
            SecretProtector.KeyFolder = before;
            Directory.Delete(folder, true); Directory.Delete(other, true);
        }
    }
}

public class ChannelPayloadTests
{
    private static readonly Notification Alert = new()
    {
        Kind = NotificationKind.Alert, Severity = Severity.Critical, Title = "example.com: Target down",
        Summary = "10 of the last 10 samples were lost.", SiteName = "Office",
    };

    [Fact]
    public void Telegram_gets_the_chat_and_a_text_that_names_the_problem()
    {
        var payload = NotificationRenderer.Telegram(Alert.AddFact("Diagnosis", "The destination is not answering"), "-100123");
        Assert.Equal("-100123", payload["chat_id"]!.ToString());
        var text = payload["text"]!.ToString();
        Assert.Contains("example.com: Target down", text);
        Assert.Contains("The destination is not answering", text);
        Assert.InRange(text.Length, 20, 4096);   // Telegram's limit for one message
    }

    [Fact]
    public void Chat_payloads_have_the_shape_each_service_expects()
    {
        Assert.NotNull(NotificationRenderer.Slack(Alert)["blocks"] as JsonArray);
        Assert.Contains("example.com: Target down", NotificationRenderer.Slack(Alert)["text"]!.ToString());
        Assert.Single((JsonArray)NotificationRenderer.Discord(Alert)["embeds"]!);
        Assert.Equal("message", NotificationRenderer.Teams(Alert)["type"]!.ToString());
        var ntfy = NotificationRenderer.Ntfy(Alert, "ops");
        Assert.Equal("ops", ntfy["topic"]!.ToString());
        Assert.Contains("Target down", ntfy["title"]!.ToString());
        var hook = NotificationRenderer.Webhook(Alert);
        Assert.Equal("alert", hook["kind"]!.ToString());
        Assert.Equal("Traceline", hook["source"]!["product"]!.ToString());
    }
}
