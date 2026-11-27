using System.Windows.Controls;
using nSpectrum.Core.Audio;
using nSpectrum.Core.Meters;
using nSpectrum.Visuals;

namespace nSpectrum.App.Meters;

/// <summary>Floating loudness meter: EBU R128 / BS.1770 momentary, short-term, integrated, LRA and true peak.</summary>
internal sealed class LoudnessMeterWindow : MeterWindow
{
    private readonly LoudnessMeterPainter _painter = new();
    private AnalysisPipeline? _pipeline;
    private LoudnessTap? _tap;
    private LoudnessMeterStyle _style;

    public LoudnessMeterWindow() : base("Loudness", "nSpectrum Loudness", 640, 360)
    {
        _style = new LoudnessMeterStyle
        {
            Theme = LedTheme.ByName(Settings.Get("Theme", LedTheme.ClassicGreen.Name)),
            Target = LoudnessTarget.ByName(Settings.Get("Target", LoudnessTarget.EbuR128.Name)),
            HistorySeconds = Math.Clamp(Settings.Get("History", 60), 0, 600),
        };
    }

    protected override void OnAttach(AnalysisPipeline pipeline)
    {
        _pipeline = pipeline;
        _tap = new LoudnessTap(pipeline.Format.SampleRate, pipeline.Format.Channels);
        pipeline.AddTap(_tap);
        _painter.ResetHistory();
    }

    protected override void OnDetach()
    {
        if (_tap is not null) _pipeline?.RemoveTap(_tap);
        _tap = null;
        _pipeline = null;
    }

    protected override void Paint(PixelBuffer px, double dt)
    {
        LoudnessFrame? frame = null;
        if (_tap is { } tap)
        {
            tap.Output.TryAcquire(out var f);
            if (f.Sequence > 0) frame = f;
        }
        _painter.Paint(px, frame, _style, dt);
    }

    private void Reset()
    {
        _tap?.RequestReset();
        _painter.ResetHistory();
    }

    protected override void BuildMenu(ItemCollection items)
    {
        items.Add(Item("Reset (restart integration)", Reset));
        items.Add(new Separator());
        Group(items, LoudnessTarget.All.Select(t => (t.Name, t)), _style.Target, v => _style = _style with { Target = v });
        items.Add(new Separator());
        items.Add(Choice("History", [("Off", 0), ("30 s", 30), ("1 min", 60), ("3 min", 180), ("10 min", 600)],
            _style.HistorySeconds, v => _style = _style with { HistorySeconds = v }));
        items.Add(Choice("Colours", LedTheme.All.Select(t => (t.Name, t)), _style.Theme, v => _style = _style with { Theme = v }));
    }

    protected override IEnumerable<(string Label, double W, double H)> Sizes =>
        [("Compact (420 × 150)", 420, 150), ("Medium (640 × 360)", 640, 360), ("Large (960 × 540)", 960, 540)];

    protected override void SaveOptions()
    {
        Settings.Set("Theme", _style.Theme.Name);
        Settings.Set("Target", _style.Target.Name);
        Settings.Set("History", _style.HistorySeconds);
    }
}
