using nSpectrum.Core.Audio;
using nSpectrum.Core.Concurrency;

namespace nSpectrum.Core.Meters;

/// <summary>One published set of loudness readings. Owned by a triple buffer: do not keep it past the next acquire.</summary>
public sealed class LoudnessFrame
{
    internal LoudnessFrame(int channels) => TruePeak = new double[channels];

    public double Momentary { get; internal set; }
    public double ShortTerm { get; internal set; }
    public double Integrated { get; internal set; }
    public double LoudnessRange { get; internal set; }
    public double MomentaryMax { get; internal set; }
    public double ShortTermMax { get; internal set; }
    public double TruePeakMax { get; internal set; }

    /// <summary>True peak per channel since the last reset, dBTP.</summary>
    public double[] TruePeak { get; }

    /// <summary>Seconds measured since the last reset.</summary>
    public double Seconds { get; internal set; }

    public long Sequence { get; internal set; }
}

/// <summary>
/// Runs a <see cref="LoudnessMeter"/> on a pipeline's worker thread and publishes its readings.
/// Attach with <see cref="AnalysisPipeline.AddTap"/>; read <see cref="Output"/> on the UI thread.
/// </summary>
public sealed class LoudnessTap : IAudioTap
{
    private readonly LoudnessMeter _meter;
    private int _resetRequested;
    private long _sequence;

    public LoudnessTap(int sampleRate, int channels)
    {
        _meter = new LoudnessMeter(sampleRate, channels);
        Output = new TripleBuffer<LoudnessFrame>(() => new LoudnessFrame(channels));
    }

    public TripleBuffer<LoudnessFrame> Output { get; }

    public int Channels => _meter.Channels;

    /// <summary>Thread-safe: restarts integrated loudness, LRA, maxima and true peaks at the next block.</summary>
    public void RequestReset() => Interlocked.Exchange(ref _resetRequested, 1);

    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) != 0) _meter.ResetMeasurements();
        _meter.Process(interleaved, channels);

        var o = Output.WriteBuffer;
        o.Momentary = _meter.Momentary;
        o.ShortTerm = _meter.ShortTerm;
        o.Integrated = _meter.Integrated;
        o.LoudnessRange = _meter.LoudnessRange;
        o.MomentaryMax = _meter.MomentaryMax;
        o.ShortTermMax = _meter.ShortTermMax;
        o.TruePeakMax = _meter.TruePeakMax;
        for (int c = 0; c < o.TruePeak.Length; c++) o.TruePeak[c] = _meter.TruePeak(c);
        o.Seconds = _meter.Seconds;
        o.Sequence = ++_sequence;
        Output.Publish();
    }
}
