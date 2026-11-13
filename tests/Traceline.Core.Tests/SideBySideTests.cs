using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Traceline.Core.Notifications;

namespace Traceline.Core.Tests;

/// <summary>Features added after a side-by-side comparison with another trace tool.</summary>
public class SilentHopTests
{
    [Fact]
    public async Task A_hop_that_stops_answering_loses_its_address_after_the_time_and_the_samples()
    {
        using var rig = new Rig();
        rig.Config.SilentMinutes = 1; rig.Config.SilentSamples = 15;
        await rig.RunAsync(5);
        Assert.Equal("10.0.0.2", rig.Session.Route[1]);

        rig.Net.Hops = [1, null, 9, 12];
        await rig.RunAsync(20);                         // 50 seconds: enough samples, not enough time
        Assert.Equal("10.0.0.2", rig.Session.Route[1]);
        await rig.RunAsync(6);                          // 65 seconds
        Assert.Null(rig.Session.Route[1]);
        Assert.Equal("10.0.0.3", rig.Session.Route[2]);
        Assert.Null(rig.Stats()[1].Ip);

        rig.Net.Hops = [1, 5, 9, 12];
        await rig.RunAsync(1);
        Assert.Equal("10.0.0.2", rig.Session.Route[1]);
        // Forgetting and finding a hop again is not a route change worth a note.
        Assert.DoesNotContain(rig.Store.Events(rig.Id, 0, long.MaxValue), e => e.Kind == "route");
    }

    [Fact]
    public async Task The_address_stays_when_the_setting_is_off_or_the_hop_answers_now_and_then()
    {
        using var rig = new Rig();
        rig.Config.SilentMinutes = 1; rig.Config.SilentSamples = 15; rig.Config.RemoveSilentHops = false;
        await rig.RunAsync(5);
        rig.Net.Hops = [1, null, 9, 12];
        await rig.RunAsync(40);
        Assert.Equal("10.0.0.2", rig.Session.Route[1]);

        rig.Config.RemoveSilentHops = true;
        for (var i = 0; i < 10; i++)
        {
            rig.Net.Hops = [1, null, 9, 12];
            await rig.RunAsync(10);
            rig.Net.Hops = [1, 5, 9, 12];               // one answer in eleven is enough to keep it
            await rig.RunAsync(1);
        }
        Assert.Equal("10.0.0.2", rig.Session.Route[1]);
    }
}

public class HopNoteTests
{
    [Fact]
    public async Task Notes_on_an_address_are_kept_shown_per_hop_and_survive_a_new_lookup()
    {
        using var rig = new Rig();
        await rig.RunAsync(3);
        var hosts = new HostDirectory(rig.Store, () => rig.Settings);
        hosts.SetNotes("10.0.0.2", "  Core switch, rack 4  ");
        hosts.SetAlias("10.0.0.2", "core-sw");
        for (var i = 0; i < 50 && hosts.Get("10.0.0.2")!.UpdatedMs == 0; i++) await Task.Delay(50);   // the first lookup
        hosts.Refresh("10.0.0.2");
        await Task.Delay(300);

        var stats = new ViewBuilder(rig.Store, hosts, _ => rig.Config).Stats(rig.Id, rig.Now - 3_600_000, rig.Now + 1);
        Assert.Equal("Core switch, rack 4", stats[1].Notes);
        Assert.Equal("core-sw", stats[1].Name);
        Assert.Null(stats[0].Notes);

        // A fresh directory reads them back from the database.
        Assert.Equal("Core switch, rack 4", new HostDirectory(rig.Store, () => rig.Settings).Get("10.0.0.2")!.Notes);
        hosts.SetNotes("10.0.0.2", "");
        Assert.Equal("", rig.Store.Host("10.0.0.2")!.Notes);
    }
}

[Collection("Secrets")]
public class WebCertificateTests
{
    [Fact]
    public void A_self_made_certificate_names_this_computer_and_is_reused()
    {
        var folder = Directory.CreateTempSubdirectory("traceline-cert").FullName;
        try
        {
            var settings = new AppSettings();
            var saves = 0;
            using var first = WebCertificate.LoadOrCreateOwn(folder, settings, () => saves++);
            Assert.True(first.HasPrivateKey);
            Assert.True(first.NotAfter > DateTime.Now.AddYears(4));
            Assert.True(first.MatchesHostname("localhost"));
            Assert.True(first.MatchesHostname(Environment.MachineName));
            Assert.True(first.MatchesHostname("127.0.0.1"));
            Assert.Equal(1, saves);
            Assert.True(SecretProtector.IsProtected(settings.OwnCertificatePassword));

            using var second = WebCertificate.LoadOrCreateOwn(folder, settings, () => saves++);
            Assert.Equal(first.Thumbprint, second.Thumbprint);
            Assert.Equal(1, saves);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_certificate_file_is_loaded_with_its_password_and_bad_ones_are_explained()
    {
        var folder = Directory.CreateTempSubdirectory("traceline-cert").FullName;
        try
        {
            var path = Path.Combine(folder, "site.pfx");
            File.WriteAllBytes(path, WebCertificate.CreateSelfSigned("secret"));
            using var served = WebCertificate.Load(path, "secret");
            Assert.True(served.HasPrivateKey);
            using var loaded = X509CertificateLoader.LoadPkcs12FromFile(path, "secret", X509KeyStorageFlags.Exportable);

            Assert.Contains("could not be read", Assert.Throws<ArgumentException>(() => WebCertificate.Load(path, "wrong")).Message);
            Assert.Contains("does not exist", Assert.Throws<ArgumentException>(() => WebCertificate.Load(Path.Combine(folder, "missing.pfx"), "")).Message);

            // The same certificate as a PEM file with the key inside it.
            var pem = Path.Combine(folder, "site.pem");
            File.WriteAllText(pem, loaded.ExportCertificatePem() + "\n" + loaded.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
            using var fromPem = WebCertificate.Load(pem, "");
            Assert.True(fromPem.HasPrivateKey);
            Assert.Equal(loaded.Thumbprint, fromPem.Thumbprint);

            // A certificate without its key is of no use for HTTPS.
            var bare = Path.Combine(folder, "bare.pem");
            File.WriteAllText(bare, loaded.ExportCertificatePem());
            Assert.ThrowsAny<ArgumentException>(() => WebCertificate.Load(bare, ""));
        }
        finally { Directory.Delete(folder, true); }
    }
}

[Collection("Secrets")]
public class AlertMessageTests
{
    /// <summary>Stands in for the network: remembers every request body and answers 200.</summary>
    private sealed class Capture : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Bodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Enqueue(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task The_subject_is_the_users_and_notifications_are_held_to_one_per_period()
    {
        {
            using var store = new Store(":memory:");
            var net = new FakeProber();
            store.Set("app", new AppSettings { SendDelayMs = 0, OwnerLookup = false, ReverseDns = false });
            store.Set("alerts", new List<AlertRule> { new()
            {
                Name = "Down", Kind = ConditionKind.OverSamples, SampleCount = 3, BadCount = 3, AddComment = false,
                EachTime = true, MinMinutesBetween = 1, Subject = "{{Target}} {{State}} ({{AlertName}})",
            } });
            var target = Target.Parse("192.0.2.50");
            target.Paused = true;   // rounds are driven by hand below
            store.SaveTarget(target);

            await using var engine = new TraceEngine(store, net);
            var views = new ViewBuilder(store, new HostDirectory(store, () => engine.Settings), _ => engine.Configs[0]);
            var capture = new Capture();
            var alerts = new AlertService(engine, views, new ChannelSender(new HttpClient(capture)), new Reports(store, views));
            alerts.SaveChannels([new WebhookChannel { Id = "hook", Name = "Hook", Enabled = true, Url = "http://hook.test/in" }]);
            engine.Start();
            var session = engine.Session(target.Id)!;

            var now = 1_800_000_000_000;
            async Task Rounds(int count) { for (var i = 0; i < count; i++) await session.RoundAsync(now += 2500, default); }
            async Task Settle(int expected)
            {
                for (var i = 0; i < 100 && capture.Bodies.Count < expected; i++) await Task.Delay(50);
                await Task.Delay(300);   // anything that should not have been sent would have arrived by now
            }

            await Rounds(4);
            net.Hops = [1, 5, 9, null];
            await Rounds(13);          // starts on the third lost sample, then "every sample" for 25 seconds more
            await Settle(1);
            var first = Assert.Single(capture.Bodies);
            Assert.Equal("192.0.2.50 started (Down)", JsonDocument.Parse(first).RootElement.GetProperty("subject").GetString());

            await Rounds(24);          // a minute after the first one, the next gets through
            await Settle(2);
            Assert.Equal(2, capture.Bodies.Count);
        }
    }
}
