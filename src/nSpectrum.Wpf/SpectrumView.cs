using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using nSpectrum.Core.Dsp;
using nSpectrum.Wpf.Rendering;

namespace nSpectrum.Wpf;

/// <summary>Look and scales of a <see cref="SpectrumView"/>. Immutable; assign a new instance to change.</summary>
public sealed record SpectrumViewOptions
{
    public SpectrumSkin Skin { get; init; } = SpectrumSkin.Measurement;

    /// <summary>Colours and shape; null uses the skin's built-in theme.</summary>
    public SkinTheme? Theme { get; init; }

    public double MinHz { get; init; } = 20;
    public double MaxHz { get; init; } = 20000;
    public FrequencyAxisKind Axis { get; init; } = FrequencyAxisKind.Logarithmic;

    /// <summary>Bottom of the level range. Anything below it is not drawn, so silence stays dark.</summary>
    public double MinDb { get; init; } = -140;
    public double MaxDb { get; init; }
    public double DbGridStep { get; init; } = 10;

    /// <summary>Bands per octave for the LED skins (1, 2, 3, 6, 12, 24).</summary>
    public int BandFraction { get; init; } = 3;

    /// <summary>LED rows for the LED skins.</summary>
    public int LedRows { get; init; } = 30;

    public ColumnReduction Reduction { get; init; } = ColumnReduction.Max;

    /// <summary>Draw the analyzer's peak-hold trace (Measurement, Aurora, Spectrogram).</summary>
    public bool ShowPeaks { get; init; } = true;

    /// <summary>LED-style bar release in dB/s. 0 shows the measured values directly.</summary>
    public double ReleaseDbPerSecond { get; init; }

    /// <summary>Hold and fall of the display peak caps (LED skins, or curves when the analyzer has no peak hold).</summary>
    public double PeakHoldSeconds { get; init; } = 1.2;
    public double PeakFallDbPerSecond { get; init; } = 12;

    /// <summary>Time constant of the Aurora RMS curve in seconds (power average on the display side).</summary>
    public double RmsCurveSeconds { get; init; } = 0.6;

    /// <summary>Glow on/off (bloom). Turn off on very weak GPUs.</summary>
    public bool Glow { get; init; } = true;

    /// <summary>Fraction of the height used by the spectrum above the waterfall in <see cref="SpectrumSkin.Spectrogram"/>.</summary>
    public double SpectrogramSplit { get; init; } = 0.38;

    /// <summary>Sensible defaults for each skin: ranges where silence is dark and LEDs read 2 dB per step.</summary>
    public static SpectrumViewOptions ForSkin(SpectrumSkin skin) => skin switch
    {
        SpectrumSkin.Aurora => new() { Skin = skin, MinDb = -96, DbGridStep = 12, ShowPeaks = true },
        SpectrumSkin.LedRack => new() { Skin = skin, MinDb = -60, DbGridStep = 6, LedRows = 30, BandFraction = 3, ReleaseDbPerSecond = 36 },
        SpectrumSkin.DotMatrix => new() { Skin = skin, MinDb = -60, DbGridStep = 6, LedRows = 20, BandFraction = 3, ReleaseDbPerSecond = 30 },
        SpectrumSkin.Spectrogram => new() { Skin = skin, MinDb = -110, DbGridStep = 20 },
        _ => new() { Skin = SpectrumSkin.Measurement, MinDb = -140, DbGridStep = 10, Glow = false },
    };

    internal bool UsesBands => Skin is SpectrumSkin.LedRack or SpectrumSkin.DotMatrix;
}

/// <summary>Render loop measurements, for the performance gate.</summary>
public readonly record struct RenderStats(double Fps, double CpuMsPerFrame, double MaxCpuMsPerFrame, string Adapter, int Width, int Height);

/// <summary>
/// A Direct3D 11 spectrum panel hosted in WPF. Rendering runs on its own thread, paced by the
/// swap chain's vsync, and reads the newest frame of <see cref="Analyzer"/> without ever blocking
/// the DSP. WPF content cannot be drawn on top of it (airspace); put labels around it instead.
/// </summary>
public sealed class SpectrumView : HwndHost
{
    private nint _hwnd;
    private Thread? _thread;
    private volatile bool _running;
    private volatile SpectrumAnalyzer? _analyzer;
    private volatile SpectrumViewOptions _options = new();
    private readonly object _statsLock = new();
    private RenderStats _stats;
    private ToneReading? _tone;
    private string? _toneUnit;
    private DistortionReading? _distortion;
    private volatile bool _frozen;
    private TaskCompletionSource<SpectrumSnapshot?>? _snapshotRequest;

    public SpectrumAnalyzer? Analyzer
    {
        get => _analyzer;
        set => _analyzer = value;
    }

    public SpectrumViewOptions Options
    {
        get => _options;
        set => _options = value ?? throw new ArgumentNullException(nameof(value));
    }

    public RenderStats Stats { get { lock (_statsLock) return _stats; } }

    /// <summary>
    /// Holds the current spectrum on screen. The analyzer keeps running (and averaging); the view
    /// just stops taking new frames, which costs nothing because the reader owns its buffer.
    /// </summary>
    public bool Frozen
    {
        get => _frozen;
        set => _frozen = value;
    }

    /// <summary>THD / THD+N of the strongest tone (updated with <see cref="PeakTone"/>), or null.</summary>
    public DistortionReading? Distortion
    {
        get { lock (_statsLock) return _distortion; }
    }

    /// <summary>
    /// Copies the spectrum currently on screen. Completes on the next rendered frame with null when
    /// there is no spectrum yet. Callers should not wait forever: nothing renders while minimised.
    /// </summary>
    public Task<SpectrumSnapshot?> CaptureAsync()
    {
        var tcs = new TaskCompletionSource<SpectrumSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _snapshotRequest, tcs)?.TrySetResult(null);
        return tcs.Task;
    }

    /// <summary>The strongest tone in the current spectrum, corrected for scalloping (updated ~10 times per second).</summary>
    public (ToneReading Reading, string Unit)? PeakTone
    {
        get { lock (_statsLock) return _tone is { } t ? (t, _toneUnit!) : null; }
    }

    /// <summary>Raised on the render thread when Direct3D fails (no device, shader error, device lost repeatedly).</summary>
    public event EventHandler<Exception>? RenderFailed;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = NativeMethods.CreateWindowEx(0, "static", "", NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        _running = true;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "nSpectrum render" };
        _thread.Start();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _running = false;
        Interlocked.Exchange(ref _snapshotRequest, null)?.TrySetResult(null);
        _thread?.Join(2000);
        _thread = null;
        NativeMethods.DestroyWindow(hwnd.Handle);
        _hwnd = 0;
    }

    private void RenderLoop()
    {
        int deviceLosses = 0;
        while (_running)
        {
            D3DSpectrumRenderer? renderer = null;
            try
            {
                NativeMethods.GetClientRect(_hwnd, out var rc);
                renderer = new D3DSpectrumRenderer(_hwnd, rc.Right - rc.Left, rc.Bottom - rc.Top);
                long started = Stopwatch.GetTimestamp();
                if (new FrameBuilder(this, renderer).Run()) return; // stopped normally
                // A driver update or sleep/resume loses the device now and then: only give up when
                // it keeps failing within a minute, not after the fourth loss in a week.
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 60) deviceLosses = 0;
                if (++deviceLosses > 3) throw new InvalidOperationException("The Direct3D device was lost repeatedly.");
            }
            catch (Exception ex)
            {
                RenderFailed?.Invoke(this, ex);
                return;
            }
            finally
            {
                renderer?.Dispose();
            }
        }
    }

    /// <summary>Turns analyzer frames into per-column levels and drives the renderer. Render thread only.</summary>
    private sealed class FrameBuilder(SpectrumView view, D3DSpectrumRenderer renderer)
    {
        private FrequencyAxis? _axis;
        private OctaveBands? _bands;
        private SpectrumInfo? _layoutInfo;
        private SpectrumViewOptions? _layoutOptions;
        private int _layoutWidth;
        private int _columns;
        private ToneEstimator? _estimator;
        private HarmonicAnalyzer? _harmonics;
        private float[] _lin = [], _db = [], _peakLin = [], _peakDb = [], _rmsLin = [], _rmsDb = [], _waterRow = [];
        private Vector4[] _levels = [];
        private readonly DisplayBallistics _ballistics = new();
        private FreqGridArray _grid;
        private bool _rmsPrimed;

        /// <summary>Returns true when stopped, false when the device was lost.</summary>
        public bool Run()
        {
            long last = Stopwatch.GetTimestamp();
            long start = last, statsStart = last, toneAt = last;
            int statsFrames = 0;
            double statsCpu = 0, statsMax = 0;

            while (view._running)
            {
                renderer.WaitForFrame(100);
                if (!view._running) break;

                nint root = NativeMethods.GetAncestor(view._hwnd, 2 /* GA_ROOT */);
                if (NativeMethods.IsIconic(root) || !NativeMethods.IsWindowVisible(view._hwnd))
                {
                    Thread.Sleep(50);
                    last = Stopwatch.GetTimestamp();
                    continue;
                }

                long t0 = Stopwatch.GetTimestamp();
                double dt = Stopwatch.GetElapsedTime(last, t0).TotalSeconds;
                last = t0;

                NativeMethods.GetClientRect(view._hwnd, out var rc);
                renderer.Resize(rc.Right - rc.Left, rc.Bottom - rc.Top);
                int w = renderer.Width, h = renderer.Height;

                SpectrumViewOptions o = view._options;
                SkinTheme theme = o.Theme ?? SkinTheme.For(o.Skin);
                SpectrumFrame? frame = null;
                bool fresh = false;
                if (view._analyzer is { } analyzer)
                {
                    if (view._frozen) frame = analyzer.Output.ReadBuffer; // keep showing what we have
                    else fresh = analyzer.Output.TryAcquire(out frame);
                    if (frame.Sequence == 0) frame = null;
                }
                if (Interlocked.Exchange(ref view._snapshotRequest, null) is { } request)
                    request.TrySetResult(frame is null ? null : SpectrumSnapshot.From(frame));

                int columns = 0;
                ReadOnlySpan<float> waterRow = default;
                if (frame is not null)
                {
                    columns = Layout(frame.Info, o, w);
                    Fill(frame, o, dt);
                    if (o.Skin == SpectrumSkin.Spectrogram && fresh) waterRow = _waterRow.AsSpan(0, columns);

                    if (Stopwatch.GetElapsedTime(toneAt, t0).TotalMilliseconds >= 100 && _estimator is not null)
                    {
                        toneAt = t0;
                        var tone = _estimator.FindPeak(frame.Values, frame.Info, 1, frame.Info.BinCount - 2);
                        DistortionReading? dist = null;
                        try { dist = _harmonics?.Measure(frame.Values); } catch (ArgumentException) { /* reconfiguring */ }
                        lock (view._statsLock) { view._tone = tone; view._toneUnit = frame.Info.UnitLabel; view._distortion = dist; }
                    }
                }

                double range = o.MaxDb - o.MinDb;
                double firstLine = Math.Ceiling(o.MinDb / o.DbGridStep) * o.DbGridStep;
                bool bands = o.UsesBands;
                var k = new FrameConstants
                {
                    ViewSize = new Vector2(w, h),
                    Time = (float)Stopwatch.GetElapsedTime(start, t0).TotalSeconds,
                    Columns = (uint)Math.Max(columns, 1),
                    Rows = bands ? o.LedRows : 1,
                    CellGap = theme.CellGap,
                    BarGap = theme.BarGap,
                    PeakThickness = theme.CurveThickness,
                    DbGridStep = (float)(o.DbGridStep / range),
                    GridOffset = (float)((firstLine - o.MinDb) / range),
                    SplitY = (float)o.SpectrogramSplit,
                    ZoneYellow = theme.ZoneYellow,
                    ZoneRed = theme.ZoneRed,
                    Glow = o.Glow ? theme.Glow : 0,
                    FillAlpha = theme.FillAlpha,
                    ColorLow = theme.ColorLow,
                    ColorMid = theme.ColorMid,
                    ColorHigh = theme.ColorHigh,
                    // No peak hold: hide the peak curve entirely (it would sit on the floor line).
                    PeakColor = frame is null || frame.Info.Settings.PeakHold ? theme.PeakColor : default,
                    CurveColor = o.Skin == SpectrumSkin.Measurement ? default : theme.CurveColor,
                    Background0 = theme.Background0,
                    Background1 = theme.Background1,
                    GridColor = theme.GridColor,
                    UnlitColor = theme.UnlitColor,
                    FreqGrid = _grid,
                };
                if (columns == 0)
                {
                    // Nothing to show yet: one empty column.
                    if (_levels.Length == 0) _levels = new Vector4[1];
                    _levels[0] = default;
                }
                int shader = o.Skin switch
                {
                    SpectrumSkin.LedRack => 1,
                    SpectrumSkin.DotMatrix => 2,
                    SpectrumSkin.Spectrogram => 3,
                    _ => 0,
                };
                float bloom = o.Glow ? theme.BloomIntensity : 0;
                if (!renderer.Draw(shader, _levels.AsSpan(0, Math.Max(columns, 1)), ref k, bloom, theme.BloomThreshold,
                        o.Glow ? theme.Vignette : 0, waterRow))
                    return false;

                double cpu = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                statsFrames++;
                statsCpu += cpu;
                statsMax = Math.Max(statsMax, cpu);
                double span = Stopwatch.GetElapsedTime(statsStart).TotalSeconds;
                if (span >= 1)
                {
                    var s = new RenderStats(statsFrames / span, statsCpu / statsFrames, statsMax, renderer.AdapterName, w, h);
                    lock (view._statsLock) view._stats = s;
                    statsStart = Stopwatch.GetTimestamp();
                    statsFrames = 0; statsCpu = 0; statsMax = 0;
                }
            }
            return true;
        }

        /// <summary>Rebuilds the bin-to-column mapping when the analyzer, size or options change. Returns the column count.</summary>
        private int Layout(SpectrumInfo info, SpectrumViewOptions o, int width)
        {
            double maxHz = Math.Min(o.MaxHz, info.SampleRate / 2.0);
            double minHz = Math.Min(o.MinHz, maxHz / 2);
            int columns = o.UsesBands ? -1 : Math.Max(1, width);
            // Band layouts do not depend on the width, but their grid lines do.
            bool same = _layoutInfo == info && ReferenceEquals(_layoutOptions, o) && (o.UsesBands ? _layoutWidth == width : _columns == columns);
            if (same) return _columns;

            if (o.UsesBands)
            {
                _bands = new OctaveBands(o.BandFraction, minHz, maxHz, info.BinWidth, info.BinCount);
                _axis = null;
                columns = _bands.Bands.Count;
            }
            else
            {
                _axis = new FrequencyAxis(columns, minHz, maxHz, o.Axis, info.BinWidth, info.BinCount);
                _bands = null;
            }
            if (_layoutInfo != info)
            {
                _estimator = new ToneEstimator(info);
                _harmonics = new HarmonicAnalyzer(info, _estimator);
            }
            _layoutInfo = info;
            _layoutOptions = o;
            _layoutWidth = width;
            _columns = columns;

            _lin = new float[columns]; _db = new float[columns];
            _peakLin = new float[columns]; _peakDb = new float[columns];
            _rmsLin = new float[columns]; _rmsDb = new float[columns];
            _waterRow = new float[columns];
            _levels = new Vector4[columns];
            _rmsPrimed = false;
            _ballistics.Reset(columns, (float)o.MinDb);

            for (int i = 0; i < 32; i++) _grid[i] = -1;
            if (_axis is not null)
            {
                int g = 0;
                foreach (double f in FrequencyAxis.GridFrequencies(minHz, maxHz))
                    if (g < 32) _grid[g++] = (float)(_axis.PositionOf(f) * width);
            }
            else
            {
                // Band looks: a line between every second band (only drawn by themes with a grid colour).
                int step = Math.Max(2, (int)Math.Ceiling(columns / 30.0));
                int g = 0;
                for (int b = step; b < columns && g < 32; b += step) _grid[g++] = (float)((double)b / columns * width);
            }
            return columns;
        }

        private void Fill(SpectrumFrame frame, SpectrumViewOptions o, double dt)
        {
            int n = _columns;
            if (_bands is not null) _bands.Compute(frame.Values, frame.Info, _lin);
            else _axis!.Map(frame.Values, _lin, o.Reduction);
            Decibels.FromPower(_lin.AsSpan(0, n), _db);

            // Display ballistics drive LED bars and caps; with release 0 the bars follow the measurement.
            _ballistics.ReleaseDbPerSecond = o.ReleaseDbPerSecond;
            _ballistics.PeakHoldSeconds = o.PeakHoldSeconds;
            _ballistics.PeakFallDbPerSecond = o.PeakFallDbPerSecond;
            _ballistics.Update(_db, dt);
            ReadOnlySpan<float> bars = o.ReleaseDbPerSecond > 0 ? _ballistics.Bars : _db;

            // Peak curve: the analyzer's peak hold when it has one, else the display caps.
            bool analyzerPeaks = o.ShowPeaks && frame.Info.Settings.PeakHold && _axis is not null;
            if (analyzerPeaks)
            {
                _axis!.Map(frame.Peaks, _peakLin, o.Reduction);
                Decibels.FromPower(_peakLin.AsSpan(0, n), _peakDb);
            }
            ReadOnlySpan<float> peaks = analyzerPeaks ? _peakDb : _ballistics.PeakCaps;

            // RMS curve: power average with its own time constant (Aurora, Spectrogram).
            float a = (float)(1 - Math.Exp(-dt / Math.Max(o.RmsCurveSeconds, 0.01)));
            if (!_rmsPrimed) { _lin.AsSpan(0, n).CopyTo(_rmsLin); _rmsPrimed = true; }
            for (int c = 0; c < n; c++) _rmsLin[c] += a * (_lin[c] - _rmsLin[c]);
            Decibels.FromPower(_rmsLin.AsSpan(0, n), _rmsDb);

            float min = (float)o.MinDb, inv = 1f / (float)(o.MaxDb - o.MinDb);
            // The analyzer's Peak hold switch controls every peak display: curves and LED caps.
            bool showPeaks = o.ShowPeaks && frame.Info.Settings.PeakHold;
            for (int c = 0; c < n; c++)
            {
                float bar = Math.Clamp((bars[c] - min) * inv, 0, 1);
                float peak = showPeaks ? Math.Clamp((peaks[c] - min) * inv, 0, 1) : 0;
                float rms = Math.Clamp((_rmsDb[c] - min) * inv, 0, 1);
                _levels[c] = new Vector4(bar, o.UsesBands ? peak : 0, rms, peak);
                _waterRow[c] = Math.Clamp((_db[c] - min) * inv, 0, 1);
            }
        }
    }
}
