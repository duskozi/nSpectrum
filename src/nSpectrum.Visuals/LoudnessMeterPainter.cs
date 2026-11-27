using System.Globalization;
using nSpectrum.Core.Meters;

namespace nSpectrum.Visuals;

/// <summary>A loudness target: programme loudness and the true-peak ceiling that go with it.</summary>
public sealed record LoudnessTarget(string Name, double Lufs, double TruePeakLimit)
{
    public static LoudnessTarget EbuR128 { get; } = new("EBU R128 (−23 LUFS)", -23, -1);
    public static LoudnessTarget AtscA85 { get; } = new("ATSC A/85 (−24 LKFS)", -24, -2);
    public static LoudnessTarget Podcast { get; } = new("Podcast (−16 LUFS)", -16, -1);
    public static LoudnessTarget Streaming { get; } = new("Streaming (−14 LUFS)", -14, -1);

    public static IReadOnlyList<LoudnessTarget> All { get; } = [EbuR128, AtscA85, Podcast, Streaming];

    public static LoudnessTarget ByName(string? name) => All.FirstOrDefault(t => t.Name == name) ?? EbuR128;
}

/// <summary>Options of the loudness meter window.</summary>
public sealed record LoudnessMeterStyle
{
    public LedTheme Theme { get; init; } = LedTheme.ClassicGreen;
    public LoudnessTarget Target { get; init; } = LoudnessTarget.EbuR128;

    /// <summary>Seconds of short-term history in the graph (0 hides it).</summary>
    public int HistorySeconds { get; init; } = 60;
}

/// <summary>
/// Draws a loudness meter: momentary and short-term bars against the target, big integrated
/// readout, LRA, true peak per channel with the ceiling, maxima and running time, and a scrolling
/// short-term history. Keeps the history, so use one painter per window.
/// </summary>
public sealed class LoudnessMeterPainter
{
    private const double ScaleMin = -60, ScaleMax = 0;
    private const int HistoryRate = 10; // points per second

    private float[] _history = [];
    private float[] _historyM = [];
    private int _head, _count;
    private double _accum;

    /// <summary>Forgets the history graph (on reset or a new source).</summary>
    public void ResetHistory()
    {
        _count = 0;
        _head = 0;
    }

    public void Paint(PixelBuffer px, LoudnessFrame? f, LoudnessMeterStyle s, double dt)
    {
        var t = s.Theme;
        int w = px.Width, h = px.Height;
        px.ResetClip();
        px.FillGradient(0, 0, w, h, t.Background2, t.Background);
        Record(f, s, dt);

        double pad = Math.Clamp(Math.Min(w, h) * 0.04, 3, 14);
        int fs = Math.Clamp((int)Math.Round(Math.Min(w, h) / 160.0), 1, 3);
        double textH = PixelBuffer.TextHeight(fs);
        bool history = s.HistorySeconds > 0 && h >= 160;
        double graphH = history ? Math.Round(h * 0.32) : 0;
        double top = pad, bottom = h - pad - graphH - (history ? pad : 0);

        // Bars: M and S with a scale between and the target line across.
        double barW = Math.Clamp(w * 0.06, 8, 40);
        double scaleW = PixelBuffer.TextWidth("-60", fs) + pad;
        double barTop = top + textH + pad * 0.5, barBottom = bottom - textH - pad * 0.5;
        double barsX = pad;
        if (barBottom - barTop > 20)
        {
            Bar(px, s, barsX, barTop, barW, barBottom - barTop, f?.Momentary ?? double.NegativeInfinity, "M", fs);
            double sx = barsX + barW + pad * 0.5;
            Scale(px, t, sx, barTop, scaleW, barBottom - barTop, fs, s.Target.Lufs);
            Bar(px, s, sx + scaleW, barTop, barW, barBottom - barTop, f?.ShortTerm ?? double.NegativeInfinity, "S", fs);
        }

        // Readouts.
        double rx = barsX + 2 * barW + scaleW + pad * 2.5;
        double rw = w - rx - pad;
        if (rw > 40)
        {
            double integrated = f?.Integrated ?? double.NegativeInfinity;
            int big = Math.Clamp((int)Math.Floor(Math.Min(rw / 60.0, (bottom - top) / 40.0)), 1, 8);
            px.Text("INTEGRATED", rx, top, fs, t.Label);
            double y = top + textH + pad * 0.4;
            px.Text(Fmt(integrated), rx, y, big, OnTarget(t, integrated, s.Target.Lufs));
            px.Text("LUFS", rx + PixelBuffer.TextWidth(Fmt(integrated) + " ", big), y + PixelBuffer.TextHeight(big) - textH, fs, t.Label);
            y += PixelBuffer.TextHeight(big) + pad;

            int mid = Math.Max(fs, Math.Min(big / 2 + 1, 4));
            double colW = rw / 2;
            Readout(px, t, rx, y, "SHORT-TERM", Fmt(f?.ShortTerm ?? double.NegativeInfinity), "LUFS", fs, mid, t.Lit(0.3f, 0.5f));
            Readout(px, t, rx + colW, y, "MOMENTARY", Fmt(f?.Momentary ?? double.NegativeInfinity), "LUFS", fs, mid, t.Lit(0.3f, 0.5f));
            y += textH + PixelBuffer.TextHeight(mid) + pad;

            double lra = f?.LoudnessRange ?? 0;
            Readout(px, t, rx, y, "RANGE (LRA)", lra.ToString("0.0", CultureInfo.InvariantCulture), "LU", fs, mid, t.Lit(0.3f, 0.5f));
            double tp = f?.TruePeakMax ?? double.NegativeInfinity;
            Rgb tpInk = tp > s.Target.TruePeakLimit ? Rgb.Hex("#FF4A3A") : tp > s.Target.TruePeakLimit - 2 ? Rgb.Hex("#FFC23A") : t.Lit(0.3f, 0.5f);
            Readout(px, t, rx + colW, y, "TRUE PEAK", Fmt(tp), "dBTP", fs, mid, tpInk);
            y += textH + PixelBuffer.TextHeight(mid) + pad;

            if (y + textH <= bottom)
            {
                string per = f is { TruePeak.Length: >= 2 } ? $"L {Fmt(f.TruePeak[0])}  R {Fmt(f.TruePeak[1])}" : "";
                string maxes = $"MAX M {Fmt(f?.MomentaryMax ?? double.NegativeInfinity)}  S {Fmt(f?.ShortTermMax ?? double.NegativeInfinity)}";
                px.Text(maxes, rx, y, fs, t.Label, TextAlign.Left, 0.8f);
                if (y + 2 * textH + 2 <= bottom && per.Length > 0) px.Text("TP " + per, rx, y + textH + 3, fs, t.Label, TextAlign.Left, 0.8f);
            }
            string time = f is null ? "--:--:--" : TimeSpan.FromSeconds(Math.Floor(f.Seconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            px.Text(time, w - pad, top, fs, t.Label, TextAlign.Right);
            px.Text(s.Target.Name.ToUpperInvariant(), w - pad, top + textH + 3, fs, t.Label, TextAlign.Right, 0.6f);
        }

        if (history) Graph(px, s, pad, h - pad - graphH, w - 2 * pad, graphH, fs);
    }

    private void Record(LoudnessFrame? f, LoudnessMeterStyle s, double dt)
    {
        int n = Math.Max(1, s.HistorySeconds * HistoryRate);
        if (_history.Length != n)
        {
            _history = new float[n];
            _historyM = new float[n];
            _head = _count = 0;
        }
        if (f is null) return;
        _accum += dt;
        while (_accum >= 1.0 / HistoryRate)
        {
            _accum -= 1.0 / HistoryRate;
            _history[_head] = (float)f.ShortTerm;
            _historyM[_head] = (float)f.Momentary;
            _head = (_head + 1) % n;
            _count = Math.Min(_count + 1, n);
        }
        _accum = Math.Min(_accum, 1.0);
    }

    private static double Pos(double lufs) => Math.Clamp((lufs - ScaleMin) / (ScaleMax - ScaleMin), 0, 1);

    private static void Bar(PixelBuffer px, LoudnessMeterStyle s, double x, double y, double bw, double bh, double lufs, string tag, int fs)
    {
        var t = s.Theme;
        int segs = Math.Clamp((int)(bh / Math.Max(3, fs * 2.5)), 20, 120);
        double pitch = bh / segs, cell = Math.Max(1, pitch * 0.7);
        int lit = (int)Math.Round(Pos(lufs) * segs);
        for (int i = 0; i < segs; i++)
        {
            double at = ScaleMin + (ScaleMax - ScaleMin) * (i + 0.5) / segs;
            float zone = at > s.Target.Lufs + 1 ? 0.95f : at >= s.Target.Lufs - 1 ? 0.72f : 0.3f;
            Rgb col = t.Lit(zone, (float)i / segs);
            double yy = y + bh - (i + 1) * pitch + (pitch - cell) / 2;
            px.FillRect(x, yy, bw, cell, i < lit ? col : t.Background + col * t.Unlit);
        }
        px.Text(tag, x + bw / 2, y - PixelBuffer.TextHeight(fs) - 3, fs, t.Label, TextAlign.Center);
        px.Text(Fmt(lufs), x + bw / 2, y + bh + 3, fs, t.Label, TextAlign.Center, 0.8f);
    }

    private static void Scale(PixelBuffer px, LedTheme t, double x, double y, double sw, double sh, int fs, double target)
    {
        double th = PixelBuffer.TextHeight(fs);
        for (double l = ScaleMin; l <= ScaleMax; l += 10)
            px.Text(l.ToString("0", CultureInfo.InvariantCulture), x + sw / 2, y + sh * (1 - Pos(l)) - th / 2, fs, t.Label, TextAlign.Center, 0.7f);
        double ty = y + sh * (1 - Pos(target));
        px.Line(x - 2, ty, x + sw + 2, ty, Math.Max(1, fs), Rgb.Hex("#FFFFFF"), 0.7f);
    }

    private static void Readout(PixelBuffer px, LedTheme t, double x, double y, string label, string value, string unit, int fs, int size, Rgb ink)
    {
        px.Text(label, x, y, fs, t.Label, TextAlign.Left, 0.8f);
        double vy = y + PixelBuffer.TextHeight(fs) + 2;
        px.Text(value, x, vy, size, ink);
        px.Text(unit, x + PixelBuffer.TextWidth(value + " ", size), vy + PixelBuffer.TextHeight(size) - PixelBuffer.TextHeight(fs), fs, t.Label, TextAlign.Left, 0.8f);
    }

    private void Graph(PixelBuffer px, LoudnessMeterStyle s, double x, double y, double gw, double gh, int fs)
    {
        var t = s.Theme;
        px.FillRect(x, y, gw, gh, t.Background2 * 1.4f);
        px.StrokeRect(x, y, gw, gh, 1, t.Divider);
        const double lo = -50, hi = -5;
        double Y(double l) => y + gh * (1 - Math.Clamp((l - lo) / (hi - lo), 0, 1));
        for (double l = -50; l <= -10; l += 10)
        {
            px.Line(x, Y(l), x + gw, Y(l), 1, t.Divider);
            px.Text(l.ToString("0", CultureInfo.InvariantCulture), x + 3, Y(l) - PixelBuffer.TextHeight(fs) - 1, fs, t.Label, TextAlign.Left, 0.5f);
        }
        px.Line(x, Y(s.Target.Lufs), x + gw, Y(s.Target.Lufs), Math.Max(1, fs), Rgb.White, 0.5f);
        int n = _history.Length;
        if (_count < 2) return;
        Rgb ink = t.Lit(0.72f, 0.5f);
        double step = gw / (n - 1);
        for (int k = 1; k < _count; k++)
        {
            int i0 = (_head - _count + k - 1 + 2 * n) % n, i1 = (_head - _count + k + 2 * n) % n;
            double x0 = x + gw - (_count - k) * step, x1 = x0 + step;
            if (!float.IsNegativeInfinity(_historyM[i0]) && !float.IsNegativeInfinity(_historyM[i1]))
                px.Line(x0, Y(_historyM[i0]), x1, Y(_historyM[i1]), 1, ink, 0.25f);
            if (!float.IsNegativeInfinity(_history[i0]) && !float.IsNegativeInfinity(_history[i1]))
                px.Line(x0, Y(_history[i0]), x1, Y(_history[i1]), Math.Max(1.5, fs), ink);
        }
    }

    private static Rgb OnTarget(LedTheme t, double lufs, double target) =>
        double.IsNegativeInfinity(lufs) ? t.Label
        : Math.Abs(lufs - target) <= 1 ? Rgb.Hex("#5CFF8A")
        : lufs > target ? Rgb.Hex("#FFB13B")
        : Rgb.Hex("#9FD8FF");

    private static string Fmt(double v) => double.IsNegativeInfinity(v) || v < -99 ? "-inf" : v.ToString("0.0", CultureInfo.InvariantCulture);
}
