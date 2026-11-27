using System.Windows.Controls;
using nSpectrum.Core.Audio;
using nSpectrum.Core.Meters;
using nSpectrum.Visuals;

namespace nSpectrum.App.Meters;

/// <summary>Floating VU meter: analog needles (IEC 60268-17 ballistics) or digital peak/RMS LED bars.</summary>
internal sealed class VuMeterWindow : MeterWindow
{
    private readonly VuMeterPainter _painter = new();
    private AnalysisPipeline? _pipeline;
    private MeterTap? _tap;
    private VuMeterStyle _style;

    public VuMeterWindow() : base("Vu", "nSpectrum VU", 640, 220)
    {
        _style = new VuMeterStyle
        {
            Kind = Enum.TryParse<VuMeterKind>(Settings.Get("Kind", "Analog"), out var k) ? k : VuMeterKind.Analog,
            Face = VuFace.ByName(Settings.Get("Face", VuFace.Classic.Name)),
            Theme = LedTheme.ByName(Settings.Get("Theme", LedTheme.ClassicGreen.Name)),
            BarMode = Enum.TryParse<DigitalBarMode>(Settings.Get("BarMode", "Rms"), out var b) ? b : DigitalBarMode.Rms,
            ReferenceDbfs = Settings.Get("Reference", -18.0),
            PeakLampDbfs = Settings.Get("PeakLamp", -3.0),
            PeakHold = Settings.Get("PeakHold", true),
            Mono = Settings.Get("Mono", false),
        };
    }

    protected override void OnAttach(AnalysisPipeline pipeline)
    {
        _pipeline = pipeline;
        _tap = new MeterTap(pipeline.Format.SampleRate);
        pipeline.AddTap(_tap);
        _painter.ResetHolds();
    }

    protected override void OnDetach()
    {
        if (_tap is not null) _pipeline?.RemoveTap(_tap);
        _tap = null;
        _pipeline = null;
    }

    protected override void Paint(PixelBuffer px, double dt)
    {
        MeterFrame? frame = null;
        if (_tap is { } tap)
        {
            tap.Output.TryAcquire(out var f);
            if (f.Sequence > 0) frame = f;
        }
        _painter.Paint(px, frame, _style, dt);
    }

    protected override void BuildMenu(ItemCollection items)
    {
        Group(items, [("Analog", VuMeterKind.Analog), ("Digital", VuMeterKind.Digital)], _style.Kind, v => _style = _style with { Kind = v });
        items.Add(new Separator());
        Group(items, [("Stereo", false), ("Mono (one meter)", true)], _style.Mono, v => _style = _style with { Mono = v });
        items.Add(new Separator());
        items.Add(Choice("Reference (0 VU)", [("−14 dBFS", -14.0), ("−18 dBFS (EBU R68)", -18.0), ("−20 dBFS (SMPTE RP155)", -20.0), ("−24 dBFS", -24.0)],
            _style.ReferenceDbfs, v => _style = _style with { ReferenceDbfs = v }));
        items.Add(Choice("Analog face", VuFace.All.Select(f => (f.Name, f)), _style.Face, v => _style = _style with { Face = v }));
        items.Add(Choice("Peak lamp", [("−6 dBFS", -6.0), ("−3 dBFS", -3.0), ("−1 dBFS", -1.0), ("0 dBFS", -0.01)],
            _style.PeakLampDbfs, v => _style = _style with { PeakLampDbfs = v }));
        items.Add(Choice("Digital colours", LedTheme.All.Select(t => (t.Name, t)), _style.Theme, v => _style = _style with { Theme = v }));
        items.Add(Choice("Digital bar", [("RMS + peak", DigitalBarMode.Rms), ("VU + peak", DigitalBarMode.Vu)], _style.BarMode, v => _style = _style with { BarMode = v }));
        items.Add(Check("Peak hold", _style.PeakHold, () => _style = _style with { PeakHold = !_style.PeakHold }));
        items.Add(Item("Reset peaks and clip", _painter.ResetHolds));
    }

    protected override IEnumerable<(string Label, double W, double H)> Sizes =>
        [("Small (420 × 150)", 420, 150), ("Medium (640 × 220)", 640, 220), ("Large (1000 × 330)", 1000, 330), ("Tall bars (200 × 520)", 200, 520)];

    protected override void SaveOptions()
    {
        Settings.Set("Kind", _style.Kind);
        Settings.Set("Face", _style.Face.Name);
        Settings.Set("Theme", _style.Theme.Name);
        Settings.Set("BarMode", _style.BarMode);
        Settings.Set("Reference", _style.ReferenceDbfs);
        Settings.Set("PeakLamp", _style.PeakLampDbfs);
        Settings.Set("PeakHold", _style.PeakHold);
        Settings.Set("Mono", _style.Mono);
    }
}
