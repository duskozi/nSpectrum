using nSpectrum.Core.Audio;
using nSpectrum.Core.Concurrency;

namespace nSpectrum.Core.Meters;

/// <summary>One published set of meter readings. Owned by a triple buffer: do not keep it past the next acquire.</summary>
public sealed class MeterFrame
{
    internal MeterFrame(int goniometerFrames)
    {
        Scope = new float[goniometerFrames * 2];
    }

    /// <summary>Channels measured: 1 (mono source) or 2. A mono source fills both slots with the same values.</summary>
    public int Channels { get; internal set; }

    public int SampleRate { get; internal set; }

    /// <summary>Sample peak per channel with digital peak-meter fall-back (instant attack, <see cref="MeterTap.PeakFallDbPerSecond"/>), linear.</summary>
    public double[] Peak { get; } = new double[2];

    /// <summary>RMS per channel, exponential 300 ms, linear. AES17: a full-scale sine reads 0.707.</summary>
    public double[] Rms { get; } = new double[2];

    /// <summary>VU needle per channel: averaged rectified value (see <see cref="VuBallistics.ToVu"/>).</summary>
    public double[] Vu { get; } = new double[2];

    /// <summary>Samples at or above full scale (|x| ≥ 0.9999) per channel since the tap was created.</summary>
    public long[] Clips { get; } = new long[2];

    /// <summary>Phase correlation −1..+1 (300 ms), or NaN when the signal is below −90 dBFS.</summary>
    public double Correlation { get; internal set; }

    /// <summary>Balance in dB, positive when R is louder (300 ms). 0 when silent.</summary>
    public double BalanceDb { get; internal set; }

    /// <summary>The most recent samples, interleaved L,R (oldest first), for a goniometer.</summary>
    public float[] Scope { get; }

    public long Sequence { get; internal set; }
}

/// <summary>
/// Level and phase metering for one source: sample peak, RMS, VU ballistics, clip counts, phase
/// correlation, balance and a goniometer sample window. Attach to an <see cref="AnalysisPipeline"/>
/// with <see cref="AnalysisPipeline.AddTap"/>; read <see cref="Output"/> from the UI thread.
/// </summary>
public sealed class MeterTap : IAudioTap
{
    /// <summary>Goniometer window in frames.</summary>
    public const int ScopeFrames = 2048;

    /// <summary>Peak fall-back, IEC 60268-18 digital peak meter: 20 dB in 1.7 s.</summary>
    public const double PeakFallDbPerSecond = 20 / 1.7;

    private const double IntegrationSeconds = 0.3;

    private readonly int _sampleRate;
    private readonly VuBallistics[] _vu;
    private readonly double[] _peak = new double[2];
    private readonly double[] _ms = new double[2];
    private readonly long[] _clips = new long[2];
    private double _lr, _ll, _rr;
    private readonly double _alpha;
    private readonly double _peakFallPerSample;
    private readonly float[] _scope = new float[ScopeFrames * 2];
    private int _scopePos;
    private long _sequence;

    public MeterTap(int sampleRate)
    {
        _sampleRate = sampleRate;
        _vu = [new VuBallistics(sampleRate), new VuBallistics(sampleRate)];
        _alpha = 1 - Math.Exp(-1.0 / (IntegrationSeconds * sampleRate));
        _peakFallPerSample = Math.Pow(10, -PeakFallDbPerSecond / 20 / sampleRate);
        Output = new TripleBuffer<MeterFrame>(() => new MeterFrame(ScopeFrames));
    }

    public TripleBuffer<MeterFrame> Output { get; }

    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        int ch = Math.Min(channels, 2);
        double a = _alpha, fall = _peakFallPerSample;
        double lr = _lr, ll = _ll, rr = _rr;
        int pos = _scopePos;
        for (int f = 0; f < frames; f++)
        {
            int i = f * channels;
            float l = interleaved[i];
            float r = ch == 2 ? interleaved[i + 1] : l;
            Channel(0, l, a, fall);
            if (ch == 2) Channel(1, r, a, fall);
            lr += a * (l * (double)r - lr);
            ll += a * (l * (double)l - ll);
            rr += a * (r * (double)r - rr);
            _scope[pos] = l;
            _scope[pos + 1] = r;
            pos += 2;
            if (pos == _scope.Length) pos = 0;
        }
        _lr = lr; _ll = ll; _rr = rr;
        _scopePos = pos;
        if (ch == 1)
        {
            _peak[1] = _peak[0]; _ms[1] = _ms[0]; _clips[1] = _clips[0];
        }
        Publish(ch);
    }

    private void Channel(int c, float x, double a, double fall)
    {
        double ax = Math.Abs(x);
        double p = _peak[c] * fall;
        _peak[c] = ax > p ? ax : p;
        _ms[c] += a * (x * (double)x - _ms[c]);
        if (ax >= 0.9999) _clips[c]++;
        _vu[c].Process(x);
    }

    private void Publish(int ch)
    {
        var o = Output.WriteBuffer;
        o.Channels = ch;
        o.SampleRate = _sampleRate;
        for (int c = 0; c < 2; c++)
        {
            o.Peak[c] = _peak[c];
            o.Rms[c] = Math.Sqrt(Math.Max(_ms[c], 0));
            o.Vu[c] = _vu[ch == 2 ? c : 0].Value;
            o.Clips[c] = _clips[c];
        }
        double energy = Math.Sqrt(Math.Max(_ll * _rr, 0));
        o.Correlation = Math.Max(_ll, _rr) < 1e-9 ? double.NaN : energy < 1e-18 ? 0 : Math.Clamp(_lr / energy, -1, 1);
        o.BalanceDb = Math.Max(_ll, _rr) < 1e-9 ? 0 : 10 * Math.Log10(Math.Max(_rr, 1e-20) / Math.Max(_ll, 1e-20));
        // Oldest first.
        int tail = _scope.Length - _scopePos;
        _scope.AsSpan(_scopePos).CopyTo(o.Scope);
        _scope.AsSpan(0, _scopePos).CopyTo(o.Scope.AsSpan(tail));
        o.Sequence = ++_sequence;
        Output.Publish();
    }
}
