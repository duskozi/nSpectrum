namespace nSpectrum.Core.Dsp;

/// <summary>Harmonic distortion of the strongest tone in a spectrum.</summary>
/// <param name="FundamentalHz">Fundamental frequency, corrected between bins.</param>
/// <param name="FundamentalDb">Fundamental level in the frame's unit (scalloping corrected for tone readings).</param>
/// <param name="Thd">Total harmonic distortion as an amplitude ratio: √(ΣHk² / H1²), harmonics inside the bandwidth only.</param>
/// <param name="ThdN">THD + noise as an amplitude ratio: √(everything but the fundamental / H1²) over the bandwidth.</param>
/// <param name="HarmonicsDbc">Level of H2, H3, … relative to the fundamental (dBc), as many as fit in the bandwidth.</param>
/// <param name="BandwidthHz">Upper edge of the measurement bandwidth (the lower edge is 20 Hz).</param>
public sealed record DistortionReading(
    double FundamentalHz,
    double FundamentalDb,
    double Thd,
    double ThdN,
    IReadOnlyList<double> HarmonicsDbc,
    double BandwidthHz)
{
    public double ThdPercent => Thd * 100;
    public double ThdDb => 20 * Math.Log10(Math.Max(Thd, 1e-15));
    public double ThdNPercent => ThdN * 100;
    public double ThdNDb => 20 * Math.Log10(Math.Max(ThdN, 1e-15));

    /// <summary>SINAD in dB (the inverse of THD+N).</summary>
    public double SinadDb => -ThdNDb;
}

/// <summary>
/// Measures THD and THD+N from a spectrum frame, the way an FFT audio analyzer does: the
/// fundamental and each harmonic are integrated over their main lobe, everything else between
/// 20 Hz and the bandwidth limit is noise. Noise hidden under the excluded lobes is filled in at
/// the average noise level around them.
/// </summary>
/// <remarks>
/// <para>The residual floor depends on the window's side lobes, because leakage of the fundamental
/// outside its integration span counts as distortion: Blackman-Harris 7-term reaches below
/// −130 dB (use it for converters and good amplifiers); 4-term Blackman-Harris about −90 dB;
/// flat-top about −80 dB; Hann about −85 dB (with a wide span, which needs fundamentals of a few hundred Hz at
/// 8k points); Kaiser β = 9 about −70 dB. Rectangular is not supported (returns null).</para>
/// <para>Works on any <see cref="SpectrumScale"/>. Allocates only the small result record.</para>
/// </remarks>
public sealed class HarmonicAnalyzer
{
    /// <summary>Highest harmonic included in THD.</summary>
    public const int MaxHarmonic = 10;

    private readonly SpectrumInfo _info;
    private readonly ToneEstimator _estimator;
    private readonly int _span; // integration half-width around each component, FFT bins
    private readonly double[] _harm = new double[MaxHarmonic + 1];

    public HarmonicAnalyzer(SpectrumInfo info, ToneEstimator? estimator = null)
    {
        _info = info;
        _estimator = estimator ?? new ToneEstimator(info);
        double pad = (double)info.FftSize / info.Window.Length;
        double lobe = info.Settings.Window switch
        {
            WindowType.Hann => 40,             // side lobes fall 18 dB/oct from −31 dB: need a wide span
            WindowType.BlackmanHarris4 => 7,   // main lobe ±4, side lobes −92 dB
            WindowType.BlackmanHarris7 => 10,  // main lobe ±7, side lobes below −160 dB
            WindowType.FlatTop => 8,           // main lobe ±5; far side lobes stay high, floor about −80 dB
            WindowType.Kaiser => Math.Sqrt(1 + Math.Pow(info.Settings.KaiserBeta / Math.PI, 2)) + 12,
            _ => 0,
        };
        _span = (int)Math.Ceiling(lobe * pad);
    }

    /// <summary>Integration half-width in bins around the fundamental and each harmonic.</summary>
    public int SpanBins => _span;

    /// <summary>
    /// Measures the strongest tone between 20 Hz and <paramref name="bandwidthHz"/> (clamped to
    /// just below Nyquist). Returns null when there is no usable tone, when the window is
    /// rectangular, or when the fundamental is too low to separate its harmonics at this FFT size.
    /// </summary>
    public DistortionReading? Measure(ReadOnlySpan<float> values, double bandwidthHz = 20000)
    {
        if (_span == 0) return null;
        var info = _info;
        double bw = info.BinWidth;
        int last = info.BinCount - 1;
        double upperHz = Math.Min(bandwidthHz, info.SampleRate / 2.0 * 0.99);
        int lo = Math.Max(1, (int)Math.Ceiling(20 / bw));
        int hi = Math.Min(last - 1, (int)Math.Floor(upperHz / bw));
        if (hi - lo < 4 * _span) return null;

        ToneReading tone = _estimator.FindPeak(values, info, lo, hi);
        double f0 = tone.Frequency;
        int k0 = tone.Bin;
        // Harmonics must be at least two spans apart, or their lobes merge with the fundamental's.
        if (f0 / bw < 2 * _span + 1) return null;
        if (k0 - _span < lo || k0 + _span > hi) return null;

        // Ratios do not depend on the scale; the level does. Power in one bin, as OctaveBands uses.
        double toBinPower = info.Scale switch
        {
            SpectrumScale.Amplitude => 1.0 / info.Window.EnbwBins,
            SpectrumScale.Power => 1.0,
            _ => bw,
        };

        double total = Sum(values, lo, hi);
        double p1 = Sum(values, k0 - _span, k0 + _span);
        if (!(p1 > 0)) return null;

        int excludedBins = 2 * _span + 1;
        double harmonicSum = 0;
        int count = 0;
        for (int h = 2; h <= MaxHarmonic; h++)
        {
            int kc = (int)Math.Round(h * f0 / bw);
            if (kc + _span > hi) break;
            _harm[h] = Sum(values, kc - _span, kc + _span);
            harmonicSum += _harm[h];
            excludedBins += 2 * _span + 1;
            count = h - 1;
        }

        // Noise outside every lobe, and its average per bin to fill in under the lobes.
        double residual = Math.Max(0, total - p1 - harmonicSum);
        int residualBins = Math.Max(1, hi - lo + 1 - excludedBins);
        double noisePerBin = residual / residualBins;

        // THD: harmonic power minus the noise that sits under each harmonic's span.
        double lobeNoise = noisePerBin * (2 * _span + 1);
        double thdPower = 0;
        var dbc = new double[count];
        for (int h = 2; h <= count + 1; h++)
        {
            double ph = Math.Max(_harm[h] - lobeNoise, 0);
            thdPower += ph;
            dbc[h - 2] = 10 * Math.Log10(Math.Max(ph, 1e-30) / p1);
        }
        double fundamental = Math.Max(p1 - lobeNoise, p1 * 1e-3);
        double thd = Math.Sqrt(thdPower / fundamental);
        // THD+N: everything in the band except the fundamental, plus the noise under its lobe.
        double thdn = Math.Sqrt((residual + harmonicSum + lobeNoise) / fundamental);

        // Tone readings already carry the scalloping-corrected level; for power and density
        // readings the lobe sum is the tone's power (Parseval), which is the same number.
        double levelDb = info.Scale == SpectrumScale.Amplitude
            ? tone.LevelDb
            : 10 * Math.Log10(Math.Max(p1 * toBinPower, 1e-30));
        return new DistortionReading(f0, levelDb, thd, thdn, dbc, upperHz);
    }

    private static double Sum(ReadOnlySpan<float> v, int a, int b)
    {
        double s = 0;
        for (int k = a; k <= b; k++) s += v[k];
        return s;
    }
}
