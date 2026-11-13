using Traceline.Core;
using Traceline.Core.Notifications;

namespace Traceline.Core.Tests;

public class AlertConditionTests
{
    private static readonly RoundInfo Quiet = new(false, null, ["10.0.0.1", "192.0.2.50"]);
    private const long Now = 1_000_000;

    private static List<(long, float)> Samples(params float[] rtts) =>
        rtts.Select((rtt, i) => (Now - (rtts.Length - 1 - i) * 2500L, rtt)).ToList();

    [Fact]
    public void Over_time_fires_when_enough_of_the_window_is_bad()
    {
        var rule = new AlertRule { Kind = ConditionKind.OverTime, WindowSeconds = 60, Percent = 30, LatencyMs = 100 };
        Assert.False(rule.IsActive(Samples(10, 10, 10, 10, 10, 10, 10, 10, 150, 10), Quiet, Now, out _));
        Assert.True(rule.IsActive(Samples(10, 150, 10, float.NaN, 10, 10, 200, 10, 10, 10), Quiet, Now, out var reason));
        Assert.Contains("30%", reason);
    }

    [Fact]
    public void Over_time_only_looks_inside_its_window_and_waits_for_a_few_samples()
    {
        var rule = new AlertRule { Kind = ConditionKind.OverTime, WindowSeconds = 10, Percent = 50 };
        // Old losses fall outside a 10 second window.
        Assert.False(rule.IsActive(Samples(float.NaN, float.NaN, float.NaN, float.NaN, 10, 10, 10, 10, 10, 10), Quiet, Now, out _));
        Assert.False(new AlertRule { Kind = ConditionKind.OverTime, Percent = 1 }.IsActive(Samples(float.NaN, float.NaN), Quiet, Now, out _));
    }

    [Fact]
    public void Latency_zero_means_only_lost_samples_count()
    {
        var rule = new AlertRule { Kind = ConditionKind.OverSamples, SampleCount = 4, BadCount = 2, LatencyMs = 0 };
        Assert.False(rule.IsActive(Samples(900, 900, 900, 900), Quiet, Now, out _));
        Assert.True(rule.IsActive(Samples(900, float.NaN, 900, float.NaN), Quiet, Now, out _));
    }

    [Fact]
    public void Over_samples_needs_the_full_count_first()
    {
        var rule = new AlertRule { Kind = ConditionKind.OverSamples, SampleCount = 10, BadCount = 10 };
        Assert.False(rule.IsActive(Samples(float.NaN, float.NaN, float.NaN), Quiet, Now, out _));
        Assert.True(rule.IsActive(Samples(Enumerable.Repeat(float.NaN, 10).ToArray()), Quiet, Now, out _));
        Assert.False(rule.IsActive(Samples([.. Enumerable.Repeat(float.NaN, 9), 12f]), Quiet, Now, out _));
    }

    [Fact]
    public void Mos_route_and_address_conditions()
    {
        var mos = new AlertRule { Kind = ConditionKind.MosBelow, Mos = 3.5, WindowSeconds = 600 };
        Assert.False(mos.IsActive(Samples(Enumerable.Repeat(20f, 20).ToArray()), Quiet, Now, out _));
        Assert.True(mos.IsActive(Samples(Enumerable.Range(0, 20).Select(i => i % 3 == 0 ? float.NaN : 300f).ToArray()), Quiet, Now, out _));

        var route = new AlertRule { Kind = ConditionKind.RouteChanged };
        Assert.False(route.IsActive([], Quiet, Now, out _));
        Assert.True(route.IsActive([], new RoundInfo(true, "Route changed at hop 2", []), Now, out var text));
        Assert.Equal("Route changed at hop 2", text);

        var ip = new AlertRule { Kind = ConditionKind.IpInRoute, Ip = "10.0.0.1" };
        Assert.True(ip.IsActive([], Quiet, Now, out _));
        Assert.False(new AlertRule { Kind = ConditionKind.IpInRoute, Ip = "10.0.0.7" }.IsActive([], Quiet, Now, out _));
    }

    [Fact]
    public void Message_variables_are_filled_in()
    {
        Assert.Equal("example.com is down (3 lost)", AlertService.Render("{{Target}} is {{State}} ({{reason}})",
            new() { ["Target"] = "example.com", ["State"] = "down", ["Reason"] = "3 lost" }));
    }
}

public class AlertServiceTests
{
    [Fact]
    public async Task An_alert_starts_once_ends_once_and_marks_the_timeline()
    {
        using var store = new Store(":memory:");
        var net = new FakeProber();
        store.Set("app", new AppSettings { SendDelayMs = 0, OwnerLookup = false, ReverseDns = false });
        store.Set("alerts", new List<AlertRule> { new() { Name = "Down", Kind = ConditionKind.OverSamples, SampleCount = 3, BadCount = 3 } });
        var target = Target.Parse("192.0.2.50");
        target.Paused = true;   // rounds are driven by hand below
        store.SaveTarget(target);

        await using var engine = new TraceEngine(store, net);
        var views = new ViewBuilder(store, new HostDirectory(store, () => engine.Settings), _ => engine.Configs[0]);
        var alerts = new AlertService(engine, views, new ChannelSender(new HttpClient()), new Reports(store, views));
        var fired = new List<AlertFired>();
        alerts.Fired += fired.Add;
        engine.Start();
        var session = engine.Session(target.Id)!;
        var rule = alerts.Rules[0];

        var now = 1_800_000_000_000;
        async Task Rounds(int count) { for (var i = 0; i < count; i++) await session.RoundAsync(now += 2500, default); }

        await Rounds(4);
        Assert.Empty(fired);
        net.Hops = [1, 5, 9, null];
        await Rounds(5);
        Assert.Equal("start", Assert.Single(fired).State);
        Assert.True(alerts.IsActive(rule.Id, target.Id));
        net.Hops = [1, 5, 9, 12];
        await Rounds(3);
        Assert.Equal(["start", "end"], fired.Select(f => f.State));
        Assert.False(alerts.IsActive(rule.Id, target.Id));
        Assert.Equal(2, store.Events(target.Id, 0, long.MaxValue).Count(e => e.Kind == "alert"));
    }
}

public class DiagnosisTests
{

    private static HopStat Hop(int hop, double avg, double loss, string? owner = null, string? asn = null, int count = 100) =>
        new(hop, $"10.0.0.{hop}", null, asn, owner, count, (int)(count * loss / 100), loss >= 100 ? null : avg, avg, avg, avg, 1, loss, 4.3);

    [Fact]
    public void Loss_in_the_middle_only_is_called_harmless()
    {
        var d = Diagnosis.From([Hop(1, 1, 0), Hop(2, 8, 0), Hop(3, 9, 40), Hop(4, 10, 0), Hop(5, 12, 0)], 100, 250);
        Assert.Equal("ok", d.Severity);
        Assert.Contains(d.Details, line => line.Contains("Hop 3") && line.Contains("not dropping your traffic"));
    }

    [Fact]
    public void Loss_from_one_hop_onward_is_pinned_on_that_hop_and_its_owner()
    {
        var d = Diagnosis.From([
            Hop(1, 1, 0, "Private network"), Hop(2, 8, 0, "EXAMPLE-ISP", "AS64500"), Hop(3, 9, 12, "EXAMPLE-ISP", "AS64500"),
            Hop(4, 10, 11, "TRANSIT", "AS64501"), Hop(5, 12, 12, "DEST", "AS64502")], 100, 250);
        Assert.Equal("bad", d.Severity);
        Assert.Contains("12% packet loss", d.Headline);
        Assert.Contains("your internet provider's network (EXAMPLE-ISP, hop 3)", d.Headline);
    }

    [Fact]
    public void Trouble_at_hop_one_points_at_the_local_network()
    {
        var d = Diagnosis.From([Hop(1, 40, 8, "Private network"), Hop(2, 48, 9), Hop(3, 50, 8)], 100, 250);
        Assert.Equal("warn", d.Severity);
        Assert.Contains("first hop", d.Headline);
        Assert.Contains("Wi-Fi", d.Headline);
    }

    [Fact]
    public void A_dead_destination_says_how_far_packets_get()
    {
        var d = Diagnosis.From([Hop(1, 1, 0), Hop(2, 8, 0), Hop(3, 0, 100)], 100, 250);
        Assert.Equal("bad", d.Severity);
        Assert.Equal("The destination is not answering", d.Headline);
        Assert.Contains(d.Details, line => line.Contains("hop 2"));

        var nothing = Diagnosis.From([Hop(1, 0, 100), Hop(2, 0, 100)], 100, 250);
        Assert.Contains("not even the first hop", nothing.Headline);
    }

    [Fact]
    public void A_silent_hop_before_the_fault_does_not_move_the_blame()
    {
        var d = Diagnosis.From([Hop(1, 1, 0), Hop(2, 0, 100), Hop(3, 9, 20), Hop(4, 12, 20)], 100, 250);
        Assert.Contains("hop 3", d.Headline);
    }

    [Fact]
    public void High_latency_is_traced_to_where_it_is_added()
    {
        var d = Diagnosis.From([Hop(1, 1, 0), Hop(2, 9, 0), Hop(3, 180, 0, "TRANSIT", "AS64501"), Hop(4, 185, 0)], 100, 250);
        Assert.Equal("warn", d.Severity);
        Assert.Contains("185 ms", d.Headline);
        Assert.Contains("hop 3", d.Headline);
    }

    [Fact]
    public void Too_few_samples_give_no_verdict()
    {
        Assert.Equal("info", Diagnosis.From([Hop(1, 1, 0, count: 2)], 100, 250).Severity);
        Assert.Equal("info", Diagnosis.From([], 100, 250).Severity);
    }
}

public class ReportTests
{
    [Fact]
    public async Task Exports_contain_the_recorded_numbers()
    {
        using var rig = new Rig();
        await rig.RunAsync(5);
        rig.Store.AddEvent(rig.Id, rig.Now - 1000, "comment", "Rebooted <the> router");
        var reports = new Reports(rig.Store, rig.Views);

        var csv = reports.SamplesCsv(rig.Id, 0, long.MaxValue, utc: true).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Time,Hop 1 (10.0.0.1),Hop 2 (10.0.0.2),Hop 3 (10.0.0.3),Hop 4 (192.0.2.50)", csv[0]);
        Assert.Equal(6, csv.Length);
        Assert.EndsWith(",1,5,9,12", csv[1]);

        var stats = reports.StatsCsv(rig.Id, rig.Now - 60_000, rig.Now + 1, 60, utc: true);
        Assert.Contains(",5,0,0,12,12,12,0,", stats);

        var text = reports.Text(rig.Session.Target, rig.Now - 60_000, rig.Now + 1);
        Assert.Contains("192.0.2.50", text);

        var html = reports.Html(rig.Session.Target, rig.Now - 60_000, rig.Now + 1, [1]);
        Assert.Contains("<svg", html);
        Assert.Contains("Rebooted &lt;the&gt; router", html);
        Assert.Contains("The connection looks healthy", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org/2000/svg", ""));
    }
}
