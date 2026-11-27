namespace nSpectrum.Core.Signals;

/// <summary>Sine with a double-precision phase accumulator (no drift over hours).</summary>
public sealed class SineGenerator : ISignalGenerator
{
    private readonly double _initialPhase;
    private double _phase;      // cycles, [0, 1)
    private double _increment;  // cycles per sample

    public SineGenerator(int sampleRate, double frequency, double peakAmplitude = 1.0, double phaseRadians = 0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
        Amplitude = peakAmplitude;
        Frequency = frequency;
        _initialPhase = phaseRadians / (2 * Math.PI);
        _phase = _initialPhase - Math.Floor(_initialPhase);
    }

    public int SampleRate { get; }

    /// <summary>Peak amplitude (1.0 = 0 dBFS).</summary>
    public double Amplitude { get; set; }

    public double Frequency
    {
        get => _increment * SampleRate;
        set
        {
            if (value < 0 || value > SampleRate / 2.0) throw new ArgumentOutOfRangeException(nameof(value));
            _increment = value / SampleRate;
        }
    }

    public void Generate(Span<float> destination)
    {
        double ph = _phase, inc = _increment, a = Amplitude;
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (float)(a * Math.Sin(2 * Math.PI * ph));
            ph += inc;
            if (ph >= 1) ph -= 1;
        }
        _phase = ph;
    }

    public void Reset() => _phase = _initialPhase - Math.Floor(_initialPhase);
}
