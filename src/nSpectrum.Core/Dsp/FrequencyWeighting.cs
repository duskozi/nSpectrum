namespace nSpectrum.Core.Dsp;

/// <summary>Standard frequency weightings (IEC 61672-1:2013).</summary>
public enum FrequencyWeighting
{
    /// <summary>Flat (no weighting).</summary>
    Z,
    /// <summary>A-weighting: approximates the ear at moderate levels. 0 dB at 1 kHz, about −19 dB at 100 Hz.</summary>
    A,
    /// <summary>C-weighting: nearly flat, rolls off below 31.5 Hz and above 8 kHz. 0 dB at 1 kHz.</summary>
    C,
}

/// <summary>Magnitude responses of the IEC 61672-1 frequency weightings.</summary>
/// <remarks>
/// Uses the standard's analogue pole frequencies (20.598997, 107.65265, 737.86223, 12194.217 Hz)
/// and normalisation constants A1000 = −2.000 dB, C1000 = −0.062 dB, so each curve is 0.00 dB at
/// 1 kHz. Applied per FFT bin to power, which is exact for the magnitude response (the phase of an
/// analogue weighting filter does not affect a power spectrum).
/// </remarks>
public static class WeightingCurves
{
    private const double F1 = 20.598997, F2 = 107.65265, F3 = 737.86223, F4 = 12194.217;
    private const double A1000 = -2.000, C1000 = -0.062;

    /// <summary>Weighting in dB at <paramref name="hz"/>. −∞ at 0 Hz for A and C.</summary>
    public static double Db(FrequencyWeighting weighting, double hz)
    {
        if (weighting == FrequencyWeighting.Z) return 0;
        if (hz <= 0) return double.NegativeInfinity;
        double f2 = hz * hz;
        double c = F4 * F4 * f2 / ((f2 + F1 * F1) * (f2 + F4 * F4));
        if (weighting == FrequencyWeighting.C) return 20 * Math.Log10(c) - C1000;
        double a = c * f2 / Math.Sqrt((f2 + F2 * F2) * (f2 + F3 * F3));
        return 20 * Math.Log10(a) - A1000;
    }

    /// <summary>Power gain (linear) at <paramref name="hz"/>.</summary>
    public static double PowerGain(FrequencyWeighting weighting, double hz)
    {
        double db = Db(weighting, hz);
        return double.IsNegativeInfinity(db) ? 0 : Math.Pow(10, db / 10);
    }

    /// <summary>Short suffix for unit labels: "", "(A)", "(C)".</summary>
    public static string Suffix(FrequencyWeighting weighting) => weighting switch
    {
        FrequencyWeighting.A => "(A)",
        FrequencyWeighting.C => "(C)",
        _ => "",
    };
}
