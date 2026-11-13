namespace Traceline.Core;

/// <summary>
/// A plain-language reading of a trace. The central rule is the one every traceroute guide teaches:
/// a problem only matters if it carries through to the destination, and it begins at the first hop
/// from which every later hop shows it too.
/// </summary>
public sealed record Diagnosis(string Severity, string Headline, List<string> Details)
{
    public static Diagnosis From(IReadOnlyList<HopStat> hops, int warnMs, int badMs)
    {
        if (hops.Count == 0) return new("info", "No data for this period", []);
        var dest = hops[^1];
        if (dest.Count < 5) return new("info", "Collecting data", ["A few more samples are needed before anything can be said."]);

        var details = new List<string>();
        var loss = dest.LossPct ?? 0;
        var single = hops.Count == 1;

        if (loss >= 100)
        {
            var lastAnswering = single ? -1 : LastIndex(hops, h => h.Count > 0 && (h.LossPct ?? 100) < 100);
            if (single)
                return new("bad", "The destination is not answering", ["Only the destination is being probed. Switch to the full route to see how far packets get."]);
            if (lastAnswering < 0)
                return new("bad", "Nothing is answering, not even the first hop",
                    ["Check this computer's own connection (cable or Wi-Fi) and that your router is on."]);
            details.Add($"Packets get as far as hop {hops[lastAnswering].Hop} ({Label(hops[lastAnswering])}) and that hop answers normally.");
            details.Add("Either the destination is down, or it (or a firewall in front of it) ignores ping. A TCP or HTTP probe to the same host tells the two apart.");
            return new("bad", "The destination is not answering", details);
        }

        var severity = "ok";
        string headline;
        if (loss >= 2)
        {
            var origin = single ? 0 : Origin(hops, h => (h.LossPct ?? 0) >= Math.Max(1, loss * 0.5));
            severity = loss >= 10 ? "bad" : "warn";
            headline = single
                ? $"{loss:0.#}% of packets to the destination are being lost"
                : $"{loss:0.#}% packet loss, starting {Place(hops, origin)}";
            if (!single) details.Add($"Every hop from {hops[origin].Hop} onward loses packets, and the hops before it do not, so the fault is at that hop or the link leading to it.");
        }
        else if ((dest.Avg ?? 0) > warnMs)
        {
            var avg = dest.Avg!.Value;
            var origin = single ? 0 : Origin(hops, h => (h.Avg ?? 0) >= avg * 0.6);
            severity = avg > badMs ? "bad" : "warn";
            headline = single
                ? $"Latency is high ({avg:0} ms on average)"
                : $"Latency is high ({avg:0} ms); most of it is added {Place(hops, origin)}";
            if (!single && origin > 0 && hops[origin - 1].Avg is double before)
                details.Add($"Hop {hops[origin - 1].Hop} answers in {before:0} ms and hop {hops[origin].Hop} in {hops[origin].Avg:0} ms. A jump like that is normal for a long-distance link (for example across an ocean) and a problem otherwise.");
        }
        else if ((dest.Jitter ?? 0) > 30)
        {
            severity = "warn";
            headline = $"Latency is unstable (jitter {dest.Jitter:0} ms)";
            details.Add("Calls and games suffer from this even when the average looks fine. Congestion or Wi-Fi interference are the usual causes.");
        }
        else
        {
            headline = "The connection looks healthy";
            details.Add($"Average {dest.Avg:0.#} ms, {loss:0.#}% loss, jitter {dest.Jitter:0.#} ms over {dest.Count} samples.");
        }

        if (dest.Mos is double mos && mos < 3.6 && severity != "ok")
            details.Add($"Voice quality estimate (MOS) is {mos:0.0} out of 4.4; below about 3.6 calls sound noticeably poor.");

        // Hops that look bad on their own while everything after them is fine.
        var harmless = hops.Take(hops.Count - 1)
            .Where((h, i) => h.Count > 0 && (h.LossPct ?? 0) is >= 5 and < 100 && (h.LossPct ?? 0) > loss + 3
                             && hops.Skip(i + 1).Any(later => (later.LossPct ?? 100) < 2))
            .Select(h => h.Hop).ToList();
        if (harmless.Count > 0)
            details.Add($"Hop{(harmless.Count > 1 ? "s" : "")} {string.Join(", ", harmless)} show{(harmless.Count > 1 ? "" : "s")} loss that later hops do not. That router is just slow to answer pings; it is not dropping your traffic.");
        if (hops.Any(h => h.Count > 0 && (h.LossPct ?? 0) >= 100) && hops.Count > 1)
            details.Add("Hops with 100% loss followed by working hops simply do not answer pings. That is normal.");

        return new(severity, headline, details);
    }

    /// <summary>The first hop from which the condition holds all the way to the destination (silent hops are skipped).</summary>
    private static int Origin(IReadOnlyList<HopStat> hops, Func<HopStat, bool> bad)
    {
        var origin = hops.Count - 1;
        for (var i = hops.Count - 2; i >= 0; i--)
        {
            var silent = hops[i].Count == 0 || (hops[i].LossPct ?? 100) >= 100;
            if (silent) continue;
            if (!bad(hops[i])) break;
            origin = i;
        }
        return origin;
    }

    private static int LastIndex(IReadOnlyList<HopStat> hops, Func<HopStat, bool> match)
    {
        for (var i = hops.Count - 1; i >= 0; i--) if (match(hops[i])) return i;
        return -1;
    }

    private static string Label(HopStat h) => h.Name ?? h.Ip ?? "unknown";

    private static string Place(IReadOnlyList<HopStat> hops, int i)
    {
        var h = hops[i];
        if (i == 0) return "at the first hop: your own router or the link to it (cable or Wi-Fi)";
        if (h.Owner is "Private network" or "Local network") return $"inside your local network (hop {h.Hop})";
        if (h.Owner?.StartsWith("Carrier-grade") == true) return $"in your internet provider's network (hop {h.Hop})";
        var providerAsn = hops.FirstOrDefault(x => x.Asn is not null)?.Asn;
        if (i == hops.Count - 1) return "at the destination or its last link";
        if (h.Asn is not null && h.Asn == providerAsn) return $"in your internet provider's network ({h.Owner}, hop {h.Hop})";
        if (h.Owner is not null) return $"in {h.Owner}'s network (hop {h.Hop}), beyond your provider";
        return $"at hop {h.Hop} ({Label(h)})";
    }
}
