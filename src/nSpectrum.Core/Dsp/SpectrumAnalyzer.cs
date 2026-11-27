using System.Numerics.Tensors;
using nSpectrum.Core.Concurrency;

namespace nSpectrum.Core.Dsp;

/// <summary>
/// One FFT analysis chain for one mono signal: sliding history, window, real FFT, scaling,
/// averaging in the power domain, peak hold, and publication into a triple buffer.
/// </summary>
/// <remarks>
/// Single-threaded: <see cref="Process"/> must be called from one thread (the DSP worker).
/// <see cref="RequestReset"/> may be called from any thread. After construction nothing here allocates.
/// The first frame appears once the window has been filled (N samples, e.g. 171 ms for 8192 at 48 kHz).
/// </remarks>
public sealed class SpectrumAnalyzer
{
    private readonly SpectrumSettings _settings;
    private readonly RealFft _fft;
    private readonly float[] _window;      // length N, zero beyond the window length
    private readonly int _windowLength;
    private readonly float[] _history;     // mirrored ring of length 2L
    private int _historyPos;
    private readonly float[] _fftInput;    // used only when zero-padding
    private readonly float[] _power;       // scratch, bin count
    private readonly float[] _average;
    private readonly float[] _peak;
    private readonly float[] _peakHoldLeft;
    private readonly float[]? _weights;    // per-bin power gain, null when flat
    private readonly float[][]? _linearRing;
    private readonly double[]? _linearSum;
    private int _linearIndex;
    private int _averagedCount;

    private readonly float _innerScale, _edgeScale;
    private readonly float _alphaRise, _alphaDecay;
    private readonly float _peakDecay;
    private readonly float _frameSeconds;

    private readonly double _hopExact;
    private double _nextFrameAt;          // stream position at which the next frame is due
    private long _position;                // samples consumed
    private long _sequence;
    private int _resetRequest;             // bit 0: averages, bit 1: peaks

    public SpectrumAnalyzer(SpectrumSettings settings, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (sampleRate < 1000 || sampleRate > 1_000_000) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _settings = settings;
        int n = settings.FftSize;
        _windowLength = settings.EffectiveWindowLength;
        _fft = new RealFft(n);

        var win = Window.Create(settings.Window, _windowLength, settings.KaiserBeta);
        _window = new float[n];
        win.Coefficients.CopyTo(_window);

        _hopExact = sampleRate / settings.AnalysisRate;
        int hop = Math.Max(1, (int)Math.Round(_hopExact));
        Info = new SpectrumInfo(settings, sampleRate, win, hop);
        _frameSeconds = (float)(_hopExact / sampleRate);

        _history = new float[2 * _windowLength];
        _fftInput = _windowLength < n ? new float[n] : [];
        int bins = Info.BinCount;
        _power = new float[bins];
        _average = new float[bins];
        _peak = new float[bins];
        _peakHoldLeft = new float[bins];

        // Scaling of |X|² into the chosen unit. Reference factor makes a full-scale sine 0 dB (AES17)
        // or makes RMS 1.0 read 0 dB.
        double s1 = win.S1, s2 = win.S2;
        double refFactor = settings.Reference == DbfsReference.SineFullScale ? 2.0 : 1.0;
        double inner, edge;
        switch (settings.Scale)
        {
            case SpectrumScale.Amplitude:
                // Peak amplitude² of a tone = (2|X|/S1)², expressed as mean square (·½), then reference.
                inner = 4.0 / (s1 * s1) * 0.5 * refFactor;
                edge = 1.0 / (s1 * s1) * refFactor; // DC: amplitude |X|/S1, mean square = amplitude²
                break;
            case SpectrumScale.Power:
                // Power in one bin of width fs/N: 2|X|²/(N·S2).
                inner = 2.0 / (n * s2) * refFactor;
                edge = 1.0 / (n * s2) * refFactor;
                break;
            case SpectrumScale.PowerDensity:
                inner = 2.0 / (sampleRate * s2) * refFactor;
                edge = 1.0 / (sampleRate * s2) * refFactor;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings));
        }
        _innerScale = (float)inner;
        _edgeScale = (float)edge;

        if (settings.Weighting != FrequencyWeighting.Z)
        {
            _weights = new float[bins];
            for (int k = 0; k < bins; k++)
                _weights[k] = (float)WeightingCurves.PowerGain(settings.Weighting, Info.BinFrequency(k));
        }

        double rise = settings.AveragingTime;
        double decay = settings.AveragingDecayTime ?? rise;
        _alphaRise = (float)(1 - Math.Exp(-_hopExact / (rise * sampleRate)));
        _alphaDecay = (float)(1 - Math.Exp(-_hopExact / (decay * sampleRate)));

        _peakDecay = (float)Math.Pow(10, -settings.PeakFallRate * _frameSeconds / 10.0);

        if (settings.Averaging == AveragingMode.Linear)
        {
            _linearRing = new float[settings.LinearFrames][];
            for (int i = 0; i < _linearRing.Length; i++) _linearRing[i] = new float[bins];
            _linearSum = new double[bins];
        }

        Output = new TripleBuffer<SpectrumFrame>(() => new SpectrumFrame(Info));
        _nextFrameAt = _hopExact;
    }

    public SpectrumInfo Info { get; }

    public SpectrumSettings Settings => _settings;

    /// <summary>Finished frames. The renderer acquires from here.</summary>
    public TripleBuffer<SpectrumFrame> Output { get; }

    /// <summary>Frames produced so far.</summary>
    public long FrameCount => _sequence;

    /// <summary>Samples consumed so far.</summary>
    public long Position => _position;

    /// <summary>Thread-safe: clears averages and/or peaks at the next frame.</summary>
    public void RequestReset(bool averages = true, bool peaks = true)
    {
        int bits = (averages ? 1 : 0) | (peaks ? 2 : 0);
        int cur;
        do { cur = Volatile.Read(ref _resetRequest); }
        while (Interlocked.CompareExchange(ref _resetRequest, cur | bits, cur) != cur);
    }

    /// <summary>Feeds mono samples. Produces zero or more frames, one per hop.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        int L = _windowLength;
        float[] h = _history;
        int i = 0;
        while (i < samples.Length)
        {
            long due = (long)Math.Ceiling(_nextFrameAt);
            int take = (int)Math.Min(samples.Length - i, Math.Max(0, due - _position));
            for (int k = 0; k < take; k++)
            {
                float s = samples[i + k];
                h[_historyPos] = s;
                h[_historyPos + L] = s;
                if (++_historyPos == L) _historyPos = 0;
            }
            i += take;
            _position += take;
            if (_position >= due)
            {
                // No frames until the window has been filled once: a half-empty window would
                // read low and bias Linear and Infinite averages.
                if (_position >= _windowLength) ComputeFrame();
                _nextFrameAt += _hopExact;
            }
        }
    }

    private void ComputeFrame()
    {
        int reset = Interlocked.Exchange(ref _resetRequest, 0);
        if ((reset & 1) != 0) ResetAverages();
        if ((reset & 2) != 0) ResetPeaks();

        // Oldest-to-newest view of the last L samples.
        ReadOnlySpan<float> view = _history.AsSpan(_historyPos, _windowLength);
        if (_fftInput.Length != 0)
        {
            view.CopyTo(_fftInput);
            view = _fftInput;
        }
        _fft.ForwardPower(view, _window, _power);

        Span<float> p = _power;
        int last = p.Length - 1;
        float dc = p[0] * _edgeScale, nyquist = p[last] * _edgeScale;
        TensorPrimitives.Multiply(p, _innerScale, p);
        p[0] = dc;
        p[last] = nyquist;
        if (_weights is not null) TensorPrimitives.Multiply(p, _weights, p);

        Average(p);

        SpectrumFrame frame = Output.WriteBuffer;
        _average.AsSpan().CopyTo(frame.Values);
        if (_settings.PeakHold)
        {
            UpdatePeaks();
            _peak.AsSpan().CopyTo(frame.Peaks);
        }
        frame.Sequence = ++_sequence;
        frame.SamplePosition = _position;
        frame.AveragedCount = _averagedCount;
        Output.Publish();
    }

    private void Average(Span<float> p)
    {
        Span<float> avg = _average;
        switch (_settings.Averaging)
        {
            case AveragingMode.None:
                p.CopyTo(avg);
                _averagedCount = 1;
                break;

            case AveragingMode.Exponential:
                if (_averagedCount == 0)
                {
                    // Seed with the first spectrum after start or reset. Starting from zero would
                    // make every reading crawl up from -inf (Slow: about 2 s to come within 1 dB).
                    p.CopyTo(avg);
                }
                else if (_alphaRise == _alphaDecay)
                {
                    // avg += α (p - avg)  →  avg = avg·(1-α) + p·α
                    TensorPrimitives.Subtract(p, avg, p);
                    TensorPrimitives.MultiplyAdd(p, _alphaRise, avg, avg);
                }
                else
                {
                    float ar = _alphaRise, ad = _alphaDecay;
                    for (int k = 0; k < avg.Length; k++)
                    {
                        float d = p[k] - avg[k];
                        avg[k] += (d > 0 ? ar : ad) * d;
                    }
                }
                _averagedCount = 1;
                break;

            case AveragingMode.Linear:
            {
                float[] slot = _linearRing![_linearIndex];
                double[] sum = _linearSum!;
                int frames = _linearRing.Length;
                if (_averagedCount < frames) _averagedCount++;
                double inv = 1.0 / _averagedCount;
                for (int k = 0; k < avg.Length; k++)
                {
                    double s = sum[k] + p[k] - slot[k];
                    sum[k] = s;
                    avg[k] = (float)(s * inv);
                    slot[k] = p[k];
                }
                if (++_linearIndex == frames) _linearIndex = 0;
                break;
            }

            case AveragingMode.Infinite:
            {
                _averagedCount++;
                float w = 1f / _averagedCount;
                TensorPrimitives.Subtract(p, avg, p);
                TensorPrimitives.MultiplyAdd(p, w, avg, avg);
                break;
            }

            case AveragingMode.MaxHold:
                TensorPrimitives.Max(avg, p, avg);
                _averagedCount++;
                break;
        }
    }

    private void UpdatePeaks()
    {
        float[] avg = _average, peak = _peak, left = _peakHoldLeft;
        float hold = (float)_settings.PeakHoldTime;
        float dt = _frameSeconds;
        float decay = _peakDecay;
        bool infinite = float.IsPositiveInfinity(hold);
        for (int k = 0; k < peak.Length; k++)
        {
            float v = avg[k];
            if (v >= peak[k])
            {
                peak[k] = v;
                left[k] = hold;
            }
            else if (!infinite)
            {
                if (left[k] > 0)
                {
                    left[k] -= dt;
                }
                else
                {
                    float d = peak[k] * decay;
                    peak[k] = d > v ? d : v;
                }
            }
        }
    }

    private void ResetAverages()
    {
        Array.Clear(_average);
        _averagedCount = 0;
        _linearIndex = 0;
        if (_linearRing is not null)
        {
            foreach (float[] r in _linearRing) Array.Clear(r);
            Array.Clear(_linearSum!);
        }
    }

    private void ResetPeaks()
    {
        Array.Clear(_peak);
        Array.Clear(_peakHoldLeft);
    }
}
