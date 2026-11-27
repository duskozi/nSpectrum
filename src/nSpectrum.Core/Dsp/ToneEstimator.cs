namespace nSpectrum.Core.Dsp;

/// <summary>Frequency and level of a tone read from a spectrum, corrected for scalloping.</summary>
public readonly record struct ToneReading(
    int Bin,
    double Frequency,
    double OffsetBins,
    double RawLevelDb,
    double LevelDb,
    double ScallopCorrectionDb);

/// <summary>
/// Reads a tone's exact frequency and level from an <see cref="SpectrumScale.Amplitude"/> spectrum.
/// </summary>
/// <remarks>
/// A tone between two bins reads low by the window's scalloping loss (up to 1.42 dB for Hann).
/// The estimator uses the two neighbours of the peak bin and the window's own DTFT response
/// R(δ): it solves (R(1-δ) - R(1+δ)) / R(δ) = (a[k+1] - a[k-1]) / a[k] for the offset δ, then
/// divides the peak amplitude by R(δ). This works for every window type, not just Hann.
/// Build one per window configuration (it tabulates the response once); reading allocates nothing.
/// </remarks>
public sealed class ToneEstimator
{
    private const int Steps = 512;        // table points per bin
    private const double Range = 2.0;     // table covers |δ| in [0, 2] bins
    private readonly double[] _response;  // R(i / Steps)

    public ToneEstimator(SpectrumInfo info) : this(info.Settings.Window, info.Window.Length, info.Settings.KaiserBeta, info.FftSize) { }

    /// <param name="windowLength">Samples in the window.</param>
    /// <param name="fftSize">FFT length (larger than the window when zero-padding).</param>
    public ToneEstimator(WindowType type, int windowLength, double kaiserBeta, int fftSize)
    {
        // The response shape converges quickly with length; tabulating from a window of at most
        // 4096 points keeps construction fast for 64k FFTs with no measurable loss of accuracy.
        double pad = (double)fftSize / windowLength; // bins of the FFT per bin of the window
        int len = Math.Min(windowLength, 4096);
        var w = Window.Create(type, len, kaiserBeta);
        int count = (int)(Range * Steps) + 2;
        _response = new double[count];
        for (int i = 0; i < count; i++)
            _response[i] = w.Response(i / (double)Steps / pad);
        Padding = pad;
    }

    /// <summary>FFT bins per window bin (1 without zero padding).</summary>
    public double Padding { get; }

    /// <summary>Normalised window amplitude response at <paramref name="deltaBins"/> FFT bins.</summary>
    public double Response(double deltaBins)
    {
        double x = Math.Abs(deltaBins) * Steps;
        int i = (int)x;
        if (i >= _response.Length - 1) return _response[^1];
        double f = x - i;
        return _response[i] + (_response[i + 1] - _response[i]) * f;
    }

    /// <summary>Reads the strongest tone between <paramref name="minBin"/> and <paramref name="maxBin"/>.</summary>
    public ToneReading FindPeak(ReadOnlySpan<float> values, SpectrumInfo info, int minBin = 1, int maxBin = int.MaxValue)
    {
        minBin = Math.Max(1, minBin);
        maxBin = Math.Min(values.Length - 2, maxBin);
        int best = minBin;
        float bestV = float.MinValue;
        for (int k = minBin; k <= maxBin; k++)
        {
            if (values[k] > bestV) { bestV = values[k]; best = k; }
        }
        return Read(values, info, best);
    }

    /// <summary>Reads the tone whose peak is at bin <paramref name="bin"/>.</summary>
    public ToneReading Read(ReadOnlySpan<float> values, SpectrumInfo info, int bin)
    {
        bin = Math.Clamp(bin, 1, values.Length - 2);
        double a0 = Math.Sqrt(Math.Max(values[bin], 1e-38f));
        double am = Math.Sqrt(Math.Max(values[bin - 1], 0f));
        double ap = Math.Sqrt(Math.Max(values[bin + 1], 0f));
        double target = (ap - am) / a0;

        // g(δ) = (R(1-δ) - R(1+δ)) / R(δ) increases monotonically on [-0.5, 0.5] FFT bins.
        double lo = -0.5, hi = 0.5;
        double glo = G(lo), ghi = G(hi);
        double delta;
        if (target <= glo) delta = lo;
        else if (target >= ghi) delta = hi;
        else
        {
            for (int it = 0; it < 40; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (G(mid) < target) lo = mid; else hi = mid;
            }
            delta = 0.5 * (lo + hi);
        }

        double r = Response(delta);
        double raw = 10 * Math.Log10(Math.Max(values[bin], 1e-38f));
        double corr = -20 * Math.Log10(r);
        return new ToneReading(bin, (bin + delta) * info.BinWidth, delta, raw, raw + corr, corr);
    }

    private double G(double d) => (Response(1 - d) - Response(1 + d)) / Response(d);
}
