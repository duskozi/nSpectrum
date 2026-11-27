namespace nSpectrum.Core.Dsp;

/// <summary>What a spectrum bin value means.</summary>
public enum SpectrumScale
{
    /// <summary>
    /// Tone / peak reading: corrected by the window's coherent gain, so a sine reads its true
    /// level when it sits on a bin centre (and after <see cref="ToneEstimator"/> correction anywhere).
    /// </summary>
    Amplitude,
    /// <summary>
    /// Noise / power reading per bin: corrected by the window's ENBW, so the sum of all bins equals
    /// the signal's total power (Parseval). Bin values depend on bin width.
    /// </summary>
    Power,
    /// <summary>Power spectral density, per hertz (dB/Hz). Independent of FFT size.</summary>
    PowerDensity,
}

/// <summary>What 0 dBFS means.</summary>
public enum DbfsReference
{
    /// <summary>AES17: a full-scale sine (peak 1.0) reads 0 dBFS.</summary>
    SineFullScale,
    /// <summary>Pure RMS: an RMS of 1.0 reads 0 dBFS, so a full-scale sine reads -3.01 dBFS.</summary>
    RmsFullScale,
}

/// <summary>How successive spectra are combined. All modes average power, never dB.</summary>
public enum AveragingMode
{
    None,
    /// <summary>Exponential time weighting with <see cref="SpectrumSettings.AveragingTime"/> (and optional separate decay time).</summary>
    Exponential,
    /// <summary>Sliding mean over the last <see cref="SpectrumSettings.LinearFrames"/> spectra.</summary>
    Linear,
    /// <summary>Mean over everything since the last reset (Leq-style).</summary>
    Infinite,
    /// <summary>Maximum since the last reset.</summary>
    MaxHold,
}

/// <summary>Standard time weightings from IEC 61672-1.</summary>
public static class TimeWeighting
{
    /// <summary>Fast: τ = 125 ms.</summary>
    public const double Fast = 0.125;
    /// <summary>Slow: τ = 1 s.</summary>
    public const double Slow = 1.0;
    /// <summary>Impulse rise: τ = 35 ms.</summary>
    public const double ImpulseRise = 0.035;
    /// <summary>Impulse decay: τ = 1.5 s.</summary>
    public const double ImpulseDecay = 1.5;
}

/// <summary>Immutable configuration of one FFT analysis chain.</summary>
public sealed record SpectrumSettings
{
    /// <summary>FFT length, power of two. 1024 to 65536 in the app; the engine accepts 16 to 2^20.</summary>
    public int FftSize { get; init; } = 8192;

    /// <summary>
    /// Number of samples actually analysed. 0 means <see cref="FftSize"/>. A smaller value
    /// zero-pads, which interpolates the spectrum but does not add resolution.
    /// </summary>
    public int WindowLength { get; init; }

    public WindowType Window { get; init; } = WindowType.Hann;

    public double KaiserBeta { get; init; } = Dsp.Window.DefaultKaiserBeta;

    /// <summary>Spectra per second. The hop size is sampleRate / AnalysisRate, independent of FFT size.</summary>
    public double AnalysisRate { get; init; } = 60;

    public SpectrumScale Scale { get; init; } = SpectrumScale.Amplitude;

    public DbfsReference Reference { get; init; } = DbfsReference.SineFullScale;

    /// <summary>Frequency weighting applied to every bin before averaging (Z = flat).</summary>
    public FrequencyWeighting Weighting { get; init; } = FrequencyWeighting.Z;

    public AveragingMode Averaging { get; init; } = AveragingMode.Exponential;

    /// <summary>Exponential time constant τ in seconds (rise, and decay unless <see cref="AveragingDecayTime"/> is set).</summary>
    public double AveragingTime { get; init; } = TimeWeighting.Fast;

    /// <summary>Optional separate decay τ in seconds, for asymmetric weightings like Impulse. Null means same as rise.</summary>
    public double? AveragingDecayTime { get; init; }

    /// <summary>Number of spectra in the <see cref="AveragingMode.Linear"/> sliding mean.</summary>
    public int LinearFrames { get; init; } = 8;

    public bool PeakHold { get; init; } = true;

    /// <summary>Seconds a peak is held before it starts to fall. <see cref="double.PositiveInfinity"/> holds until reset.</summary>
    public double PeakHoldTime { get; init; } = 1.5;

    /// <summary>Fall rate of a released peak, in dB per second.</summary>
    public double PeakFallRate { get; init; } = 20;

    /// <summary>Effective number of analysed samples.</summary>
    public int EffectiveWindowLength => WindowLength <= 0 ? FftSize : WindowLength;

    public void Validate()
    {
        if (FftSize < RealFft.MinSize || FftSize > RealFft.MaxSize || (FftSize & (FftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(FftSize), FftSize, "Must be a power of two.");
        if (WindowLength < 0 || WindowLength > FftSize)
            throw new ArgumentOutOfRangeException(nameof(WindowLength), WindowLength, "Must be 0 or between 2 and FftSize.");
        if (WindowLength is > 0 and < 2)
            throw new ArgumentOutOfRangeException(nameof(WindowLength));
        if (!(AnalysisRate > 0) || double.IsInfinity(AnalysisRate))
            throw new ArgumentOutOfRangeException(nameof(AnalysisRate));
        if (Averaging == AveragingMode.Exponential && !(AveragingTime > 0))
            throw new ArgumentOutOfRangeException(nameof(AveragingTime));
        if (AveragingDecayTime is { } d && !(d > 0))
            throw new ArgumentOutOfRangeException(nameof(AveragingDecayTime));
        if (Averaging == AveragingMode.Linear && (LinearFrames < 1 || LinearFrames > 1024))
            throw new ArgumentOutOfRangeException(nameof(LinearFrames));
        if (PeakHold && (PeakHoldTime < 0 || PeakFallRate < 0))
            throw new ArgumentOutOfRangeException(nameof(PeakHoldTime));
    }
}
