using System.Globalization;
using nSpectrum.Core.Meters;

namespace nSpectrum.Visuals;

/// <summary>Options of the phase meter window.</summary>
public sealed record PhaseMeterStyle
{
    public LedTheme Theme { get; init; } = LedTheme.ClassicGreen;

    /// <summary>Scales the goniometer to the signal level so quiet material fills the scope.</summary>
    public bool AutoGain { get; init; } = true;

    /// <summary>Trace persistence, 0 (none) to 0.95 (long afterglow).</summary>
    public float Persistence { get; init; } = 0.80f;

    public bool ShowGoniometer { get; init; } = true;

    /// <summary>Colour of the goniometer trace.</summary>
    public Rgb Trace { get; init; } = Rgb.Hex("#5CFF8A");
}

/// <summary>
/// Draws a goniometer (M/S vectorscope: mono is vertical, L and R on the diagonals) with afterglow
/// and a correlation meter from −1 (out of phase) through 0 (wide / unrelated) to +1 (mono).
/// Keeps the afterglow and auto-gain state, so use one painter per window.
/// </summary>
public sealed class PhaseMeterPainter
{
    private float[] _accum = [];
    private int _side;
    private double _gain = 1;
    private double _corr = double.NaN;

    public void Paint(PixelBuffer px, MeterFrame? f, PhaseMeterStyle s, double dt)
    {
        var t = s.Theme;
        int w = px.Width, h = px.Height;
        px.ResetClip();
        px.FillGradient(0, 0, w, h, t.Background2, t.Background);
        double pad = Math.Clamp(Math.Min(w, h) * 0.035, 3, 14);
        int fs = Math.Clamp((int)Math.Round(Math.Min(w, h) / 200.0), 1, 2);
        double textH = PixelBuffer.TextHeight(fs);
        double corrH = Math.Clamp(h * 0.12, 14, 46);
        double corrBlock = corrH + textH * 2 + pad * 1.5;
        if (!s.ShowGoniometer) corrBlock = h - pad;

        // Correlation smoothing for the display (the DSP already integrates 300 ms).
        double target = f?.Correlation ?? double.NaN;
        if (double.IsNaN(target)) _corr = double.NaN;
        else _corr = double.IsNaN(_corr) ? target : _corr + (target - _corr) * Math.Min(1, dt * 12);

        if (s.ShowGoniometer)
        {
            double avail = h - corrBlock - pad;
            int side = (int)Math.Max(0, Math.Min(w - 2 * pad, avail - pad));
            if (side >= 16)
            {
                double gx = Math.Round((w - side) / 2.0), gy = Math.Round(pad);
                Goniometer(px, f, s, (int)gx, (int)gy, side, dt, fs);
            }
        }
        double cy = s.ShowGoniometer ? h - corrBlock + pad * 0.5 : (h - corrH) / 2 - textH;
        Correlation(px, s, pad, cy, w - 2 * pad, corrH, fs, f);
    }

    private void Goniometer(PixelBuffer px, MeterFrame? f, PhaseMeterStyle s, int gx, int gy, int side, double dt, int fs)
    {
        var t = s.Theme;
        if (_side != side)
        {
            _side = side;
            _accum = new float[side * side];
        }
        // Afterglow: exponential decay per frame, normalised to 60 fps.
        float decay = (float)Math.Pow(Math.Clamp(s.Persistence, 0, 0.97), dt * 60);
        for (int i = 0; i < _accum.Length; i++) _accum[i] *= decay;

        double half = side / 2.0, cx = half, cy = half;
        double radius = half * 0.92;
        if (f is not null)
        {
            var scope = f.Scope;
            int frames = scope.Length / 2;
            if (s.AutoGain)
            {
                double peak = 1e-6;
                for (int i = 0; i < scope.Length; i++) peak = Math.Max(peak, Math.Abs(scope[i]));
                double want = Math.Clamp(0.7 / peak, 1, 60);
                _gain += (want - _gain) * Math.Min(1, dt * (want < _gain ? 8 : 1.5));
            }
            else _gain = 1;
            float deposit = (float)(0.9 * 2048.0 / frames);
            double k = radius * _gain / Math.Sqrt(2) / Math.Sqrt(2);
            for (int i = 0; i < frames; i++)
            {
                double l = scope[2 * i], r = scope[2 * i + 1];
                double x = cx + (r - l) * k;   // S horizontal: L leans left, R right
                double y = cy - (l + r) * k;   // M vertical: in-phase goes up
                Splat(x, y, deposit);
            }
        }

        // Graticule.
        var grid = t.Label * 0.35f;
        px.FillRect(gx, gy, side, side, t.Background * 0.6f);
        px.Arc(gx + cx, gy + cy, radius, 1, -Math.PI, Math.PI, grid, 0.6f);
        px.Arc(gx + cx, gy + cy, radius * 0.5, 1, -Math.PI, Math.PI, grid, 0.35f);
        px.Line(gx + cx, gy + cy - radius, gx + cx, gy + cy + radius, 1, grid, 0.6f);
        px.Line(gx + cx - radius, gy + cy, gx + cx + radius, gy + cy, 1, grid, 0.35f);
        double d = radius / Math.Sqrt(2);
        px.Line(gx + cx - d, gy + cy - d, gx + cx + d, gy + cy + d, 1, grid, 0.45f);
        px.Line(gx + cx + d, gy + cy - d, gx + cx - d, gy + cy + d, 1, grid, 0.45f);

        // Trace.
        var trace = s.Trace;
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                float v = _accum[y * side + x];
                if (v < 0.01f) continue;
                Rgb col = v > 1.5f ? Rgb.Lerp(trace, Rgb.White, Math.Min(1, (v - 1.5f) / 3)) : trace;
                px.AddPixel(gx + x, gy + y, col, Math.Min(1, v));
            }
        }

        px.Text("M", gx + cx, gy + 2, fs, t.Label, TextAlign.Center);
        px.Text("L", gx + cx - d - 3 * fs, gy + cy - d - 3 * fs, fs, t.Label, TextAlign.Center);
        px.Text("R", gx + cx + d + 3 * fs, gy + cy - d - 3 * fs, fs, t.Label, TextAlign.Center);
        px.Text("+S", gx + side - 2, gy + cy - PixelBuffer.TextHeight(fs) - 2, fs, t.Label, TextAlign.Right, 0.6f);
        px.Text("-S", gx + 2, gy + cy - PixelBuffer.TextHeight(fs) - 2, fs, t.Label, TextAlign.Left, 0.6f);
        if (f is { Channels: 2 } && !double.IsNaN(f.Correlation))
        {
            double bal = f.BalanceDb;
            string b = Math.Abs(bal) < 0.05 ? "BAL C" : bal > 0 ? $"BAL R+{bal:0.0}" : $"BAL L+{-bal:0.0}";
            px.Text(b, gx + 2, gy + side - PixelBuffer.TextHeight(fs) - 2, fs, t.Label, TextAlign.Left, 0.6f);
        }
        if (s.AutoGain && _gain > 1.05)
            px.Text($"+{20 * Math.Log10(_gain):0} dB", gx + side - 2, gy + side - PixelBuffer.TextHeight(fs) - 2, fs, t.Label, TextAlign.Right, 0.6f);
    }

    private void Splat(double x, double y, float amount)
    {
        int x0 = (int)Math.Floor(x - 0.5), y0 = (int)Math.Floor(y - 0.5);
        float fx = (float)(x - 0.5 - x0), fy = (float)(y - 0.5 - y0);
        Put(x0, y0, amount * (1 - fx) * (1 - fy));
        Put(x0 + 1, y0, amount * fx * (1 - fy));
        Put(x0, y0 + 1, amount * (1 - fx) * fy);
        Put(x0 + 1, y0 + 1, amount * fx * fy);
    }

    private void Put(int x, int y, float a)
    {
        if ((uint)x >= (uint)_side || (uint)y >= (uint)_side) return;
        _accum[y * _side + x] += a;
    }

    private void Correlation(PixelBuffer px, PhaseMeterStyle s, double x, double y, double w, double h, int fs, MeterFrame? f)
    {
        var t = s.Theme;
        double textH = PixelBuffer.TextHeight(fs);
        string value = double.IsNaN(_corr) ? "--" : _corr.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        px.Text("CORRELATION", x, y, fs, t.Label, TextAlign.Left, 0.8f);
        px.Text(value, x + w, y, fs, double.IsNaN(_corr) ? t.Label : CorrColor(_corr), TextAlign.Right);
        double by = y + textH + 4;
        int segs = Math.Clamp((int)(w / Math.Max(5, h * 0.35)), 20, 80);
        if (segs % 2 == 1) segs++;
        double pitch = w / segs, cell = Math.Max(1, pitch * 0.74);
        int centre = segs / 2;
        int target = double.IsNaN(_corr) ? centre : (int)Math.Round((_corr + 1) / 2 * segs);
        for (int i = 0; i < segs; i++)
        {
            double v = ((i + 0.5) / segs) * 2 - 1;
            Rgb col = CorrColor(v);
            bool lit = !double.IsNaN(_corr) && (target >= centre ? i >= centre && i < target : i >= target && i < centre);
            bool marker = !double.IsNaN(_corr) && (i == Math.Clamp(target - (target > centre ? 1 : 0), 0, segs - 1));
            px.FillRect(x + i * pitch + (pitch - cell) / 2, by, cell, h,
                lit || marker ? (marker ? Rgb.Lerp(col, Rgb.White, 0.2f) : col) : t.Background + col * t.Unlit);
        }
        double ly = by + h + 3;
        px.Text("-1", x, ly, fs, t.Label);
        px.Text("0", x + w / 2, ly, fs, t.Label, TextAlign.Center);
        px.Text("+1", x + w, ly, fs, t.Label, TextAlign.Right);
        px.FillRect(x + w / 2 - 0.5, by - 2, 1, h + 4, t.Label, 0.5f);
    }

    private static Rgb CorrColor(double v) =>
        v < -0.05 ? Rgb.Hex("#FF3B2E") : v < 0.3 ? Rgb.Hex("#FFC21F") : Rgb.Hex("#3BFF5A");
}
