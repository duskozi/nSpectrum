namespace nSpectrum.Core.Signals;

/// <summary>Small, fast, allocation-free PRNG (xoshiro128**) with Gaussian output.</summary>
internal struct FastRandom
{
    private uint _s0, _s1, _s2, _s3;
    private float _spare;
    private bool _hasSpare;

    public FastRandom(ulong seed)
    {
        // SplitMix64 to expand the seed.
        ulong z = seed;
        _s0 = (uint)Next(ref z); _s1 = (uint)Next(ref z); _s2 = (uint)Next(ref z); _s3 = (uint)Next(ref z);
        if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 1;
        _spare = 0;
        _hasSpare = false;

        static ulong Next(ref ulong x)
        {
            ulong r = x += 0x9E3779B97F4A7C15UL;
            r = (r ^ (r >> 30)) * 0xBF58476D1CE4E5B9UL;
            r = (r ^ (r >> 27)) * 0x94D049BB133111EBUL;
            return r ^ (r >> 31);
        }
    }

    public uint NextUInt()
    {
        uint result = BitRotate(_s1 * 5, 7) * 9;
        uint t = _s1 << 9;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitRotate(_s3, 11);
        return result;

        static uint BitRotate(uint x, int k) => (x << k) | (x >> (32 - k));
    }

    /// <summary>Uniform in (0, 1).</summary>
    public double NextDouble() => ((NextUInt() >> 8) + 0.5) * (1.0 / 16777216.0);

    /// <summary>Standard normal (Box-Muller, pairs).</summary>
    public float NextGaussian()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }
        double u1 = NextDouble(), u2 = NextDouble();
        double r = Math.Sqrt(-2 * Math.Log(u1));
        double a = 2 * Math.PI * u2;
        _spare = (float)(r * Math.Sin(a));
        _hasSpare = true;
        return (float)(r * Math.Cos(a));
    }
}

public enum NoiseDistribution { Gaussian, Uniform }

/// <summary>White noise with a set RMS. Flat power spectral density σ²·2/fs (one-sided).</summary>
public sealed class WhiteNoiseGenerator : ISignalGenerator
{
    private readonly ulong _seed;
    private FastRandom _rng;

    public WhiteNoiseGenerator(int sampleRate, double rms, NoiseDistribution distribution = NoiseDistribution.Gaussian, ulong seed = 1)
    {
        SampleRate = sampleRate;
        Rms = rms;
        Distribution = distribution;
        _seed = seed;
        _rng = new FastRandom(seed);
    }

    public int SampleRate { get; }
    public double Rms { get; set; }
    public NoiseDistribution Distribution { get; }

    public void Generate(Span<float> destination)
    {
        if (Distribution == NoiseDistribution.Gaussian)
        {
            float g = (float)Rms;
            for (int i = 0; i < destination.Length; i++) destination[i] = g * _rng.NextGaussian();
        }
        else
        {
            // Uniform on [-a, a] has RMS a/√3.
            double a = Rms * Math.Sqrt(3);
            for (int i = 0; i < destination.Length; i++) destination[i] = (float)(a * (2 * _rng.NextDouble() - 1));
        }
    }

    public void Reset() => _rng = new FastRandom(_seed);
}

/// <summary>
/// Pink noise (-3 dB/octave, equal power per octave) with a set RMS, accurate at any sample rate.
/// </summary>
/// <remarks>
/// Gaussian white noise goes through a cascade of first-order pole/zero sections spaced half an
/// octave apart (poles from 2 Hz up to fs, zeros √2 above each pole, matched-z mapping), followed
/// by a 7-tap linear-phase correction FIR that removes the matched-z error near Nyquist. The slope
/// is within about ±0.05 dB of ideal from 20 Hz to 20 kHz at 44.1 to 192 kHz. Output RMS is
/// calibrated from the filter's exact impulse-response energy.
/// </remarks>
public sealed class PinkNoiseGenerator : ISignalGenerator
{
    private static readonly double[] Correction = [0.9331, 0.0797, -0.0179, 0.0053];

    private readonly ulong _seed;
    private FastRandom _rng;
    private readonly double[] _a;      // pole coefficients
    private readonly double[] _b;      // zero coefficients
    private readonly double[] _state;  // previous input of each section's x
    private readonly double[] _y;      // previous output of each section
    private readonly double[] _fir = new double[7];
    private readonly double[] _firHistory = new double[7];
    private int _firPos;
    private double _gain;
    private double _rms;

    public PinkNoiseGenerator(int sampleRate, double rms, ulong seed = 1)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
        _seed = seed;
        _rng = new FastRandom(seed);

        var a = new List<double>();
        var b = new List<double>();
        double ratio = Math.Sqrt(2);
        for (double f = 2.0; f < sampleRate; f *= ratio)
        {
            a.Add(Math.Exp(-2 * Math.PI * f / sampleRate));
            b.Add(Math.Exp(-2 * Math.PI * f * Math.Sqrt(ratio) / sampleRate));
        }
        _a = [.. a];
        _b = [.. b];
        _state = new double[_a.Length];
        _y = new double[_a.Length];

        _fir[3] = Correction[0];
        for (int k = 1; k < 4; k++) _fir[3 - k] = _fir[3 + k] = Correction[k] / 2;

        // Energy of the unit impulse response → RMS for unit-variance white input.
        double energy = 0;
        int length = (int)Math.Min(int.MaxValue, 40L * sampleRate / (2 * Math.PI * 2.0) + 64);
        for (int n = 0; n < length; n++)
        {
            double y = Step(n == 0 ? 1.0 : 0.0);
            energy += y * y;
        }
        Array.Clear(_state); Array.Clear(_y); Array.Clear(_firHistory); _firPos = 0;
        UnitRms = Math.Sqrt(energy);
        Rms = rms;
    }

    public int SampleRate { get; }

    /// <summary>Output RMS of the raw filter for unit-variance white input.</summary>
    internal double UnitRms { get; }

    public double Rms
    {
        get => _rms;
        set { _rms = value; _gain = value / UnitRms; }
    }

    /// <summary>Linear magnitude response of the shaping filter at <paramref name="hz"/> (before RMS gain).</summary>
    public double Response(double hz)
    {
        double w = 2 * Math.PI * hz / SampleRate;
        double cr = Math.Cos(w), ci = -Math.Sin(w); // z^-1
        double mag = 1;
        for (int i = 0; i < _a.Length; i++)
        {
            double nr = 1 - _b[i] * cr, ni = -_b[i] * ci;
            double dr = 1 - _a[i] * cr, di = -_a[i] * ci;
            mag *= Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        }
        double fir = Correction[0];
        for (int k = 1; k < 4; k++) fir += Correction[k] * Math.Cos(k * w);
        return mag * Math.Abs(fir);
    }

    public void Generate(Span<float> destination)
    {
        double g = _gain;
        for (int i = 0; i < destination.Length; i++)
            destination[i] = (float)(g * Step(_rng.NextGaussian()));
    }

    private double Step(double x)
    {
        for (int i = 0; i < _a.Length; i++)
        {
            double y = x - _b[i] * _state[i] + _a[i] * _y[i];
            _state[i] = x;
            _y[i] = y;
            x = y;
        }
        _firHistory[_firPos] = x;
        double acc = 0;
        int p = _firPos;
        for (int k = 0; k < 7; k++)
        {
            acc += _fir[k] * _firHistory[p];
            if (--p < 0) p = 6;
        }
        if (++_firPos == 7) _firPos = 0;
        return acc;
    }

    public void Reset()
    {
        _rng = new FastRandom(_seed);
        Array.Clear(_state); Array.Clear(_y); Array.Clear(_firHistory); _firPos = 0;
    }
}
