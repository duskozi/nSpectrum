using System.Numerics;

namespace nSpectrum.Wpf;

/// <summary>The built-in looks. All are our own designs, drawn entirely in pixel shaders.</summary>
public enum SpectrumSkin
{
    /// <summary>Plain, precise spectrum for reading values: wide range, no glow.</summary>
    Measurement,
    /// <summary>Translucent gradient bars with a white RMS curve and a gold peak curve, soft glow.</summary>
    Aurora,
    /// <summary>Segmented LED bars on fractional-octave bands, unlit segments visible, peak caps.</summary>
    LedRack,
    /// <summary>Round LEDs in a dot matrix, green/yellow/red zones, white-hot peak dots.</summary>
    DotMatrix,
    /// <summary>Spectrum on top and a scrolling waterfall (spectrogram) below.</summary>
    Spectrogram,
}

/// <summary>Colours and shape parameters of a skin. Colours are linear RGB; values above 1 glow.</summary>
public sealed record SkinTheme
{
    public Vector4 ColorLow { get; init; }
    public Vector4 ColorMid { get; init; }
    public Vector4 ColorHigh { get; init; }
    public Vector4 PeakColor { get; init; }
    public Vector4 CurveColor { get; init; }
    public Vector4 Background0 { get; init; }
    public Vector4 Background1 { get; init; }
    public Vector4 GridColor { get; init; }
    public Vector4 UnlitColor { get; init; }
    public float CellGap { get; init; }
    public float BarGap { get; init; }
    public float ZoneYellow { get; init; } = 0.7f;
    public float ZoneRed { get; init; } = 0.88f;
    public float Glow { get; init; }
    public float FillAlpha { get; init; } = 1;
    public float CurveThickness { get; init; } = 1.1f;
    public float BloomIntensity { get; init; }
    public float BloomThreshold { get; init; } = 0.75f;
    public float Vignette { get; init; }

    private static Vector4 C(float r, float g, float b, float a = 1) => new(r, g, b, a);

    public static SkinTheme Measurement { get; } = new()
    {
        ColorLow = C(0.06f, 0.45f, 0.42f), ColorMid = C(0.24f, 0.78f, 0.52f), ColorHigh = C(0.62f, 0.95f, 0.45f),
        PeakColor = C(1.0f, 0.76f, 0.30f), CurveColor = C(0, 0, 0, 0),
        Background0 = C(0.055f, 0.065f, 0.085f), Background1 = C(0.03f, 0.035f, 0.045f),
        GridColor = C(0.6f, 0.66f, 0.74f, 0.16f), UnlitColor = C(0, 0, 0, 0),
        Glow = 0, FillAlpha = 0.95f, CurveThickness = 0.9f, BloomIntensity = 0, Vignette = 0,
    };

    public static SkinTheme Aurora { get; } = new()
    {
        ColorLow = C(0.04f, 0.42f, 0.70f), ColorMid = C(0.20f, 0.90f, 0.70f), ColorHigh = C(0.95f, 0.95f, 0.55f),
        PeakColor = C(1.0f, 0.74f, 0.28f), CurveColor = C(0.92f, 0.96f, 1.0f, 0.9f),
        Background0 = C(0.055f, 0.07f, 0.13f), Background1 = C(0.008f, 0.01f, 0.018f),
        GridColor = C(0.55f, 0.65f, 0.85f, 0.10f), UnlitColor = C(0, 0, 0, 0),
        Glow = 0.6f, FillAlpha = 0.55f, CurveThickness = 1.1f, BloomIntensity = 0.9f, Vignette = 0.35f,
    };

    public static SkinTheme LedRack { get; } = new()
    {
        ColorLow = C(0.16f, 1.0f, 0.10f), ColorMid = C(1.0f, 0.78f, 0.05f), ColorHigh = C(1.0f, 0.16f, 0.10f),
        PeakColor = C(1.0f, 0.20f, 0.12f), CurveColor = C(0, 0, 0, 0),
        Background0 = C(0.020f, 0.024f, 0.028f), Background1 = C(0.004f, 0.004f, 0.006f),
        GridColor = C(0, 0, 0, 0), UnlitColor = C(0.10f, 0.13f, 0.11f, 0.16f),
        CellGap = 0.34f, BarGap = 0.24f, ZoneYellow = 0.70f, ZoneRed = 0.87f,
        Glow = 0.7f, BloomIntensity = 0.9f, Vignette = 0.35f,
    };

    /// <summary>
    /// The LED rack as seen through the dark glass of a hi-fi front panel (the app's rack skin):
    /// green, yellow and red zones, faint unlit segments, a fine grid and a warm peak cap.
    /// </summary>
    public static SkinTheme RetroRack { get; } = LedRack with
    {
        ColorLow = C(0.19f, 0.87f, 0.14f), ColorMid = C(0.93f, 0.85f, 0.12f), ColorHigh = C(1.0f, 0.24f, 0.09f),
        PeakColor = C(1.0f, 0.42f, 0.08f),
        Background0 = C(0.030f, 0.045f, 0.038f), Background1 = C(0.012f, 0.018f, 0.016f),
        GridColor = C(0.47f, 0.59f, 0.51f, 0.16f), UnlitColor = C(0.10f, 0.14f, 0.11f, 0.10f),
        CellGap = 0.28f, BarGap = 0.18f, ZoneYellow = 0.55f, ZoneRed = 0.83f,
        Glow = 0.7f, BloomIntensity = 0.8f, Vignette = 0.18f,
    };

    public static SkinTheme DotMatrix { get; } = new()
    {
        ColorLow = C(0.22f, 1.0f, 0.16f), ColorMid = C(1.0f, 0.76f, 0.08f), ColorHigh = C(1.0f, 0.18f, 0.12f),
        PeakColor = C(1.0f, 0.92f, 0.80f), CurveColor = C(0, 0, 0, 0),
        Background0 = C(0.018f, 0.020f, 0.024f), Background1 = C(0.003f, 0.003f, 0.004f),
        GridColor = C(0, 0, 0, 0), UnlitColor = C(0.11f, 0.12f, 0.12f, 0.20f),
        CellGap = 0, BarGap = 0, ZoneYellow = 0.66f, ZoneRed = 0.86f,
        Glow = 0.65f, BloomIntensity = 0.9f, Vignette = 0.35f,
    };

    public static SkinTheme Spectrogram { get; } = Aurora with
    {
        Background0 = C(0.04f, 0.05f, 0.09f), Glow = 0.5f, CurveThickness = 1.0f,
    };

    public static SkinTheme For(SpectrumSkin skin) => skin switch
    {
        SpectrumSkin.Aurora => Aurora,
        SpectrumSkin.LedRack => LedRack,
        SpectrumSkin.DotMatrix => DotMatrix,
        SpectrumSkin.Spectrogram => Spectrogram,
        _ => Measurement,
    };
}
