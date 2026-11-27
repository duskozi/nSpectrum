using nSpectrum.Core.Dsp;

namespace nSpectrum.Core.Meters;

/// <summary>
/// UI-side model of an LED band display: maps the latest spectrum of each channel to N log-spaced
/// bands, applies an optional tilt, and runs LED ballistics (fast attack, smooth release, peak caps).
/// Call <see cref="Update"/> once per rendered frame on one thread.
/// </summary>
public sealed class LedSpectrumModel
{
    private OctaveBands? _bands;
    private SpectrumInfo? _info;
    private int _bandCount;
    private double _minHz, _maxHz;
    private float[] _power = [];
    private float[] _target = [];
    private float[] _tilt = [];
    private double _tiltDbPerOctave = double.NaN;
    private readonly DisplayBallistics[] _ballistics = [new(), new()];
    private int _channels;

    /// <summary>Bottom of the display in dBFS (power per band).</summary>
    public double FloorDb { get; set; } = -66;

    /// <summary>Top of the display in dBFS.</summary>
    public double CeilingDb { get; set; } = 0;

    /// <summary>Treble tilt in dB per octave around 1 kHz (0 = flat, 3 = pink noise reads flat).</summary>
    public double TiltDbPerOctave { get; set; }

    public double ReleaseDbPerSecond { get => _ballistics[0].ReleaseDbPerSecond; set { foreach (var b in _ballistics) b.ReleaseDbPerSecond = value; } }
    public double PeakHoldSeconds { get => _ballistics[0].PeakHoldSeconds; set { foreach (var b in _ballistics) b.PeakHoldSeconds = value; } }
    public double PeakFallDbPerSecond { get => _ballistics[0].PeakFallDbPerSecond; set { foreach (var b in _ballistics) b.PeakFallDbPerSecond = value; } }

    public LedSpectrumModel()
    {
        ReleaseDbPerSecond = 38;
        PeakHoldSeconds = 0.9;
        PeakFallDbPerSecond = 14;
    }

    /// <summary>Bands currently shown.</summary>
    public IReadOnlyList<OctaveBand> Bands => _bands?.Bands ?? [];

    /// <summary>Channels currently shown (0 before the first frame).</summary>
    public int Channels => _channels;

    /// <summary>Bar level per band, as 0..1 of the display range.</summary>
    public float[][] Level { get; } = [[], []];

    /// <summary>Peak cap per band, as 0..1 of the display range.</summary>
    public float[][] Peak { get; } = [[], []];

    /// <summary>Latest band levels in dBFS before ballistics (for readouts and tests).</summary>
    public float[][] RawDb { get; } = [[], []];

    /// <summary>
    /// Advances the display from the analyzers' newest frames. <paramref name="frames"/> holds one
    /// frame per channel (1 or 2), each possibly stale; <paramref name="dtSeconds"/> is the real time step.
    /// </summary>
    public void Update(ReadOnlySpan<SpectrumFrame> frames, int bandCount, double minHz, double maxHz, double dtSeconds)
    {
        if (frames.Length == 0) return;
        var info = frames[0].Info;
        maxHz = Math.Min(maxHz, info.SampleRate / 2.0 * 0.98);
        bool layout = _bands is null || _info is null || _info.BinCount != info.BinCount || _info.SampleRate != info.SampleRate || _info.Scale != info.Scale || bandCount != _bandCount || minHz != _minHz || maxHz != _maxHz;
        if (layout)
        {
            _bands = new OctaveBands(OctaveBands.LogSpaced(bandCount, minHz, maxHz), info.BinWidth, info.BinCount);
            _info = info;
            _bandCount = bandCount;
            _minHz = minHz;
            _maxHz = maxHz;
            _power = new float[bandCount];
            _target = new float[bandCount];
            _tiltDbPerOctave = double.NaN;
        }
        if (_tiltDbPerOctave != TiltDbPerOctave || _tilt.Length != bandCount)
        {
            _tilt = new float[bandCount];
            for (int b = 0; b < bandCount; b++) _tilt[b] = (float)(TiltDbPerOctave * Math.Log2(_bands!.Bands[b].Center / 1000));
            _tiltDbPerOctave = TiltDbPerOctave;
        }
        int channels = Math.Min(frames.Length, 2);
        float floor = (float)FloorDb;
        float range = (float)(CeilingDb - FloorDb);
        for (int c = 0; c < channels; c++)
        {
            if (layout || channels != _channels || Level[c].Length != bandCount)
            {
                Level[c] = new float[bandCount];
                Peak[c] = new float[bandCount];
                RawDb[c] = new float[bandCount];
                _ballistics[c].Reset(bandCount, floor - 1);
            }
            var frame = frames[c];
            if (frame.Info.BinCount != info.BinCount || frame.Info.Scale != info.Scale) continue; // reconfiguring: skip a frame
            _bands!.Compute(frame.Values, info, _power);
            for (int b = 0; b < bandCount; b++)
            {
                float db = 10 * MathF.Log10(Math.Max(_power[b], 1e-20f)) + _tilt[b];
                RawDb[c][b] = db;
                _target[b] = Math.Max(db, floor - 1);
            }
            var ball = _ballistics[c];
            ball.Update(_target, dtSeconds);
            var bars = ball.Bars;
            var caps = ball.PeakCaps;
            for (int b = 0; b < bandCount; b++)
            {
                Level[c][b] = Math.Clamp((bars[b] - floor) / range, 0, 1);
                Peak[c][b] = Math.Clamp((caps[b] - floor) / range, 0, 1);
            }
        }
        _channels = channels;
    }
}
