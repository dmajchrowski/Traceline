using System.Net;
using Traceline.Core;

namespace Traceline.Core.Tests;

/// <summary>A scripted network: tests decide what each hop answers in each round.</summary>
public sealed class FakeProber : IProber
{
    /// <summary>Round-trip time per hop in ms; null = that hop does not answer. The last entry is the destination.</summary>
    public double?[] Hops = [1, 5, 9, 12];
    public string?[]? Ips;
    public int Probes;

    public Task<ProbeResult> ProbeAsync(Target target, TraceConfig config, IPAddress? address, int ttl, CancellationToken ct)
    {
        Probes++;
        var index = ttl <= 0 ? Hops.Length - 1 : ttl - 1;
        if (index >= Hops.Length || Hops[index] is not double rtt) return Task.FromResult(ProbeResult.Lost);
        var isDestination = index == Hops.Length - 1;
        var ip = isDestination ? address! : IPAddress.Parse(Ips?[index] ?? $"10.0.0.{index + 1}");
        return Task.FromResult(new ProbeResult(ip, rtt, isDestination));
    }
}

public sealed class Rig : IDisposable
{
    public readonly Store Store = new(":memory:");
    public readonly FakeProber Net = new();
    public readonly AppSettings Settings = new() { OwnerLookup = false, ReverseDns = false };
    public readonly TraceConfig Config = new() { SendDelayMs = 0 };
    public readonly TraceSession Session;
    public readonly ViewBuilder Views;
    public long Now = 1_800_000_000_000;

    public Rig(string input = "192.0.2.50")
    {
        var target = Target.Parse(input);
        Store.SaveTarget(target);
        Session = new TraceSession(target, Store, Net, () => Settings, () => Config);
        Views = new ViewBuilder(Store, new HostDirectory(Store, () => Settings), _ => Config);
    }

    public long Id => Session.Target.Id;

    /// <summary>Runs rounds 2.5 s apart.</summary>
    public async Task RunAsync(int rounds = 1)
    {
        for (var i = 0; i < rounds; i++) { Now += 2500; await Session.RoundAsync(Now, default); }
    }

    /// <summary>Statistics for the last hour.</summary>
    public List<HopStat> Stats() => Views.Stats(Id, Now - 3_600_000, Now + 1);

    public void Dispose() => Store.Dispose();
}
