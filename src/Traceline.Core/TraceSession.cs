using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace Traceline.Core;

public sealed record RoundInfo(bool RouteChanged, string? ChangeText, string?[] Route);

/// <summary>What a remote agent is asked to do for one round, and what it answers per hop.</summary>
public sealed record AgentRequest(Target Target, TraceConfig Config, string? Address, bool FinalOnly, int ProbeLength);
public sealed record AgentHop(string? Ip, double? Rtt, bool Reached);

/// <summary>
/// Traces one target. Each round sends one probe per hop at the same moment (slightly staggered), works out
/// where the destination sits in the route, stores the sample and keeps a per-minute roll-up.
/// </summary>
public sealed class TraceSession(Target target, Store store, IProber prober, Func<AppSettings> settings, Func<TraceConfig> config,
    Func<string, CancellationToken, Task<IPAddress[]>>? lookup = null)
{
    private static readonly HttpClient AgentHttp = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>Finds the addresses of a name. Tests put their own in its place.</summary>
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookup = lookup ?? Dns.GetHostAddressesAsync;

    private readonly object _lock = new();
    private IPAddress? _address;
    private long _lastResolveMs;
    private int? _knownLength;      // hops to the destination, once it has answered
    private int? _silentLength;     // how far to probe while the destination is not answering
    private int _failStreak, _roundsSinceDiscovery;
    private string?[] _route = [];
    private long _routeId;
    private bool _seeded;
    private long _lastRouteEventMs;
    private long _minute = -1;
    private Agg[] _minuteAggs = [];
    private int[] _missed = [];         // per hop: rounds in a row without an answer
    private long[] _missedSince = [];   // per hop: when that run of silence began

    public Target Target { get; } = target;
    public TraceConfig Config => config();
    public string? Error { get; private set; }
    public Sample? LastSample { get; private set; }
    public string?[] Route { get { lock (_lock) return _route.ToArray(); } }

    /// <summary>Samples from roughly the last hour, oldest first. Used for the summary and for alerts.</summary>
    private readonly List<Sample> _recent = [];

    /// <summary>Recent destination results.</summary>
    public List<(long Ts, float Rtt)> Recent(long since) => Recent("", since);

    /// <summary>
    /// Recent results for what an alert watches: "" = the destination, "hop:N" = that hop number,
    /// "ip:a.b.c.d" = wherever that address is in the route. Rounds in which it was not probed are skipped.
    /// </summary>
    public List<(long Ts, float Rtt)> Recent(string watch, long since)
    {
        lock (_lock)
        {
            var list = new List<(long, float)>();
            foreach (var s in _recent)
            {
                if (s.Ts < since) continue;
                if (watch.Length == 0) { list.Add((s.Ts, s.Rtts[^1])); continue; }
                if (s.FinalOnly) continue;
                var index = watch.StartsWith("hop:", StringComparison.Ordinal) && int.TryParse(watch.AsSpan(4), out var hop) ? hop - 1
                    : watch.StartsWith("ip:", StringComparison.Ordinal) ? Array.IndexOf(store.Route(s.RouteId), watch[3..]) : -1;
                if (index >= 0 && index < s.Rtts.Length) list.Add((s.Ts, s.Rtts[index]));
            }
            return list;
        }
    }

    public event Action<TraceSession, Sample, RoundInfo>? SampleTaken;

    public async Task RoundAsync(long now, CancellationToken ct)
    {
        var cfg = config();
        if (Target.Kind is ProbeKind.Icmp or ProbeKind.Tcp && !await ResolveAsync(now, ct)) return;
        var address = _address;

        var finalOnly = Target.FinalHopOnly || Target.Kind != ProbeKind.Icmp;
        var probeLength = 1;
        if (!finalOnly)
        {
            lock (_lock)
            {
                if (++_roundsSinceDiscovery >= 20) { _silentLength = null; _roundsSinceDiscovery = 0; }
                probeLength = Math.Min(_knownLength ?? _silentLength ?? cfg.MaxHops, cfg.MaxHops);
            }
        }

        ProbeResult[] results;
        try
        {
            results = cfg.PacketType == PacketType.Agent && Target.Kind == ProbeKind.Icmp
                ? await ViaAgentAsync(cfg, address, finalOnly, probeLength, ct)
                : await RoundProbe.RunAsync(prober, Target, cfg, address, finalOnly, probeLength, ct);
        }
        catch (ProbeSetupException ex)
        {
            Error = ex.Message;
            return;
        }

        Sample sample;
        RoundInfo info;
        lock (_lock)
        {
            if (!_seeded)
            {
                _seeded = true;
                if (store.LastRoute(Target.Id) is var (id, hops)) { _routeId = id; _route = hops.ToArray(); }
            }
            int length;
            var reached = Array.FindIndex(results, r => r.Reached);
            if (finalOnly)
            {
                length = 1;
            }
            else if (reached >= 0)
            {
                length = reached + 1;
                _knownLength = length; _silentLength = null; _failStreak = 0;
            }
            else if (_knownLength is int known)
            {
                // The destination did not answer this round. If a router answered where the destination used to be,
                // the route has grown; after a few misses in a row, look for the destination again from scratch.
                length = known;
                var routerAtEnd = known <= results.Length && results[known - 1].Responder is not null;
                if (routerAtEnd || ++_failStreak >= 5) { _knownLength = null; _failStreak = 0; }
            }
            else
            {
                // Never reached: show the hops that answered plus one silent row for the destination.
                // A hop near the end that skips a round must not make the route look shorter, so the length only grows.
                var lastAnswer = Array.FindLastIndex(results, r => r.Responder is not null);
                length = Math.Min(Math.Max(lastAnswer + 2, _route.Length), cfg.MaxHops);
                _silentLength = Math.Min(lastAnswer + 4, cfg.MaxHops);
            }

            var rtts = new float[length];
            var route = new string?[length];
            Array.Copy(_route, route, Math.Min(_route.Length, length));
            var changed = _route.Length > 0 && _route.Length != length;
            string? changeText = changed ? $"Route length changed from {_route.Length} to {length} hops" : null;
            for (var i = 0; i < length; i++)
            {
                var isDestination = i == length - 1;
                var r = i < results.Length ? results[i] : ProbeResult.Lost;
                if (isDestination && !finalOnly && reached < 0) r = ProbeResult.Lost;
                if (!isDestination && r.Reached) r = ProbeResult.Lost;   // the destination answering in a hop's place says nothing about that hop
                rtts[i] = double.IsNaN(r.RttMs) ? float.NaN : (float)r.RttMs;
                if (!isDestination && SilentForTooLong(i, r.Responder is not null, now, cfg)) route[i] = null;
                var ip = isDestination ? (address?.ToString() ?? r.Responder?.ToString() ?? Target.Host) : r.Responder?.ToString();
                if (ip is null || route[i] == ip) continue;
                if (route[i] is not null && !isDestination)
                {
                    changed = true;
                    changeText ??= $"Route changed at hop {i + 1}: {route[i]} → {ip}";
                }
                route[i] = ip;
            }
            if (!route.AsSpan().SequenceEqual(_route) || _routeId == 0)
            {
                _route = route;
                _routeId = store.RouteId(Target.Id, route);
            }
            // Load-balanced routes can flip constantly; note a change at most once a minute.
            if (changed && now - _lastRouteEventMs < 60_000) { changed = false; changeText = null; }
            if (changed) _lastRouteEventMs = now;

            sample = new Sample(now, _routeId, finalOnly, rtts);
            info = new RoundInfo(changed, changeText, route);
            store.AddSample(Target.Id, sample);
            if (changed) store.AddEvent(Target.Id, now, "route", changeText!);
            AddToRollup(sample);
            var at = _recent.Count;
            while (at > 0 && _recent[at - 1].Ts > now) at--;   // rounds can finish out of order
            _recent.Insert(at, sample);
            if (_recent[0].Ts < now - 3_600_000) _recent.RemoveAll(r => r.Ts < now - 3_600_000);
            if (LastSample is null || LastSample.Ts <= now) LastSample = sample;
            Error = null;
        }
        SampleTaken?.Invoke(this, sample, info);
    }

    /// <summary>Asks another Traceline to send this round and report what came back.</summary>
    private async Task<ProbeResult[]> ViaAgentAsync(TraceConfig cfg, IPAddress? address, bool finalOnly, int probeLength, CancellationToken ct)
    {
        if (!Uri.TryCreate(cfg.AgentUrl, UriKind.Absolute, out var agent)) throw new ProbeSetupException("This configuration traces through a remote agent, but no agent address is set.");
        var inner = new TraceConfig
        {
            PacketType = cfg.AgentPacketType == PacketType.Agent ? PacketType.Icmp : cfg.AgentPacketType, TcpPort = cfg.TcpPort,
            TimeoutMs = cfg.TimeoutMs, PacketSize = cfg.PacketSize, SendDelayMs = cfg.SendDelayMs, MaxHops = cfg.MaxHops, AllowFragment = cfg.AllowFragment,
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(agent, "/api/agent/round"))
            {
                Content = JsonContent.Create(new AgentRequest(Target, inner, address?.ToString(), finalOnly, probeLength), options: Store.Json),
            };
            request.Headers.Add("X-Agent-Key", cfg.AgentKey);
            using var response = await AgentHttp.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProbeSetupException("The remote agent rejected the agent key.");
            if (!response.IsSuccessStatusCode)
                throw new ProbeSetupException($"The remote agent answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            var hops = await response.Content.ReadFromJsonAsync<AgentHop[]>(Store.Json, ct) ?? [];
            return hops.Select(h => new ProbeResult(IPAddress.TryParse(h.Ip, out var ip) ? ip : null, h.Rtt ?? double.NaN, h.Reached)).ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new ProbeSetupException($"Could not reach the remote agent at {cfg.AgentUrl}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProbeSetupException($"The remote agent at {cfg.AgentUrl} did not answer in time.");
        }
    }

    /// <summary>
    /// Keeps count of how long a hop has not answered, and says when its address should be dropped from the route:
    /// after both the configured time and the configured number of rounds without a single answer.
    /// </summary>
    private bool SilentForTooLong(int index, bool answered, long now, TraceConfig cfg)
    {
        if (_missed.Length <= index) { Array.Resize(ref _missed, index + 8); Array.Resize(ref _missedSince, index + 8); }
        if (answered) { _missed[index] = 0; return false; }
        if (_missed[index]++ == 0) _missedSince[index] = now;
        return cfg.RemoveSilentHops && _missed[index] >= Math.Max(1, cfg.SilentSamples) && now - _missedSince[index] >= cfg.SilentMinutes * 60_000L;
    }

    private void AddToRollup(Sample s)
    {
        var minute = s.Ts / 60_000 * 60_000;
        if (minute != _minute)
        {
            FlushRollupLocked();
            _minute = minute;
            // After a restart inside a minute, continue that minute's summary instead of replacing it.
            _minuteAggs = store.Rollups(Target.Id, minute, minute + 1).FirstOrDefault()?.Aggs ?? [];
        }
        var needed = s.FinalOnly ? 1 : s.Rtts.Length + 1;
        if (_minuteAggs.Length < needed) Array.Resize(ref _minuteAggs, needed);
        _minuteAggs[0].Add(s.Rtts[^1]);
        if (!s.FinalOnly) for (var i = 0; i < s.Rtts.Length; i++) _minuteAggs[i + 1].Add(s.Rtts[i]);
    }

    private void FlushRollupLocked()
    {
        if (_minute >= 0 && _minuteAggs.Length > 0) store.SaveRollup(Target.Id, _minute, _routeId, _minuteAggs);
    }

    /// <summary>Saves the roll-up for the minute in progress (called when tracing stops).</summary>
    public void Flush() { lock (_lock) FlushRollupLocked(); }

    /// <summary>Starts a fresh route discovery, e.g. after switching between full-route and destination-only.</summary>
    public void ResetRoute() { lock (_lock) { _knownLength = null; _silentLength = null; _route = []; _routeId = 0; _seeded = true; _missed = []; _missedSince = []; } }

    private async Task<bool> ResolveAsync(long now, CancellationToken ct)
    {
        if (_address is not null && (!Target.FollowDns || now - _lastResolveMs < 300_000)) return true;
        _lastResolveMs = now;
        if (IPAddress.TryParse(Target.Host, out var literal)) { SetAddress(literal, now); return true; }
        IPAddress[] found;
        try
        {
            found = await _lookup(Target.Host, ct);
        }
        catch (SocketException)
        {
            if (_address is null && !UseStoredAddress(now)) Error = $"Could not find the address of {Target.Host}.";
            return _address is not null;
        }
        if (Target.Family == "ipv4") found = found.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        if (Target.Family == "ipv6") found = found.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
        if (found.Length == 0)
        {
            if (_address is null && !UseStoredAddress(now)) Error = $"{Target.Host} has no {(Target.Family == "ipv6" ? "IPv6" : Target.Family == "ipv4" ? "IPv4" : "usable")} address.";
            return _address is not null;
        }
        // Keep the current address while the name still lists it, so round-robin names do not cause constant switching.
        if (_address is null || !found.Contains(_address)) SetAddress(found[0], now);
        return true;
    }

    /// <summary>
    /// After a restart, a name that cannot be looked up just now (the computer is off, the name server is out of reach)
    /// is traced at the address the target had before. An outage that lasts across a restart then keeps counting as
    /// loss, as it does while the program runs, instead of leaving no data at all. A target that follows its name's
    /// address tries the name again every five minutes, like any other.
    /// </summary>
    private bool UseStoredAddress(long now)
    {
        if (!IPAddress.TryParse(Target.Address, out var stored)) return false;
        if (Target.Family == "ipv4" && stored.AddressFamily != AddressFamily.InterNetwork) return false;
        if (Target.Family == "ipv6" && stored.AddressFamily != AddressFamily.InterNetworkV6) return false;
        _address = stored;
        if (settings().CommentOnIpChange)
            store.AddEvent(Target.Id, now, "ip", $"Could not look up {Target.Host}; tracing its last known address, {stored}");
        return true;
    }

    private void SetAddress(IPAddress address, long now)
    {
        var previous = _address;
        _address = address;
        var text = address.ToString();
        if (Target.Address == text) return;
        if (previous is not null || Target.Address.Length > 0)
        {
            if (settings().CommentOnIpChange)
                store.AddEvent(Target.Id, now, "ip", $"Target address changed from {(previous?.ToString() ?? Target.Address)} to {text}");
            ResetRoute();
        }
        Target.Address = text;
        store.SaveTarget(Target);
    }
}
