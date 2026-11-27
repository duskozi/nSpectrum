namespace nSpectrum.Visuals;

/// <summary>Colour scheme for LED-style displays (band analyzer, digital meters, correlation bar).</summary>
public sealed record LedTheme
{
    public required string Name { get; init; }

    /// <summary>Lit colour for a segment at height <c>level</c> (0..1) in column <c>column</c> (0..1).</summary>
    public required Func<float, float, Rgb> Lit { get; init; }

    /// <summary>Brightness of unlit segments relative to their lit colour.</summary>
    public float Unlit { get; init; } = 0.12f;

    /// <summary>Peak cap brightness multiplier (values above 1 push toward white).</summary>
    public float PeakBoost { get; init; } = 1.15f;

    public Rgb Background { get; init; } = Rgb.Hex("#050607");
    public Rgb Background2 { get; init; } = Rgb.Hex("#0B0D0F");
    public Rgb Label { get; init; } = Rgb.Hex("#8E979F");
    public Rgb Divider { get; init; } = Rgb.Hex("#16191C");

    /// <summary>Glow strength around lit segments.</summary>
    public float Glow { get; init; } = 0.16f;

    private static Rgb Zones(float level, Rgb low, Rgb mid, Rgb high, float yellow = 0.62f, float red = 0.84f) =>
        level >= red ? high : level >= yellow ? mid : low;

    public static LedTheme ClassicGreen { get; } = new()
    {
        Name = "Classic green",
        Lit = (l, _) => Zones(l, Rgb.Hex("#3BFF2E"), Rgb.Hex("#FFC81A"), Rgb.Hex("#FF2A1E")),
    };

    public static LedTheme Amber { get; } = new()
    {
        Name = "Amber",
        Lit = (l, _) => Zones(l, Rgb.Hex("#FFA417"), Rgb.Hex("#FFC23A"), Rgb.Hex("#FF4E1A"), 0.70f, 0.88f),
        Background = Rgb.Hex("#070503"), Background2 = Rgb.Hex("#0E0A06"),
        Label = Rgb.Hex("#A8865A"),
    };

    public static LedTheme IceBlue { get; } = new()
    {
        Name = "Ice blue",
        Lit = (l, _) => Zones(l, Rgb.Hex("#25B8FF"), Rgb.Hex("#7FE3FF"), Rgb.Hex("#F2FBFF"), 0.66f, 0.88f),
        Background = Rgb.Hex("#03060A"), Background2 = Rgb.Hex("#071019"),
        Label = Rgb.Hex("#7894AA"), Unlit = 0.14f,
    };

    public static LedTheme Vfd { get; } = new()
    {
        Name = "VFD teal",
        Lit = (l, _) => Zones(l, Rgb.Hex("#3DF5D0"), Rgb.Hex("#3DF5D0"), Rgb.Hex("#FF5A4A"), 0.99f, 0.90f),
        Background = Rgb.Hex("#020706"), Background2 = Rgb.Hex("#06100E"),
        Label = Rgb.Hex("#4FA696"), Unlit = 0.10f, Glow = 0.22f,
    };

    public static LedTheme Ruby { get; } = new()
    {
        Name = "Ruby",
        Lit = (l, _) => Rgb.Lerp(Rgb.Hex("#C8102A"), Rgb.Hex("#FF6A4D"), Math.Clamp((l - 0.6f) / 0.4f, 0, 1)),
        Background = Rgb.Hex("#070203"), Background2 = Rgb.Hex("#100507"),
        Label = Rgb.Hex("#A0646C"), Unlit = 0.14f,
    };

    public static LedTheme Sunset { get; } = new()
    {
        Name = "Sunset",
        Lit = (l, _) => l < 0.5f
            ? Rgb.Lerp(Rgb.Hex("#7A3CFF"), Rgb.Hex("#FF3FA4"), l / 0.5f)
            : Rgb.Lerp(Rgb.Hex("#FF3FA4"), Rgb.Hex("#FFB13B"), Math.Min(1, (l - 0.5f) / 0.45f)),
        Background = Rgb.Hex("#060309"), Background2 = Rgb.Hex("#0D0714"),
        Label = Rgb.Hex("#9A7FB0"), Unlit = 0.13f,
    };

    public static LedTheme Rainbow { get; } = new()
    {
        Name = "Rainbow",
        Lit = (l, c) => Rgb.Hsv(0.78f * c, 0.85f, 1f) * (0.80f + 0.2f * l),
        Unlit = 0.13f,
    };

    public static IReadOnlyList<LedTheme> All { get; } = [ClassicGreen, Amber, IceBlue, Vfd, Ruby, Sunset, Rainbow];

    public static LedTheme ByName(string? name) => All.FirstOrDefault(t => t.Name == name) ?? ClassicGreen;
}
