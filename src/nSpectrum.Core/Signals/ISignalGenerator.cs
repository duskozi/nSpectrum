namespace nSpectrum.Core.Signals;

/// <summary>A mono test-signal source. Implementations never allocate in <see cref="Generate"/>.</summary>
public interface ISignalGenerator
{
    int SampleRate { get; }

    /// <summary>Fills <paramref name="destination"/> with the next samples.</summary>
    void Generate(Span<float> destination);

    /// <summary>Restarts the signal from its initial state.</summary>
    void Reset();
}

/// <summary>Level helpers for generators. dBFS follows AES17: a full-scale sine is 0 dBFS.</summary>
public static class Level
{
    /// <summary>Peak amplitude of a sine at <paramref name="dbfs"/>.</summary>
    public static double SinePeak(double dbfs) => Math.Pow(10, dbfs / 20);

    /// <summary>RMS of a signal whose AES17 level is <paramref name="dbfs"/> (a full-scale sine has RMS 1/√2).</summary>
    public static double Rms(double dbfs) => Math.Pow(10, dbfs / 20) / Math.Sqrt(2);

    /// <summary>AES17 dBFS of a signal with RMS <paramref name="rms"/>.</summary>
    public static double DbfsFromRms(double rms) => 20 * Math.Log10(rms * Math.Sqrt(2));
}
