using System.Windows.Controls;
using nSpectrum.Core.Audio;
using nSpectrum.Core.Meters;
using nSpectrum.Visuals;

namespace nSpectrum.App.Meters;

/// <summary>Floating phase meter: goniometer (vectorscope) and correlation meter.</summary>
internal sealed class PhaseMeterWindow : MeterWindow
{
    private readonly PhaseMeterPainter _painter = new();
    private AnalysisPipeline? _pipeline;
    private MeterTap? _tap;
    private PhaseMeterStyle _style;

    private static readonly (string, Rgb)[] Traces =
    [
        ("Green", Rgb.Hex("#5CFF8A")), ("Amber", Rgb.Hex("#FFB13B")), ("Cyan", Rgb.Hex("#4FD8FF")), ("White", Rgb.Hex("#E8F0FF")),
    ];

    public PhaseMeterWindow() : base("Phase", "nSpectrum Phase", 360, 440)
    {
        string trace = Settings.Get("Trace", "Green");
        _style = new PhaseMeterStyle
        {
            Theme = LedTheme.ByName(Settings.Get("Theme", LedTheme.ClassicGreen.Name)),
            AutoGain = Settings.Get("AutoGain", true),
            Persistence = (float)Settings.Get("Persistence", 0.8),
            ShowGoniometer = Settings.Get("Goniometer", true),
            Trace = Traces.FirstOrDefault(t => t.Item1 == trace, Traces[0]).Item2,
        };
    }

    protected override void OnAttach(AnalysisPipeline pipeline)
    {
        _pipeline = pipeline;
        _tap = new MeterTap(pipeline.Format.SampleRate);
        pipeline.AddTap(_tap);
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
        items.Add(Check("Show goniometer", _style.ShowGoniometer, () => _style = _style with { ShowGoniometer = !_style.ShowGoniometer }));
        items.Add(Check("Auto gain", _style.AutoGain, () => _style = _style with { AutoGain = !_style.AutoGain }));
        items.Add(Choice("Afterglow", [("Off", 0f), ("Short", 0.6f), ("Medium", 0.8f), ("Long", 0.92f)], _style.Persistence, v => _style = _style with { Persistence = v }));
        items.Add(Choice("Trace colour", Traces, _style.Trace, v => _style = _style with { Trace = v }));
        items.Add(Choice("Meter colours", LedTheme.All.Select(t => (t.Name, t)), _style.Theme, v => _style = _style with { Theme = v }));
    }

    protected override IEnumerable<(string Label, double W, double H)> Sizes =>
        [("Small (260 × 320)", 260, 320), ("Medium (360 × 440)", 360, 440), ("Large (560 × 680)", 560, 680), ("Correlation bar only (480 × 110)", 480, 110)];

    protected override void SaveOptions()
    {
        Settings.Set("Theme", _style.Theme.Name);
        Settings.Set("AutoGain", _style.AutoGain);
        Settings.Set("Persistence", _style.Persistence);
        Settings.Set("Goniometer", _style.ShowGoniometer);
        Settings.Set("Trace", Traces.FirstOrDefault(t => t.Item2 == _style.Trace, Traces[0]).Item1);
    }
}
