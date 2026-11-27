namespace nSpectrum.Core.Meters;

/// <summary>
/// Loudness and true-peak meter after ITU-R BS.1770-4 and EBU R128 / Tech 3341 / Tech 3342:
/// momentary (400 ms), short-term (3 s) and gated integrated loudness in LUFS, loudness range (LRA)
/// in LU, and true peak in dBTP per channel.
/// </summary>
/// <remarks>
/// <para>K-weighting is the two-stage filter of BS.1770 (high shelf + RLB high-pass), designed for
/// the actual sample rate from the standard's analogue prototypes, so 44.1, 88.2, 96 and 192 kHz
/// read the same as 48 kHz. Channel weights follow BS.1770: 1.0 for L, R, C, 1.41 for the surrounds,
/// LFE excluded (5.1 and 7.1 in WAVEFORMATEXTENSIBLE order). A mono source counts as one channel.</para>
/// <para>Integrated loudness and LRA keep exact energy sums in 0.01 LU histogram bins instead of a
/// growing list of blocks, so a meter can run for days without allocating; the gate thresholds
/// are resolved to 0.01 LU.</para>
/// <para>True peak oversamples 4× below 96 kHz and 2× below 192 kHz with a 96-tap (4×) Kaiser-windowed
/// sinc interpolator, flat to about 19.5 kHz at 48 kHz.</para>
/// <para>Single-threaded: call <see cref="Process"/> from one thread. Allocation-free after construction.</para>
/// </remarks>
public sealed class LoudnessMeter
{
    /// <summary>Absolute gate (BS.1770-4).</summary>
    public const double AbsoluteGateLufs = -70;

    private const double HistMin = -70, HistMax = 10, HistStep = 0.01;
    private const int HistBins = (int)((HistMax - HistMin) / HistStep);
    private const int ShortBlocks = 30;   // 3 s of 100 ms sub-blocks
    private const int MomentaryBlocks = 4; // 400 ms

    private readonly int _channels;
    private readonly double[] _weight;
    // K-weighting biquads (direct form I), shared coefficients, per-channel state.
    private readonly double _b0, _b1, _b2, _a1, _a2;  // stage 1: high shelf
    private readonly double _c1, _c2;                // stage 2: high-pass, b = 1, -2, 1
    private readonly double[] _x1, _x2, _y1, _y2;    // stage 1 state
    private readonly double[] _u1, _u2;              // stage 2 state (its input = stage-1 output)
    private readonly double[] _v1, _v2;

    private readonly int _subLength;   // samples per 100 ms sub-block
    private int _subFill;
    private double _subEnergy;
    private readonly double[] _subRing = new double[ShortBlocks];
    private int _subIndex;
    private long _subCount;

    private readonly long[] _intCount = new long[HistBins];
    private readonly double[] _intEnergy = new double[HistBins];
    private readonly long[] _lraCount = new long[HistBins];
    private readonly double[] _lraEnergy = new double[HistBins];

    // True peak.
    private readonly int _os;
    private readonly int _taps;          // per phase
    private readonly double[] _poly;     // [phase * _taps + j]
    private readonly double[] _hist;     // per channel mirrored history, 2 * _taps each
    private readonly int[] _histPos;
    private readonly double[] _truePeak;

    public LoudnessMeter(int sampleRate, int channels)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        _channels = channels;
        _weight = ChannelWeights(channels);

        // Stage 1: high shelf, +4 dB above ~1.7 kHz (BS.1770 pre-filter).
        {
            const double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1 + k / q + k * k;
            _b0 = (vh + vb * k / q + k * k) / a0;
            _b1 = 2 * (k * k - vh) / a0;
            _b2 = (vh - vb * k / q + k * k) / a0;
            _a1 = 2 * (k * k - 1) / a0;
            _a2 = (1 - k / q + k * k) / a0;
        }
        // Stage 2: RLB high-pass at ~38 Hz.
        {
            const double f0 = 38.13547087602444, q = 0.5003270373238773;
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double a0 = 1 + k / q + k * k;
            _c1 = 2 * (k * k - 1) / a0;
            _c2 = (1 - k / q + k * k) / a0;
        }
        _x1 = new double[channels]; _x2 = new double[channels];
        _y1 = new double[channels]; _y2 = new double[channels];
        _u1 = new double[channels]; _u2 = new double[channels];
        _v1 = new double[channels]; _v2 = new double[channels];

        _subLength = (int)Math.Round(sampleRate / 10.0);

        _os = sampleRate < 96000 ? 4 : sampleRate < 192000 ? 2 : 1;
        _taps = 24;
        _poly = DesignInterpolator(_os, _taps);
        _hist = new double[channels * 2 * _taps];
        _histPos = new int[channels];
        _truePeak = new double[channels];

        ResetMeasurements();
    }

    public int SampleRate { get; }
    public int Channels => _channels;

    /// <summary>Oversampling factor of the true-peak detector.</summary>
    public int Oversampling => _os;

    /// <summary>BS.1770 coefficients of the first (shelf) stage: b0, b1, b2, a1, a2.</summary>
    public (double B0, double B1, double B2, double A1, double A2) ShelfCoefficients => (_b0, _b1, _b2, _a1, _a2);

    /// <summary>BS.1770 coefficients of the second (high-pass) stage: a1, a2 (b = 1, −2, 1).</summary>
    public (double A1, double A2) HighPassCoefficients => (_c1, _c2);

    /// <summary>Momentary loudness (400 ms), LUFS; −∞ before the first 400 ms.</summary>
    public double Momentary { get; private set; }

    /// <summary>Short-term loudness (3 s), LUFS; over what is available during the first 3 s.</summary>
    public double ShortTerm { get; private set; }

    /// <summary>Gated integrated loudness since the last reset, LUFS; −∞ until a block passes the gate.</summary>
    public double Integrated { get; private set; }

    /// <summary>Loudness range (EBU Tech 3342), LU; 0 until there are short-term values above the gates.</summary>
    public double LoudnessRange { get; private set; }

    public double MomentaryMax { get; private set; }
    public double ShortTermMax { get; private set; }

    /// <summary>Highest true peak of any channel since the last reset, dBTP.</summary>
    public double TruePeakMax
    {
        get
        {
            double m = 0;
            foreach (double p in _truePeak) m = Math.Max(m, p);
            return ToDb(m);
        }
    }

    /// <summary>Highest true peak of channel <paramref name="channel"/> since the last reset, dBTP.</summary>
    public double TruePeak(int channel) => ToDb(_truePeak[channel]);

    /// <summary>Seconds measured since the last reset.</summary>
    public double Seconds => (_subCount * (double)_subLength + _subFill) / SampleRate;

    /// <summary>Clears integrated loudness, LRA, maxima and true peaks; keeps the K-weighting filter states.</summary>
    public void ResetMeasurements()
    {
        Array.Clear(_intCount); Array.Clear(_intEnergy);
        Array.Clear(_lraCount); Array.Clear(_lraEnergy);
        Array.Clear(_subRing);
        Array.Clear(_truePeak);
        Array.Clear(_hist); // or the interpolator would report peaks of samples from before the reset
        _subIndex = 0; _subCount = 0; _subFill = 0; _subEnergy = 0;
        Momentary = ShortTerm = Integrated = double.NegativeInfinity;
        MomentaryMax = ShortTermMax = double.NegativeInfinity;
        LoudnessRange = 0;
    }

    /// <summary>Feeds interleaved samples; <paramref name="channels"/> must match the meter.</summary>
    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels != _channels) throw new ArgumentException("Channel count mismatch.", nameof(channels));
        int frames = interleaved.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            int i = f * channels;
            double e = 0;
            for (int c = 0; c < channels; c++)
            {
                double x = interleaved[i + c];
                TruePeakStep(c, x);
                if (_weight[c] == 0) continue;
                // Stage 1.
                double y = _b0 * x + _b1 * _x1[c] + _b2 * _x2[c] - _a1 * _y1[c] - _a2 * _y2[c];
                _x2[c] = _x1[c]; _x1[c] = x;
                _y2[c] = _y1[c]; _y1[c] = y;
                // Stage 2.
                double z = y - 2 * _u1[c] + _u2[c] - _c1 * _v1[c] - _c2 * _v2[c];
                _u2[c] = _u1[c]; _u1[c] = y;
                _v2[c] = _v1[c]; _v1[c] = z;
                e += _weight[c] * z * z;
            }
            _subEnergy += e;
            if (++_subFill == _subLength) EndSubBlock();
        }
        // Denormals would slow the filters to a crawl in long silences.
        for (int c = 0; c < channels; c++)
        {
            if (Math.Abs(_y1[c]) < 1e-30) { _y1[c] = 0; _y2[c] = 0; }
            if (Math.Abs(_v1[c]) < 1e-30) { _v1[c] = 0; _v2[c] = 0; }
        }
    }

    private void EndSubBlock()
    {
        _subRing[_subIndex] = _subEnergy;
        if (++_subIndex == ShortBlocks) _subIndex = 0;
        _subCount++;
        _subEnergy = 0;
        _subFill = 0;

        if (_subCount >= MomentaryBlocks)
        {
            double z = LastBlocks(MomentaryBlocks) / (MomentaryBlocks * (double)_subLength);
            double l = ToLufs(z);
            Momentary = l;
            if (l > MomentaryMax) MomentaryMax = l;
            if (l > AbsoluteGateLufs) Add(_intCount, _intEnergy, l, z);
            Integrated = Gated(_intCount, _intEnergy, -10, out _);
        }

        int n = (int)Math.Min(_subCount, ShortBlocks);
        double zs = LastBlocks(n) / (n * (double)_subLength);
        ShortTerm = ToLufs(zs);
        if (_subCount >= ShortBlocks)
        {
            if (ShortTerm > ShortTermMax) ShortTermMax = ShortTerm;
            if (ShortTerm > AbsoluteGateLufs) Add(_lraCount, _lraEnergy, ShortTerm, zs);
            LoudnessRange = Range();
        }
    }

    private double LastBlocks(int count)
    {
        double s = 0;
        for (int k = 1; k <= count; k++)
        {
            int idx = _subIndex - k;
            if (idx < 0) idx += ShortBlocks;
            s += _subRing[idx];
        }
        return s;
    }

    private static int BinOf(double lufs) => Math.Clamp((int)Math.Floor((lufs - HistMin) / HistStep), 0, HistBins - 1);

    private static double BinLower(int bin) => HistMin + bin * HistStep;

    private static void Add(long[] count, double[] energy, double lufs, double z)
    {
        int b = BinOf(lufs);
        count[b]++;
        energy[b] += z;
    }

    /// <summary>Mean loudness of the blocks above a relative gate, and the gate's first bin.</summary>
    private static double Gated(long[] count, double[] energy, double relativeLu, out int firstBin)
    {
        long n = 0;
        double e = 0;
        for (int b = 0; b < HistBins; b++) { n += count[b]; e += energy[b]; }
        firstBin = HistBins;
        if (n == 0) return double.NegativeInfinity;
        double gate = ToLufs(e / n) + relativeLu;
        firstBin = BinOf(gate);
        // The bin holding the gate is split: only its blocks above the gate count. Its blocks are
        // within 0.01 LU of each other, so take them when the bin's mean is above the gate.
        n = 0; e = 0;
        for (int b = firstBin; b < HistBins; b++)
        {
            if (count[b] == 0) continue;
            if (b == firstBin && ToLufs(energy[b] / count[b]) <= gate) continue;
            n += count[b]; e += energy[b];
        }
        return n == 0 ? double.NegativeInfinity : ToLufs(e / n);
    }

    /// <summary>LRA = P95 − P10 of the short-term loudness above the absolute and −20 LU relative gates.</summary>
    private double Range()
    {
        Gated(_lraCount, _lraEnergy, -20, out int first);
        long n = 0;
        for (int b = first; b < HistBins; b++) n += _lraCount[b];
        if (n == 0) return 0;
        double p10 = Percentile(first, n, 0.10), p95 = Percentile(first, n, 0.95);
        return Math.Max(0, p95 - p10);
    }

    private double Percentile(int first, long n, double p)
    {
        // Nearest rank on the sorted values; each bin's values sit at its centre.
        long rank = Math.Max(1, (long)Math.Ceiling(p * n));
        long acc = 0;
        for (int b = first; b < HistBins; b++)
        {
            acc += _lraCount[b];
            if (acc >= rank) return BinLower(b) + HistStep / 2;
        }
        return BinLower(HistBins - 1);
    }

    private void TruePeakStep(int c, double x)
    {
        int t = _taps;
        int baseIdx = c * 2 * t;
        int w = _histPos[c];
        _hist[baseIdx + w] = x;
        _hist[baseIdx + w + t] = x;
        // Newest sample at w + t, then back in time down to w + 1.
        double peak = _truePeak[c];
        double ax = Math.Abs(x);
        if (ax > peak) peak = ax;
        if (_os > 1)
        {
            int newest = baseIdx + w + t;
            for (int p = 0; p < _os; p++)
            {
                double acc = 0;
                int ci = p * t;
                for (int j = 0; j < t; j++) acc += _poly[ci + j] * _hist[newest - j];
                double a = Math.Abs(acc);
                if (a > peak) peak = a;
            }
        }
        _truePeak[c] = peak;
        if (++w == t) w = 0;
        _histPos[c] = w;
    }

    /// <summary>Polyphase Kaiser-windowed sinc interpolator, unity gain per phase.</summary>
    private static double[] DesignInterpolator(int os, int tapsPerPhase)
    {
        int n = os * tapsPerPhase;
        var h = new double[n];
        double beta = 7.0, center = (n - 1) / 2.0;
        double i0b = Dsp.Window.BesselI0(beta);
        for (int i = 0; i < n; i++)
        {
            double t = (i - center) / os;              // in input samples
            double sinc = t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
            double r = (i - center) / (n / 2.0);
            double win = Dsp.Window.BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
            h[i] = sinc * win;
        }
        var poly = new double[n];
        for (int p = 0; p < os; p++)
        {
            double sum = 0;
            for (int j = 0; j < tapsPerPhase; j++) sum += h[p + os * j];
            for (int j = 0; j < tapsPerPhase; j++) poly[p * tapsPerPhase + j] = h[p + os * j] / sum;
        }
        return poly;
    }

    private static double[] ChannelWeights(int channels)
    {
        var w = new double[channels];
        Array.Fill(w, 1.0);
        if (channels == 6) { w[3] = 0; w[4] = w[5] = 1.41; }                       // L R C LFE Ls Rs
        else if (channels == 8) { w[3] = 0; for (int i = 4; i < 8; i++) w[i] = 1.41; } // L R C LFE Lb Rb Ls Rs
        return w;
    }

    /// <summary>L = −0.691 + 10·log10(z).</summary>
    public static double ToLufs(double meanSquare) => meanSquare > 0 ? -0.691 + 10 * Math.Log10(meanSquare) : double.NegativeInfinity;

    private static double ToDb(double linear) => linear > 0 ? 20 * Math.Log10(linear) : double.NegativeInfinity;
}
