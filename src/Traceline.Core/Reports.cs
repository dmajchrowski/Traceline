using System.Globalization;
using System.Net;
using System.Text;

namespace Traceline.Core;

/// <summary>Exports: CSV, traceroute-style text, statistics over time, and a self-contained HTML report.</summary>
public sealed class Reports(Store store, ViewBuilder views)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Time(long ms, bool utc = false)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return (utc ? t : t.ToLocalTime()).ToString("yyyy-MM-dd HH:mm:ss", Inv);
    }

    private static string Num(double? v, string format = "0.##") => v is double d ? d.ToString(format, Inv) : "";

    /// <summary>One row per sample, one column per hop; empty cells are lost packets.</summary>
    public string SamplesCsv(long targetId, long from, long to, bool utc)
    {
        var samples = store.Samples(targetId, from, to);
        var width = samples.Count == 0 ? 0 : samples.Max(s => s.Rtts.Length);
        var route = samples.Count == 0 ? [] : store.Route(samples[^1].RouteId);
        var sb = new StringBuilder("Time");
        for (var i = 0; i < width; i++) sb.Append(Inv, $",Hop {i + 1}{(i < route.Length && route[i] is { } ip ? $" ({ip})" : "")}");
        sb.AppendLine();
        foreach (var s in samples)
        {
            sb.Append(Time(s.Ts, utc));
            // Destination-only samples have a single value; it belongs in the last column.
            var offset = s.FinalOnly ? width - 1 : 0;
            for (var i = 0; i < width; i++)
            {
                var j = i - offset;
                sb.Append(',');
                if (j >= 0 && j < s.Rtts.Length && !float.IsNaN(s.Rtts[j])) sb.Append(s.Rtts[j].ToString("0.##", Inv));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Destination statistics per period of <paramref name="groupMinutes"/> minutes.</summary>
    public string StatsCsv(long targetId, long from, long to, int groupMinutes, bool utc)
    {
        var step = Math.Max(1, groupMinutes) * 60_000L;
        var sb = new StringBuilder("Period start,Samples,Lost,Loss %,Average ms,Minimum ms,Maximum ms,Jitter ms,MOS\r\n");
        for (var start = from / step * step; start < to; start += step)
        {
            var dest = views.Stats(targetId, start, Math.Min(start + step, to)).LastOrDefault();
            if (dest is null || dest.Count == 0) continue;
            sb.AppendLine(string.Join(',', Time(start, utc), dest.Count, dest.Lost, Num(dest.LossPct), Num(dest.Avg), Num(dest.Min), Num(dest.Max), Num(dest.Jitter), Num(dest.Mos)));
        }
        return sb.ToString();
    }

    /// <summary>A fixed-width table in the style of a command-line traceroute.</summary>
    public string Text(Target target, long from, long to)
    {
        var stats = views.Stats(target.Id, from, to);
        var sb = new StringBuilder();
        sb.AppendLine($"Target: {target.Name} ({target.Address})");
        sb.AppendLine($"Period: {Time(from)} to {Time(to)}");
        sb.AppendLine();
        sb.AppendLine($"{"Hop",3}  {"Count",6}  {"Loss%",6}  {"Avg",8}  {"Min",8}  {"Max",8}  {"Jitter",7}  {"Address",-39}  Name");
        foreach (var h in stats)
            sb.AppendLine($"{h.Hop,3}  {h.Count,6}  {Num(h.LossPct, "0.0"),6}  {Num(h.Avg, "0.0"),8}  {Num(h.Min, "0.0"),8}  {Num(h.Max, "0.0"),8}  {Num(h.Jitter, "0.0"),7}  {h.Ip ?? "-",-39}  {h.Name ?? h.Owner ?? ""}");
        return sb.ToString();
    }

    /// <summary>A single HTML file with no outside references, so it can be emailed or opened on any computer.</summary>
    public string Html(Target target, long from, long to, int[] hops)
    {
        if (!hops.Contains(0)) hops = [.. hops, 0];
        var view = views.Build(target.Id, from, to, from, to, hops, ViewBuilder.BucketsFor(from, to, target.IntervalMs, 500));
        var e = (string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Network report: {{e(target.Name)}}</title>
            <style>
            body{font:14px/1.5 system-ui,Segoe UI,sans-serif;color:#1c1c1c;max-width:1040px;margin:24px auto;padding:0 16px}
            h1{font-size:22px;margin:0 0 4px} h2{font-size:16px;margin:28px 0 8px} .muted{color:#666}
            table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums} th,td{padding:4px 8px;border-bottom:1px solid #e3e3e3;text-align:right;white-space:nowrap}
            th:nth-child(2),td:nth-child(2),th:nth-child(3),td:nth-child(3){text-align:left;white-space:normal}
            .box{border-left:4px solid #888;background:#f6f6f6;padding:10px 14px;margin:12px 0}.bad{border-color:#c42b1c}.warn{border-color:#c26a00}.ok{border-color:#0f7b0f}
            svg{width:100%;height:auto;display:block;border:1px solid #e3e3e3}
            @media print{body{margin:0} }
            </style></head><body>
            <h1>Network report: {{e(target.Name)}}</h1>
            <div class="muted">{{e(target.Address)}} &middot; {{Time(from)}} to {{Time(to)}} (local time of the measuring computer, {{e(Environment.MachineName)}})</div>
            <div class="box {{view.Diagnosis.Severity}}"><strong>{{e(view.Diagnosis.Headline)}}</strong>
            """);
        foreach (var d in view.Diagnosis.Details) sb.Append($"<div>{e(d)}</div>");
        sb.Append("</div><h2>Route and statistics for the period</h2><table><tr><th>Hop</th><th>Address</th><th>Name / network</th><th>Sent</th><th>Loss %</th><th>Avg</th><th>Min</th><th>Max</th><th>Jitter</th></tr>");
        foreach (var h in view.Hops)
            sb.Append($"<tr><td>{h.Hop}</td><td>{e(h.Ip ?? "-")}</td><td>{e(h.Name)}{(h.Owner is null ? "" : $" <span class=\"muted\">{e(h.Owner)}</span>")}</td><td>{h.Count}</td><td>{Num(h.LossPct, "0.0")}</td><td>{Num(h.Avg, "0.0")}</td><td>{Num(h.Min, "0.0")}</td><td>{Num(h.Max, "0.0")}</td><td>{Num(h.Jitter, "0.0")}</td></tr>");
        sb.Append("</table><div class=\"muted\">Times are in milliseconds. A hop that shows loss while later hops do not is only slow to answer; it is not losing traffic.</div>");

        foreach (var t in view.Timelines.OrderBy(t => t.Hop == 0 ? int.MaxValue : t.Hop))
        {
            var hop = t.Hop == 0 ? view.Hops.LastOrDefault() : view.Hops.FirstOrDefault(h => h.Hop == t.Hop);
            sb.Append($"<h2>{(t.Hop == 0 ? "Destination" : $"Hop {t.Hop}")}: {e(hop?.Name ?? hop?.Ip ?? target.Name)}</h2>");
            sb.Append(Chart(t, view.Config));
            sb.Append("<div class=\"muted\">Black line: average latency. Grey band: minimum to maximum. Red bars from the top: packet loss.</div>");
        }

        var notes = view.Events;
        if (notes.Count > 0)
        {
            sb.Append("<h2>Notes and events</h2><table><tr><th style=\"text-align:left\">Time</th><th>What</th><th>Text</th></tr>");
            foreach (var ev in notes)
                sb.Append($"<tr><td style=\"text-align:left\">{Time(ev.Ts)}</td><td>{e(ev.Kind switch { "comment" => "Note", "route" => "Route change", "ip" => "Address", _ => "Alert" })}</td><td>{e(ev.Text)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append($"<p class=\"muted\">Made with Traceline on {Time(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}.</p></body></html>");
        return sb.ToString();
    }

    private static string Chart(Timeline t, TraceConfig cfg)
    {
        const int w = 1000, h = 180, left = 44, top = 6, bottom = 20;
        var plotW = w - left - 6.0; var plotH = h - top - bottom;
        var n = t.Avg.Length;
        var max = Math.Max(10, t.Max.Max(v => v ?? 0) * 1.1);
        double X(double i) => left + i * plotW / n;
        double Y(double ms) => top + plotH - Math.Min(ms, max) / max * plotH;
        var f = (double v) => v.ToString("0.#", Inv);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {w} {h}\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"system-ui,sans-serif\" font-size=\"11\">");
        // Background bands: good, warning, bad.
        sb.Append($"<rect x=\"{left}\" y=\"{top}\" width=\"{f(plotW)}\" height=\"{plotH}\" fill=\"#fde7e9\"/>");
        foreach (var (limit, colour) in new[] { ((double)cfg.BadMs, "#fff4ce"), (cfg.WarnMs, "#dff6dd") })
        {
            var y = Y(Math.Min(limit, max));
            sb.Append($"<rect x=\"{left}\" y=\"{f(y)}\" width=\"{f(plotW)}\" height=\"{f(top + plotH - y)}\" fill=\"{colour}\"/>");
        }
        var line = new StringBuilder();
        var pen = false;
        for (var i = 0; i < n; i++)
        {
            if (t.Loss[i] is double loss && loss > 0)
                sb.Append($"<rect x=\"{f(X(i))}\" y=\"{top}\" width=\"{f(Math.Max(1, plotW / n))}\" height=\"{f(loss / 100 * plotH)}\" fill=\"#c42b1c\" opacity=\"0.75\"/>");
            if (t.Avg[i] is not double avg) { pen = false; continue; }
            sb.Append($"<rect x=\"{f(X(i))}\" y=\"{f(Y(t.Max[i] ?? avg))}\" width=\"{f(Math.Max(1, plotW / n))}\" height=\"{f(Math.Max(1, Y(t.Min[i] ?? avg) - Y(t.Max[i] ?? avg)))}\" fill=\"#000\" opacity=\"0.18\"/>");
            line.Append(pen ? 'L' : 'M').Append(f(X(i + 0.5))).Append(' ').Append(f(Y(avg)));
            pen = true;
        }
        sb.Append($"<path d=\"{line}\" fill=\"none\" stroke=\"#111\" stroke-width=\"1.2\"/>");
        sb.Append($"<text x=\"{left - 4}\" y=\"{top + 10}\" text-anchor=\"end\">{f(max)} ms</text><text x=\"{left - 4}\" y=\"{top + plotH}\" text-anchor=\"end\">0</text>");
        for (var k = 0; k <= 4; k++)
        {
            var ts = t.From + (long)(k / 4.0 * n * t.BucketMs);
            var anchor = k == 0 ? "start" : k == 4 ? "end" : "middle";
            sb.Append($"<text x=\"{f(left + k / 4.0 * plotW)}\" y=\"{h - 5}\" text-anchor=\"{anchor}\">{DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime():MM-dd HH:mm}</text>");
        }
        return sb.Append("</svg>").ToString();
    }
}
