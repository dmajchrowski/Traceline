using System.Net;
using System.Text;
using Traceline.Core.Notifications;

namespace Traceline.Core.Tests;

/// <summary>The help the page gets while a channel is set up: the host name of a webhook address and "Find chat ID" for Telegram.</summary>
[Collection("Secrets")]
public class ChannelSetupTests
{
    /// <summary>Stands in for Telegram: remembers the address asked for and gives a prepared answer.</summary>
    private sealed class Telegram(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Asked;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void A_webhook_channel_is_listed_by_the_host_in_its_address_only()
    {
        Assert.Equal("discord.com", ChannelSender.UrlHost(new DiscordChannel { WebhookUrl = "https://discord.com/api/webhooks/1/secret" }));
        Assert.Equal("hooks.slack.com", ChannelSender.UrlHost(new SlackChannel { WebhookUrl = SecretProtector.Protect("https://hooks.slack.com/services/T/B/x") }));
        Assert.Equal("hook.test", ChannelSender.UrlHost(new WebhookChannel { Url = " http://hook.test/in " }));
        Assert.Equal("", ChannelSender.UrlHost(new TeamsChannel { WebhookUrl = "not an address" }));
        Assert.Equal("", ChannelSender.UrlHost(new TeamsChannel()));
        Assert.Equal("", ChannelSender.UrlHost(new TelegramChannel { BotToken = "https://example.test/x" }));
    }

    [Fact]
    public async Task Find_chat_ID_takes_the_chat_that_wrote_to_the_bot_last()
    {
        var telegram = new Telegram(HttpStatusCode.OK, """
            {"ok":true,"result":[
              {"update_id":1,"message":{"chat":{"id":111,"first_name":"Ann"},"text":"hi"}},
              {"update_id":2,"my_chat_member":{"chat":{"id":-222,"title":"Network alerts"}}},
              {"update_id":3,"message":{"chat":{"id":111,"first_name":"Ann"},"text":"again"}},
              {"update_id":4,"edited_message":{"chat":{"id":333}}}
            ]}
            """);
        var found = await new ChannelSender(new HttpClient(telegram)).FindTelegramChatAsync(SecretProtector.Protect("123:abc_DEF-9"), default);
        Assert.True(found.Ok);
        Assert.Equal("111", found.ChatId);
        Assert.Equal("Found 2 chats; using the most recent (\"Ann\").", found.Text);
        Assert.Equal("https://api.telegram.org/bot123:abc_DEF-9/getUpdates", telegram.Asked);
    }

    [Fact]
    public async Task Find_chat_ID_says_what_to_do_when_it_finds_nothing()
    {
        var none = await new ChannelSender(new HttpClient(new Telegram(HttpStatusCode.OK, """{"ok":true,"result":[]}"""))).FindTelegramChatAsync("123:abc", default);
        Assert.False(none.Ok);
        Assert.StartsWith("No messages found.", none.Text);

        var refused = await new ChannelSender(new HttpClient(new Telegram(HttpStatusCode.Unauthorized, """{"ok":false,"error_code":401,"description":"Unauthorized"}"""))).FindTelegramChatAsync("123:wrong", default);
        Assert.False(refused.Ok);
        Assert.StartsWith("Telegram does not know this bot token.", refused.Text);

        var busy = await new ChannelSender(new HttpClient(new Telegram(HttpStatusCode.Conflict, """{"ok":false,"error_code":409,"description":"Conflict: a webhook is active"}"""))).FindTelegramChatAsync("123:abc", default);
        Assert.Equal("Telegram answered HTTP 409: Conflict: a webhook is active.", busy.Text);
    }

    [Fact]
    public async Task Find_chat_ID_asks_nothing_with_a_token_that_could_change_the_address()
    {
        var telegram = new Telegram(HttpStatusCode.OK, "{}");
        var sender = new ChannelSender(new HttpClient(telegram));
        Assert.Equal("Enter the bot token first.", (await sender.FindTelegramChatAsync("  ", default)).Text);
        Assert.StartsWith("This does not look like a bot token.", (await sender.FindTelegramChatAsync("123:abc/../../x?y", default)).Text);
        Assert.Null(telegram.Asked);
    }
}
