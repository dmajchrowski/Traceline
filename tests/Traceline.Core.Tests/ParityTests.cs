using Traceline.Core;

namespace Traceline.Core.Tests;

/// <summary>Tests for what version 1.1 added: configurations, per-hop alerts, sessions, workspaces and discovery helpers.</summary>
public class PerHopTests
{
    [Fact]
    public async Task Recent_results_can_follow_a_hop_by_number_or_by_address()
    {
        using var rig = new Rig();
        rig.Net.Hops = [1, 40, 9, 12];
        await rig.RunAsync(3);
        Assert.All(rig.Session.Recent("hop:2", 0), r => Assert.Equal(40, r.Rtt));
        Assert.All(rig.Session.Recent("ip:10.0.0.3", 0), r => Assert.Equal(9, r.Rtt));
        Assert.All(rig.Session.Recent("", 0), r => Assert.Equal(12, r.Rtt));
        Assert.Equal(3, rig.Session.Recent("hop:2", 0).Count);
        Assert.Empty(rig.Session.Recent("ip:203.0.113.9", 0));
        Assert.Empty(rig.Session.Recent("hop:9", 0));
    }

    [Fact]
    public async Task An_alert_on_a_hop_fires_while_the_destination_is_fine()
    {
        using var store = new Store(":memory:");
        var net = new FakeProber { Hops = [1, null, 9, 12] };
        store.Set("configs", new List<TraceConfig> { new() { SendDelayMs = 0 } });
        store.Set("alerts", new List<AlertRule>
        {
            new() { Name = "Hop 2 silent", Kind = ConditionKind.OverSamples, SampleCount = 3, BadCount = 3, Watch = "hop:2" },
            new() { Name = "Destination down", Kind = ConditionKind.OverSamples, SampleCount = 3, BadCount = 3 },
        });
        var target = Target.Parse("192.0.2.50");
        target.Paused = true;
        store.SaveTarget(target);
        await using var engine = new TraceEngine(store, net);
        var views = new ViewBuilder(store, new HostDirectory(store, () => engine.Settings), _ => engine.Configs[0]);
        var alerts = new AlertService(engine, views, new Notifications.ChannelSender(new HttpClient()), new Reports(store, views));
        var fired = new List<AlertFired>();
        alerts.Fired += fired.Add;
        engine.Start();
        var now = 1_800_000_000_000;
        for (var i = 0; i < 4; i++) await engine.Session(target.Id)!.RoundAsync(now += 2500, default);
        var one = Assert.Single(fired);
        Assert.Equal("Hop 2 silent", one.Rule);
        Assert.Contains("(hop 2)", one.Text);
    }
}

public class ConfigurationTests
{
    [Fact]
    public void Settings_from_version_one_become_the_default_configuration()
    {
        using var store = new Store(":memory:");
        store.Set("app", new AppSettings { WarnMs = 80, BadMs = 300, MaxHops = 20, SendDelayMs = 5 });
        var engine = new TraceEngine(store, new FakeProber());
        var config = Assert.Single(engine.Configs);
        Assert.Equal((TraceConfig.DefaultId, 80, 300, 20, 5), (config.Id, config.WarnMs, config.BadMs, config.MaxHops, config.SendDelayMs));
    }

    [Fact]
    public async Task A_target_uses_its_own_configuration_and_falls_back_to_the_default()
    {
        using var store = new Store(":memory:");
        await using var engine = new TraceEngine(store, new FakeProber());
        engine.SaveConfigs([.. engine.Configs, new TraceConfig { Id = "tcp", Name = "TCP 443", PacketType = PacketType.Tcp, StartFinalHopOnly = true }]);
        var plain = engine.Add("192.0.2.1");
        var tcp = engine.Add("192.0.2.2", configId: "tcp");
        Assert.Equal(PacketType.Icmp, engine.ConfigFor(plain).PacketType);
        Assert.Equal(PacketType.Tcp, engine.ConfigFor(tcp).PacketType);
        Assert.True(tcp.FinalHopOnly);
        engine.SaveConfigs(engine.Configs.Where(c => c.Id != "tcp").ToList());
        Assert.Equal(TraceConfig.DefaultId, engine.ConfigFor(tcp).Id);
        Assert.Throws<ArgumentException>(() => engine.SaveConfigs([]));
    }

    [Fact]
    public async Task The_maximum_hop_count_limits_how_far_a_round_probes()
    {
        using var rig = new Rig();
        rig.Config.MaxHops = 6;
        rig.Net.Hops = [1, 2, 3, 4, 5, 6, 7, 8, 9];   // the destination is further away than the limit
        await rig.RunAsync();
        Assert.Equal(6, rig.Net.Probes);
    }
}

public class SessionAndWorkspaceTests
{
    /// <summary>An engine with one paused target, so that rounds only happen when a test asks for them.</summary>
    private static (TraceEngine Engine, Store Store, Target Target) Engine(Action<AppSettings>? settings = null, bool keep = false)
    {
        var store = new Store(":memory:");
        var app = new AppSettings { OwnerLookup = false, ReverseDns = false };
        settings?.Invoke(app);
        store.Set("app", app);
        store.Set("configs", new List<TraceConfig> { new() { SendDelayMs = 0 } });
        var target = Target.Parse("192.0.2.1");
        target.Paused = true; target.Keep = keep;
        store.SaveTarget(target);
        var engine = new TraceEngine(store, new FakeProber());
        engine.Start();
        return (engine, store, target);
    }

    [Fact]
    public async Task Closing_keeps_the_session_and_reopening_brings_the_target_back()
    {
        var (engine, store, target) = Engine();
        await using var __ = engine; using var ___ = store;
        await engine.Session(target.Id)!.RoundAsync(1_800_000_000_000, default);

        Assert.True(await engine.CloseAsync(target.Id));
        Assert.Empty(engine.Sessions);
        Assert.Single(engine.AllSessions);
        Assert.Single(store.Samples(target.Id, 0, long.MaxValue));

        await engine.UpdateAsync(target.Id, t => t.Closed = false);
        Assert.Single(engine.Sessions);
    }

    [Theory]
    [InlineData("selected", false, false)]
    [InlineData("selected", true, true)]
    [InlineData("after", false, false)]   // the session below is far shorter than ten minutes
    [InlineData("always", false, true)]
    public async Task The_auto_save_setting_decides_whether_a_closed_session_is_kept(string mode, bool keepFlag, bool expectKept)
    {
        var (engine, store, target) = Engine(s => { s.AutoSaveMode = mode; s.AutoSaveMinutes = 10; }, keepFlag);
        await using var __ = engine; using var ___ = store;
        await engine.Session(target.Id)!.RoundAsync(1_800_000_000_000, default);
        await engine.Session(target.Id)!.RoundAsync(1_800_000_060_000, default);

        Assert.Equal(expectKept, await engine.CloseAsync(target.Id));
        Assert.Equal(expectKept ? 1 : 0, engine.AllSessions.Count());
        Assert.Equal(expectKept ? 2 : 0, store.Samples(target.Id, 0, long.MaxValue).Count);
    }

    [Fact]
    public async Task Only_the_active_workspace_is_listed_and_switching_swaps_the_set()
    {
        var (engine, store, _) = Engine();
        await using var __ = engine; using var ___ = store;
        engine.SaveWorkspaces([.. engine.Workspaces, new Workspace { Id = "office", Name = "Office" }]);
        await engine.SwitchWorkspaceAsync("office");
        Assert.Empty(engine.Sessions);
        var second = engine.Add("192.0.2.2");
        Assert.Equal("office", second.WorkspaceId);
        await engine.SwitchWorkspaceAsync("");
        Assert.Equal("192.0.2.1", Assert.Single(engine.Sessions).Target.Input);
        Assert.Equal(2, engine.AllSessions.Count());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SwitchWorkspaceAsync("nowhere"));
    }

    [Fact]
    public async Task A_session_survives_being_exported_and_imported()
    {
        using var rig = new Rig();
        rig.Net.Hops = [1, null, 9, 12];
        await rig.RunAsync(30);
        rig.Store.AddEvent(rig.Id, rig.Now - 5000, "comment", "cable replaced");
        var file = SessionArchive.Export(rig.Store, rig.Session.Target);

        using var other = new Store(":memory:");
        var imported = SessionArchive.Import(other, new MemoryStream(file), "");
        Assert.True(imported.Closed);
        Assert.EndsWith("(imported)", imported.Name);
        var views = new ViewBuilder(other, new HostDirectory(other, () => rig.Settings), _ => rig.Config);
        var original = rig.Stats();
        var copy = views.Stats(imported.Id, rig.Now - 3_600_000, rig.Now + 1);
        Assert.Equal(original.Select(h => (h.Hop, h.Ip, h.Count, h.Lost, h.Avg)), copy.Select(h => (h.Hop, h.Ip, h.Count, h.Lost, h.Avg)));
        Assert.Equal("cable replaced", Assert.Single(other.Events(imported.Id, 0, long.MaxValue)).Text);
        // Long ranges read the one-minute summaries, which the import has to rebuild.
        Assert.Equal(30, other.Rollups(imported.Id, 0, long.MaxValue).Sum(r => r.Aggs[0].N));

        Assert.Throws<ArgumentException>(() => SessionArchive.Import(other, new MemoryStream("nonsense"u8.ToArray()), ""));
    }
}

public class DiscoveryAndViewHelperTests
{
    [Theory]
    [InlineData("192.168.1.0/24", true, 24)]
    [InlineData("10.0.4.77/22", true, 22)]
    [InlineData("192.168.1.0", false, 0)]
    [InlineData("192.168.1.0/33", false, 0)]
    [InlineData("fe80::1/64", false, 0)]
    public void Networks_are_parsed(string cidr, bool ok, int prefix)
    {
        Assert.Equal(ok, NetworkDiscovery.TryParseCidr(cidr, out var network, out var parsed));
        if (!ok) return;
        Assert.Equal(prefix, parsed);
        Assert.Equal(0u, network & ~(uint.MaxValue << (32 - prefix)));   // host bits are cleared
    }

    [Fact]
    public void A_vendor_list_in_the_ieee_format_is_read()
    {
        var folder = Directory.CreateTempSubdirectory("traceline-test").FullName;
        try
        {
            File.WriteAllText(NetworkDiscovery.VendorFile(folder), "OUI/MA-L\t\tOrganization\n\n28-6F-B9   (hex)\t\tExample Networks Ltd.\n286FB9     (base 16)\t\tExample Networks Ltd.\n");
            Assert.Equal("Example Networks Ltd.", NetworkDiscovery.Vendors(folder)["286FB9"]);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(600_000, 2500, 240)]        // ten minutes at 2.5 s: one bucket per sample
    [InlineData(600_000, 500, 600)]         // more samples than the picture is wide: capped
    [InlineData(86_400_000, 2500, 600)]     // a day is drawn from one-minute summaries, capped
    [InlineData(30_000, 2500, 20)]          // never fewer than twenty
    public void Timelines_get_at_most_one_bucket_per_sample(long span, int interval, int expected) =>
        Assert.Equal(expected, ViewBuilder.BucketsFor(0, span, interval));

    [Fact]
    public void A_dns_pointer_answer_with_a_compressed_name_is_read()
    {
        var query = MiniDns.BuildQuery("8.1.168.192.in-addr.arpa", MiniDns.TypePtr, out _);
        // Answer: name = pointer to the question, type PTR, class IN, ttl, then "printer" + pointer back to nothing useful -> "printer.local"
        byte[] local = [5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0];
        byte[] rdata = [7, (byte)'p', (byte)'r', (byte)'i', (byte)'n', (byte)'t', (byte)'e', (byte)'r', .. local];
        byte[] reply = [.. query, 0xC0, 0x0C, 0, 12, 0, 1, 0, 0, 0, 60, 0, (byte)rdata.Length, .. rdata];
        reply[2] = 0x84; reply[7] = 1;
        Assert.Equal("printer.local", Assert.Single(MiniDns.Parse(reply).Texts));
    }

    [Fact]
    public async Task Timelines_carry_jitter_and_the_view_names_its_configuration()
    {
        using var rig = new Rig();
        var start = rig.Now;
        foreach (var rtt in new double[] { 10, 20, 10, 20 }) { rig.Net.Hops = [1, 5, 9, rtt]; await rig.RunAsync(); }
        var view = rig.Views.Build(rig.Id, start + 1, start + 10_001, start + 1, start + 10_001, [0], 1);
        Assert.Equal(10, view.Timelines[0].Jitter[0]);
        Assert.Same(rig.Config, view.Config);
    }
}
