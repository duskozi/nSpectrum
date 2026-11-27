using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using nSpectrum.App.Meters;
using nSpectrum.Capture;
using nSpectrum.Core.Audio;
using nSpectrum.Core.Dsp;
using nSpectrum.Core.Meters;
using nSpectrum.Core.Signals;
using nSpectrum.Wpf;

namespace nSpectrum.App.ViewModels;

/// <summary>
/// Everything the main window shows and does, without the window: the options and their saved
/// values, the audio source and analysis pipeline, the generator, and the readouts on the screen.
/// The window binds to it, mirrors <see cref="Analyzer"/>, <see cref="ViewOptions"/> and
/// <see cref="Frozen"/> into the Direct3D view, and manages its own placement and meter windows.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISpectrumScreen _screen;
    private readonly IDialogs _dialogs;
    private readonly Dictionary<string, string> _settings;
    private readonly AudioDevices _devices = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // Windows sends a burst of endpoint notifications for one plug or unplug: handle them once.
    private readonly DispatcherTimer _deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Process _process = Process.GetCurrentProcess();
    private IAudioSource? _source;
    private bool _loading = true;
    private TimeSpan _lastCpu;
    private TimeSpan _lastDsp;
    private long _lastTick = Stopwatch.GetTimestamp();
    private long _inputPreviewUntil;

    internal MainViewModel(ISpectrumScreen screen, IDialogs dialogs, Dictionary<string, string> settings)
    {
        _screen = screen;
        _dialogs = dialogs;
        _settings = settings;

        NextLookCommand = new RelayCommand(() => SelectedLook = Next(Looks, SelectedLook));
        NextBandsCommand = new RelayCommand(() => SelectedBands = Next(BandOptions, SelectedBands), () => BandsEnabled);
        ResetCommand = new RelayCommand(() => { foreach (var a in Pipeline?.Analyzers ?? []) a.RequestReset(); });
        ToggleFreezeCommand = new RelayCommand(() => Frozen = !Frozen);
        ExportCommand = new RelayCommand(() => _ = ExportCsvAsync());
        ToggleGeneratorCommand = new RelayCommand(ToggleGenerator);

        _selectedFft = FftSizes[4];       // 16k points: band looks need fine low-frequency bins
        _selectedWindow = Windows[1];
        _selectedAveraging = Averagings[2]; // Fast 125 ms
        _selectedReading = Readings[0];
        _selectedWeighting = Weightings[0];
        _selectedRate = Rates[0];
        _selectedLook = Looks[2];          // LED rack: the look the rack skin is designed around
        _selectedBands = BandOptions[2];
        _selectedGenSignal = GenSignals[0];

        LoadSettings();
        RefreshDevices();
        _loading = false;

        _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); RefreshDevices(); };
        _devices.Changed += (_, _) => _dispatcher.BeginInvoke(() => { _deviceDebounce.Stop(); _deviceDebounce.Start(); });
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    // Option lists

    public IReadOnlyList<Choice<int>> FftSizes { get; } =
        [.. new[] { 1024, 2048, 4096, 8192, 16384, 32768, 65536 }.Select(n => new Choice<int>(n.ToString(CultureInfo.InvariantCulture), n))];

    public IReadOnlyList<Choice<WindowType>> Windows { get; } =
    [
        new("Hann", WindowType.Hann),
        new("Blackman-Harris", WindowType.BlackmanHarris4),
        new("Flat-top (levels)", WindowType.FlatTop),
        new("Kaiser β=9", WindowType.Kaiser),
        new("Blackman-Harris 7 (distortion)", WindowType.BlackmanHarris7),
        new("Rectangular", WindowType.Rectangular),
    ];

    /// <summary>Averaging choices, each with the settings it applies to the analyzer.</summary>
    public IReadOnlyList<Choice<Func<SpectrumSettings, SpectrumSettings>>> Averagings { get; } =
    [
        new("None", s => s with { Averaging = AveragingMode.None }),
        new("Ultra fast 25 ms", s => s with { Averaging = AveragingMode.Exponential, AveragingTime = 0.025 }),
        new("Fast 125 ms", s => s with { Averaging = AveragingMode.Exponential, AveragingTime = TimeWeighting.Fast }),
        new("Slow 1 s", s => s with { Averaging = AveragingMode.Exponential, AveragingTime = TimeWeighting.Slow }),
        new("Impulse", s => s with { Averaging = AveragingMode.Exponential, AveragingTime = TimeWeighting.ImpulseRise, AveragingDecayTime = TimeWeighting.ImpulseDecay }),
        new("Linear 8", s => s with { Averaging = AveragingMode.Linear, LinearFrames = 8 }),
        new("Infinite (Leq)", s => s with { Averaging = AveragingMode.Infinite }),
        new("Max hold", s => s with { Averaging = AveragingMode.MaxHold }),
    ];

    public IReadOnlyList<Choice<SpectrumScale>> Readings { get; } =
    [
        new("Tone level (dBFS)", SpectrumScale.Amplitude),
        new("Noise per bin (dBFS)", SpectrumScale.Power),
        new("Density (dBFS/Hz)", SpectrumScale.PowerDensity),
    ];

    public IReadOnlyList<Choice<FrequencyWeighting>> Weightings { get; } =
        [new("Z", FrequencyWeighting.Z), new("A", FrequencyWeighting.A), new("C", FrequencyWeighting.C)];

    public IReadOnlyList<Choice<int>> Rates { get; } =
        [.. new[] { 60, 120, 144 }.Select(r => new Choice<int>(r.ToString(CultureInfo.InvariantCulture), r))];

    public IReadOnlyList<Choice<SpectrumSkin>> Looks { get; } =
    [
        new("Measurement", SpectrumSkin.Measurement),
        new("Aurora", SpectrumSkin.Aurora),
        new("LED rack", SpectrumSkin.LedRack),
        new("Dot matrix", SpectrumSkin.DotMatrix),
        new("Spectrogram", SpectrumSkin.Spectrogram),
    ];

    public IReadOnlyList<Choice<int>> BandOptions { get; } =
        [.. new[] { 1, 2, 3, 6, 12, 24 }.Select(b => new Choice<int>(b == 1 ? "1/1 octave" : $"1/{b} octave", b))];

    public ObservableCollection<SourceItem> Sources { get; } = [];

    public ObservableCollection<ChannelRoute> Routes { get; } = [];

    // Selected options

    private SourceItem? _selectedSource;
    public SourceItem? SelectedSource
    {
        get => _selectedSource;
        set { if (SetProperty(ref _selectedSource, value) && !_loading) OpenSource(); }
    }

    private ChannelRoute? _selectedRoute;
    public ChannelRoute? SelectedRoute
    {
        get => _selectedRoute;
        set { if (SetProperty(ref _selectedRoute, value) && !_loading) Reconfigure(); }
    }

    private Choice<int> _selectedFft;
    public Choice<int> SelectedFft
    {
        get => _selectedFft;
        set { if (value is not null && SetProperty(ref _selectedFft, value) && !_loading) Reconfigure(); }
    }

    private Choice<WindowType> _selectedWindow;
    public Choice<WindowType> SelectedWindow
    {
        get => _selectedWindow;
        set { if (value is not null && SetProperty(ref _selectedWindow, value) && !_loading) Reconfigure(); }
    }

    private Choice<Func<SpectrumSettings, SpectrumSettings>> _selectedAveraging;
    public Choice<Func<SpectrumSettings, SpectrumSettings>> SelectedAveraging
    {
        get => _selectedAveraging;
        set { if (value is not null && SetProperty(ref _selectedAveraging, value) && !_loading) Reconfigure(); }
    }

    private Choice<SpectrumScale> _selectedReading;
    public Choice<SpectrumScale> SelectedReading
    {
        get => _selectedReading;
        set
        {
            if (value is null || !SetProperty(ref _selectedReading, value)) return;
            OnPropertyChanged(nameof(RmsReading));
            if (!_loading) Reconfigure(); // also applies the view options: the dB window depends on the reading
        }
    }

    private Choice<FrequencyWeighting> _selectedWeighting;
    public Choice<FrequencyWeighting> SelectedWeighting
    {
        get => _selectedWeighting;
        set { if (value is not null && SetProperty(ref _selectedWeighting, value) && !_loading) Reconfigure(); }
    }

    private Choice<int> _selectedRate;
    public Choice<int> SelectedRate
    {
        get => _selectedRate;
        set { if (value is not null && SetProperty(ref _selectedRate, value) && !_loading) Reconfigure(); }
    }

    private Choice<SpectrumSkin> _selectedLook;
    public Choice<SpectrumSkin> SelectedLook
    {
        get => _selectedLook;
        set
        {
            if (value is null || !SetProperty(ref _selectedLook, value)) return;
            OnPropertyChanged(nameof(LookToolTip));
            if (!_loading) OnLookChanged();
        }
    }

    private Choice<int> _selectedBands;
    public Choice<int> SelectedBands
    {
        get => _selectedBands;
        set
        {
            if (value is null || !SetProperty(ref _selectedBands, value)) return;
            OnPropertyChanged(nameof(BandsLabel));
            if (!_loading) ApplyViewOptions();
        }
    }

    private bool _glow = true;
    public bool Glow
    {
        get => _glow;
        set { if (SetProperty(ref _glow, value) && !_loading) ApplyViewOptions(); }
    }

    private bool _peakHold = true;
    public bool PeakHold
    {
        get => _peakHold;
        set { if (SetProperty(ref _peakHold, value) && !_loading) Reconfigure(); }
    }

    private bool _frozen;
    /// <summary>Holds the spectrum on screen (Space).</summary>
    public bool Frozen
    {
        get => _frozen;
        set => SetProperty(ref _frozen, value);
    }

    private bool _optionsOpen = true;
    /// <summary>Whether the options panel above the rack is shown.</summary>
    public bool OptionsOpen
    {
        get => _optionsOpen;
        set => SetProperty(ref _optionsOpen, value);
    }

    /// <summary>The front RMS button: lit for the noise-per-bin reading, dark for tone level.</summary>
    public bool RmsReading
    {
        get => SelectedReading.Value == SpectrumScale.Power;
        set => SelectedReading = Readings[value ? 1 : 0];
    }

    /// <summary>The front bands button: "1/3 OCT" and so on.</summary>
    public string BandsLabel => SelectedBands.Value == 1 ? "1/1 OCT" : $"1/{SelectedBands.Value} OCT";

    public string LookToolTip => $"Spectrum look: {SelectedLook}. Click for the next one.";

    private bool _bandsEnabled = true;
    /// <summary>Band width only applies to the LED and dot-matrix looks.</summary>
    public bool BandsEnabled
    {
        get => _bandsEnabled;
        private set => SetProperty(ref _bandsEnabled, value);
    }

    // Commands

    public ICommand NextLookCommand { get; }
    public ICommand NextBandsCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ToggleFreezeCommand { get; }
    public ICommand ExportCommand { get; }

    // Analysis state the window mirrors into the Direct3D view and the meters

    private AnalysisPipeline? _pipeline;
    /// <summary>The running pipeline, or null with no source open. Meter windows attach to it.</summary>
    public AnalysisPipeline? Pipeline
    {
        get => _pipeline;
        private set => SetProperty(ref _pipeline, value);
    }

    private SpectrumAnalyzer? _analyzer;
    /// <summary>The analyzer the spectrum screen shows.</summary>
    public SpectrumAnalyzer? Analyzer
    {
        get => _analyzer;
        private set => SetProperty(ref _analyzer, value);
    }

    private SpectrumViewOptions _viewOptions = SpectrumViewOptions.ForSkin(SpectrumSkin.LedRack);
    /// <summary>How the spectrum screen draws: look, frequency and dB range, bands, glow.</summary>
    public SpectrumViewOptions ViewOptions
    {
        get => _viewOptions;
        private set => SetProperty(ref _viewOptions, value);
    }

    /// <summary>Levels for the rack's VU meters while a source is open.</summary>
    public MeterTap? VuTap { get; private set; }

    // Screen readouts

    private string _statusText = "";
    /// <summary>The screen's status line: source kind, rate, FFT size, averaging and unit.</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _statusToolTip = "";
    public string StatusToolTip { get => _statusToolTip; private set => SetProperty(ref _statusToolTip, value); }

    private string _toneText = "";
    public string ToneText { get => _toneText; private set => SetProperty(ref _toneText, value); }

    private string? _toneToolTip;
    public string? ToneToolTip { get => _toneToolTip; private set => SetProperty(ref _toneToolTip, value); }

    private string _distortionText = "";
    public string DistortionText { get => _distortionText; private set => SetProperty(ref _distortionText, value); }

    private string _distortionToolTip = "THD and THD+N of the strongest tone, 20 Hz to 20 kHz. Use Blackman-Harris 7 for readings below -90 dB.";
    public string DistortionToolTip { get => _distortionToolTip; private set => SetProperty(ref _distortionToolTip, value); }

    private string _unitLabel = "dBFS";
    /// <summary>Unit of the dB scale left of the screen.</summary>
    public string UnitLabel { get => _unitLabel; private set => SetProperty(ref _unitLabel, value); }

    private string? _error;
    /// <summary>A message shown in place of the spectrum, or null.</summary>
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    // Lifetime

    /// <summary>Applies the view options and opens the saved source. Called once the window is loaded.</summary>
    public void Start()
    {
        ApplyViewOptions();
        OpenSource();
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _deviceDebounce.Stop();
        StopGenerator();
        CloseSource();
        _devices.Dispose();
        _process.Dispose();
    }

    // Sources

    private void RefreshDevices()
    {
        bool wasLoading = _loading;
        _loading = true;
        string? selectedId = SelectedSource?.Device?.Id;
        string? selectedLabel = SelectedSource?.Label;
        if (SelectedSource is null)
        {
            // First run of the session: the source picked last time.
            string id = _settings.Get("SourceId", ""), label = _settings.Get("SourceLabel", "");
            selectedId = id.Length > 0 ? id : null;
            selectedLabel = label.Length > 0 ? label : null;
        }
        string? genId = SelectedGenDevice?.Id;

        Sources.Clear();
        GenDevices.Clear();
        IReadOnlyList<AudioDeviceInfo> list = [];
        try { list = _devices.List(); }
        catch (Exception ex) { Error = "Could not list audio devices: " + ex.Message; }

        foreach (var d in list)
        {
            Sources.Add(new SourceItem($"{d}  ·  {d.SampleRate / 1000.0:0.#} kHz {d.Channels} ch", d, null));
            if (d.Kind == DeviceKind.Loopback) GenDevices.Add(d);
        }
        Sources.Add(new SourceItem("Internal: 1 kHz sine, -18 dBFS (48 kHz)", null,
            () => new GeneratorAudioSource("Internal sine", new SineGenerator(48000, 1000, Level.SinePeak(-18)))));
        Sources.Add(new SourceItem("Internal: pink noise, -20 dBFS (48 kHz)", null,
            () => new GeneratorAudioSource("Internal pink noise", new PinkNoiseGenerator(48000, Level.Rms(-20)))));
        Sources.Add(new SourceItem("Internal: log sweep 20 Hz to 20 kHz (96 kHz)", null,
            () => new GeneratorAudioSource("Internal sweep", new LogSweepGenerator(96000, 20, 20000, 10, Level.SinePeak(-6)))));

        var keep = Sources.FirstOrDefault(i => selectedId != null ? i.Device?.Id == selectedId : i.Label == selectedLabel);
        SelectedSource = keep
            ?? Sources.FirstOrDefault(i => i.Device is { Kind: DeviceKind.Loopback, IsDefault: true })
            ?? Sources[0];
        SelectedGenDevice = GenDevices.FirstOrDefault(d => d.Id == genId)
            ?? GenDevices.FirstOrDefault(d => d.IsDefault)
            ?? GenDevices.FirstOrDefault();
        _loading = wasLoading;

        // The open device disappeared: fall back to the new selection.
        if (!wasLoading && _source is WasapiSource ws && keep?.Device?.Id != ws.Info.Id) OpenSource();
    }

    private void OpenSource()
    {
        Frozen = false;
        CloseSource();
        Error = null;
        if (SelectedSource is not { } item) return;
        try
        {
            _source = item.Internal is not null ? item.Internal() : WasapiSource.Open(_devices, item.Device!);
            FillRoutes(_source.Format.Channels);
            var pipeline = new AnalysisPipeline(_source.Format, Specs())
            {
                SynthesizeSilence = _source.DeliversNothingWhenSilent,
                WorkerStarted = () => Mmcss.JoinProAudio(),
            };
            _source.DataAvailable = pipeline.Write;
            var opened = _source;
            _source.Stopped += (_, ex) => _dispatcher.BeginInvoke(() =>
            {
                // Ignore late events from a source that has since been closed or replaced.
                if (ex is not null && ReferenceEquals(_source, opened)) Error = "Capture stopped: " + ex.Message;
            });
            pipeline.Faulted += (_, ex) =>
            {
                App.Log("DSP worker stopped", ex);
                _dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(Pipeline, pipeline)) Error = $"Analysis stopped after an internal error: {ex.Message}. Pick the source again to restart. Details: {App.LogPath}";
                });
            };
            pipeline.Start();
            _source.Start();
            VuTap = new MeterTap(_source.Format.SampleRate);
            pipeline.AddTap(VuTap);
            Pipeline = pipeline;
            Analyzer = pipeline.Analyzers[0];
            _lastDsp = TimeSpan.Zero;
            ApplyViewOptions();
        }
        catch (Exception ex)
        {
            CloseSource();
            Error = $"Could not open {item.Label}: {ex.Message}";
        }
    }

    private void CloseSource()
    {
        Analyzer = null;
        var pipeline = _pipeline;
        Pipeline = null; // meter windows detach before the pipeline goes away
        if (VuTap is not null) pipeline?.RemoveTap(VuTap);
        VuTap = null;
        if (_source is not null)
        {
            _source.DataAvailable = null;
            try { _source.Dispose(); } catch { }
            _source = null;
        }
        pipeline?.Dispose();
    }

    private void FillRoutes(int channels)
    {
        bool was = _loading;
        _loading = true;
        string prev = SelectedRoute?.ToString() ?? _settings.Get("Route", "");
        Routes.Clear();
        Routes.Add(ChannelRoute.Left);
        if (channels >= 2)
        {
            Routes.Add(ChannelRoute.Right);
            Routes.Add(ChannelRoute.Mid);
            Routes.Add(ChannelRoute.Side);
        }
        for (int c = 2; c < channels; c++) Routes.Add(ChannelRoute.Channel(c));
        SelectedRoute = Routes.Where(r => r.ToString() == prev).Cast<ChannelRoute?>().FirstOrDefault()
            ?? Routes[channels >= 2 ? 2 : 0]; // Mid = (L+R)/2 by default
        _loading = was;
    }

    /// <summary>Shows a source name on the screen for two seconds while the INPUT knob turns.</summary>
    public void PreviewSource(SourceItem item)
    {
        ToneText = "INPUT  ▸  " + item.Label;
        DistortionText = "";
        _inputPreviewUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
    }

    // Analysis and display

    private IEnumerable<AnalyzerSpec> Specs()
    {
        var s = new SpectrumSettings
        {
            FftSize = SelectedFft.Value,
            Window = SelectedWindow.Value,
            Scale = SelectedReading.Value,
            Weighting = SelectedWeighting.Value,
            AnalysisRate = SelectedRate.Value,
            PeakHold = PeakHold,
        };
        return [new AnalyzerSpec(SelectedRoute ?? ChannelRoute.Left, SelectedAveraging.Value(s))];
    }

    private void Reconfigure()
    {
        Frozen = false; // a new analyzer has no frame to hold
        if (Pipeline is null) return;
        try
        {
            Pipeline.Configure(Specs());
            Analyzer = Pipeline.Analyzers[0];
            ApplyViewOptions();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private static bool IsBandLook(SpectrumSkin skin) => skin is SpectrumSkin.LedRack or SpectrumSkin.DotMatrix;

    private void OnLookChanged()
    {
        // Band looks need fine low-frequency resolution: at least 16k points (2.9 Hz bins at 48 kHz).
        if (IsBandLook(SelectedLook.Value) && SelectedFft.Value < 16384)
        {
            SelectedFft = FftSizes.First(c => c.Value == 16384); // reconfigures and applies the view options
            return;
        }
        ApplyViewOptions();
    }

    private void ApplyViewOptions()
    {
        var skin = SelectedLook.Value;
        bool bandLook = IsBandLook(skin);
        var o = SpectrumViewOptions.ForSkin(skin);
        double maxHz = Math.Min(20000, (_source?.Format.SampleRate ?? 48000) / 2.0);
        if (_source is { } src && src.Format.SampleRate >= 88200 && !bandLook)
            maxHz = src.Format.SampleRate / 2.0;
        // Density readings sit about 40 dB lower than per-bin readings: shift the window.
        double shift = SelectedReading.Value == SpectrumScale.PowerDensity ? -40 : 0;
        if (bandLook) o = o with { DbGridStep = 10 }; // labelled every 10 dB like the rack's screen
        if (skin == SpectrumSkin.LedRack) o = o with { Theme = SkinTheme.RetroRack, LedRows = 24 };
        UnitLabel = Analyzer?.Info.UnitLabel ?? (SelectedReading.Value == SpectrumScale.PowerDensity ? "dBFS/Hz" : "dBFS");
        BandsEnabled = bandLook;
        ViewOptions = o with
        {
            MinHz = 20,
            MaxHz = maxHz,
            MinDb = o.MinDb + shift,
            MaxDb = o.MaxDb + shift,
            BandFraction = SelectedBands.Value,
            Glow = Glow && o.Glow,
        };
    }

    private void UpdateStatus()
    {
        var stats = _screen.Stats;
        long now = Stopwatch.GetTimestamp();
        double wall = Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;

        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        double cpuPct = wall > 0 ? (cpu - _lastCpu).TotalSeconds / wall * 100 : 0;
        _lastCpu = cpu;

        double dspPct = 0;
        if (Pipeline is not null)
        {
            var dsp = Pipeline.DspTime;
            dspPct = wall > 0 ? (dsp - _lastDsp).TotalSeconds / wall * 100 : 0;
            _lastDsp = dsp;
        }

        var info = Pipeline?.Analyzers[0].Info;
        string fmt = _source is null ? "no source" : $"{_source.Format}";
        string fft = info is null ? "" : $"  ·  {info.FftSize} pt, {info.BinWidth:0.##} Hz bins";
        StatusToolTip = $"{fmt}{fft}  ·  {stats.Fps:0} fps  ·  render {stats.CpuMsPerFrame:0.00} ms (max {stats.MaxCpuMsPerFrame:0.0})"
            + $"  ·  DSP {dspPct:0.0}%  ·  process {cpuPct:0.0}% of one core"
            + (Pipeline?.Overruns > 0 ? $"  ·  overruns {Pipeline.Overruns}" : "")
            + (string.IsNullOrEmpty(stats.Adapter) ? "" : $"  ·  {stats.Adapter}");
        string kind = SelectedSource?.Device?.Kind switch
        {
            DeviceKind.Loopback => "LOOPBACK",
            DeviceKind.Input => "INPUT",
            _ => "INTERNAL",
        };
        StatusText = _source is null ? "NO SOURCE" : string.Join("  ·  ", new[]
        {
            kind,
            $"{_source.Format.SampleRate / 1000.0:0.#} kHz",
            info is null ? "" : $"FFT {info.FftSize}",
            SelectedAveraging.Label.ToUpper(CultureInfo.CurrentCulture),
            info?.UnitLabel ?? "",
        }.Where(p => p.Length > 0)) + (Pipeline?.Overruns > 0 ? "  ·  OVERRUNS" : "");

        if (Stopwatch.GetTimestamp() < _inputPreviewUntil) return; // the INPUT knob is showing a source name

        string tone = "";
        if (_screen.PeakTone is { } t && t.Reading.LevelDb > -200)
        {
            var r = t.Reading;
            bool toneReading = info?.Scale == SpectrumScale.Amplitude;
            double level = toneReading ? r.LevelDb : r.RawLevelDb;
            tone = $"PEAK  {r.Frequency:0.00} Hz   {level:0.00} {t.Unit}".Replace('-', '−');
            ToneToolTip = toneReading ? $"Peak tone, scalloping corrected (+{r.ScallopCorrectionDb:0.00} dB)" : "Strongest bin";
        }
        ToneText = Frozen ? "FROZEN   " + tone : tone;

        // Only meaningful for a test tone: hide it for music and noise (THD+N above -10 dB).
        if (_screen.Distortion is { ThdN: < 0.3 } d)
        {
            DistortionText = $"THD {Pct(d.ThdPercent)}   THD+N {Pct(d.ThdNPercent)}";
            DistortionToolTip = $"THD {Pct(d.ThdPercent)} ({d.ThdDb:0.0} dB)   THD+N {Pct(d.ThdNPercent)} ({d.ThdNDb:0.0} dB)"
                + (d.HarmonicsDbc.Count >= 2 ? $"   H2 {d.HarmonicsDbc[0]:0} H3 {d.HarmonicsDbc[1]:0} dBc" : "")
                + "\nStrongest tone, 20 Hz to 20 kHz. Use Blackman-Harris 7 for readings below -90 dB.";
        }
        else
        {
            DistortionText = "";
        }

        static string Pct(double p) => p >= 1 ? $"{p:0.00} %" : p >= 0.01 ? $"{p:0.0000} %" : $"{p:0.000000} %";
    }

    /// <summary>Saves the spectrum on screen (frozen or live) as CSV.</summary>
    private async Task ExportCsvAsync()
    {
        var capture = _screen.CaptureAsync();
        if (await Task.WhenAny(capture, Task.Delay(1000)) != capture || await capture is not { } snapshot)
        {
            _dialogs.ShowInfo("There is no spectrum to export yet.");
            return;
        }
        string folder = _settings.Get("ExportFolder", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (_dialogs.AskSaveCsvPath(folder, $"nSpectrum {DateTime.Now:yyyy-MM-dd HHmmss}.csv") is not { } path) return;
        try
        {
            await using (var w = new StreamWriter(path))
                snapshot.WriteCsv(w, ViewOptions.MinHz, ViewOptions.MaxHz);
            _settings.Set("ExportFolder", Path.GetDirectoryName(path) ?? "");
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning($"Could not save {path}: {ex.Message}");
        }
    }

    // Settings

    private void LoadSettings()
    {
        _selectedFft = Restore(FftSizes, "Fft", _selectedFft);
        _selectedWindow = Restore(Windows, "Window", _selectedWindow);
        _selectedAveraging = Restore(Averagings, "Averaging", _selectedAveraging);
        _selectedReading = Restore(Readings, "Reading", _selectedReading);
        _selectedWeighting = Restore(Weightings, "Weighting", _selectedWeighting);
        _selectedRate = Restore(Rates, "Rate", _selectedRate);
        _selectedLook = Restore(Looks, "Look", _selectedLook);
        _selectedBands = Restore(BandOptions, "Bands", _selectedBands);
        _selectedGenSignal = Restore(GenSignals, "GenSignal", _selectedGenSignal);
        _glow = _settings.Get("Glow", true);
        _peakHold = _settings.Get("PeakHold", true);
        _genFrequencyText = _settings.Get("GenFrequency", _genFrequencyText);
        _genLevelText = _settings.Get("GenLevel", _genLevelText);
        _optionsOpen = _settings.Get("OptionsOpen", true);
        SyncGeneratorKnobs();
    }

    public void SaveSettings()
    {
        _settings.Set("Fft", SelectedFft.Label);
        _settings.Set("Window", SelectedWindow.Label);
        _settings.Set("Averaging", SelectedAveraging.Label);
        _settings.Set("Reading", SelectedReading.Label);
        _settings.Set("Weighting", SelectedWeighting.Label);
        _settings.Set("Rate", SelectedRate.Label);
        _settings.Set("Look", SelectedLook.Label);
        _settings.Set("Bands", SelectedBands.Label);
        _settings.Set("GenSignal", SelectedGenSignal.ToString());
        _settings.Set("Glow", Glow);
        _settings.Set("PeakHold", PeakHold);
        _settings.Set("GenFrequency", GenFrequencyText);
        _settings.Set("GenLevel", GenLevelText);
        _settings.Set("Route", SelectedRoute?.ToString() ?? "");
        // The device id survives renames; the label identifies the internal test sources.
        if (SelectedSource is { } src)
        {
            _settings.Set("SourceId", src.Device?.Id ?? "");
            _settings.Set("SourceLabel", src.Label);
        }
        _settings.Set("OptionsOpen", OptionsOpen);
    }

    /// <summary>The item whose label was saved, if it still exists, else the fallback.</summary>
    private T Restore<T>(IEnumerable<T> items, string key, T fallback)
    {
        string saved = _settings.Get(key, "");
        if (saved.Length == 0) return fallback;
        foreach (var item in items)
            if (item?.ToString() == saved) return item;
        // Older versions saved longer labels ("Z (flat)", "60 Hz"): match on the first word.
        foreach (var item in items)
            if (saved.StartsWith(item + " ", StringComparison.Ordinal)) return item;
        return fallback;
    }

    private static T Next<T>(IReadOnlyList<T> items, T current) =>
        items[(items.IndexOf(current) + 1) % items.Count];
}

file static class ListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> items, T item)
    {
        for (int i = 0; i < items.Count; i++)
            if (EqualityComparer<T>.Default.Equals(items[i], item)) return i;
        return -1;
    }
}
