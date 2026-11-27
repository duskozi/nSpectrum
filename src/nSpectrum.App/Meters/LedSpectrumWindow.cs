using System.Windows.Controls;
using nSpectrum.Core.Audio;
using nSpectrum.Core.Dsp;
using nSpectrum.Core.Meters;
using nSpectrum.Visuals;

namespace nSpectrum.App.Meters;

/// <summary>Floating segmented-LED spectrum analyzer: stereo or mono, 12 to 48 bands, themes, peak caps, labels.</summary>
internal sealed class LedSpectrumWindow : MeterWindow
{
    private const double MinHz = 40, MaxHz = 16000;

    private readonly LedSpectrumModel _model = new();
    private readonly SpectrumFrame[] _frames = new SpectrumFrame[2];
    private AnalysisPipeline? _pipeline;
    private LedSpectrumTap? _tap;

    private bool _stereo;
    private int _bands;
    private int _segments;
    private LedTheme _theme;
    private bool _labels, _peaks, _glow;
    private double _range, _tilt;
    private string _speed;
    private int _fft;          // 0 = auto
    private double _smoothing; // ms, 0 = none

    // Shown before the first spectrum arrives.
    private float[][] _zeros = [[], []];
    private IReadOnlyList<OctaveBand> _idleBands = [];

    public LedSpectrumWindow() : base("Led", "nSpectrum LED", 1100, 380)
    {
        _stereo = Settings.Get("Stereo", true);
        _bands = Settings.Get("Bands", 24);
        _segments = Settings.Get("Segments", 20);
        _theme = LedTheme.ByName(Settings.Get("Theme", LedTheme.ClassicGreen.Name));
        _labels = Settings.Get("Labels", true);
        _peaks = Settings.Get("Peaks", true);
        _glow = Settings.Get("Glow", true);
        _range = Settings.Get("Range", 66.0);
        _tilt = Settings.Get("Tilt", 0.0);
        _speed = Settings.Get("Speed", "Normal");
        _fft = Settings.Get("FftSize", 0);
        _smoothing = Math.Clamp(Settings.Get("SmoothingMs", LedSpectrumTap.DefaultSmoothingMs), 0, 100);
        ApplyModel();
    }

    protected override bool RackFrame => true;

    protected override void OnAttach(AnalysisPipeline pipeline)
    {
        _pipeline = pipeline;
        _tap = new LedSpectrumTap(pipeline.Format.SampleRate, pipeline.Format.Channels, _stereo, _fft, _smoothing);
        pipeline.AddTap(_tap);
    }

    protected override void OnDetach()
    {
        if (_tap is not null) _pipeline?.RemoveTap(_tap);
        _tap = null;
        _pipeline = null;
    }

    private void ApplyModel()
    {
        _model.FloorDb = -_range;
        _model.CeilingDb = 0;
        _model.TiltDbPerOctave = _tilt;
        (_model.ReleaseDbPerSecond, _model.PeakFallDbPerSecond) = _speed switch
        {
            "Fast" => (70.0, 24.0),
            "Slow" => (20.0, 8.0),
            _ => (38.0, 14.0),
        };
    }

    protected override void Paint(PixelBuffer px, double dt)
    {
        var style = new LedSpectrumStyle
        {
            Theme = _theme, Segments = _segments, ShowLabels = _labels, ShowPeaks = _peaks, Glow = _glow,
            ShowChannelTags = false,
        };
        var tap = _tap;
        if (tap is not null)
        {
            var analyzers = tap.Analyzers;
            bool any = true;
            for (int c = 0; c < analyzers.Count; c++)
            {
                analyzers[c].Output.TryAcquire(out var f);
                _frames[c] = f;
                any &= analyzers[c].FrameCount > 0;
            }
            if (any)
            {
                _model.Update(_frames.AsSpan(0, analyzers.Count), _bands, MinHz, MaxHz, dt);
                LedSpectrumPainter.Paint(px, _model, style);
                return;
            }
        }
        // No signal yet: all LEDs dark.
        int channels = _stereo && (Format?.Channels ?? 2) >= 2 ? 2 : 1;
        if (_zeros[0].Length != _bands)
        {
            _zeros = [new float[_bands], new float[_bands]];
            _idleBands = OctaveBands.LogSpaced(_bands, MinHz, MaxHz);
        }
        LedSpectrumPainter.Paint(px, channels, _bands, i => _idleBands[i].Label, _zeros, _zeros, style);
    }

    protected override void BuildMenu(ItemCollection items)
    {
        Group(items, [("Mono", false), ("Stereo", true)], _stereo, v => { _stereo = v; _tap?.SetStereo(v); });
        items.Add(new Separator());
        Group(items, [("16 bands", 16), ("24 bands", 24), ("32 bands", 32)], _bands, v => _bands = v);
        items.Add(Choice("More bands", [("10 bands", 10), ("12 bands", 12), ("20 bands", 20), ("40 bands", 40), ("48 bands", 48), ("64 bands", 64)], _bands, v => _bands = v));
        items.Add(new Separator());
        Group(items, LedTheme.All.Select(t => (t.Name, t)), _theme, v => _theme = v);
        items.Add(new Separator());
        items.Add(Check("Show frequency labels", _labels, () => _labels = !_labels));
        items.Add(Check("Show peak indicator", _peaks, () => _peaks = !_peaks));
        items.Add(Check("Glow", _glow, () => _glow = !_glow));
        items.Add(Choice("LED rows", [("12", 12), ("16", 16), ("20", 20), ("24", 24), ("32", 32), ("40", 40)], _segments, v => _segments = v));
        items.Add(Choice("Range", [("48 dB", 48.0), ("60 dB", 60.0), ("66 dB", 66.0), ("72 dB", 72.0), ("90 dB", 90.0)], _range, v => { _range = v; ApplyModel(); }));
        items.Add(Choice("Tilt", [("Flat", 0.0), ("+3 dB/oct (pink reads flat)", 3.0), ("+4.5 dB/oct", 4.5)], _tilt, v => { _tilt = v; ApplyModel(); }));
        string auto = $"Auto ({LedSpectrumTap.AutoFftSize(Format?.SampleRate ?? 48000)})";
        items.Add(Choice("FFT size", new[] { (auto, 0) }.Concat(new[] { 2048, 4096, 8192, 16384, 32768, 65536 }.Select(n => (BinLabel(n), n))),
            _fft, v => { _fft = v; _tap?.SetAnalysis(_fft, _smoothing); }));
        items.Add(Choice("Smoothing", new[] { 0.0, 10, 20, 30, 45, 60, 80, 100 }.Select(ms => (ms == 0 ? "None" : $"{ms:0} ms", ms)),
            _smoothing, v => { _smoothing = v; _tap?.SetAnalysis(_fft, _smoothing); }));
        items.Add(Choice("Fall speed", [("Fast", "Fast"), ("Normal", "Normal"), ("Slow", "Slow")], _speed, v => { _speed = v; ApplyModel(); }));
    }

    private string BinLabel(int n)
    {
        double bin = (Format?.SampleRate ?? 48000) / (double)n;
        return $"{n}  ({bin:0.#} Hz bins)";
    }

    protected override IEnumerable<(string Label, double W, double H)> Sizes =>
        [("Compact (640 × 220)", 640, 220), ("Medium (1100 × 380)", 1100, 380), ("Wide (1600 × 420)", 1600, 420), ("Large (1600 × 700)", 1600, 700)];

    protected override void SaveOptions()
    {
        Settings.Set("Stereo", _stereo);
        Settings.Set("Bands", _bands);
        Settings.Set("Segments", _segments);
        Settings.Set("Theme", _theme.Name);
        Settings.Set("Labels", _labels);
        Settings.Set("Peaks", _peaks);
        Settings.Set("Glow", _glow);
        Settings.Set("Range", _range);
        Settings.Set("Tilt", _tilt);
        Settings.Set("Speed", _speed);
        Settings.Set("FftSize", _fft);
        Settings.Set("SmoothingMs", _smoothing);
    }
}
