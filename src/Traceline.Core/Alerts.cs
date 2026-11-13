using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Traceline.Core.Notifications;

namespace Traceline.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ConditionKind>))]
public enum ConditionKind { OverTime, OverSamples, MosBelow, RouteChanged, IpInRoute, Timer }

/// <summary>
/// One alert: a condition on a target's destination or on one hop, when to tell someone, and what to do.
/// A sample is "bad" when it was lost or slower than <see cref="LatencyMs"/> (0 = only lost samples count).
/// </summary>
public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ConditionKind Kind { get; set; }
    /// <summary>What is watched: "" = the destination, "hop:N" = hop number N, "ip:a.b.c.d" = that address wherever it is in the route.</summary>
    public string Watch { get; set; } = "";
    public double LatencyMs { get; set; }
    /// <summary>OverTime and MosBelow: the period looked at. OverTime fires when at least <see cref="Percent"/> of its samples are bad.</summary>
    public int WindowSeconds { get; set; } = 600;
    public double Percent { get; set; } = 10;
    /// <summary>OverSamples: fires when at least <see cref="BadCount"/> of the last <see cref="SampleCount"/> samples are bad.</summary>
    public int SampleCount { get; set; } = 10;
    public int BadCount { get; set; } = 10;
    public double Mos { get; set; } = 3.5;
    public string Ip { get; set; } = "";
    public int TimerMinutes { get; set; } = 1440;
    /// <summary>Empty = every target.</summary>
    public List<long> TargetIds { get; set; } = [];

    // When to act
    public bool NotifyOnStart { get; set; } = true;
    public bool NotifyOnEnd { get; set; } = true;
    /// <summary>While the condition lasts, act again this often (0 = never).</summary>
    public int RepeatMinutes { get; set; }
    /// <summary>Act on every sample while the condition is met.</summary>
    public bool EachTime { get; set; }
    /// <summary>Act on every sample while the condition is not met.</summary>
    public bool EachTimeNotMet { get; set; }

    // What to do
    /// <summary>Empty = every enabled channel. "none" = no channel.</summary>
    public List<string> ChannelIds { get; set; } = [];
    public bool AddComment { get; set; } = true;
    public string LogFile { get; set; } = "";
    /// <summary>Optional message text; see <see cref="AlertService.Variables"/> for the {{names}} that are filled in.</summary>
    public string Message { get; set; } = "";
    /// <summary>Optional subject line for email (and title elsewhere); the same {{names}} are filled in. Empty = the standard one.</summary>
    public string Subject { get; set; } = "";
    /// <summary>Send at most one notification per this many minutes for a target (0 = no limit). The other actions are not held back.</summary>
    public int MinMinutesBetween { get; set; }
    /// <summary>Attach a picture of the trace, or the recent samples as CSV, to email notifications.</summary>
    public bool AttachImage { get; set; }
    public bool AttachData { get; set; }
    /// <summary>Folders to save a picture of the trace or the recent samples (CSV) into. Variables are allowed.</summary>
    public string SaveImageFolder { get; set; } = "";
    public string ExportDataFolder { get; set; } = "";
    /// <summary>Program to start, with arguments. Only runs when "allow alerts to run programs" is on in Settings.</summary>
    public string RunProgram { get; set; } = "";
    public string RunArguments { get; set; } = "";
    /// <summary>"add" or "remove": put the target on (or take it off) a summary screen while the condition lasts.</summary>
    public string SummaryAction { get; set; } = "";
    public string SummaryId { get; set; } = "";
    /// <summary>"full" or "final": switch the target to full-route or destination-only tracing while the condition lasts.</summary>
    public string RouteAction { get; set; } = "";
    /// <summary>Sound played in open browsers and by the tray icon when the alert starts: a name from the sound list, "" for the default beep, "none" for silence.</summary>
    public string Sound { get; set; } = "";

    /// <summary>True for conditions that are a moment rather than a state, so they have no "ended" notification.</summary>
    [JsonIgnore] public bool IsPulse => Kind is ConditionKind.RouteChanged or ConditionKind.Timer;

    public bool AppliesTo(long targetId) => TargetIds.Count == 0 || TargetIds.Contains(targetId);

    private bool Bad(float rtt) => float.IsNaN(rtt) || (LatencyMs > 0 && rtt > LatencyMs);

    private string BadText => LatencyMs > 0 ? $"lost or slower than {LatencyMs:0} ms" : "lost";

    /// <summary>
    /// Whether the condition holds, given the watched hop's recent samples (oldest first) and the latest round.
    /// Timers are handled by the caller because they depend on when the alert last fired.
    /// </summary>
    public bool IsActive(IReadOnlyList<(long Ts, float Rtt)> recent, RoundInfo info, long now, out string reason)
    {
        reason = "";
        switch (Kind)
        {
            case ConditionKind.OverTime:
            {
                var window = recent.Where(r => r.Ts > now - WindowSeconds * 1000L).ToList();
                if (window.Count < 5) return false;
                var bad = window.Count(r => Bad(r.Rtt));
                var pct = 100.0 * bad / window.Count;
                reason = $"{pct:0.#}% of samples in the last {Span(WindowSeconds)} were {BadText} ({bad} of {window.Count})";
                return pct >= Percent;
            }
            case ConditionKind.OverSamples:
            {
                if (recent.Count < SampleCount) return false;
                var bad = recent.Skip(recent.Count - SampleCount).Count(r => Bad(r.Rtt));
                reason = $"{bad} of the last {SampleCount} samples were {BadText}";
                return bad >= BadCount;
            }
            case ConditionKind.MosBelow:
            {
                var agg = new Agg();
                foreach (var r in recent.Where(r => r.Ts > now - WindowSeconds * 1000L)) agg.Add(r.Rtt);
                if (agg.N < 5) return false;
                reason = $"voice quality (MOS) was {agg.Mos:0.0} over the last {Span(WindowSeconds)}";
                return agg.Mos < Mos;
            }
            case ConditionKind.RouteChanged:
                reason = info.ChangeText ?? "the route changed";
                return info.RouteChanged;
            case ConditionKind.IpInRoute:
                reason = $"{Ip} is in the route";
                return Ip.Length > 0 && info.Route.Contains(Ip.Trim());
            default:
                return false;
        }
    }

    private static string Span(int seconds) => seconds >= 120 ? $"{seconds / 60} minutes" : $"{seconds} seconds";
}

public sealed record DeliveryRecord(long Ts, string Channel, bool Ok, string Text);

public sealed record AlertFired(long Ts, long TargetId, string Target, string Rule, string State, string Text, string Sound);

/// <summary>Checks every alert after each sample and carries out its actions.</summary>
public sealed class AlertService
{
    private sealed class State { public bool Active; public long LastNotify; public long LastTimer; public long LastSent; public int Count; }

    // A notification about a network problem often cannot get out while the problem lasts, so keep trying for about an hour and a half.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(30), .. Enumerable.Repeat(TimeSpan.FromMinutes(1), 5), .. Enumerable.Repeat(TimeSpan.FromMinutes(5), 5), .. Enumerable.Repeat(TimeSpan.FromMinutes(15), 4)];

    /// <summary>Names that can be written as {{Name}} in message text, file paths and program arguments.</summary>
    public static readonly string[] Variables =
        ["Target", "Address", "Host", "HostIP", "AlertName", "State", "Reason", "Avg", "Loss", "Diagnosis", "ConfigName", "Count", "Now", "UtcNow", "Date", "Time"];

    private readonly TraceEngine _engine;
    private readonly ViewBuilder _views;
    private readonly ChannelSender _sender;
    private readonly Reports _reports;
    private readonly ConcurrentDictionary<(string, long), State> _states = new();
    private readonly ConcurrentQueue<DeliveryRecord> _deliveries = new();
    private List<AlertRule> _rules;
    private List<ChannelConfig> _channels;

    public event Action<AlertFired>? Fired;

    public AlertService(TraceEngine engine, ViewBuilder views, ChannelSender sender, Reports reports)
    {
        _engine = engine; _views = views; _sender = sender; _reports = reports;
        _rules = engine.Store.Get<List<AlertRule>>("alerts");
        _channels = engine.Store.Get<List<ChannelConfig>>("channels");
        engine.SampleTaken += OnSample;
    }

    public IReadOnlyList<AlertRule> Rules => _rules;
    public IReadOnlyList<ChannelConfig> Channels => _channels;
    public IEnumerable<DeliveryRecord> Deliveries => _deliveries.Reverse();

    public void SaveRules(List<AlertRule> rules) { _engine.Store.Set("alerts", rules); _rules = rules; }

    public void SaveChannels(List<ChannelConfig> channels)
    {
        // Secrets are encrypted before they are written; values that are already encrypted pass through unchanged.
        foreach (var c in channels) c.TransformSecrets(s => SecretProtector.IsProtected(s) ? s : SecretProtector.Protect(s));
        _engine.Store.Set("channels", channels);
        _channels = channels;
    }

    public bool IsActive(string ruleId, long targetId) => _states.TryGetValue((ruleId, targetId), out var s) && s.Active;

    /// <summary>The sound of the first alert that is active on a target, or null when none is.</summary>
    public string? ActiveSound(long targetId) => _rules.FirstOrDefault(r => IsActive(r.Id, targetId))?.Sound;

    private void OnSample(TraceSession session, Sample sample, RoundInfo info)
    {
        var now = sample.Ts;
        foreach (var rule in _rules)
        {
            if (!rule.Enabled || !rule.AppliesTo(session.Target.Id)) continue;
            var state = _states.GetOrAdd((rule.Id, session.Target.Id), _ => new State { LastTimer = now });
            if (rule.Kind == ConditionKind.Timer)
            {
                if (now - state.LastTimer < rule.TimerMinutes * 60_000L) continue;
                state.LastTimer = now;
                Fire(rule, session, state, now, "timer", $"scheduled report (every {rule.TimerMinutes} minutes)");
                continue;
            }
            var active = rule.IsActive(session.Recent(rule.Watch, now - 3_600_000), info, now, out var reason);
            if (rule.IsPulse)
            {
                if (active) Fire(rule, session, state, now, "start", reason);
            }
            else if (active && !state.Active)
            {
                state.Active = true; state.LastNotify = now;
                Fire(rule, session, state, now, "start", reason, notify: rule.NotifyOnStart || rule.EachTime);
            }
            else if (!active && state.Active)
            {
                state.Active = false;
                Fire(rule, session, state, now, "end", "conditions are back to normal", notify: rule.NotifyOnEnd || rule.EachTimeNotMet);
            }
            else if (active && (rule.EachTime || (rule.RepeatMinutes > 0 && now - state.LastNotify >= rule.RepeatMinutes * 60_000L)))
            {
                state.LastNotify = now;
                Fire(rule, session, state, now, "repeat", reason);
            }
            else if (!active && rule.EachTimeNotMet)
            {
                Fire(rule, session, state, now, "clear", "conditions are not met");
            }
        }
    }

    private void Fire(AlertRule rule, TraceSession session, State state, long now, string stateName, string reason, bool notify = true)
    {
        var target = session.Target;
        var count = stateName is "end" or "clear" ? state.Count : ++state.Count;
        var label = stateName switch { "end" => "ended", "repeat" => "continues", "timer" => "report", "clear" => "is clear", _ => "started" };
        var where = rule.Watch.Length == 0 ? "" : $" ({rule.Watch.Replace("hop:", "hop ").Replace("ip:", "")})";
        var line = $"{rule.Name}{where} {label}: {reason}";
        var quiet = stateName is "repeat" or "clear";
        if (rule.AddComment && !quiet) _engine.Store.AddEvent(target.Id, now, "alert", line);
        if (!quiet || !rule.EachTime && !rule.EachTimeNotMet) Fired?.Invoke(new AlertFired(now, target.Id, target.Name, rule.Name, stateName, line, rule.Sound));
        // "Send at most every N minutes": a notification that comes too soon after the last one is dropped, not queued.
        if (notify && rule.MinMinutesBetween > 0 && state.LastSent > 0 && now - state.LastSent < rule.MinMinutesBetween * 60_000L) notify = false;
        if (notify) state.LastSent = now;
        _ = Task.Run(() => ActAsync(rule, session, now, stateName, label, reason, count, notify));
    }

    private async Task ActAsync(AlertRule rule, TraceSession session, long now, string stateName, string label, string reason, int count, bool notify)
    {
        try
        {
            var target = session.Target;
            var stats = _views.Stats(target.Id, now - 600_000, now + 1);
            var cfg = session.Config;
            var diagnosis = Diagnosis.From(stats, cfg.WarnMs, cfg.BadMs);
            var watched = rule.Watch.StartsWith("hop:", StringComparison.Ordinal) && int.TryParse(rule.Watch.AsSpan(4), out var hopNumber) ? stats.FirstOrDefault(h => h.Hop == hopNumber)
                : rule.Watch.StartsWith("ip:", StringComparison.Ordinal) ? stats.FirstOrDefault(h => h.Ip == rule.Watch[3..]) : stats.LastOrDefault();
            var avg = watched?.Avg is double a ? $"{a:0.#} ms" : "n/a";
            var loss = watched?.LossPct is double l ? $"{l:0.#}%" : "n/a";
            var local = DateTimeOffset.FromUnixTimeMilliseconds(now).ToLocalTime();
            var values = new Dictionary<string, string>
            {
                ["Target"] = target.Name, ["Address"] = target.Address, ["Host"] = watched?.Name ?? watched?.Ip ?? target.Name, ["HostIP"] = watched?.Ip ?? target.Address,
                ["AlertName"] = rule.Name, ["State"] = label, ["Reason"] = reason, ["Avg"] = avg, ["Loss"] = loss, ["Diagnosis"] = diagnosis.Headline,
                ["ConfigName"] = cfg.Name, ["Count"] = count.ToString(CultureInfo.InvariantCulture),
                ["Now"] = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["UtcNow"] = local.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["Date"] = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["Time"] = local.ToString("HH-mm-ss", CultureInfo.InvariantCulture),
            };
            var safeName = string.Concat(target.Name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
            var stamp = local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var starting = stateName is "start" or "repeat" or "timer";

            if (rule.LogFile.Length > 0 && stateName != "clear")
                Try("Log file", now, () =>
                {
                    var path = Render(rule.LogFile, values);
                    if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
                    File.AppendAllText(path, $"{values["Now"]}\t{target.Name}\t{target.Address}\t{rule.Name}\t{label}\t{reason}{Environment.NewLine}");
                });

            // A picture and the data are made at most once and shared by "save" and "attach".
            byte[]? image = null, data = null;
            var window = Math.Max(600_000L, rule.WindowSeconds * 1000L);
            if (starting && (rule.SaveImageFolder.Length > 0 || (rule.AttachImage && notify)) && TraceImage.Supported)
                Try("Image", now, () => image = TraceImage.Render(target, _views.Build(target.Id, now - window, now + 1, now - window, now + 1, [0], ViewBuilder.BucketsFor(now - window, now, target.IntervalMs))));
            if (starting && (rule.ExportDataFolder.Length > 0 || (rule.AttachData && notify)))
                Try("Data export", now, () => data = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(_reports.SamplesCsv(target.Id, now - 3_600_000, now + 1, false))).ToArray());
            if (image is not null && rule.SaveImageFolder.Length > 0)
                Try("Save image", now, () => Save(Render(rule.SaveImageFolder, values), $"{safeName}-{stamp}.png", image));
            if (data is not null && rule.ExportDataFolder.Length > 0)
                Try("Export data", now, () => Save(Render(rule.ExportDataFolder, values), $"{safeName}-{stamp}.csv", data));

            if (rule.RunProgram.Length > 0 && stateName != "clear")
            {
                if (!_engine.Settings.AllowRunProgram) Record(now, "Run program", false, "Not started: turn on \"Allow alerts to run programs\" in Settings first.");
                else Try("Run program", now, () =>
                {
                    Process.Start(new ProcessStartInfo(Render(rule.RunProgram, values), Render(rule.RunArguments, values)) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                    Record(now, "Run program", true, $"Started {rule.RunProgram}");
                });
            }

            if (rule.SummaryAction is "add" or "remove" && stateName is "start" or "end")
                Try("Summary", now, () =>
                {
                    var summaries = _engine.Store.Get<List<SummaryDef>>("summaries");
                    if (summaries.FirstOrDefault(s => s.Id == rule.SummaryId) is not { } summary) return;
                    var item = new SummaryItem(target.Id, 0);
                    var add = (rule.SummaryAction == "add") == (stateName == "start");
                    summary.Items.Remove(item);
                    if (add) summary.Items.Add(item);
                    _engine.Store.Set("summaries", summaries);
                });

            if (rule.RouteAction is "full" or "final" && stateName is "start" or "end" && target.Kind == ProbeKind.Icmp)
            {
                var finalOnly = (rule.RouteAction == "final") == (stateName == "start");
                if (target.FinalHopOnly != finalOnly) await _engine.UpdateAsync(target.Id, t => t.FinalHopOnly = finalOnly);
            }

            if (!notify || rule.ChannelIds.Contains("none")) return;
            var notification = new Notification
            {
                Kind = stateName switch { "end" or "clear" => NotificationKind.Recovery, "repeat" => NotificationKind.Reminder, "timer" => NotificationKind.Info, _ => NotificationKind.Alert },
                Severity = stateName switch { "end" or "clear" => Severity.Good, "timer" => Severity.Info, _ => diagnosis.Severity == "bad" ? Severity.Critical : Severity.Warning },
                Title = $"{target.Name}: {rule.Name}{(stateName == "end" ? " ended" : "")}",
                CustomSubject = rule.Subject.Trim().Length > 0 ? Render(rule.Subject.Trim(), values) : null,
                Summary = rule.Message.Length > 0 ? Render(rule.Message, values) : char.ToUpperInvariant(reason[0]) + reason[1..] + ".",
                SiteName = _engine.Settings.SiteName,
                ServiceName = target.Name,
                Time = local,
            };
            notification.AddFact("Target", target.Address.Length > 0 && target.Address != target.Name ? $"{target.Name} ({target.Address})" : target.Name)
                .AddFact("Watching", rule.Watch.Length == 0 ? null : $"{values["Host"]} ({rule.Watch.Replace("hop:", "hop ").Replace("ip:", "")})")
                .AddFact("Last 10 minutes", $"average {avg}, loss {loss}")
                .AddFact("Diagnosis", diagnosis.Headline);
            if (diagnosis.Details.Count > 0) notification.AddFact("Details", string.Join(" ", diagnosis.Details));
            if (rule.AttachImage && image is not null) notification.Attachments.Add(new NotificationAttachment($"{safeName}-{stamp}.png", "image/png", image));
            if (rule.AttachData && data is not null) notification.Attachments.Add(new NotificationAttachment($"{safeName}-{stamp}.csv", "text/csv", data));

            var channels = _channels.Where(c => c.Enabled && (rule.ChannelIds.Count == 0 || rule.ChannelIds.Contains(c.Id))).ToList();
            if (channels.Count == 0 && stateName is "start" or "end") Record(now, "(none)", false, $"No notification channel is set up, so nobody was told: {notification.Title}");
            await Task.WhenAll(channels.Select(c => DeliverAsync(c, notification)));
        }
        catch (Exception ex)
        {
            Record(now, "(internal)", false, ex.Message);
        }
    }

    private void Try(string what, long now, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or ExternalException)
        {
            Record(now, what, false, ex.Message);
        }
    }

    private static void Save(string folder, string name, byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
    }

    private async Task DeliverAsync(ChannelConfig channel, Notification notification)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = await _sender.SendAsync(channel, notification, CancellationToken.None);
                Record(_engine.Now, channel.Name, true, $"{text}: {notification.Title}");
                return;
            }
            catch (DeliveryException ex)
            {
                var retry = ex.Retryable && attempt < RetryDelays.Length;
                Record(_engine.Now, channel.Name, false, ex.Message + (retry ? $" Trying again in {RetryDelays[attempt].TotalSeconds:0} s." : ""));
                if (!retry) return;
                await Task.Delay(RetryDelays[attempt]);
            }
            catch (Exception ex)
            {
                Record(_engine.Now, channel.Name, false, ex.Message);
                return;
            }
        }
    }

    /// <summary>Sends a test message through one channel and returns what happened; throws <see cref="DeliveryException"/> on failure.</summary>
    public Task<string> TestAsync(ChannelConfig channel) => _sender.SendAsync(channel, new Notification
    {
        Kind = NotificationKind.Test, Severity = Severity.Info, Title = "Test message",
        Summary = "If you can read this, Traceline can reach you through this channel.", SiteName = _engine.Settings.SiteName,
    }, CancellationToken.None);

    private void Record(long ts, string channel, bool ok, string text)
    {
        _deliveries.Enqueue(new DeliveryRecord(ts, channel, ok, text));
        while (_deliveries.Count > 200) _deliveries.TryDequeue(out _);
    }

    public static string Render(string template, Dictionary<string, string> values)
    {
        foreach (var (key, value) in values) template = template.Replace("{{" + key + "}}", value, StringComparison.OrdinalIgnoreCase);
        return template;
    }
}
