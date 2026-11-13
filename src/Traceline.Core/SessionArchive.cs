using System.IO.Compression;
using System.Text.Json;

namespace Traceline.Core;

/// <summary>
/// A recorded session as one file (compressed JSON, ".traceline"), for handing to someone else or keeping
/// outside the database. Importing it creates a closed target that can be viewed or reopened.
/// </summary>
public static class SessionArchive
{
    private sealed class Doc
    {
        public string Format { get; set; } = "traceline-session";
        public int Version { get; set; } = 1;
        public Target Target { get; set; } = new();
        public List<string?[]> Routes { get; set; } = [];
        public List<Row> Samples { get; set; } = [];
        public List<Note> Events { get; set; } = [];
    }

    /// <summary>T = time, R = index into Routes, F = destination-only, V = round-trip times (null = lost).</summary>
    private sealed class Row
    {
        public long T { get; set; }
        public int R { get; set; }
        public bool F { get; set; }
        public float?[] V { get; set; } = [];
    }

    private sealed record Note(long Ts, string Kind, string Text);

    public static byte[] Export(Store store, Target target, long from = 0, long to = long.MaxValue)
    {
        var doc = new Doc { Target = target };
        var routeIndex = new Dictionary<long, int>();
        foreach (var s in store.Samples(target.Id, from, to))
        {
            if (!routeIndex.TryGetValue(s.RouteId, out var index))
            {
                routeIndex[s.RouteId] = index = doc.Routes.Count;
                doc.Routes.Add(store.Route(s.RouteId));
            }
            doc.Samples.Add(new Row { T = s.Ts, R = index, F = s.FinalOnly, V = s.Rtts.Select(v => float.IsNaN(v) ? (float?)null : v).ToArray() });
        }
        doc.Events = store.Events(target.Id, from, to, int.MaxValue).Select(e => new Note(e.Ts, e.Kind, e.Text)).ToList();
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gzip, doc, Store.Json);
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a comma-separated text export in the layout other trace tools write ("one column per hop, one row
    /// per sample", with sample times): a "Host Information" block of hop, name, address lines and a
    /// "Sample Information" block of time, value, value... lines. Anything that is not a number counts as lost.
    /// </summary>
    public static Target ImportText(Store store, string text, string workspaceId)
    {
        var lines = text.Split('\n').Select(l => l.Trim('\r', ' ', '\uFEFF')).ToList();
        int hostsAt = lines.FindIndex(l => l.StartsWith("Host Information", StringComparison.OrdinalIgnoreCase)),
            samplesAt = lines.FindIndex(l => l.StartsWith("Sample Information", StringComparison.OrdinalIgnoreCase));
        if (hostsAt < 0 || samplesAt < hostsAt) throw new ArgumentException("That is not a text export in the \"one column per hop, one row per sample\" layout.");
        var route = new List<string?>();
        var names = new List<string>();
        foreach (var line in lines.Skip(hostsAt + 1).Take(samplesAt - hostsAt - 1))
        {
            var parts = line.Split(',');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var hop) || hop != route.Count + 1) continue;
            var ip = parts.Skip(1).Select(p => p.Trim()).FirstOrDefault(p => System.Net.IPAddress.TryParse(p, out _) && p.Contains('.') | p.Contains(':'));
            route.Add(ip);
            names.Add(parts.Skip(1).Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0 && p != ip) ?? ip ?? "");
        }
        if (route.Count == 0) throw new ArgumentException("The export lists no hops.");
        var rows = new List<Sample>();
        foreach (var line in lines.Skip(samplesAt + 1))
        {
            if (line.Length < 3 || !line.StartsWith('"')) continue;
            var quote = line.IndexOf('"', 1);
            if (quote < 0) continue;
            var stamp = line[1..quote];
            if (!DateTime.TryParse(stamp, System.Globalization.CultureInfo.CurrentCulture, out var time)
                && !DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, out time)
                && !DateTime.TryParse(stamp, System.Globalization.CultureInfo.GetCultureInfo("en-US"), out time)) continue;
            var values = line[(quote + 1)..].TrimStart(',').Split(',');
            var rtts = new float[route.Count];
            for (var i = 0; i < rtts.Length; i++)
                rtts[i] = i < values.Length && float.TryParse(values[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : float.NaN;
            rows.Add(new Sample(new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local)).ToUnixTimeMilliseconds(), 0, false, rtts));
        }
        if (rows.Count == 0) throw new ArgumentException("The export has no samples with times. Export again with \"Include sample times\" turned on.");

        var host = route[^1] ?? names[^1];
        var target = new Target
        {
            Input = host, Host = host, Address = route[^1] ?? "", Name = $"{(names[^1].Length > 0 ? names[^1] : host)} (imported)",
            Kind = ProbeKind.Icmp, Closed = true, Keep = true, WorkspaceId = workspaceId, CreatedMs = rows[0].Ts,
        };
        store.SaveTarget(target);
        store.Batch(() =>
        {
            var routeId = store.RouteId(target.Id, [.. route]);
            long minute = -1;
            Agg[] aggs = [];
            void Flush() { if (minute >= 0) store.SaveRollup(target.Id, minute, routeId, aggs); }
            foreach (var row in rows.OrderBy(r => r.Ts))
            {
                store.AddSample(target.Id, row with { RouteId = routeId });
                var m = row.Ts / 60_000 * 60_000;
                if (m != minute) { Flush(); minute = m; aggs = new Agg[route.Count + 1]; }
                aggs[0].Add(row.Rtts[^1]);
                for (var i = 0; i < row.Rtts.Length; i++) aggs[i + 1].Add(row.Rtts[i]);
            }
            Flush();
        });
        return target;
    }

    public static Target Import(Store store, Stream file, string workspaceId)
    {
        Doc? doc;
        try
        {
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            doc = JsonSerializer.Deserialize<Doc>(gzip, Store.Json);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new ArgumentException("That file is not a Traceline session.");
        }
        if (doc is not { Format: "traceline-session" }) throw new ArgumentException("That file is not a Traceline session.");

        var target = doc.Target;
        target.Id = 0; target.Closed = true; target.Paused = false; target.Keep = true; target.ConfigId = ""; target.WorkspaceId = workspaceId;
        target.Name += " (imported)";
        store.SaveTarget(target);
        store.Batch(() =>
        {
            var routeIds = doc.Routes.Select(r => store.RouteId(target.Id, r)).ToList();
            long minute = -1, lastRoute = 0;
            Agg[] aggs = [];
            void Flush() { if (minute >= 0 && aggs.Length > 0) store.SaveRollup(target.Id, minute, lastRoute, aggs); }
            foreach (var row in doc.Samples.OrderBy(r => r.T))
            {
                if (row.R < 0 || row.R >= routeIds.Count || row.V.Length == 0) continue;
                var rtts = row.V.Select(v => v ?? float.NaN).ToArray();
                store.AddSample(target.Id, new Sample(row.T, routeIds[row.R], row.F, rtts));
                var m = row.T / 60_000 * 60_000;
                if (m != minute) { Flush(); minute = m; aggs = []; }
                var needed = row.F ? 1 : rtts.Length + 1;
                if (aggs.Length < needed) Array.Resize(ref aggs, needed);
                aggs[0].Add(rtts[^1]);
                if (!row.F) for (var i = 0; i < rtts.Length; i++) aggs[i + 1].Add(rtts[i]);
                lastRoute = routeIds[row.R];
            }
            Flush();
            foreach (var e in doc.Events) store.AddEvent(target.Id, e.Ts, e.Kind, e.Text);
        });
        return target;
    }
}
