namespace nSpectrum.Core.Dsp;

/// <summary>Fixed facts about the spectra one analyzer produces.</summary>
public sealed class SpectrumInfo
{
    internal SpectrumInfo(SpectrumSettings settings, int sampleRate, Window window, int hopSamples)
    {
        Settings = settings;
        SampleRate = sampleRate;
        Window = window;
        HopSamples = hopSamples;
        BinCount = settings.FftSize / 2 + 1;
        BinWidth = (double)sampleRate / settings.FftSize;
        EnbwHz = sampleRate * window.S2 / (window.S1 * window.S1);
    }

    public SpectrumSettings Settings { get; }
    public int SampleRate { get; }
    public int FftSize => Settings.FftSize;
    public int BinCount { get; }

    /// <summary>Spacing between bins, fs / N (Hz).</summary>
    public double BinWidth { get; }

    /// <summary>Equivalent noise bandwidth of one bin (Hz).</summary>
    public double EnbwHz { get; }

    /// <summary>Nominal hop between spectra, in samples.</summary>
    public int HopSamples { get; }

    public Window Window { get; }

    public SpectrumScale Scale => Settings.Scale;
    public FrequencyWeighting Weighting => Settings.Weighting;
    public DbfsReference Reference => Settings.Reference;

    public double BinFrequency(int bin) => bin * BinWidth;

    /// <summary>Short unit label for readouts, e.g. "dBFS", "dBFS(A)" or "dBFS/Hz".</summary>
    public string UnitLabel => "dBFS" + WeightingCurves.Suffix(Weighting) + (Scale == SpectrumScale.PowerDensity ? "/Hz" : "");
}

/// <summary>
/// One published spectrum. Values are linear and normalised so that 10·log10(value) is the level
/// in the frame's unit (see <see cref="SpectrumInfo"/>). Owned by a <see cref="Concurrency.TripleBuffer{T}"/>:
/// the reader must not keep a reference beyond its next acquire.
/// </summary>
public sealed class SpectrumFrame
{
    public SpectrumFrame(SpectrumInfo info)
    {
        Info = info;
        Values = new float[info.BinCount];
        Peaks = new float[info.BinCount];
    }

    public SpectrumInfo Info { get; }

    /// <summary>Averaged spectrum, linear power-like units.</summary>
    public float[] Values { get; }

    /// <summary>Peak-hold spectrum, same units. All zeros when peak hold is off.</summary>
    public float[] Peaks { get; }

    /// <summary>Monotonic frame counter of the producing analyzer.</summary>
    public long Sequence { get; internal set; }

    /// <summary>Index (in the source stream) of the sample just after the analysed window.</summary>
    public long SamplePosition { get; internal set; }

    /// <summary>Spectra averaged into <see cref="Values"/> (for Linear and Infinite modes).</summary>
    public int AveragedCount { get; internal set; }

    /// <summary>Time of the end of the analysed window, in seconds of stream time.</summary>
    public double Time => (double)SamplePosition / Info.SampleRate;
}
