using nSpectrum.Core.Meters;

namespace nSpectrum.Visuals;

/// <summary>Options of the LED band display.</summary>
public sealed record LedSpectrumStyle
{
    public LedTheme Theme { get; init; } = LedTheme.ClassicGreen;

    /// <summary>LED rows per column.</summary>
    public int Segments { get; init; } = 20;

    public bool ShowLabels { get; init; } = true;
    public bool ShowPeaks { get; init; } = true;
    public bool Glow { get; init; } = true;

    /// <summary>Draws a small L / R tag in each panel of a stereo display.</summary>
    public bool ShowChannelTags { get; init; }
}

/// <summary>
/// Draws a segmented LED spectrum: one panel per channel side by side, a column of LEDs per band,
/// unlit segments faintly visible, separate peak caps and band labels underneath.
/// </summary>
public static class LedSpectrumPainter
{
    /// <summary>Paints the model's current state into the whole buffer.</summary>
    public static void Paint(PixelBuffer px, LedSpectrumModel model, LedSpectrumStyle style)
    {
        int ch = model.Channels;
        var bands = model.Bands;
        Paint(px, ch, bands.Count, i => bands[i].Label, model.Level, model.Peak, style);
    }

    /// <summary>
    /// Paints raw data: <paramref name="level"/> and <paramref name="peak"/> hold 0..1 per band and
    /// channel. Used by <see cref="Paint(PixelBuffer, LedSpectrumModel, LedSpectrumStyle)"/> and previews.
    /// </summary>
    public static void Paint(PixelBuffer px, int channels, int bandCount, Func<int, string> label,
                             float[][] level, float[][] peak, LedSpectrumStyle style)
    {
        var t = style.Theme;
        int w = px.Width, h = px.Height;
        px.ResetClip();
        px.FillGradient(0, 0, w, h, t.Background2, t.Background);
        if (channels <= 0 || bandCount <= 0) return;

        double pad = Math.Clamp(Math.Min(w, h) * 0.025, 3, 14);
        int fontScale = h >= 520 && w >= 900 ? 2 : 1;
        double labelH = style.ShowLabels ? PixelBuffer.TextHeight(fontScale) + pad * 0.8 : 0;
        double gapPanels = channels > 1 ? Math.Max(pad * 2, w * 0.02) : 0;
        double panelW = (w - 2 * pad - gapPanels * (channels - 1)) / channels;
        double areaH = h - 2 * pad - labelH;
        if (panelW < 4 || areaH < 4) return;

        int segs = Math.Max(4, style.Segments);
        double colPitch = panelW / bandCount;
        double rowPitch = areaH / segs;
        double cellW = Math.Max(1, colPitch * (colPitch >= 6 ? 0.76 : 0.85));
        double cellH = Math.Max(1, rowPitch * (rowPitch >= 5 ? 0.66 : 0.8));
        // Snap to whole pixels when cells are large enough: crisper LEDs.
        if (cellW >= 3) cellW = Math.Round(cellW);
        if (cellH >= 3) cellH = Math.Round(cellH);
        double glowR = Math.Max(2, Math.Min(cellW, cellH) * 0.45);

        for (int c = 0; c < channels; c++)
        {
            double x0 = pad + c * (panelW + gapPanels);
            double yBottom = pad + areaH;
            if (c > 0)
                px.FillRect(x0 - gapPanels / 2 - 1, pad * 0.5, 2, h - pad, t.Divider);
            var lv = level[c];
            var pk = peak[c];
            for (int b = 0; b < bandCount; b++)
            {
                double cx = x0 + b * colPitch + (colPitch - cellW) / 2;
                if (cellW >= 3) cx = Math.Round(cx);
                float colFrac = bandCount > 1 ? (float)b / (bandCount - 1) : 0;
                int lit = b < lv.Length ? (int)Math.Round(lv[b] * segs) : 0;
                int peakRow = style.ShowPeaks && b < pk.Length && pk[b] > 0 ? Math.Min(segs - 1, (int)Math.Ceiling(pk[b] * segs) - 1) : -1;
                if (peakRow < lit) peakRow = -1; // cap sits inside the bar: hidden
                for (int r = 0; r < segs; r++)
                {
                    double cy = yBottom - (r + 1) * rowPitch + (rowPitch - cellH) / 2;
                    if (cellH >= 3) cy = Math.Round(cy);
                    float rowFrac = (r + 0.5f) / segs;
                    Rgb col = t.Lit(rowFrac, colFrac);
                    if (r < lit || r == peakRow)
                    {
                        if (r == peakRow) col = Boost(col, t.PeakBoost);
                        if (style.Glow && t.Glow > 0)
                            px.FillRect(cx - glowR, cy - glowR, cellW + 2 * glowR, cellH + 2 * glowR, col, t.Glow * 0.35f, additive: true);
                        px.FillRect(cx, cy, cellW, cellH, col);
                    }
                    else
                    {
                        px.FillRect(cx, cy, cellW, cellH, t.Background + col * t.Unlit);
                    }
                }
            }

            if (style.ShowChannelTags && channels > 1)
                px.Text(c == 0 ? "L" : "R", x0 + 1, pad * 0.3, fontScale, t.Label, TextAlign.Left, 0.7f);

            if (style.ShowLabels)
            {
                int maxW = 0;
                for (int b = 0; b < bandCount; b++) maxW = Math.Max(maxW, PixelBuffer.TextWidth(label(b), fontScale));
                int every = Math.Max(1, (int)Math.Ceiling((maxW + 2 * fontScale) / colPitch));
                double ly = yBottom + pad * 0.55;
                for (int b = 0; b < bandCount; b += every)
                    px.Text(label(b), x0 + (b + 0.5) * colPitch, ly, fontScale, t.Label, TextAlign.Center);
            }
        }
    }

    private static Rgb Boost(Rgb c, float k)
    {
        if (k <= 1) return c * k;
        float over = k - 1;
        return Rgb.Lerp(c, Rgb.White, over);
    }
}
