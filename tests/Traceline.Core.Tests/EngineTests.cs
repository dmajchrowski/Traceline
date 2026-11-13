using System.Net;
using Traceline.Core;

namespace Traceline.Core.Tests;

public class AggTests
{
    [Fact]
    public void Statistics_ignore_lost_samples_for_latency_but_count_them_for_loss()
    {
        var a = new Agg();
        foreach (var v in new[] { 10f, 20f, float.NaN, 30f }) a.Add(v);
        Assert.Equal(4, a.N);
        Assert.Equal(1, a.Lost);
        Assert.Equal(20, a.Avg, 3);
        Assert.Equal(10, a.Min);
        Assert.Equal(30, a.Max);
        Assert.Equal(25, a.LossPct, 3);
        Assert.Equal(10, a.Jitter, 3);   // |20-10| and |30-20|
        Assert.Equal(30, a.Last);
    }

    [Fact]
    public void Merging_two_halves_gives_the_same_totals_as_one_pass()
    {
        float[] values = [5, 7, float.NaN, 6, 20, 4];
        var whole = new Agg(); foreach (var v in values) whole.Add(v);
        var first = new Agg(); foreach (var v in values[..3]) first.Add(v);
        var second = new Agg(); foreach (var v in values[3..]) second.Add(v);
        first.Merge(second);
        Assert.Equal(whole.N, first.N);
        Assert.Equal(whole.Lost, first.Lost);
        Assert.Equal(whole.Avg, first.Avg, 6);
        Assert.Equal(whole.Min, first.Min);
        Assert.Equal(whole.Max, first.Max);
    }

    [Fact]
    public void Mos_is_high_for_a_clean_link_and_low_for_a_lossy_one()
    {
        var clean = new Agg(); for (var i = 0; i < 50; i++) clean.Add(20);
        var lossy = new Agg(); for (var i = 0; i < 50; i++) lossy.Add(i % 4 == 0 ? float.NaN : 250 + i % 7 * 20);
        var dead = new Agg(); for (var i = 0; i < 5; i++) dead.Add(float.NaN);
        Assert.InRange(clean.Mos, 4.3, 4.5);
        Assert.True(lossy.Mos < 2.5);
        Assert.Equal(1, dead.Mos);
    }

    [Fact]
    public void An_aggregate_survives_being_stored()
    {
        var a = new Agg(); foreach (var v in new[] { 10f, float.NaN, 14f }) a.Add(v);
        var buffer = new float[Agg.BlobFloats];
        a.Write(buffer);
        var b = Agg.Read(buffer);
        Assert.Equal((a.N, a.Lost, a.Avg, a.Min, a.Max, a.Jitter), (b.N, b.Lost, b.Avg, b.Min, b.Max, b.Jitter));
    }
}

public class TargetParseTests
{
    [Theory]
    [InlineData("example.com", ProbeKind.Icmp, "example.com", 0, false)]
    [InlineData("ipv6:example.com", ProbeKind.Icmp, "example.com", 0, false)]
    [InlineData("tcp://example.com:443", ProbeKind.Tcp, "example.com", 443, true)]
    [InlineData("https://example.com/health", ProbeKind.Http, "example.com", 443, true)]
    [InlineData("dns://example.com@1.1.1.1", ProbeKind.Dns, "example.com", 0, true)]
    public void Inputs_are_understood(string input, ProbeKind kind, string host, int port, bool finalOnly)
    {
        var t = Target.Parse(input);
        Assert.Equal((kind, host, port, finalOnly), (t.Kind, t.Host, t.Port, t.FinalHopOnly));
    }

    [Fact]
    public void Protocol_prefix_and_dns_server_are_kept()
    {
        Assert.Equal("ipv6", Target.Parse("ipv6:example.com").Family);
        Assert.Equal("1.1.1.1", Target.Parse("dns://example.com@1.1.1.1").Extra);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tcp://example.com")]
    [InlineData("two words")]
    public void Bad_inputs_are_rejected(string input) => Assert.Throws<ArgumentException>(() => Target.Parse(input));
}

public class TraceSessionTests
{
    [Fact]
    public async Task Finds_the_route_and_then_probes_only_as_far_as_the_destination()
    {
        using var rig = new Rig();
        await rig.RunAsync();
        Assert.Equal(new float[] { 1, 5, 9, 12 }, rig.Session.LastSample!.Rtts);
        Assert.Equal("10.0.0.1 10.0.0.2 10.0.0.3 192.0.2.50", string.Join(' ', rig.Session.Route));
        Assert.Equal(rig.Config.MaxHops, rig.Net.Probes);

        rig.Net.Probes = 0;
        await rig.RunAsync();
        Assert.Equal(4, rig.Net.Probes);
    }

    [Fact]
    public async Task A_silent_hop_is_recorded_as_lost_without_breaking_the_route()
    {
        using var rig = new Rig();
        rig.Net.Hops = [1, null, 9, 12];
        await rig.RunAsync(3);
        var stats = rig.Stats();
        Assert.Equal(4, stats.Count);
        Assert.Equal(100, stats[1].LossPct);
        Assert.Null(stats[1].Ip);
        Assert.Equal(0, stats[3].LossPct);
    }

    [Fact]
    public async Task A_destination_that_never_answers_gets_one_lost_row_after_the_last_answering_hop()
    {
        using var rig = new Rig();
        rig.Net.Hops = [1, 5, 9, null];
        await rig.RunAsync(2);
        var stats = rig.Stats();
        Assert.Equal(4, stats.Count);
        Assert.Equal("192.0.2.50", stats[3].Ip);
        Assert.Equal(100, stats[3].LossPct);
        Assert.Equal(0, stats[2].LossPct);
    }

    [Fact]
    public async Task A_silent_destination_with_a_flaky_last_router_causes_no_route_changes()
    {
        using var rig = new Rig();
        for (var round = 0; round < 12; round++)
        {
            rig.Net.Hops = [1, 5, round % 3 == 1 ? null : 9, null];   // hop 3 skips every third round; the destination never answers
            await rig.RunAsync();
        }
        Assert.Equal(4, rig.Session.LastSample!.Rtts.Length);
        Assert.Empty(rig.Store.Events(rig.Id, 0, long.MaxValue));
        var stats = rig.Stats();
        Assert.Equal(100, stats[3].LossPct);
        Assert.InRange(stats[2].LossPct!.Value, 30, 40);
    }

    [Fact]
    public async Task A_lost_destination_packet_keeps_the_route_shape()
    {
        using var rig = new Rig();
        await rig.RunAsync(3);
        rig.Net.Hops = [1, 5, 9, null];
        await rig.RunAsync();
        Assert.Equal(4, rig.Session.LastSample!.Rtts.Length);
        Assert.True(float.IsNaN(rig.Session.LastSample.Rtts[3]));
        Assert.Equal(25, rig.Stats()[3].LossPct);
    }

    [Fact]
    public async Task A_changed_hop_is_noted_once_and_reported_to_listeners()
    {
        using var rig = new Rig();
        var changes = new List<string?>();
        rig.Session.SampleTaken += (_, _, info) => { if (info.RouteChanged) changes.Add(info.ChangeText); };
        await rig.RunAsync(2);
        rig.Net.Ips = ["10.0.0.1", "10.9.9.9", "10.0.0.3", null];
        await rig.RunAsync(2);
        Assert.Single(changes);
        Assert.Contains("hop 2", changes[0]);
        Assert.Contains("10.9.9.9", changes[0]);
        Assert.Single(rig.Store.Events(rig.Id, 0, long.MaxValue), e => e.Kind == "route");
        Assert.Equal("10.9.9.9", rig.Session.Route[1]);
    }

    [Fact]
    public async Task A_route_that_grows_is_rediscovered()
    {
        using var rig = new Rig();
        await rig.RunAsync(2);
        rig.Net.Hops = [1, 5, 9, 11, 14];   // a router now answers where the destination used to be
        await rig.RunAsync(3);
        Assert.Equal(5, rig.Session.LastSample!.Rtts.Length);
        Assert.Equal(14, rig.Session.LastSample.Rtts[4]);
        Assert.Equal("192.0.2.50", rig.Session.Route[4]);
    }

    [Fact]
    public async Task Destination_only_mode_sends_a_single_probe()
    {
        using var rig = new Rig();
        rig.Session.Target.FinalHopOnly = true;
        await rig.RunAsync(4);
        Assert.Equal(4, rig.Net.Probes);
        Assert.True(rig.Session.LastSample!.FinalOnly);
        var stats = rig.Stats();
        Assert.Single(stats);
        Assert.Equal(12, stats[0].Avg);
    }

    [Fact]
    public async Task A_restart_remembers_hop_addresses_and_reports_no_route_change()
    {
        using var rig = new Rig();
        await rig.RunAsync(2);
        var restarted = new TraceSession(rig.Session.Target, rig.Store, rig.Net, () => rig.Settings, () => rig.Config);
        var changed = false;
        restarted.SampleTaken += (_, _, info) => changed |= info.RouteChanged;
        rig.Net.Hops = [null, 5, 9, 12];   // hop 1 happens not to answer in the first round after the restart
        await restarted.RoundAsync(rig.Now + 2500, default);
        Assert.Equal("10.0.0.1", restarted.Route[0]);
        Assert.False(changed);
        Assert.Empty(rig.Store.Events(rig.Id, 0, long.MaxValue));
    }

    [Fact]
    public async Task After_a_restart_a_name_that_cannot_be_looked_up_is_traced_at_its_last_address()
    {
        using var rig = new Rig("printer.example");
        var target = rig.Session.Target;
        var known = true;
        var address = IPAddress.Parse("192.0.2.77");
        Task<IPAddress[]> Lookup(string name, CancellationToken ct) =>
            known ? Task.FromResult(new[] { address }) : throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        TraceSession Start() => new(target, rig.Store, rig.Net, () => rig.Settings, () => rig.Config, Lookup);

        // Without an address from before there is nothing to trace, and it says why.
        known = false;
        var fresh = Start();
        await fresh.RoundAsync(rig.Now += 2500, default);
        Assert.Null(fresh.LastSample);
        Assert.Equal("Could not find the address of printer.example.", fresh.Error);

        known = true;
        var first = Start();
        await first.RoundAsync(rig.Now += 2500, default);
        Assert.Equal("192.0.2.77", target.Address);

        // The computer goes off and Traceline is restarted: the outage keeps being recorded as loss.
        known = false;
        rig.Net.Hops = [null];
        var restarted = Start();
        await restarted.RoundAsync(rig.Now += 2500, default);
        await restarted.RoundAsync(rig.Now += 2500, default);
        Assert.Null(restarted.Error);
        Assert.Equal(rig.Now, restarted.LastSample!.Ts);
        Assert.Equal(2, restarted.Recent(rig.Now - 6000).Count(r => float.IsNaN(r.Rtt)));
        var note = Assert.Single(rig.Store.Events(rig.Id, 0, long.MaxValue));
        Assert.Equal("Could not look up printer.example; tracing its last known address, 192.0.2.77", note.Text);

        // It comes back with another address; the name is asked again five minutes after the last try.
        known = true;
        address = IPAddress.Parse("192.0.2.99");
        rig.Net.Hops = [1, 5, 9, 12];
        await restarted.RoundAsync(rig.Now += 2500, default);
        Assert.Equal("192.0.2.77", target.Address);
        await restarted.RoundAsync(rig.Now += 300_000, default);
        Assert.Equal("192.0.2.99", target.Address);
        Assert.Contains(rig.Store.Events(rig.Id, 0, long.MaxValue), e => e.Text == "Target address changed from 192.0.2.77 to 192.0.2.99");
    }

    [Fact]
    public async Task Recent_results_follow_the_destination()
    {
        using var rig = new Rig();
        await rig.RunAsync(3);
        var recent = rig.Session.Recent(0);
        Assert.Equal(3, recent.Count);
        Assert.All(recent, r => Assert.Equal(12, r.Rtt));
    }
}

public class ViewTests
{
    [Fact]
    public async Task Timeline_buckets_hold_min_avg_max_and_loss()
    {
        using var rig = new Rig();
        var start = rig.Now;
        double?[] pattern = [10, 30, null, 20];
        foreach (var value in pattern) { rig.Net.Hops = [1, 5, 9, value]; await rig.RunAsync(); }
        var view = rig.Views.Build(rig.Id, start + 1, start + 10_001, start + 1, start + 10_001, [0, 1], 2);
        var destination = view.Timelines.Single(t => t.Hop == 0);
        Assert.Equal(10, destination.Min[0]); Assert.Equal(30, destination.Max[0]); Assert.Equal(20, destination.Avg[0]);
        Assert.Equal(0, destination.Loss[0]); Assert.Equal(50, destination.Loss[1]);
        Assert.Equal(1, view.Timelines.Single(t => t.Hop == 1).Avg[0]);
        Assert.Equal(25, view.Hops[^1].LossPct);
    }

    [Fact]
    public async Task Long_ranges_use_the_minute_summaries_and_agree_with_the_samples()
    {
        using var rig = new Rig();
        await rig.RunAsync(100);
        rig.Session.Flush();
        var raw = rig.Views.Stats(rig.Id, rig.Now - 3_600_000, rig.Now + 1);
        var coarse = rig.Views.Stats(rig.Id, rig.Now - ViewBuilder.RawLimitMs - 3_600_000, rig.Now + 1);
        Assert.Equal(4, coarse.Count);
        Assert.Equal(raw[^1].Count, coarse[^1].Count);
        Assert.Equal(raw[^1].Avg, coarse[^1].Avg);
        Assert.Equal(raw[0].Avg, coarse[0].Avg);
        Assert.Null(coarse[^1].Cur);
    }
}

public class StoreTests
{
    [Fact]
    public void Events_can_be_asked_for_by_kind_as_the_alert_log_does()
    {
        using var store = new Store(":memory:");
        var target = Target.Parse("192.0.2.1");
        store.SaveTarget(target);
        store.AddEvent(target.Id, 1000, "comment", "a note");
        store.AddEvent(target.Id, 2000, "alert", "Down started");
        store.AddEvent(target.Id, 3000, "route", "the route changed");
        store.AddEvent(target.Id, 4000, "alert", "Down ended");
        Assert.Equal(["Down started", "Down ended"], store.Events(null, 0, long.MaxValue, 5000, "alert").Select(e => e.Text));
        // When there are more than asked for, it is the latest that come back: other kinds do not use up the room.
        Assert.Equal(["Down ended"], store.Events(null, 0, long.MaxValue, 1, "alert").Select(e => e.Text));
        Assert.Equal(4, store.Events(target.Id, 0, long.MaxValue).Count);
    }

    [Fact]
    public void Samples_events_and_settings_round_trip_and_can_be_deleted()
    {
        using var store = new Store(":memory:");
        var target = Target.Parse("example.com");
        store.SaveTarget(target);
        var route = store.RouteId(target.Id, ["10.0.0.1", null, "192.0.2.1"]);
        store.AddSample(target.Id, new Sample(1000, route, false, [1.5f, float.NaN, 9f]));
        store.AddEvent(target.Id, 1500, "comment", "router rebooted");
        store.Set("app", new AppSettings { WarnMs = 42 });

        var sample = Assert.Single(store.Samples(target.Id, 0, 2000));
        Assert.Equal(1.5f, sample.Rtts[0]); Assert.True(float.IsNaN(sample.Rtts[1]));
        Assert.Equal("10.0.0.1||192.0.2.1", string.Join('|', store.Route(sample.RouteId)));
        Assert.Equal(route, store.RouteId(target.Id, ["10.0.0.1", null, "192.0.2.1"]));
        Assert.Equal("router rebooted", Assert.Single(store.Events(target.Id, 0, 2000)).Text);
        Assert.Equal(42, store.Get<AppSettings>("app").WarnMs);
        Assert.Equal("example.com", Assert.Single(store.Targets()).Host);

        store.DeleteTarget(target.Id);
        Assert.Empty(store.Targets());
        Assert.Empty(store.Samples(target.Id, 0, 2000));
        Assert.Empty(store.Events(target.Id, 0, 2000));
    }
}

public class LookupTests
{
    [Theory]
    [InlineData("192.168.1.1", "Private network")]
    [InlineData("10.1.2.3", "Private network")]
    [InlineData("172.20.0.1", "Private network")]
    [InlineData("100.72.0.1", "Carrier-grade NAT (your ISP)")]
    [InlineData("8.8.8.8", null)]
    [InlineData("fe80::1", "Local network")]
    public void Private_ranges_are_labelled_without_a_lookup(string ip, string? expected) =>
        Assert.Equal(expected, HostDirectory.LocalOwner(IPAddress.Parse(ip)));

    [Fact]
    public void Owner_lookup_names_are_built_the_way_the_service_expects()
    {
        Assert.Equal("4.3.2.1.origin.asn.cymru.com", HostDirectory.OriginQueryName(IPAddress.Parse("1.2.3.4")));
        var v6 = HostDirectory.OriginQueryName(IPAddress.Parse("2001:db8::1"));
        Assert.StartsWith("1.0.0.0.", v6);
        Assert.EndsWith(".8.b.d.0.1.0.0.2.origin6.asn.cymru.com", v6);
    }

    [Fact]
    public void A_dns_text_answer_is_read_from_a_reply_packet()
    {
        var query = MiniDns.BuildQuery("as13335.asn.cymru.com", MiniDns.TypeTxt, out _);
        var text = "13335 | US | arin | 2010-07-14 | CLOUDFLARENET, US"u8.ToArray();
        byte[] answer = [0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 60, 0, (byte)(text.Length + 1), (byte)text.Length, .. text];
        byte[] reply = [.. query, .. answer];
        reply[2] = 0x81; reply[3] = 0x80; reply[7] = 1;
        var parsed = MiniDns.Parse(reply);
        Assert.Equal(0, parsed.Rcode);
        Assert.Equal("13335 | US | arin | 2010-07-14 | CLOUDFLARENET, US", Assert.Single(parsed.Texts));
    }
}
