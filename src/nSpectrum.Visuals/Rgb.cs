using System.Globalization;

namespace nSpectrum.Visuals;

/// <summary>An sRGB colour with float channels in 0..1 (values above 1 are clamped when written).</summary>
public readonly record struct Rgb(float R, float G, float B)
{
    public static Rgb Hex(string hex)
    {
        hex = hex.TrimStart('#');
        uint v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
    }

    public static Rgb operator *(Rgb c, float k) => new(c.R * k, c.G * k, c.B * k);
    public static Rgb operator +(Rgb a, Rgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B);

    public static Rgb Lerp(Rgb a, Rgb b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    /// <summary>Hue 0..1, saturation and value 0..1.</summary>
    public static Rgb Hsv(float h, float s, float v)
    {
        h = (h % 1 + 1) % 1 * 6;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => new(v, t, p),
            1 => new(q, v, p),
            2 => new(p, v, t),
            3 => new(p, q, v),
            4 => new(t, p, v),
            _ => new(v, p, q),
        };
    }

    public static Rgb Black => new(0, 0, 0);
    public static Rgb White => new(1, 1, 1);
}
