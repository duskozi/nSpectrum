using System.Globalization;

namespace nSpectrum.Core.Dsp;

/// <summary>One fractional-octave band (IEC 61260-1:2014, base-10).</summary>
public readonly record struct OctaveBand(double Center, double Lower, double Upper, string Label);

/// <summary>
/// Fractional-octave bands (1/1 to 1/24 octave) and their levels computed from an FFT spectrum by
/// summing bin powers over each band ("FFT synthesis", the fast method of the plan). The IIR
/// filter-bank reference method arrives in Phase 2; this one is exact for noise and tones whose
/// main lobe lies inside a band, and reads the band's share of a bin when a band is narrower
/// than one bin (see <see cref="MinBinsPerBand"/>).
/// </summary>
public sealed class OctaveBands
{
    /// <summary>Octave ratio G = 10^(3/10) (base-10 system).</summary>
    public const double G = 1.9952623149688795;

    private readonly int[] _firstBin;
    private readonly float[][] _weights;

    /// <param name="fraction">Bands per octave: 1, 2, 3, 6, 12 or 24.</param>
    public OctaveBands(int fraction, double minHz, double maxHz, double binWidth, int binCount)
        : this(Validated(fraction), Create(fraction, minHz, maxHz), binWidth, binCount)
    {
    }

    /// <summary>Any list of bands (e.g. <see cref="LogSpaced"/>), summed the same way. <see cref="Fraction"/> is 0.</summary>
    public OctaveBands(IReadOnlyList<OctaveBand> bands, double binWidth, int binCount)
        : this(0, bands, binWidth, binCount)
    {
    }

    private static int Validated(int fraction) =>
        fraction is 1 or 2 or 3 or 6 or 12 or 24 ? fraction : throw new ArgumentOutOfRangeException(nameof(fraction));

    private OctaveBands(int fraction, IReadOnlyList<OctaveBand> bands, double binWidth, int binCount)
    {
        Fraction = fraction;
        Bands = bands;
        BinWidth = binWidth;
        BinCount = binCount;

        _firstBin = new int[Bands.Count];
        _weights = new float[Bands.Count][];
        double minBins = double.MaxValue;
        for (int b = 0; b < Bands.Count; b++)
        {
            double lo = Bands[b].Lower / binWidth, hi = Bands[b].Upper / binWidth; // in bins
            minBins = Math.Min(minBins, hi - lo);
            // Bin k covers [k - 0.5, k + 0.5) in bin units; weight = overlap with [lo, hi).
            int k0 = Math.Max(0, (int)Math.Floor(lo + 0.5));
            int k1 = Math.Min(binCount - 1, (int)Math.Ceiling(hi - 0.5));
            var w = new float[Math.Max(0, k1 - k0 + 1)];
            for (int k = k0; k <= k1; k++)
            {
                double overlap = Math.Min(hi, k + 0.5) - Math.Max(lo, k - 0.5);
                w[k - k0] = (float)Math.Clamp(overlap, 0, 1);
            }
            _firstBin[b] = k0;
            _weights[b] = w;
        }
        MinBinsPerBand = minBins;
    }

    public int Fraction { get; }
    public IReadOnlyList<OctaveBand> Bands { get; }
    public double BinWidth { get; }
    public int BinCount { get; }

    /// <summary>Width in bins of the narrowest band. Below about 4, low bands are under-resolved: use a larger FFT.</summary>
    public double MinBinsPerBand { get; }

    /// <summary>
    /// Band powers (linear, same reference as the frame) from a spectrum frame of any
    /// <see cref="SpectrumScale"/>. Allocates nothing.
    /// </summary>
    public void Compute(ReadOnlySpan<float> values, SpectrumInfo info, Span<float> bandPower)
    {
        if (bandPower.Length < Bands.Count) throw new ArgumentException("Too small.", nameof(bandPower));
        // Convert each bin to "power in this bin" so bins can be summed.
        float toBinPower = info.Scale switch
        {
            SpectrumScale.Amplitude => (float)(1.0 / info.Window.EnbwBins),
            SpectrumScale.Power => 1f,
            _ => (float)info.BinWidth,
        };
        for (int b = 0; b < _weights.Length; b++)
        {
            float[] w = _weights[b];
            int k0 = _firstBin[b];
            float sum = 0;
            for (int i = 0; i < w.Length; i++) sum += w[i] * values[k0 + i];
            bandPower[b] = sum * toBinPower;
        }
    }

    /// <summary>The bands of 1/<paramref name="fraction"/> octave whose centres lie in [minHz, maxHz].</summary>
    public static IReadOnlyList<OctaveBand> Create(int fraction, double minHz, double maxHz)
    {
        var list = new List<OctaveBand>();
        double half = Math.Pow(G, 1.0 / (2 * fraction));
        for (int x = -200; x <= 200; x++)
        {
            // IEC 61260-1: odd b → G^(x/b), even b → G^((2x+1)/(2b)), relative to 1 kHz.
            double fm = fraction % 2 == 1
                ? 1000 * Math.Pow(G, (double)x / fraction)
                : 1000 * Math.Pow(G, (2.0 * x + 1) / (2 * fraction));
            if (fm < minHz * 0.99 || fm > maxHz * 1.01) continue; // nominal 20 Hz is 19.95 Hz exact
            list.Add(new OctaveBand(fm, fm / half, fm * half, NominalLabel(fm, fraction)));
        }
        return list;
    }

    /// <summary>
    /// <paramref name="count"/> bands of equal width on a log axis whose edges run from
    /// <paramref name="minHz"/> to <paramref name="maxHz"/> (the band layout of consumer LED analyzers).
    /// Labels are the centre frequency rounded to 2 significant digits ("45", "1.2k", "14k").
    /// </summary>
    public static IReadOnlyList<OctaveBand> LogSpaced(int count, double minHz, double maxHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (!(minHz > 0) || !(maxHz > minHz)) throw new ArgumentOutOfRangeException(nameof(maxHz));
        var list = new OctaveBand[count];
        double step = Math.Log(maxHz / minHz) / count;
        for (int b = 0; b < count; b++)
        {
            double lo = minHz * Math.Exp(step * b), hi = minHz * Math.Exp(step * (b + 1));
            double fc = Math.Sqrt(lo * hi);
            list[b] = new OctaveBand(fc, lo, hi, ShortLabel(fc));
        }
        return list;
    }

    /// <summary>Compact frequency label with 2 significant digits: 45, 230, 1.2k, 14k.</summary>
    public static string ShortLabel(double hz)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(hz)) - 1);
        double f = Math.Round(hz / mag) * mag;
        if (f >= 10000) return (f / 1000).ToString("0", CultureInfo.InvariantCulture) + "k";
        if (f >= 1000) return (f / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return f.ToString("0", CultureInfo.InvariantCulture);
    }

    private static readonly double[] ThirdOctaveNominal =
    [
        10, 12.5, 16, 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
        1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000, 25000,
        31500, 40000, 50000, 63000, 80000, 100000,
    ];

    /// <summary>Nominal label: the ISO preferred number for 1/1 and 1/3 octave, 3 significant digits otherwise.</summary>
    public static string NominalLabel(double fm, int fraction)
    {
        double f = fm;
        if (fraction is 1 or 3)
        {
            double best = ThirdOctaveNominal[0];
            foreach (double n in ThirdOctaveNominal)
                if (Math.Abs(Math.Log(n / fm)) < Math.Abs(Math.Log(best / fm))) best = n;
            f = best;
        }
        else
        {
            double mag = Math.Pow(10, Math.Floor(Math.Log10(fm)) - 2);
            f = Math.Round(fm / mag) * mag;
        }
        if (f >= 1000)
        {
            double k = f / 1000;
            if (Math.Abs(k - Math.Round(k)) < 1e-9) return k.ToString("0", CultureInfo.InvariantCulture) + "k";
            // 1k25, 3k15, 12k5 style
            string s = k.ToString("0.##", CultureInfo.InvariantCulture);
            return s.Replace(".", "k");
        }
        return f.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
