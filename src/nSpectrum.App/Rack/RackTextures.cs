using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace nSpectrum.App.Rack;

/// <summary>
/// Procedural textures for the rack skin: brushed metal for the faceplates and wood grain for the
/// side cheeks. Generated once at startup (a few milliseconds) so the app ships no image files.
/// </summary>
internal static class RackTextures
{
    /// <summary>Adds the texture brushes to the application resources under their keys.</summary>
    public static void Register(ResourceDictionary resources)
    {
        resources["RackBrushedTexture"] = BrushedMetal();
        resources["RackWoodTexture"] = Wood();
    }

    /// <summary>
    /// White horizontal streaks with varying alpha, laid over the faceplate gradient. Each row has
    /// its own brightness that drifts slowly along x, like a brushed aluminium plate.
    /// </summary>
    private static ImageBrush BrushedMetal()
    {
        const int w = 1024, h = 512;
        var rnd = new Random(7);
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            double rowBase = rnd.NextDouble();
            double phase = rnd.NextDouble() * Math.PI * 2;
            double freq = 1 + rnd.NextDouble() * 3;
            double streak = rnd.NextDouble() < 0.06 ? 0.5 + rnd.NextDouble() * 0.5 : 0; // occasional deeper scratch
            for (int x = 0; x < w; x++)
            {
                double drift = 0.5 + 0.5 * Math.Sin(phase + freq * x * Math.PI * 2 / w);
                double v = 0.55 * rowBase + 0.30 * drift + 0.15 * rnd.NextDouble() + streak * 0.35;
                // Alpha 0..~40: the gradient underneath does the shading, this only adds grain.
                uint a = (uint)Math.Clamp(v * 40, 0, 255);
                uint c = rowBase > 0.5 ? 255u : 0u; // light and dark streaks
                px[y * w + x] = (a << 24) | (c << 16) | (c << 8) | c;
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, Premultiply(px), w * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill, Opacity = 0.55 };
        brush.Freeze();
        return brush;
    }

    /// <summary>Dark vertical grain with knots, alpha only, laid over the cheek's brown gradient.</summary>
    private static ImageBrush Wood()
    {
        const int w = 64, h = 512;
        var rnd = new Random(4);
        var px = new uint[w * h];
        var cols = new double[w];
        for (int x = 0; x < w; x++) cols[x] = rnd.NextDouble();
        // Smooth the column darkness a little so the grain lines are a few pixels wide.
        for (int pass = 0; pass < 2; pass++)
            for (int x = 1; x < w - 1; x++) cols[x] = (cols[x - 1] + 2 * cols[x] + cols[x + 1]) / 4;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double wave = Math.Sin(y * 0.021 + x * 0.35 + 3 * Math.Sin(y * 0.004 + x * 0.05));
                double v = 0.55 * cols[x] + 0.30 * (0.5 + 0.5 * wave) + 0.15 * rnd.NextDouble();
                uint a = (uint)Math.Clamp(Math.Pow(v, 1.6) * 150, 0, 255);
                px[y * w + x] = (a << 24) | (0x1Eu << 16) | (0x0Cu << 8) | 0x02u;
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, Premultiply(px), w * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    // Pbgra32 wants premultiplied colour.
    private static uint[] Premultiply(uint[] px)
    {
        for (int i = 0; i < px.Length; i++)
        {
            uint p = px[i], a = p >> 24;
            uint r = ((p >> 16) & 0xFF) * a / 255, g = ((p >> 8) & 0xFF) * a / 255, b = (p & 0xFF) * a / 255;
            px[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }
        return px;
    }
}
