namespace nSpectrum.Core.Signals;

/// <summary>
/// Exponential (log) sine sweep from <see cref="StartFrequency"/> to <see cref="EndFrequency"/>
/// over <see cref="Duration"/> seconds, then repeats. Phase stays continuous across repeats.
/// </summary>
public sealed class LogSweepGenerator : ISignalGenerator
{
    private long _sample;
    private double _phase;
    private readonly long _length;

    public LogSweepGenerator(int sampleRate, double startFrequency, double endFrequency, double duration, double peakAmplitude = 1.0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!(startFrequency > 0) || !(endFrequency > startFrequency) || endFrequency > sampleRate / 2.0)
            throw new ArgumentOutOfRangeException(nameof(endFrequency));
        if (!(duration > 0)) throw new ArgumentOutOfRangeException(nameof(duration));
        SampleRate = sampleRate;
        StartFrequency = startFrequency;
        EndFrequency = endFrequency;
        Duration = duration;
        Amplitude = peakAmplitude;
        _length = Math.Max(1, (long)Math.Round(duration * sampleRate));
    }

    public int SampleRate { get; }
    public double StartFrequency { get; }
    public double EndFrequency { get; }
    public double Duration { get; }
    public double Amplitude { get; set; }

    /// <summary>Instantaneous frequency at <paramref name="seconds"/> into a sweep.</summary>
    public double FrequencyAt(double seconds) => StartFrequency * Math.Pow(EndFrequency / StartFrequency, seconds / Duration);

    public void Generate(Span<float> destination)
    {
        double k = Math.Log(EndFrequency / StartFrequency) / _length;
        for (int i = 0; i < destination.Length; i++)
        {
            double f = StartFrequency * Math.Exp(k * _sample);
            destination[i] = (float)(Amplitude * Math.Sin(2 * Math.PI * _phase));
            _phase += f / SampleRate;
            if (_phase >= 1) _phase -= 1;
            if (++_sample >= _length) _sample = 0;
        }
    }

    public void Reset()
    {
        _sample = 0;
        _phase = 0;
    }
}
