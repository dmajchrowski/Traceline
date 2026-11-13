using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Traceline.Core;

/// <summary>
/// Owns one <see cref="TraceSession"/> per target and runs their probe rounds on schedule. Also keeps the
/// named configurations and workspaces, because both decide what runs and how.
/// </summary>
public sealed class TraceEngine : IAsyncDisposable
{
    private sealed record Running(TraceSession Session, CancellationTokenSource Stop, Task Loop);

    private readonly Store _store;
    private readonly IProber _prober;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<long, Running> _running = new();
    private AppSettings _settings;
    private List<TraceConfig> _configs;
    private List<Workspace> _workspaces;

    public TraceEngine(Store store, IProber prober, TimeProvider? clock = null)
    {
        _store = store; _prober = prober; _clock = clock ?? TimeProvider.System;
        _settings = store.Get<AppSettings>("app");
        _configs = store.Get<List<TraceConfig>>("configs");
        if (_configs.All(c => c.Id != TraceConfig.DefaultId))
        {
            // First run, or an upgrade from 1.0 where these values lived in the general settings.
            _configs.Insert(0, new TraceConfig
            {
                WarnMs = _settings.WarnMs ?? 100, BadMs = _settings.BadMs ?? 250,
                SendDelayMs = _settings.SendDelayMs ?? 10, MaxHops = _settings.MaxHops ?? 30,
            });
            store.Set("configs", _configs);
        }
        _workspaces = store.Get<List<Workspace>>("workspaces");
        if (_workspaces.Count == 0) { _workspaces.Add(new Workspace { Id = "", Name = "Main workspace" }); store.Set("workspaces", _workspaces); }
        if (_workspaces.All(w => w.Id != _settings.ActiveWorkspace)) _settings.ActiveWorkspace = _workspaces[0].Id;
    }

    public Store Store => _store;
    public IProber Prober => _prober;
    public AppSettings Settings => _settings;
    public long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public event Action<TraceSession, Sample, RoundInfo>? SampleTaken;
    public event Action? TargetsChanged;

    public void SaveSettings(AppSettings settings) { _store.Set("app", settings); _settings = settings; }

    // ---------------------------------------------------------------- configurations

    public IReadOnlyList<TraceConfig> Configs => _configs;

    public TraceConfig ConfigFor(Target target) =>
        _configs.FirstOrDefault(c => c.Id == target.ConfigId) ?? _configs.First(c => c.Id == TraceConfig.DefaultId);

    public void SaveConfigs(List<TraceConfig> configs)
    {
        if (configs.All(c => c.Id != TraceConfig.DefaultId)) throw new ArgumentException("The default configuration cannot be removed.");
        if (configs.Any(c => string.IsNullOrWhiteSpace(c.Name))) throw new ArgumentException("Every configuration needs a name.");
        foreach (var c in configs)
        {
            c.TimeoutMs = Math.Clamp(c.TimeoutMs, 200, 30_000); c.PacketSize = Math.Clamp(c.PacketSize, 28, 1500);
            c.SendDelayMs = Math.Clamp(c.SendDelayMs, 0, 500); c.MaxHops = Math.Clamp(c.MaxHops, 5, 64);
            c.WarnMs = Math.Max(1, c.WarnMs); c.BadMs = Math.Max(c.WarnMs + 1, c.BadMs);
            c.LossScalePct = Math.Clamp(c.LossScalePct, 1, 100);
        }
        _store.Set("configs", configs);
        _configs = configs;
        foreach (var r in _running.Values) r.Session.ResetRoute();
        TargetsChanged?.Invoke();
    }

    // ---------------------------------------------------------------- workspaces

    public IReadOnlyList<Workspace> Workspaces => _workspaces;
    public string ActiveWorkspace => _settings.ActiveWorkspace;

    public void SaveWorkspaces(List<Workspace> workspaces)
    {
        if (workspaces.Count == 0) throw new ArgumentException("At least one workspace must remain.");
        _store.Set("workspaces", workspaces);
        _workspaces = workspaces;
    }

    /// <summary>Makes another workspace the running one: its targets start, everyone else's stop (their data stays).</summary>
    public async Task SwitchWorkspaceAsync(string id)
    {
        if (_workspaces.All(w => w.Id != id)) throw new KeyNotFoundException();
        _settings.ActiveWorkspace = id;
        SaveSettings(_settings);
        foreach (var key in _running.Keys.ToList())
            if (_running.TryRemove(key, out var running)) { await StopAsync(running); Launch(running.Session.Target, running.Session); }
        TargetsChanged?.Invoke();
    }

    // ---------------------------------------------------------------- targets

    public void Start()
    {
        foreach (var target in _store.Targets()) Launch(target);
    }

    private bool ShouldRun(Target t) => !t.Paused && !t.Closed && t.WorkspaceId == _settings.ActiveWorkspace;

    /// <summary>Open targets of the active workspace.</summary>
    public IEnumerable<TraceSession> Sessions => AllSessions.Where(s => !s.Target.Closed && s.Target.WorkspaceId == _settings.ActiveWorkspace);

    /// <summary>Every target there is data for, including closed ones and other workspaces.</summary>
    public IEnumerable<TraceSession> AllSessions => _running.Values.Select(r => r.Session).OrderBy(s => s.Target.Id);

    public TraceSession? Session(long id) => _running.TryGetValue(id, out var r) ? r.Session : null;

    public Target Add(string input, int? intervalMs = null, string? configId = null)
    {
        var target = Target.Parse(input);
        if (Sessions.Any(s => string.Equals(s.Target.Input, target.Input, StringComparison.OrdinalIgnoreCase) && s.Target.ConfigId == (configId ?? "")))
            throw new ArgumentException($"{target.Input} is already being traced.");
        target.CreatedMs = Now;
        target.WorkspaceId = _settings.ActiveWorkspace;
        target.ConfigId = configId is null or TraceConfig.DefaultId ? "" : configId;
        if (intervalMs is int ms) target.IntervalMs = Math.Clamp(ms, 500, 3_600_000);
        if (ConfigFor(target).StartFinalHopOnly) target.FinalHopOnly = true;
        _store.SaveTarget(target);
        Launch(target);
        TargetsChanged?.Invoke();
        return target;
    }

    /// <summary>Applies changed settings (interval, pause, destination-only, name, configuration) and restarts the target's loop.</summary>
    public async Task UpdateAsync(long id, Action<Target> change)
    {
        if (!_running.TryRemove(id, out var running)) throw new KeyNotFoundException();
        await StopAsync(running);
        var target = running.Session.Target;
        var (wasFinalOnly, wasConfig) = (target.FinalHopOnly, target.ConfigId);
        change(target);
        target.IntervalMs = Math.Clamp(target.IntervalMs, 500, 3_600_000);
        if (target.Kind != ProbeKind.Icmp) target.FinalHopOnly = true;
        if (target.ConfigId == TraceConfig.DefaultId) target.ConfigId = "";
        _store.SaveTarget(target);
        if (wasFinalOnly != target.FinalHopOnly || wasConfig != target.ConfigId) running.Session.ResetRoute();
        Launch(target, running.Session);
        TargetsChanged?.Invoke();
    }

    /// <summary>
    /// Stops tracing a target. Its session is kept for later (and can be reopened) unless the auto-save setting
    /// says to discard it; returns whether it was kept.
    /// </summary>
    public async Task<bool> CloseAsync(long id)
    {
        var session = Session(id) ?? throw new KeyNotFoundException();
        var range = _store.DataRange(id);
        var keep = session.Target.Keep || _settings.AutoSaveMode switch
        {
            "selected" => false,
            "after" => range is var (first, last, _) && last - first >= _settings.AutoSaveMinutes * 60_000L,
            _ => true,
        };
        if (!keep) { await RemoveAsync(id); return false; }
        await UpdateAsync(id, t => t.Closed = true);
        return true;
    }

    public async Task RemoveAsync(long id)
    {
        if (_running.TryRemove(id, out var running)) await StopAsync(running);
        _store.DeleteTarget(id);
        TargetsChanged?.Invoke();
    }

    /// <summary>Registers a target that was created outside <see cref="Add"/> (an imported session).</summary>
    public void Adopt(Target target) { Launch(target); TargetsChanged?.Invoke(); }

    private void Launch(Target target, TraceSession? existing = null)
    {
        var session = existing ?? new TraceSession(target, _store, _prober, () => _settings, () => ConfigFor(target));
        if (existing is null) session.SampleTaken += (s, sample, info) => SampleTaken?.Invoke(s, sample, info);
        var stop = new CancellationTokenSource();
        var loop = ShouldRun(target) ? Task.Run(() => LoopAsync(session, stop.Token)) : Task.CompletedTask;
        _running[target.Id] = new Running(session, stop, loop);
    }

    private async Task LoopAsync(TraceSession session, CancellationToken ct)
    {
        // Rounds start on schedule even if the previous one is still waiting for late replies,
        // so a slow or lossy route does not stretch the sampling interval.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(session.Target.IntervalMs), _clock);
        var inFlight = new List<Task>();
        try
        {
            do
            {
                inFlight.RemoveAll(t => t.IsCompleted);
                if (inFlight.Count < 8) inFlight.Add(RoundSafeAsync(session, ct));
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        await Task.WhenAll(inFlight);
        session.Flush();
    }

    private async Task RoundSafeAsync(TraceSession session, CancellationToken ct)
    {
        try { await session.RoundAsync(Now, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Round failed for {session.Target.Input}: {ex}"); }
    }

    private static async Task StopAsync(Running running)
    {
        running.Stop.Cancel();
        try { await running.Loop; } catch (OperationCanceledException) { }
        running.Stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _running.Keys.ToList())
            if (_running.TryRemove(id, out var running)) await StopAsync(running);
    }
}

/// <summary>
/// Names and owners for hop addresses. Lookups run in the background and are remembered in the database,
/// so the UI simply shows whatever is known so far.
/// </summary>
public sealed class HostDirectory(Store store, Func<AppSettings> settings)
{
    private readonly ConcurrentDictionary<string, HostInfo> _known = new();
    private readonly ConcurrentDictionary<string, bool> _pending = new();
    private readonly ConcurrentDictionary<string, string> _asNames = new();
    private readonly SemaphoreSlim _gate = new(4);
    private const long StaleMs = 7L * 24 * 3600 * 1000;

    public HostInfo? Get(string? ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var address)) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_known.TryGetValue(ip, out var info) && store.Host(ip) is { } saved) _known[ip] = info = saved;
        if ((info is null || now - info.UpdatedMs > StaleMs) && _pending.TryAdd(ip, true))
            _ = Task.Run(() => LookupAsync(ip, address));
        return info;
    }

    /// <summary>Gives an address a name of the user's choosing (empty removes it).</summary>
    public void SetAlias(string ip, string alias)
    {
        var info = Get(ip) ?? new HostInfo { Ip = ip };
        info.Alias = alias.Trim();
        _known[ip] = info;
        store.SaveHost(info);
    }

    /// <summary>Keeps a note about an address (empty removes it).</summary>
    public void SetNotes(string ip, string notes)
    {
        var info = Get(ip) ?? new HostInfo { Ip = ip };
        info.Notes = notes.Trim();
        _known[ip] = info;
        store.SaveHost(info);
    }

    /// <summary>Looks the name and owner of an address up again now, instead of when the stored answer is a week old.</summary>
    public void Refresh(string ip)
    {
        Get(ip);   // loads what is stored, so that the name and notes the user gave survive the new lookup
        if (IPAddress.TryParse(ip, out var address) && _pending.TryAdd(ip, true)) _ = Task.Run(() => LookupAsync(ip, address));
    }

    private async Task LookupAsync(string ip, IPAddress address)
    {
        await _gate.WaitAsync();
        try
        {
            var cfg = settings();
            var info = new HostInfo { Ip = ip, UpdatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Alias = _known.TryGetValue(ip, out var old) ? old.Alias : "" };
            if (cfg.ReverseDns)
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromSeconds(5));
                    if (!string.Equals(entry.HostName, ip, StringComparison.OrdinalIgnoreCase)) info.Name = entry.HostName;
                }
                catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException) { }
            }
            if (LocalOwner(address) is { } local) info.Owner = local;
            else if (cfg.OwnerLookup) await LookupOwnerAsync(address, info);
            // Put what was found into the entry that is already there, so a name or note the user saves meanwhile is not lost.
            if (_known.TryGetValue(ip, out var latest))
            {
                latest.Name = info.Name; latest.Asn = info.Asn; latest.Owner = info.Owner; latest.UpdatedMs = info.UpdatedMs;
                info = latest;
            }
            _known[ip] = info;
            store.SaveHost(info);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Host lookup failed for {ip}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            _pending.TryRemove(ip, out _);
        }
    }

    /// <summary>Labels for address ranges that never appear in public routing tables.</summary>
    public static string? LocalOwner(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return "This computer";
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (a.IsIPv6LinkLocal) return "Local network";
            var first = a.GetAddressBytes()[0];
            return (first & 0xFE) == 0xFC ? "Private network" : null;
        }
        var b = a.GetAddressBytes();
        return b[0] switch
        {
            10 => "Private network",
            172 when b[1] is >= 16 and <= 31 => "Private network",
            192 when b[1] == 168 => "Private network",
            169 when b[1] == 254 => "Local network",
            100 when b[1] is >= 64 and <= 127 => "Carrier-grade NAT (your ISP)",
            _ => null,
        };
    }

    /// <summary>The Team Cymru lookup name for an address, e.g. 4.3.2.1.origin.asn.cymru.com.</summary>
    public static string OriginQueryName(IPAddress a)
    {
        var bytes = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
            return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.origin.asn.cymru.com";
        var nibbles = bytes.Reverse().SelectMany(x => new[] { (x & 0xF).ToString("x"), (x >> 4).ToString("x") });
        return string.Join('.', nibbles) + ".origin6.asn.cymru.com";
    }

    private async Task LookupOwnerAsync(IPAddress address, HostInfo info)
    {
        foreach (var server in MiniDns.SystemServers().Take(2))
        {
            var origin = await MiniDns.QueryAsync(server, OriginQueryName(address), MiniDns.TypeTxt, 4000, default);
            if (origin is null) continue;
            if (origin.Texts.Count == 0) return;
            // "15169 | 8.8.8.0/24 | US | arin | 2023-12-28"
            var asn = origin.Texts[0].Split('|')[0].Trim().Split(' ')[0];
            if (asn.Length == 0) return;
            info.Asn = "AS" + asn;
            if (!_asNames.TryGetValue(asn, out var name))
            {
                // "15169 | US | arin | 2000-03-30 | GOOGLE, US"
                var detail = await MiniDns.QueryAsync(server, $"AS{asn}.asn.cymru.com", MiniDns.TypeTxt, 4000, default);
                name = detail?.Texts.FirstOrDefault()?.Split('|').LastOrDefault()?.Trim() ?? "";
                if (name.Length > 0) _asNames[asn] = name;
            }
            info.Owner = name;
            return;
        }
    }
}
