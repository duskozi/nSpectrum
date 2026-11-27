using System.Globalization;
using nSpectrum.Core.Meters;

namespace nSpectrum.Visuals;

/// <summary>Analog needle meters or digital LED bar meters.</summary>
public enum VuMeterKind { Analog, Digital }

/// <summary>What the main bar of the digital meter follows.</summary>
public enum DigitalBarMode
{
    /// <summary>RMS over 300 ms in AES17 dBFS (a sine reads its peak level), with the peak on top.</summary>
    Rms,
    /// <summary>VU ballistics, drawn at the dBFS level of the equivalent sine.</summary>
    Vu,
}

/// <summary>Look of the analog meter face.</summary>
public sealed record VuFace
{
    public required string Name { get; init; }
    public Rgb FaceTop { get; init; }
    public Rgb FaceBottom { get; init; }
    public Rgb Ink { get; init; }
    public Rgb Red { get; init; }
    public Rgb Needle { get; init; }
    public Rgb Bezel { get; init; } = Rgb.Hex("#121416");
    public Rgb Backlight { get; init; }
    public float BacklightIntensity { get; init; }

    public static VuFace Classic { get; } = new()
    {
        Name = "Classic cream",
        FaceTop = Rgb.Hex("#F3E7C4"), FaceBottom = Rgb.Hex("#DCC894"),
        Ink = Rgb.Hex("#1D1A16"), Red = Rgb.Hex("#C8231B"), Needle = Rgb.Hex("#16130F"),
        Backlight = Rgb.Hex("#FFB648"), BacklightIntensity = 0.10f,
    };

    public static VuFace Amber { get; } = new()
    {
        Name = "Amber backlit",
        FaceTop = Rgb.Hex("#F7B84B"), FaceBottom = Rgb.Hex("#B8661A"),
        Ink = Rgb.Hex("#2A1606"), Red = Rgb.Hex("#9E1208"), Needle = Rgb.Hex("#1A0D03"),
        Backlight = Rgb.Hex("#FFD27A"), BacklightIntensity = 0.22f,
    };

    public static VuFace Night { get; } = new()
    {
        Name = "Night blue",
        FaceTop = Rgb.Hex("#0E1B2E"), FaceBottom = Rgb.Hex("#060C16"),
        Ink = Rgb.Hex("#9FD4FF"), Red = Rgb.Hex("#FF5A4E"), Needle = Rgb.Hex("#FF8A3D"),
        Backlight = Rgb.Hex("#2E7BFF"), BacklightIntensity = 0.12f,
    };

    public static VuFace Studio { get; } = new()
    {
        Name = "Studio black",
        FaceTop = Rgb.Hex("#1B1C1E"), FaceBottom = Rgb.Hex("#0C0D0E"),
        Ink = Rgb.Hex("#E9E9E4"), Red = Rgb.Hex("#FF4436"), Needle = Rgb.Hex("#F2F2EE"),
        Backlight = Rgb.Hex("#FFFFFF"), BacklightIntensity = 0.04f,
    };

    public static IReadOnlyList<VuFace> All { get; } = [Classic, Amber, Night, Studio];

    public static VuFace ByName(string? name) => All.FirstOrDefault(f => f.Name == name) ?? Classic;
}

/// <summary>Options of the VU meter window.</summary>
public sealed record VuMeterStyle
{
    public VuMeterKind Kind { get; init; } = VuMeterKind.Analog;
    public VuFace Face { get; init; } = VuFace.Classic;
    public LedTheme Theme { get; init; } = LedTheme.ClassicGreen;
    public DigitalBarMode BarMode { get; init; } = DigitalBarMode.Rms;

    /// <summary>dBFS of the sine that reads 0 VU (EBU R68: −18, SMPTE RP155: −20).</summary>
    public double ReferenceDbfs { get; init; } = -18;

    /// <summary>Sample peak (dBFS) that lights the analog meter's peak lamp.</summary>
    public double PeakLampDbfs { get; init; } = -3;

    public bool PeakHold { get; init; } = true;

    /// <summary>Show one meter only, fed from the left (or mono) channel.</summary>
    public bool Mono { get; init; }
}

/// <summary>
/// Draws analog VU meters (needles, IEC 60268-17 scale) or digital peak/RMS bar meters from a
/// <see cref="MeterFrame"/>. Keeps peak-hold, lamp and clip-indicator state between frames, so use
/// one painter per window and call <see cref="Paint"/> once per rendered frame.
/// </summary>
public sealed class VuMeterPainter
{
    private readonly double[] _hold = [-200, -200];
    private readonly double[] _holdAge = [0, 0];
    private readonly double[] _lamp = [0, 0];
    private readonly long[] _clipSeen = [0, 0];
    private readonly double[] _clipAge = [99, 99];
    private readonly double[] _maxPeak = [-200, -200];
    private readonly double[] _needle = [0, 0];

    /// <summary>Seconds the digital peak-hold segment stays put.</summary>
    public double HoldSeconds { get; set; } = 2.0;

    /// <summary>Clears peak hold, maximum readouts and clip indicators.</summary>
    public void ResetHolds()
    {
        for (int c = 0; c < 2; c++) { _hold[c] = -200; _maxPeak[c] = -200; _clipAge[c] = 99; }
    }

    public void Paint(PixelBuffer px, MeterFrame? frame, VuMeterStyle style, double dt)
    {
        int n = style.Mono || frame is { Channels: 1 } ? 1 : 2;
        Advance(frame, style, dt);
        px.ResetClip();
        if (style.Kind == VuMeterKind.Analog) PaintAnalog(px, frame, style, n);
        else PaintDigital(px, frame, style, n);
    }

    private static double Db(double lin) => lin > 1e-10 ? 20 * Math.Log10(lin) : -200;

    private void Advance(MeterFrame? f, VuMeterStyle s, double dt)
    {
        for (int c = 0; c < 2; c++)
        {
            double peak = f is null ? -200 : Db(f.Peak[c]);
            _holdAge[c] += dt;
            if (peak >= _hold[c] || _holdAge[c] > HoldSeconds) { _hold[c] = peak; _holdAge[c] = 0; }
            _maxPeak[c] = Math.Max(_maxPeak[c], peak);
            _lamp[c] = peak >= s.PeakLampDbfs ? 0.6 : Math.Max(0, _lamp[c] - dt);
            if (f is not null && f.Clips[c] != _clipSeen[c]) { _clipSeen[c] = f.Clips[c]; _clipAge[c] = 0; }
            else _clipAge[c] += dt;
            // The needle's own ballistics are in the DSP; here only the end stops.
            double vu = f is null ? -100 : VuBallistics.ToVu(f.Vu[c], s.ReferenceDbfs);
            _needle[c] = Math.Clamp(Math.Pow(10, (vu - 3) / 20), 0, 1.06);
        }
    }

    // ---------------------------------------------------------------- analog

    private static readonly (double Vu, string Label)[] VuMarks =
    [
        (-20, "20"), (-10, "10"), (-7, "7"), (-5, "5"), (-3, "3"), (-2, "2"), (-1, "1"), (0, "0"), (1, "1"), (2, "2"), (3, "3"),
    ];

    private const double Sweep = 0.84; // radians each side of vertical

    private static double Angle(double fraction) => -Sweep + 2 * Sweep * fraction;

    private static double FractionOfVu(double vu) => Math.Pow(10, (vu - 3) / 20);

    private void PaintAnalog(PixelBuffer px, MeterFrame? f, VuMeterStyle s, int n)
    {
        var face = s.Face;
        int w = px.Width, h = px.Height;
        px.Clear(Rgb.Hex("#0A0B0C"));
        double pad = Math.Clamp(Math.Min(w, h) * 0.03, 3, 12);
        // Side by side or stacked, whichever gives bigger meters at a 1.75:1 face.
        const double aspect = 1.75;
        double SizeFor(int cols, int rows)
        {
            double cw = (w - pad * (cols + 1)) / cols, ch = (h - pad * (rows + 1)) / rows;
            return Math.Min(cw, ch * aspect);
        }
        bool stacked = n == 2 && SizeFor(1, 2) > SizeFor(2, 1);
        int cols = n == 2 && !stacked ? 2 : 1, rows = stacked ? 2 : 1;
        double mw = SizeFor(cols, rows), mh = mw / aspect;
        double totalW = cols * mw + (cols - 1) * pad, totalH = rows * mh + (rows - 1) * pad;
        double ox = (w - totalW) / 2, oy = (h - totalH) / 2;
        for (int c = 0; c < n; c++)
        {
            double x = ox + (stacked ? 0 : c * (mw + pad));
            double y = oy + (stacked ? c * (mh + pad) : 0);
            string tag = n == 1 ? (f is { Channels: 1 } ? "MONO" : "L") : c == 0 ? "L" : "R";
            Meter(px, x, y, mw, mh, face, _needle[c], _lamp[c] > 0, tag);
        }
    }

    private static void Meter(PixelBuffer px, double x, double y, double w, double h, VuFace face, double needle, bool lamp, string tag)
    {
        if (w < 20 || h < 12) return;
        double bezel = Math.Max(2, h * 0.045);
        px.FillRect(x, y, w, h, face.Bezel);
        px.StrokeRect(x, y, w, h, 1, Rgb.Hex("#2A2D31"));
        double fx = x + bezel, fy = y + bezel, fw = w - 2 * bezel, fh = h - 2 * bezel;
        px.SetClip(fx, fy, fw, fh);
        px.FillGradient(fx, fy, fw, fh, face.FaceTop, face.FaceBottom);
        if (face.BacklightIntensity > 0) px.Glow(fx + fw / 2, fy + fh * 0.55, fw * 0.55, face.Backlight, face.BacklightIntensity);

        double cx = fx + fw / 2;
        double pivotY = fy + fh * 1.08;
        double r = fh * 0.86;
        int scale = Math.Max(1, (int)Math.Round(fh / 95));
        double line = Math.Max(1, fh / 150);

        // Scale arc: black to 0 VU, thick red from 0 to +3.
        double a0 = Angle(FractionOfVu(-20)), aZero = Angle(FractionOfVu(0)), a3 = Angle(1);
        px.Arc(cx, pivotY, r, line, a0, aZero, face.Ink);
        double redW = Math.Max(2, fh * 0.035);
        px.Arc(cx, pivotY, r + redW / 2 - line / 2, redW, aZero, a3, face.Red);
        // Inner percentage arc (thin) for the classic double scale.
        px.Arc(cx, pivotY, r * 0.86, line * 0.8, Angle(0.0), Angle(FractionOfVu(0)), face.Ink, 0.7f);

        // Minor ticks every 1 VU from -10, and at -15.
        foreach (double vu in new double[] { -15, -9, -8, -6, -4 })
        {
            double a = Angle(FractionOfVu(vu));
            var (x0, y0) = PixelBuffer.Polar(cx, pivotY, r, a);
            var (x1, y1) = PixelBuffer.Polar(cx, pivotY, r + fh * 0.045, a);
            px.Line(x0, y0, x1, y1, line, face.Ink);
        }
        foreach (var (vu, label) in VuMarks)
        {
            double a = Angle(FractionOfVu(vu));
            bool red = vu > 0;
            var ink = red ? face.Red : face.Ink;
            var (x0, y0) = PixelBuffer.Polar(cx, pivotY, r, a);
            var (x1, y1) = PixelBuffer.Polar(cx, pivotY, r + fh * 0.085, a);
            px.Line(x0, y0, x1, y1, line * 1.4, ink);
            var (lx, ly) = PixelBuffer.Polar(cx, pivotY, r + fh * 0.085 + 5 * scale, a);
            px.Text(label, lx, ly - PixelBuffer.TextHeight(scale), scale, ink, TextAlign.Center);
        }
        // Signs at the scale ends.
        var (mx, my) = PixelBuffer.Polar(cx, pivotY, r + fh * 0.12, a0 - 0.12);
        px.Text("-", mx, my - PixelBuffer.TextHeight(scale), scale, face.Ink, TextAlign.Center);
        var (plx, ply) = PixelBuffer.Polar(cx, pivotY, r + fh * 0.12, a3 + 0.10);
        px.Text("+", plx, ply - PixelBuffer.TextHeight(scale), scale, face.Red, TextAlign.Center);
        // Percentage ticks on the inner arc.
        foreach (double pct in new double[] { 0, 20, 40, 60, 80, 100 })
        {
            double a = Angle(pct / 100 * FractionOfVu(0));
            var (x0, y0) = PixelBuffer.Polar(cx, pivotY, r * 0.86, a);
            var (x1, y1) = PixelBuffer.Polar(cx, pivotY, r * 0.86 - fh * 0.04, a);
            px.Line(x0, y0, x1, y1, line, face.Ink, 0.7f);
        }

        int big = Math.Max(1, (int)Math.Round(fh / 45));
        px.Text("VU", cx, fy + fh * 0.50, big, face.Ink, TextAlign.Center);
        px.Text(tag, fx + fw * 0.06, fy + fh * 0.70, scale, face.Ink, TextAlign.Left, 0.75f);

        // Peak lamp.
        double lr = Math.Max(2.5, fh * 0.045);
        double lx0 = fx + fw * 0.90, ly0 = fy + fh * 0.72;
        px.FillCircle(lx0, ly0, lr + 1, Rgb.Hex("#2A0705"));
        px.FillCircle(lx0, ly0, lr, lamp ? Rgb.Hex("#FF2A1A") : Rgb.Hex("#5A1410"));
        if (lamp) px.Glow(lx0, ly0, lr * 4, Rgb.Hex("#FF3A20"), 0.35f);
        px.Text("PEAK", lx0, ly0 + lr + 3, Math.Max(1, scale - (scale > 1 ? 1 : 0)), face.Ink, TextAlign.Center, 0.75f);

        // Needle with a soft shadow.
        double an = Angle(needle);
        var (nx0, ny0) = PixelBuffer.Polar(cx, pivotY, r * 0.30, an);
        var (nx1, ny1) = PixelBuffer.Polar(cx, pivotY, r * 1.07, an);
        double nw = Math.Max(1.2, fh / 110);
        px.Line(nx0 + nw * 1.5, ny0 + nw * 2, nx1 + nw * 1.5, ny1 + nw * 2, nw * 1.6, Rgb.Black, 0.18f);
        px.Line(nx0, ny0, nx1, ny1, nw, face.Needle);

        // Cover over the pivot.
        double coverY = fy + fh * 0.84;
        px.FillGradient(fx, coverY, fw, fy + fh - coverY, Rgb.Hex("#2B2E33"), Rgb.Hex("#141618"));
        px.FillRect(fx, coverY, fw, 1, Rgb.Hex("#474B52"));
        px.FillCircle(cx, coverY + (fy + fh - coverY) * 0.55, Math.Max(1.5, fh * 0.025), Rgb.Hex("#5A5F66"));
        // Glass sheen.
        px.FadeRect(fx, fy, fw, fh * 0.4, Rgb.White, 0.07f, 0f, additive: true);
        px.ResetClip();
    }

    // ---------------------------------------------------------------- digital

    private static readonly (double Db, double Pos)[] Knots = [(-60, 0), (-40, 0.14), (-30, 0.27), (-20, 0.44), (-10, 0.68), (0, 1)];

    /// <summary>Position 0..1 along the digital meter for a dBFS value (expanded near the top).</summary>
    public static double Position(double db)
    {
        if (db <= Knots[0].Db) return 0;
        for (int i = 1; i < Knots.Length; i++)
            if (db <= Knots[i].Db)
            {
                var (d0, p0) = Knots[i - 1];
                var (d1, p1) = Knots[i];
                return p0 + (p1 - p0) * (db - d0) / (d1 - d0);
            }
        return 1;
    }

    private void PaintDigital(PixelBuffer px, MeterFrame? f, VuMeterStyle s, int n)
    {
        var t = s.Theme;
        int w = px.Width, h = px.Height;
        px.FillGradient(0, 0, w, h, t.Background2, t.Background);
        bool vertical = h > w * 1.2;
        double pad = Math.Clamp(Math.Min(w, h) * 0.04, 3, 14);
        int fs = Math.Max(1, (int)Math.Round(Math.Min(w, h) / (vertical ? 120.0 : 70.0)));
        fs = Math.Min(fs, 3);
        double textH = PixelBuffer.TextHeight(fs);
        double readoutW = vertical ? 0 : PixelBuffer.TextWidth("-00.0", fs) + pad * 2 + PixelBuffer.TextWidth("CLIP", fs);
        double tagW = PixelBuffer.TextWidth("R", fs) + pad;

        // Long axis: from start to end; across: per channel lanes with the scale in between.
        double longStart = vertical ? h - pad - textH * 2.6 : pad + tagW;
        double longLen = vertical ? longStart - pad - textH * 1.5 : w - pad - readoutW - longStart;
        double acrossAvail = (vertical ? w : h) - 2 * pad;
        double scaleBand = vertical ? PixelBuffer.TextWidth("-60", fs) + pad * 1.2 : textH + pad;
        double lane = (acrossAvail - scaleBand) / n;
        if (n == 1) lane = acrossAvail - scaleBand;
        if (longLen < 10 || lane < 2) return;

        int segs = Math.Clamp((int)(longLen / Math.Max(4, fs * 3.0)), 24, 160);
        double pitch = longLen / segs;
        double cell = Math.Max(1, pitch * 0.72);

        double laneSize = Math.Min(lane, vertical ? w * 0.3 : h * 0.4);
        double totalAcross = n * laneSize + scaleBand;
        double across0 = ((vertical ? w : h) - totalAcross) / 2;

        for (int c = 0; c < n; c++)
        {
            // Lane order: L, scale, R (stereo) or bar then scale (mono).
            double a0 = across0 + (c == 0 ? 0 : laneSize + scaleBand);
            double barDb = -200, peakDb = -200;
            if (f is not null)
            {
                peakDb = Db(f.Peak[c]);
                barDb = s.BarMode == DigitalBarMode.Vu
                    ? VuBallistics.ToVu(f.Vu[c], s.ReferenceDbfs) + s.ReferenceDbfs
                    : Db(f.Rms[c] * Math.Sqrt(2));
            }
            int barSegs = (int)Math.Round(Position(barDb) * segs);
            int peakSegs = (int)Math.Round(Position(peakDb) * segs);
            int holdSeg = s.PeakHold ? (int)Math.Round(Position(_hold[c]) * segs) - 1 : -1;
            for (int i = 0; i < segs; i++)
            {
                double dbAt = DbAt((i + 0.5) / segs);
                float zone = dbAt >= -6 ? 0.95f : dbAt >= s.ReferenceDbfs ? 0.72f : 0.3f;
                Rgb col = t.Lit(zone, (float)i / segs);
                float k;
                if (i < barSegs) k = 1f;
                else if (i < peakSegs) k = 0.45f;
                else if (i == holdSeg && holdSeg > 0) k = 1.1f;
                else k = -1;
                double along = longStart + (vertical ? -(i + 1) * pitch + (pitch - cell) / 2 : i * pitch + (pitch - cell) / 2);
                Rgb fill = k < 0 ? t.Background + col * t.Unlit : k > 1 ? Rgb.Lerp(col, Rgb.White, 0.25f) : col * k + t.Background * (1 - k);
                if (vertical) px.FillRect(a0, along, laneSize, cell, fill);
                else px.FillRect(along, a0, cell, laneSize, fill);
            }
            string tag = n == 1 ? (f is { Channels: 1 } ? "M" : "L") : c == 0 ? "L" : "R";
            if (vertical)
            {
                double cxl = a0 + laneSize / 2;
                px.Text(tag, cxl, pad * 0.4 + 0, fs, t.Label, TextAlign.Center);
                // Readouts under the bar: peak hold max and the bar value.
                px.Text(Fmt(_maxPeak[c]), cxl, longStart + pad * 0.6, fs, PeakInk(t, _maxPeak[c]), TextAlign.Center);
                px.Text(Fmt(barDb), cxl, longStart + pad * 0.6 + textH * 1.3, fs, t.Label, TextAlign.Center);
                if (_clipAge[c] < 2) px.FillRect(a0, pad * 0.4 + textH + 2, laneSize, Math.Max(2, fs * 2), Rgb.Hex("#FF2A1E"));
            }
            else
            {
                double cyl = a0 + laneSize / 2 - textH / 2;
                px.Text(tag, pad, cyl, fs, t.Label);
                double rx = longStart + longLen + pad;
                px.Text(Fmt(_maxPeak[c]), rx + PixelBuffer.TextWidth("-00.0", fs), cyl, fs, PeakInk(t, _maxPeak[c]), TextAlign.Right);
                double clipX = rx + PixelBuffer.TextWidth("-00.0", fs) + pad * 0.8;
                bool clip = _clipAge[c] < 2;
                double cw = PixelBuffer.TextWidth("CLIP", fs) + 4;
                px.FillRect(clipX - 2, cyl - 2, cw, textH + 4, clip ? Rgb.Hex("#FF2A1E") : Rgb.Hex("#2A0C0A"));
                px.Text("CLIP", clipX, cyl, fs, clip ? Rgb.White : Rgb.Hex("#6A2A24"));
            }
        }

        // Scale.
        double scaleA = across0 + laneSize + pad * 0.5;
        foreach (double db in new double[] { -60, -40, -30, -20, -15, -10, -6, -3, 0 })
        {
            double p = Position(db);
            string label = db == 0 ? "0" : db.ToString("0", CultureInfo.InvariantCulture);
            if (vertical)
                px.Text(label, scaleA + (scaleBand - pad) / 2, longStart - p * longLen - textH / 2, fs, t.Label, TextAlign.Center);
            else
                px.Text(label, longStart + p * longLen, scaleA, fs, t.Label, TextAlign.Center);
        }
        string mode = s.BarMode == DigitalBarMode.Vu ? $"VU  0 VU = {s.ReferenceDbfs:0} dBFS" : "PEAK + RMS  dBFS";
        if (!vertical && h - (across0 + totalAcross) > textH + 2)
            px.Text(mode, longStart, across0 + totalAcross + 2, fs, t.Label, TextAlign.Left, 0.6f);
    }

    private static double DbAt(double pos)
    {
        for (int i = 1; i < Knots.Length; i++)
            if (pos <= Knots[i].Pos)
            {
                var (d0, p0) = Knots[i - 1];
                var (d1, p1) = Knots[i];
                return d0 + (d1 - d0) * (pos - p0) / (p1 - p0);
            }
        return 0;
    }

    private static Rgb PeakInk(LedTheme t, double db) => db >= -0.1 ? Rgb.Hex("#FF4A3A") : db >= -6 ? Rgb.Hex("#FFC23A") : t.Label;

    private static string Fmt(double db) => db <= -99 ? "-inf" : db.ToString("0.0", CultureInfo.InvariantCulture);
}
