using System.Runtime.CompilerServices;

namespace nSpectrum.Visuals;

/// <summary>Horizontal text alignment for <see cref="PixelBuffer.Text"/>.</summary>
public enum TextAlign { Left, Center, Right }

/// <summary>
/// A small software rasteriser: opaque 32-bit BGRA pixels (WPF Bgra32/Pbgra32, Win32 DIB, PNG after
/// swizzle), anti-aliased rectangles, lines, circles and arcs, and a crisp 5×7 pixel font.
/// Everything is clipped to <see cref="Clip"/>. Not thread-safe; nothing allocates after
/// <see cref="Resize"/>.
/// </summary>
public sealed class PixelBuffer
{
    private int _cx0, _cy0, _cx1, _cy1;

    public PixelBuffer(int width, int height)
    {
        Pixels = [];
        Resize(width, height);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Row-major pixels, 0xAARRGGBB (BGRA bytes in memory on little-endian).</summary>
    public uint[] Pixels { get; private set; }

    /// <summary>Changes the size (contents are undefined afterwards) and resets the clip.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            Pixels = new uint[width * height];
        }
        ResetClip();
    }

    /// <summary>Restricts drawing to a rectangle (intersected with the buffer).</summary>
    public void SetClip(double x, double y, double w, double h)
    {
        _cx0 = Math.Clamp((int)Math.Floor(x), 0, Width);
        _cy0 = Math.Clamp((int)Math.Floor(y), 0, Height);
        _cx1 = Math.Clamp((int)Math.Ceiling(x + w), 0, Width);
        _cy1 = Math.Clamp((int)Math.Ceiling(y + h), 0, Height);
    }

    public void ResetClip() { _cx0 = 0; _cy0 = 0; _cx1 = Width; _cy1 = Height; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(Rgb c) =>
        0xFF000000u | ((uint)(Math.Clamp(c.R, 0f, 1f) * 255f + 0.5f) << 16)
                    | ((uint)(Math.Clamp(c.G, 0f, 1f) * 255f + 0.5f) << 8)
                    | (uint)(Math.Clamp(c.B, 0f, 1f) * 255f + 0.5f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Blend(int i, Rgb c, float a)
    {
        uint p = Pixels[i];
        float r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
        r += (Math.Clamp(c.R, 0f, 1f) * 255f - r) * a;
        g += (Math.Clamp(c.G, 0f, 1f) * 255f - g) * a;
        b += (Math.Clamp(c.B, 0f, 1f) * 255f - b) * a;
        Pixels[i] = 0xFF000000u | ((uint)(r + 0.5f) << 16) | ((uint)(g + 0.5f) << 8) | (uint)(b + 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Add(int i, Rgb c, float a)
    {
        uint p = Pixels[i];
        int r = (int)((p >> 16) & 255) + (int)(c.R * a * 255f);
        int g = (int)((p >> 8) & 255) + (int)(c.G * a * 255f);
        int b = (int)(p & 255) + (int)(c.B * a * 255f);
        Pixels[i] = 0xFF000000u | ((uint)Math.Min(r, 255) << 16) | ((uint)Math.Min(g, 255) << 8) | (uint)Math.Min(b, 255);
    }

    /// <summary>Adds light to one pixel (no anti-aliasing). Ignores pixels outside the clip.</summary>
    public void AddPixel(int x, int y, Rgb c, float a)
    {
        if (x < _cx0 || x >= _cx1 || y < _cy0 || y >= _cy1) return;
        Add(y * Width + x, c, a);
    }

    /// <summary>Reads one pixel back as a colour (tests, previews).</summary>
    public Rgb Get(int x, int y)
    {
        uint p = Pixels[y * Width + x];
        return new Rgb(((p >> 16) & 255) / 255f, ((p >> 8) & 255) / 255f, (p & 255) / 255f);
    }

    public void Clear(Rgb c) => Pixels.AsSpan().Fill(Pack(c));

    /// <summary>Filled rectangle with anti-aliased edges. <paramref name="additive"/> adds light instead of covering.</summary>
    public void FillRect(double x, double y, double w, double h, Rgb c, float alpha = 1, bool additive = false)
    {
        if (w <= 0 || h <= 0 || alpha <= 0) return;
        double x1 = x + w, y1 = y + h;
        int ix0 = Math.Max(_cx0, (int)Math.Floor(x)), ix1 = Math.Min(_cx1, (int)Math.Ceiling(x1));
        int iy0 = Math.Max(_cy0, (int)Math.Floor(y)), iy1 = Math.Min(_cy1, (int)Math.Ceiling(y1));
        if (ix0 >= ix1 || iy0 >= iy1) return;
        uint solid = Pack(c);
        for (int py = iy0; py < iy1; py++)
        {
            float cy = (float)(Math.Min(y1, py + 1) - Math.Max(y, py));
            int row = py * Width;
            for (int px = ix0; px < ix1; px++)
            {
                float cx = (float)(Math.Min(x1, px + 1) - Math.Max(x, px));
                float a = cx * cy * alpha;
                if (a <= 0.002f) continue;
                if (additive) Add(row + px, c, a);
                else if (a >= 0.998f) Pixels[row + px] = solid;
                else Blend(row + px, c, a);
            }
        }
    }

    /// <summary>Rectangle filled with a vertical gradient from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    public void FillGradient(double x, double y, double w, double h, Rgb top, Rgb bottom)
    {
        int iy0 = Math.Max(_cy0, (int)Math.Floor(y)), iy1 = Math.Min(_cy1, (int)Math.Ceiling(y + h));
        for (int py = iy0; py < iy1; py++)
        {
            float t = h <= 1 ? 0 : (float)Math.Clamp((py + 0.5 - y) / h, 0, 1);
            double ry = Math.Max(y, py), ry1 = Math.Min(y + h, py + 1);
            FillRect(x, ry, w, ry1 - ry, Rgb.Lerp(top, bottom, t));
        }
    }

    /// <summary>Rectangle of one colour whose opacity fades from <paramref name="alphaTop"/> to <paramref name="alphaBottom"/>.</summary>
    public void FadeRect(double x, double y, double w, double h, Rgb c, float alphaTop, float alphaBottom, bool additive = false)
    {
        int iy0 = Math.Max(_cy0, (int)Math.Floor(y)), iy1 = Math.Min(_cy1, (int)Math.Ceiling(y + h));
        for (int py = iy0; py < iy1; py++)
        {
            float t = h <= 1 ? 0 : (float)Math.Clamp((py + 0.5 - y) / h, 0, 1);
            double ry = Math.Max(y, py), ry1 = Math.Min(y + h, py + 1);
            FillRect(x, ry, w, ry1 - ry, c, alphaTop + (alphaBottom - alphaTop) * t, additive);
        }
    }

    /// <summary>Rectangle outline of width <paramref name="t"/>, drawn inside the rectangle.</summary>
    public void StrokeRect(double x, double y, double w, double h, double t, Rgb c, float alpha = 1)
    {
        FillRect(x, y, w, t, c, alpha);
        FillRect(x, y + h - t, w, t, c, alpha);
        FillRect(x, y + t, t, h - 2 * t, c, alpha);
        FillRect(x + w - t, y + t, t, h - 2 * t, c, alpha);
    }

    /// <summary>Anti-aliased line with round caps.</summary>
    public void Line(double x0, double y0, double x1, double y1, double width, Rgb c, float alpha = 1, bool additive = false)
    {
        double r = Math.Max(width, 0.5) / 2;
        int ix0 = Math.Max(_cx0, (int)Math.Floor(Math.Min(x0, x1) - r - 1)), ix1 = Math.Min(_cx1, (int)Math.Ceiling(Math.Max(x0, x1) + r + 1));
        int iy0 = Math.Max(_cy0, (int)Math.Floor(Math.Min(y0, y1) - r - 1)), iy1 = Math.Min(_cy1, (int)Math.Ceiling(Math.Max(y0, y1) + r + 1));
        double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
        for (int py = iy0; py < iy1; py++)
        {
            for (int px = ix0; px < ix1; px++)
            {
                double qx = px + 0.5 - x0, qy = py + 0.5 - y0;
                double t = len2 > 0 ? Math.Clamp((qx * dx + qy * dy) / len2, 0, 1) : 0;
                double ex = qx - t * dx, ey = qy - t * dy;
                double d = Math.Sqrt(ex * ex + ey * ey);
                float a = (float)Math.Clamp(r + 0.5 - d, 0, 1) * alpha;
                if (width < 1) a *= (float)width;
                if (a <= 0.002f) continue;
                if (additive) Add(py * Width + px, c, a); else Blend(py * Width + px, c, a);
            }
        }
    }

    /// <summary>Anti-aliased filled circle.</summary>
    public void FillCircle(double cx, double cy, double radius, Rgb c, float alpha = 1, bool additive = false)
    {
        int ix0 = Math.Max(_cx0, (int)Math.Floor(cx - radius - 1)), ix1 = Math.Min(_cx1, (int)Math.Ceiling(cx + radius + 1));
        int iy0 = Math.Max(_cy0, (int)Math.Floor(cy - radius - 1)), iy1 = Math.Min(_cy1, (int)Math.Ceiling(cy + radius + 1));
        for (int py = iy0; py < iy1; py++)
        {
            for (int px = ix0; px < ix1; px++)
            {
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                float a = (float)Math.Clamp(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1) * alpha;
                if (a <= 0.002f) continue;
                if (additive) Add(py * Width + px, c, a); else Blend(py * Width + px, c, a);
            }
        }
    }

    /// <summary>Soft radial light (quadratic fall-off), added to the image.</summary>
    public void Glow(double cx, double cy, double radius, Rgb c, float intensity)
    {
        int ix0 = Math.Max(_cx0, (int)Math.Floor(cx - radius)), ix1 = Math.Min(_cx1, (int)Math.Ceiling(cx + radius));
        int iy0 = Math.Max(_cy0, (int)Math.Floor(cy - radius)), iy1 = Math.Min(_cy1, (int)Math.Ceiling(cy + radius));
        double inv = 1 / (radius * radius);
        for (int py = iy0; py < iy1; py++)
        {
            for (int px = ix0; px < ix1; px++)
            {
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                double q = 1 - (dx * dx + dy * dy) * inv;
                if (q <= 0) continue;
                Add(py * Width + px, c, (float)(q * q) * intensity);
            }
        }
    }

    /// <summary>
    /// Arc stroke centred on (<paramref name="cx"/>, <paramref name="cy"/>). Angles in radians,
    /// measured clockwise from straight up; <paramref name="a0"/> &lt; <paramref name="a1"/>.
    /// </summary>
    public void Arc(double cx, double cy, double radius, double width, double a0, double a1, Rgb c, float alpha = 1)
    {
        double ro = radius + width / 2 + 1;
        int ix0 = Math.Max(_cx0, (int)Math.Floor(cx - ro)), ix1 = Math.Min(_cx1, (int)Math.Ceiling(cx + ro));
        int iy0 = Math.Max(_cy0, (int)Math.Floor(cy - ro)), iy1 = Math.Min(_cy1, (int)Math.Ceiling(cy + ro));
        double hw = width / 2;
        for (int py = iy0; py < iy1; py++)
        {
            for (int px = ix0; px < ix1; px++)
            {
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                double radial = Math.Clamp(hw + 0.5 - Math.Abs(d - radius), 0, 1);
                if (radial <= 0) continue;
                double ang = Math.Atan2(dx, -dy);
                // Angular coverage, converted to pixels along the arc so the ends are anti-aliased too.
                double along = Math.Min(ang - a0, a1 - ang) * d;
                double ac = Math.Clamp(along + 0.5, 0, 1);
                float a = (float)(radial * ac) * alpha;
                if (width < 1) a *= (float)width;
                if (a <= 0.002f) continue;
                Blend(py * Width + px, c, a);
            }
        }
    }

    /// <summary>Point on a circle at angle <paramref name="a"/> (clockwise from up).</summary>
    public static (double X, double Y) Polar(double cx, double cy, double radius, double a) =>
        (cx + radius * Math.Sin(a), cy - radius * Math.Cos(a));

    /// <summary>Width in pixels of <paramref name="text"/> at integer <paramref name="scale"/>.</summary>
    public static int TextWidth(string text, int scale) => text.Length == 0 ? 0 : (text.Length * 6 - 1) * Math.Max(1, scale);

    /// <summary>Height in pixels of a text line at <paramref name="scale"/>.</summary>
    public static int TextHeight(int scale) => 7 * Math.Max(1, scale);

    /// <summary>Crisp 5×7 pixel text. <paramref name="y"/> is the top of the glyphs.</summary>
    public void Text(string text, double x, double y, int scale, Rgb c, TextAlign align = TextAlign.Left, float alpha = 1)
    {
        scale = Math.Max(1, scale);
        int w = TextWidth(text, scale);
        int px = (int)Math.Round(align switch { TextAlign.Center => x - w / 2.0, TextAlign.Right => x - w, _ => x });
        int py = (int)Math.Round(y);
        foreach (char ch in text)
        {
            ReadOnlySpan<byte> g = PixelFont.Glyph(ch);
            for (int row = 0; row < 7; row++)
            {
                byte bits = g[row];
                if (bits == 0) continue;
                for (int col = 0; col < 5; col++)
                {
                    if ((bits & (0x10 >> col)) == 0) continue;
                    // Merge horizontal runs into one rectangle.
                    int run = 1;
                    while (col + run < 5 && (bits & (0x10 >> (col + run))) != 0) run++;
                    FillRect(px + col * scale, py + row * scale, run * scale, scale, c, alpha);
                    col += run - 1;
                }
            }
            px += 6 * scale;
        }
    }
}
