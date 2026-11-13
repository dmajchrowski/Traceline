using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.Versioning;

namespace Traceline.Core;

/// <summary>
/// Draws a target's route table and timelines as one PNG, for "save image", alert attachments and the
/// clipboard. Uses the Windows drawing library, so it is only available on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TraceImage
{
    private const int W = 1400, Row = 22, Pad = 16, ChartH = 150, GraphX = 900;
    private static readonly Color Good = Color.FromArgb(220, 243, 226), Warn = Color.FromArgb(253, 241, 199), Bad = Color.FromArgb(251, 220, 220);
    private static readonly Color Ink = Color.FromArgb(27, 31, 30), Muted = Color.FromArgb(104, 113, 110), Loss = Color.FromArgb(198, 40, 40), Rule = Color.FromArgb(223, 227, 226);

    public static bool Supported => OperatingSystem.IsWindows();

    public static byte[] Render(Target target, TargetView view, string fileFormat = "png")
    {
        var cfg = view.Config;
        var height = Pad * 2 + 70 + (view.Hops.Count + 1) * Row + view.Timelines.Count * (ChartH + 44) + 10;
        using var bitmap = new Bitmap(W, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.White);
        using Font title = new("Segoe UI", 14, FontStyle.Bold), text = new("Segoe UI", 9), bold = new("Segoe UI", 9, FontStyle.Bold), mono = new("Consolas", 9);
        using SolidBrush ink = new(Ink), muted = new(Muted);
        using Pen rule = new(Rule), line = new(Ink, 1.3f);
        var right = new StringFormat { Alignment = StringAlignment.Far };
        var clip = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        string Time(long ms, string format = "yyyy-MM-dd HH:mm:ss") => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
        static string N(double? v) => v is double d ? d.ToString(d >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) : "";

        float y = Pad;
        g.DrawString($"{target.Name}  {(target.Address != target.Name ? target.Address : "")}", title, ink, Pad, y);
        y += 28;
        g.DrawString($"{Time(view.FocusFrom)} to {Time(view.FocusTo)}   ·   {Environment.MachineName}   ·   Traceline", text, muted, Pad, y);
        y += 20;
        g.DrawString(view.Diagnosis.Headline, bold, ink, Pad, y);
        y += 26;

        // Route table
        (string Head, int X, int Width, bool Right)[] columns =
        [
            ("Hop", Pad, 34, true), ("Count", 56, 50, true), ("Address", 118, 150, false), ("Name", 272, 300, false),
            ("Avg", 576, 52, true), ("Min", 630, 52, true), ("Cur", 684, 52, true), ("Max", 738, 52, true), ("Jitter", 792, 48, true), ("Loss %", 842, 50, true),
        ];
        foreach (var c in columns)
            g.DrawString(c.Head, bold, muted, new RectangleF(c.X, y, c.Width, Row), c.Right ? right : clip);
        var scale = Math.Max(10, Math.Min(view.Hops.Select(h => h.Max ?? 0).DefaultIfEmpty(0).Max(), 4 * view.Hops.Select(h => h.Avg ?? 0).DefaultIfEmpty(0).Max())) * 1.05;
        float graphW = W - Pad - GraphX;
        g.DrawString($"0 – {N(scale)} ms", text, muted, new RectangleF(GraphX, y, graphW, Row), right);
        y += Row;
        float Gx(double ms) => GraphX + (float)(Math.Min(ms, scale) / scale * graphW);
        foreach (var h in view.Hops)
        {
            g.DrawLine(rule, Pad, y, W - Pad, y);
            var cells = new[] { h.Hop.ToString(), h.Count.ToString(), h.Ip ?? "-", h.Name ?? h.Owner ?? "", N(h.Avg), N(h.Min), N(h.Cur), N(h.Max), N(h.Jitter), N(h.LossPct) };
            for (var i = 0; i < columns.Length; i++)
            {
                var c = columns[i];
                using var brush = new SolidBrush(i == 9 && (h.LossPct ?? 0) > 0 && (h.LossPct ?? 0) < 100 ? Loss : Ink);
                g.DrawString(cells[i], i == 2 ? mono : h == view.Hops[^1] ? bold : text, brush, new RectangleF(c.X, y + 3, c.Width, Row), c.Right ? right : clip);
            }
            using (var b = new SolidBrush(Bad)) g.FillRectangle(b, GraphX, y + 3, graphW, Row - 6);
            using (var b = new SolidBrush(Warn)) g.FillRectangle(b, GraphX, y + 3, Gx(cfg.BadMs) - GraphX, Row - 6);
            using (var b = new SolidBrush(Good)) g.FillRectangle(b, GraphX, y + 3, Gx(cfg.WarnMs) - GraphX, Row - 6);
            if (h.Min is double min && h.Max is double max) g.DrawLine(line, Gx(min), y + Row / 2f, Gx(max), y + Row / 2f);
            if (h.Avg is double avg) g.FillRectangle(ink, Gx(avg) - 2, y + 5, 4, Row - 10);
            y += Row;
        }
        y += 14;

        // Timelines
        var destinationPeak = view.Timelines.FirstOrDefault(t => t.Hop == 0)?.Max.Max(v => v ?? 0) ?? 0;
        foreach (var t in view.Timelines.OrderBy(t => t.Hop == 0 ? int.MaxValue : t.Hop))
        {
            var hop = t.Hop == 0 ? view.Hops.LastOrDefault() : view.Hops.FirstOrDefault(h => h.Hop == t.Hop);
            g.DrawString($"{(t.Hop == 0 ? "Destination" : $"Hop {t.Hop}")}  ·  {hop?.Name ?? hop?.Ip ?? target.Name}", bold, ink, Pad, y);
            y += 20;
            const int left = Pad + 58;
            float plotW = W - Pad - left, plotH = ChartH;
            var n = t.Avg.Length;
            var peak = cfg.ScaleMode switch { "fixed" => cfg.ScaleMs, "destination" => destinationPeak * 1.15, _ => t.Max.Max(v => v ?? 0) * 1.15 };
            var top = Math.Max(10, peak);
            float X(double i) => left + (float)(i * plotW / n);
            float Y(double ms) => y + plotH - (float)(Math.Min(ms, top) / top * plotH);
            using (var b = new SolidBrush(Bad)) g.FillRectangle(b, left, y, plotW, plotH);
            using (var b = new SolidBrush(Warn)) g.FillRectangle(b, left, Y(cfg.BadMs), plotW, y + plotH - Y(cfg.BadMs));
            using (var b = new SolidBrush(Good)) g.FillRectangle(b, left, Y(cfg.WarnMs), plotW, y + plotH - Y(cfg.WarnMs));
            var barW = Math.Max(1, plotW / n);
            using var range = new SolidBrush(Color.FromArgb(70, Ink));
            using var lossBrush = new SolidBrush(Color.FromArgb(200, Loss));
            var points = new List<PointF>();
            void FlushLine() { if (points.Count > 1) g.DrawLines(line, points.ToArray()); points.Clear(); }
            for (var i = 0; i < n; i++)
            {
                if (t.Loss[i] is double loss && loss > 0)
                    g.FillRectangle(lossBrush, X(i), y, barW, Math.Max(3, (float)(Math.Min(loss, cfg.LossScalePct) / cfg.LossScalePct * plotH)));
                if (t.Avg[i] is not double avg) { FlushLine(); continue; }
                if (cfg.ShowMinMax) g.FillRectangle(range, X(i), Y(t.Max[i] ?? avg), barW, Math.Max(1, Y(t.Min[i] ?? avg) - Y(t.Max[i] ?? avg)));
                points.Add(new PointF(X(i + 0.5), Y(avg)));
            }
            FlushLine();
            g.DrawString($"{N(top)} ms", text, muted, new RectangleF(Pad, y, 56, 16), right);
            g.DrawString("0", text, muted, new RectangleF(Pad, y + plotH - 14, 56, 16), right);
            foreach (var e in view.Events.Where(e => t.Hop == 0 && e.Ts >= view.From && e.Ts <= view.To))
            {
                var ex = left + (float)((e.Ts - view.From) / (double)(view.To - view.From) * plotW);
                using var marker = new SolidBrush(e.Kind == "alert" ? Loss : e.Kind == "comment" ? Color.FromArgb(21, 101, 192) : Color.FromArgb(123, 63, 181));
                g.FillPolygon(marker, [new PointF(ex, y + plotH - 9), new PointF(ex - 5, y + plotH), new PointF(ex + 5, y + plotH)]);
            }
            y += plotH + 2;
            for (var k = 0; k <= 6; k++)
            {
                var ts = view.From + (view.To - view.From) * k / 6;
                var format = new StringFormat { Alignment = k == 0 ? StringAlignment.Near : k == 6 ? StringAlignment.Far : StringAlignment.Center };
                g.DrawString(Time(ts, view.To - view.From > 20 * 3600_000L ? "MM-dd HH:mm" : "HH:mm:ss"), text, muted, left + plotW * k / 6, y, format);
            }
            y += 22;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, fileFormat switch { "gif" => ImageFormat.Gif, "bmp" => ImageFormat.Bmp, _ => ImageFormat.Png });
        return stream.ToArray();
    }
}
