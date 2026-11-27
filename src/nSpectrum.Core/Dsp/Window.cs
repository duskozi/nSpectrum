namespace nSpectrum.Core.Dsp;

/// <summary>Analysis window shapes.</summary>
public enum WindowType
{
    /// <summary>No taper. Only correct for synchronous (bin-centred) test signals.</summary>
    Rectangular,
    /// <summary>Hann (raised cosine). Good general default.</summary>
    Hann,
    /// <summary>4-term Blackman-Harris, about -92 dB side lobes. Best for dynamic range.</summary>
    BlackmanHarris4,
    /// <summary>5-term flat-top (SRS/Matlab coefficients). Amplitude within about ±0.01 dB for tones anywhere between bins.</summary>
    FlatTop,
    /// <summary>Kaiser window with adjustable β.</summary>
    Kaiser,
    /// <summary>
    /// 7-term Blackman-Harris (Albrecht 2001), side lobes below −160 dB. Wide main lobe (±7 bins);
    /// the window for distortion and noise-floor measurements below −100 dB.
    /// </summary>
    BlackmanHarris7,
}

/// <summary>
/// A sampled analysis window with the correction factors needed to read correct levels.
/// </summary>
/// <remarks>
/// Windows are generated in the "periodic" (DFT-even) form, which is the right one for spectral
/// analysis. Correction factors follow Heinzel, Rüdiger and Schilling, "Spectrum and spectral
/// density estimation by the DFT" (2002): S1 = Σw, S2 = Σw².
/// </remarks>
public sealed class Window
{
    private readonly float[] _coefficients;

    private Window(WindowType type, double kaiserBeta, float[] coefficients)
    {
        Type = type;
        KaiserBeta = kaiserBeta;
        _coefficients = coefficients;

        double s1 = 0, s2 = 0;
        foreach (float w in coefficients)
        {
            s1 += w;
            s2 += (double)w * w;
        }
        S1 = s1;
        S2 = s2;
    }

    public WindowType Type { get; }

    /// <summary>β used for <see cref="WindowType.Kaiser"/>; 0 for other types.</summary>
    public double KaiserBeta { get; }

    public int Length => _coefficients.Length;

    public ReadOnlySpan<float> Coefficients => _coefficients;

    /// <summary>Σ w[n].</summary>
    public double S1 { get; }

    /// <summary>Σ w[n]².</summary>
    public double S2 { get; }

    /// <summary>Coherent gain, the mean of the window. A tone's bin amplitude is scaled by this.</summary>
    public double CoherentGain => S1 / Length;

    /// <summary>Equivalent noise bandwidth in bins: N·S2 / S1².</summary>
    public double EnbwBins => Length * S2 / (S1 * S1);

    /// <summary>
    /// |W(δ)| / W(0): the window's normalised amplitude response for a tone δ bins away from a
    /// bin centre, evaluated from the DTFT of the coefficients. Used to undo scalloping loss.
    /// </summary>
    public double Response(double deltaBins)
    {
        if (deltaBins == 0) return 1.0;
        double re = 0, im = 0;
        double step = -2.0 * Math.PI * deltaBins / Length;
        for (int n = 0; n < _coefficients.Length; n++)
        {
            double a = step * n;
            re += _coefficients[n] * Math.Cos(a);
            im += _coefficients[n] * Math.Sin(a);
        }
        return Math.Sqrt(re * re + im * im) / S1;
    }

    /// <summary>Default β for <see cref="WindowType.Kaiser"/> (side lobes near -70 dB).</summary>
    public const double DefaultKaiserBeta = 9.0;

    public static Window Create(WindowType type, int length, double kaiserBeta = DefaultKaiserBeta)
    {
        if (length < 2) throw new ArgumentOutOfRangeException(nameof(length));
        var w = new float[length];
        double n = length; // periodic form: divide by N, not N-1
        switch (type)
        {
            case WindowType.Rectangular:
                Array.Fill(w, 1f);
                kaiserBeta = 0;
                break;
            case WindowType.Hann:
                for (int i = 0; i < length; i++)
                    w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
                kaiserBeta = 0;
                break;
            case WindowType.BlackmanHarris4:
                Cosine(w, n, 0.35875, 0.48829, 0.14128, 0.01168, 0);
                kaiserBeta = 0;
                break;
            case WindowType.FlatTop:
                Cosine(w, n, 0.21557895, 0.41663158, 0.277263158, 0.083578947, 0.006947368);
                kaiserBeta = 0;
                break;
            case WindowType.BlackmanHarris7:
                Cosine(w, n, 0.27105140069342, 0.43329793923448, 0.21812299954311, 0.06592544638803,
                    0.01081174209837, 0.00077658482522, 0.00001388721735);
                kaiserBeta = 0;
                break;
            case WindowType.Kaiser:
                if (kaiserBeta < 0) throw new ArgumentOutOfRangeException(nameof(kaiserBeta));
                double i0b = BesselI0(kaiserBeta);
                for (int i = 0; i < length; i++)
                {
                    double r = 2.0 * i / n - 1.0; // periodic: r spans [-1, 1)
                    w[i] = (float)(BesselI0(kaiserBeta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
        return new Window(type, kaiserBeta, w);
    }

    /// <summary>Sum-of-cosines window a0 − a1·cos x + a2·cos 2x − … (alternating signs).</summary>
    private static void Cosine(float[] w, double n, params ReadOnlySpan<double> a)
    {
        for (int i = 0; i < w.Length; i++)
        {
            double x = 2 * Math.PI * i / n;
            double v = a[0];
            for (int k = 1; k < a.Length; k++) v += ((k & 1) == 1 ? -a[k] : a[k]) * Math.Cos(k * x);
            w[i] = (float)v;
        }
    }

    /// <summary>Modified Bessel function of the first kind, order 0 (power series).</summary>
    internal static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 200; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}
