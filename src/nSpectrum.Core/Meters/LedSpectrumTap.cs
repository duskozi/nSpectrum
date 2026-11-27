using nSpectrum.Core.Audio;
using nSpectrum.Core.Dsp;

namespace nSpectrum.Core.Meters;

/// <summary>
/// The analyzers behind an LED band display: one per channel in stereo, or one on (L+R)/2 in mono.
/// Runs its own FFT chain (independent of the main analyzer's settings) sized for good low-band
/// resolution. Attach with <see cref="AnalysisPipeline.AddTap"/>.
/// </summary>
public sealed class LedSpectrumTap : IAudioTap
{
    private sealed record Stage(ChannelRoute[] Routes, SpectrumAnalyzer[] Analyzers);

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly float[] _mono = new float[8192];
    private volatile Stage _stage;

    private int _fftSize;
    private double _smoothingMs;

    /// <param name="fftSize">FFT length (power of two), or 0 for <see cref="AutoFftSize"/>.</param>
    /// <param name="smoothingMs">Exponential smoothing time constant in ms; 0 turns smoothing off.</param>
    public LedSpectrumTap(int sampleRate, int channels, bool stereo = true, int fftSize = 0, double smoothingMs = DefaultSmoothingMs)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _fftSize = fftSize;
        _smoothingMs = smoothingMs;
        _stage = Build(stereo);
    }

    public const double DefaultSmoothingMs = 45;

    /// <summary>Default FFT length: 2.9 Hz bins at 44.1/48 kHz, about the same at higher rates.</summary>
    public static int AutoFftSize(int sampleRate) => sampleRate <= 50000 ? 16384 : sampleRate <= 100000 ? 32768 : 65536;

    /// <summary>FFT settings for the band display: Hann, power per bin, 60 spectra/s.</summary>
    public static SpectrumSettings SettingsFor(int sampleRate, int fftSize = 0, double smoothingMs = DefaultSmoothingMs) => new()
    {
        FftSize = fftSize > 0 ? fftSize : AutoFftSize(sampleRate),
        Window = WindowType.Hann,
        Scale = SpectrumScale.Power,
        AnalysisRate = 60,
        Averaging = smoothingMs > 0 ? AveragingMode.Exponential : AveragingMode.None,
        AveragingTime = smoothingMs > 0 ? smoothingMs / 1000 : TimeWeighting.Fast,
        PeakHold = false,
    };

    /// <summary>FFT length in use.</summary>
    public int FftSize => _fftSize > 0 ? _fftSize : AutoFftSize(_sampleRate);

    /// <summary>Smoothing time constant in ms (0 = off).</summary>
    public double SmoothingMs => _smoothingMs;

    /// <summary>Changes FFT size (0 = auto) and smoothing. Allocates on the caller's thread and swaps atomically.</summary>
    public void SetAnalysis(int fftSize, double smoothingMs)
    {
        if (fftSize == _fftSize && smoothingMs == _smoothingMs) return;
        _fftSize = fftSize;
        _smoothingMs = smoothingMs;
        _stage = Build(IsStereo);
    }

    /// <summary>True when two channels are analysed.</summary>
    public bool IsStereo => _stage.Analyzers.Length == 2;

    public int SampleRate => _sampleRate;

    /// <summary>Current analyzers (1 or 2). The array is replaced by <see cref="SetStereo"/>.</summary>
    public IReadOnlyList<SpectrumAnalyzer> Analyzers => _stage.Analyzers;

    /// <summary>Switches between stereo and mono. Allocates on the caller's thread and swaps atomically.</summary>
    public void SetStereo(bool stereo)
    {
        if (stereo == IsStereo && (stereo || _stage.Routes[0] == ChannelRoute.Mid)) return;
        _stage = Build(stereo);
    }

    private Stage Build(bool stereo)
    {
        var s = SettingsFor(_sampleRate, _fftSize, _smoothingMs);
        ChannelRoute[] routes = stereo && _channels >= 2 ? [ChannelRoute.Left, ChannelRoute.Right] : [ChannelRoute.Mid];
        return new Stage(routes, [.. routes.Select(_ => new SpectrumAnalyzer(s, _sampleRate))]);
    }

    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        var stage = _stage;
        int maxFrames = _mono.Length;
        for (int off = 0; off < interleaved.Length; off += maxFrames * channels)
        {
            var block = interleaved.Slice(off, Math.Min(maxFrames * channels, interleaved.Length - off));
            Span<float> mono = _mono.AsSpan(0, block.Length / channels);
            for (int i = 0; i < stage.Routes.Length; i++)
            {
                stage.Routes[i].Extract(block, channels, mono);
                stage.Analyzers[i].Process(mono);
            }
        }
    }
}
