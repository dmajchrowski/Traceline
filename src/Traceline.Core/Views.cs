namespace Traceline.Core;

public sealed record HopStat(int Hop, string? Ip, string? Name, string? Asn, string? Owner, int Count, int Lost,
    double? Avg, double? Min, double? Max, double? Cur, double? Jitter, double? LossPct, double? Mos, string? DnsName = null, string? Notes = null);

/// <summary>Bucketed history for one hop (hop 0 = the destination). Arrays are index-aligned; null = no data in that bucket.</summary>
public sealed record Timeline(int Hop, long From, long BucketMs, double?[] Min, double?[] Avg, double?[] Max, double?[] Loss, double?[] Jitter);

public sealed record TargetView(long From, long To, long FocusFrom, long FocusTo, bool Coarse,
    List<HopStat> Hops, List<Timeline> Timelines, List<TimelineEvent> Events, Diagnosis Diagnosis, TraceConfig Config);

/// <summary>Turns stored samples into hop statistics and timelines for any time range.</summary>
public sealed class ViewBuilder(Store store, HostDirectory hosts, Func<long, TraceConfig> configFor)
{
    /// <summary>Ranges up to this long are read sample by sample; longer ones use the per-minute roll-ups.</summary>
    public const long RawLimitMs = 6 * 3600 * 1000;

    /// <summary>
    /// How many timeline buckets suit a period: no more than one per sample (or per minute for long ranges),
    /// so that a line through the averages has no holes that are not real gaps.
    /// </summary>
    public static int BucketsFor(long from, long to, int intervalMs, int max = 600)
    {
        var perBucket = to - from > RawLimitMs ? Math.Max(60_000, intervalMs) : Math.Max(1, intervalMs);
        return (int)Math.Clamp((to - from + perBucket - 1) / perBucket, 20, max);
    }

    private sealed class Pass(int[] hops, long from, long to, int buckets)
    {
        public Agg[] Total = [];
        public readonly Agg[][] Series = hops.Select(_ => new Agg[buckets]).ToArray();
        public long RouteId;
        public int Length;

        private int Bucket(long ts) => (int)Math.Clamp((ts - from) * buckets / Math.Max(1, to - from), 0, buckets - 1);

        public void Add(Sample s)
        {
            var needed = s.FinalOnly ? 1 : s.Rtts.Length + 1;
            if (Total.Length < needed) Array.Resize(ref Total, needed);
            Total[0].Add(s.Rtts[^1]);
            if (!s.FinalOnly) for (var i = 0; i < s.Rtts.Length; i++) Total[i + 1].Add(s.Rtts[i]);
            RouteId = s.RouteId; Length = s.FinalOnly ? 0 : s.Rtts.Length;
            if (buckets == 0) return;
            var b = Bucket(s.Ts);
            for (var h = 0; h < hops.Length; h++)
            {
                if (hops[h] == 0) Series[h][b].Add(s.Rtts[^1]);
                else if (!s.FinalOnly && hops[h] <= s.Rtts.Length) Series[h][b].Add(s.Rtts[hops[h] - 1]);
            }
        }

        public void Add(RollupRow r)
        {
            if (Total.Length < r.Aggs.Length) Array.Resize(ref Total, r.Aggs.Length);
            for (var i = 0; i < r.Aggs.Length; i++) Total[i].Merge(r.Aggs[i]);
            RouteId = r.RouteId; Length = r.Aggs.Length - 1;
            if (buckets == 0) return;
            var b = Bucket(r.Ts);
            for (var h = 0; h < hops.Length; h++)
                if (hops[h] < r.Aggs.Length) Series[h][b].Merge(r.Aggs[hops[h]]);
        }
    }

    private Pass Run(long targetId, long from, long to, int[] hops, int buckets, out bool coarse)
    {
        coarse = to - from > RawLimitMs;
        var pass = new Pass(hops, from, to, buckets);
        if (!coarse)
        {
            foreach (var sample in store.Samples(targetId, from, to)) pass.Add(sample);
            return pass;
        }
        var covered = from;
        foreach (var row in store.Rollups(targetId, from / 60_000 * 60_000, to)) { pass.Add(row); covered = row.Ts + 60_000; }
        // The minute in progress has no summary yet; fill the live edge from the samples themselves.
        foreach (var sample in store.Samples(targetId, Math.Max(covered, to - 600_000), to)) pass.Add(sample);
        return pass;
    }

    /// <summary>Statistics per hop for a period; the last entry is the destination.</summary>
    public List<HopStat> Stats(long targetId, long from, long to) => ToStats(Run(targetId, from, to, [], 0, out var coarse), coarse);

    private List<HopStat> ToStats(Pass pass, bool coarse)
    {
        var list = new List<HopStat>();
        if (pass.Total.Length == 0) return list;
        var route = store.Route(pass.RouteId);
        // Destination-only data has no per-hop rows: show just the destination.
        var length = pass.Length;
        for (var hop = 1; hop <= Math.Max(length, 1); hop++)
        {
            var isDestination = hop >= length;
            var agg = isDestination ? pass.Total[0] : pass.Total[hop];
            var ip = isDestination ? route.LastOrDefault() : hop - 1 < route.Length ? route[hop - 1] : null;
            var host = hosts.Get(ip);
            list.Add(new HopStat(hop, ip, Blank(host?.Alias) ?? Blank(host?.Name), Blank(host?.Asn), Blank(host?.Owner), agg.N, agg.Lost,
                R(agg.Avg), agg.Received > 0 ? R(agg.Min) : null, agg.Received > 0 ? R(agg.Max) : null,
                coarse ? null : R(agg.Last), R(agg.Jitter), R(agg.LossPct), R(agg.Mos), Blank(host?.Name), Blank(host?.Notes)));
        }
        return list;
    }

    public TargetView Build(long targetId, long from, long to, long focusFrom, long focusTo, int[] hops, int buckets)
    {
        buckets = Math.Clamp(buckets, 1, 2000);
        var config = configFor(targetId);
        var windowPass = Run(targetId, from, to, hops, buckets, out var coarse);
        var bucketMs = Math.Max(1, (to - from) / buckets);
        var timelines = new List<Timeline>();
        for (var h = 0; h < hops.Length; h++)
        {
            var series = windowPass.Series[h];
            timelines.Add(new Timeline(hops[h], from, bucketMs,
                series.Select(a => a.Received > 0 ? R(a.Min) : null).ToArray(),
                series.Select(a => R(a.Avg)).ToArray(),
                series.Select(a => a.Received > 0 ? R(a.Max) : null).ToArray(),
                series.Select(a => a.N > 0 ? R(a.LossPct) : null).ToArray(),
                series.Select(a => a.JN > 0 ? R(a.Jitter) : null).ToArray()));
        }
        var sameRange = focusFrom == from && focusTo == to;
        var focusCoarse = coarse;
        var stats = ToStats(sameRange ? windowPass : Run(targetId, focusFrom, focusTo, [], 0, out focusCoarse), sameRange ? coarse : focusCoarse);
        return new TargetView(from, to, focusFrom, focusTo, coarse, stats, timelines,
            store.Events(targetId, from, to), Diagnosis.From(stats, config.WarnMs, config.BadMs), config);
    }

    private static double? R(double v) => double.IsNaN(v) ? null : Math.Round(v, 2);
    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
