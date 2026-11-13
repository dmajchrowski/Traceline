namespace Traceline.Core;

/// <summary>Running statistics for one hop over some period. Samples and whole aggregates can both be added.</summary>
public struct Agg
{
    public int N, Lost, JN;
    public double Sum, Min, Max, JSum;
    /// <summary>Most recent sample (NaN = lost) and the most recent answered one, for jitter.</summary>
    public double Last, Prev;
    private bool _hasPrev;

    public readonly int Received => N - Lost;
    public readonly double Avg => Received > 0 ? Sum / Received : double.NaN;
    public readonly double LossPct => N > 0 ? 100.0 * Lost / N : double.NaN;
    /// <summary>Mean absolute difference between consecutive answered samples.</summary>
    public readonly double Jitter => JN > 0 ? JSum / JN : Received > 0 ? 0 : double.NaN;

    public void Add(float rtt)
    {
        N++;
        if (float.IsNaN(rtt)) { Lost++; Last = double.NaN; return; }
        if (Received == 1) { Min = rtt; Max = rtt; }
        else { if (rtt < Min) Min = rtt; if (rtt > Max) Max = rtt; }
        Sum += rtt;
        if (_hasPrev) { JSum += Math.Abs(rtt - Prev); JN++; }
        Prev = rtt; _hasPrev = true; Last = rtt;
    }

    /// <summary>Adds a later aggregate to this one.</summary>
    public void Merge(in Agg o)
    {
        if (o.N == 0) return;
        if (o.Received > 0)
        {
            if (Received == 0) { Min = o.Min; Max = o.Max; }
            else { if (o.Min < Min) Min = o.Min; if (o.Max > Max) Max = o.Max; }
        }
        N += o.N; Lost += o.Lost; Sum += o.Sum; JSum += o.JSum; JN += o.JN;
        Last = o.Last;
        if (o._hasPrev) { Prev = o.Prev; _hasPrev = true; }
    }

    /// <summary>
    /// Voice quality estimate from 1 (unusable) to about 4.4 (excellent), using the simplified E-model
    /// that is common for ping-based tools: latency, jitter and loss lower an R-factor that maps to MOS.
    /// </summary>
    public readonly double Mos
    {
        get
        {
            if (N == 0) return double.NaN;
            if (Received == 0) return 1;
            var effective = Avg + Jitter * 2 + 10;
            var r = effective < 160 ? 93.2 - effective / 40 : 93.2 - (effective - 120) / 10;
            r -= LossPct * 2.5;
            r = Math.Clamp(r, 0, 100);
            return Math.Round(1 + 0.035 * r + 0.000007 * r * (r - 60) * (100 - r), 2);
        }
    }

    public const int BlobFloats = 7;

    public readonly void Write(Span<float> dst)
    {
        dst[0] = N; dst[1] = Lost; dst[2] = (float)Sum; dst[3] = (float)Min; dst[4] = (float)Max; dst[5] = (float)JSum; dst[6] = JN;
    }

    public static Agg Read(ReadOnlySpan<float> src) => new()
    {
        N = (int)src[0], Lost = (int)src[1], Sum = src[2], Min = src[3], Max = src[4], JSum = src[5], JN = (int)src[6],
        Last = double.NaN,
    };
}
