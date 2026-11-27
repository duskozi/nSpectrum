using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using nSpectrum.App.Meters;
using nSpectrum.App.ViewModels;
using nSpectrum.Core.Dsp;
using nSpectrum.Wpf;

namespace nSpectrum.App;

/// <summary>
/// The main window. Options, readouts and the audio side live in <see cref="MainViewModel"/>;
/// this class mirrors the view model into the Direct3D spectrum view, draws the axis labels,
/// opens the meter windows and places the window (see MainWindow.Rack.cs).
/// </summary>
public partial class MainWindow : System.Windows.Window, IDialogs
{
    private readonly Dictionary<string, string> _settings = WindowSettings.For("Main");
    private readonly Dictionary<string, MeterWindow> _meters = [];

    public MainWindow()
    {
        InitializeComponent();
        Vm = new MainViewModel(new SpectrumScreen(View), this, _settings);
        DataContext = Vm;
        Vm.PropertyChanged += OnViewModelChanged;

        _docked = _settings.Get("Docked", true);
        Topmost = _settings.Get("Topmost", false);

        LedButton.Click += (_, _) => ToggleMeter("Led", LedButton, () => new LedSpectrumWindow());
        VuButton.Click += (_, _) => ToggleMeter("Vu", VuButton, () => new VuMeterWindow());
        PhaseButton.Click += (_, _) => ToggleMeter("Phase", PhaseButton, () => new PhaseMeterWindow());
        LoudnessButton.Click += (_, _) => ToggleMeter("Loudness", LoudnessButton, () => new LoudnessMeterWindow());
        PreviewKeyDown += OnKey;
        WireRack();
        SyncInputKnob();

        View.SizeChanged += (_, _) => DrawAxes();
        View.RenderFailed += (_, ex) => Dispatcher.BeginInvoke(() => ShowError("Direct3D 11 could not start: " + ex.Message));

        SourceInitialized += (_, _) => PlaceWindow(); // before the window first shows: no jump from the top-left corner
        Loaded += (_, _) =>
        {
            PlaceWindow();
            Vm.Start();
            View.Options = Vm.ViewOptions; // in case the options came out the same as the defaults: no change event
            DrawAxes();
            RestoreMeters();
        };
        Closed += (_, _) => Shutdown();
    }

    public MainViewModel Vm { get; }

    /// <summary>Mirrors the view model into what cannot be bound: the Direct3D view, the meter windows, the window shape.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Analyzer):
                View.Analyzer = Vm.Analyzer;
                break;
            case nameof(MainViewModel.ViewOptions):
                View.Options = Vm.ViewOptions;
                DrawAxes();
                break;
            case nameof(MainViewModel.Frozen):
                View.Frozen = Vm.Frozen;
                break;
            case nameof(MainViewModel.Error):
                if (Vm.Error is { } message) ShowError(message);
                else HideError();
                break;
            case nameof(MainViewModel.Pipeline):
                foreach (var m in _meters.Values) m.Attach(Vm.Pipeline);
                break;
            case nameof(MainViewModel.SelectedSource):
                SyncInputKnob();
                break;
            case nameof(MainViewModel.OptionsOpen):
                UpdateWindowRegion();
                break;
            case nameof(MainViewModel.UnitLabel):
                UnitText.FontSize = Vm.UnitLabel.Length > 6 ? 11 : Vm.UnitLabel.Length > 4 ? 13 : 15;
                break;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        View.Visibility = Visibility.Hidden; // the Direct3D window would cover the message
    }

    private void HideError()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        View.Visibility = Visibility.Visible;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // typing in the generator boxes
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            Vm.ToggleFreezeCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Vm.ExportCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Axis labels

    private static readonly Brush DbInk = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xD9, 0xA4)));
    private static readonly Brush FreqInk = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xE4)));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>The dB scale left of the screen and the frequency labels under it, in the rack's lettering.</summary>
    private void DrawAxes()
    {
        var o = Vm.ViewOptions;
        double w = View.ActualWidth, h = View.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double maxHz = o.MaxHz, minHz = Math.Min(o.MinHz, maxHz / 2);

        // The mini rack uses smaller lettering, and labels the dB scale less often if they would crowd.
        double labelSize = _mini ? 12 : 17, dbRight = _mini ? 38 : 54;

        TextBlock Label(Canvas canvas, string text, double x, double y, bool centre, Brush ink, double size = 0)
        {
            if (size == 0) size = labelSize;
            var t = new TextBlock { Text = text, Foreground = ink, FontSize = size, FontWeight = FontWeights.SemiBold };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, centre ? x - t.DesiredSize.Width / 2 : x - t.DesiredSize.Width);
            Canvas.SetTop(t, centre ? y : y - t.DesiredSize.Height / 2);
            canvas.Children.Add(t);
            return t;
        }

        FreqAxis.Children.Clear();
        double lastRight = double.NegativeInfinity;
        if (o.Skin is SpectrumSkin.LedRack or SpectrumSkin.DotMatrix)
        {
            // Label the octave bands (31.5, 63, 125 … 16k) and the first band, keeping labels apart.
            var bands = OctaveBands.Create(o.BandFraction, minHz, maxHz);
            for (int i = 0; i < bands.Count; i++)
            {
                double octave = Math.Log2(bands[i].Center / 1000);
                bool preferred = i == 0 || Math.Abs(octave - Math.Round(octave)) < 0.05;
                double x = (i + 0.5) / bands.Count * w;
                if (!preferred || x - 20 < lastRight + 8) continue;
                var t = Label(FreqAxis, bands[i].Label, x, _mini ? 2 : 4, true, FreqInk);
                lastRight = x + t.DesiredSize.Width / 2;
            }
        }
        else
        {
            foreach (double f in FrequencyAxis.GridFrequencies(minHz, maxHz))
            {
                double x = Math.Log(f / minHz) / Math.Log(maxHz / minHz) * w;
                if (x - 20 < lastRight + 8) continue;
                var t = Label(FreqAxis, FrequencyAxis.Label(f), x, _mini ? 2 : 4, true, FreqInk);
                lastRight = x + t.DesiredSize.Width / 2;
            }
        }
        var hz = new TextBlock { Text = "Hz", Foreground = FreqInk, FontSize = _mini ? 10 : 13, FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(hz, Math.Max(w + 3, lastRight + 3));
        Canvas.SetTop(hz, _mini ? 4 : 8);
        FreqAxis.Children.Add(hz);

        DbAxis.Children.Clear();
        double levelHeight = o.Skin == SpectrumSkin.Spectrogram ? h * o.SpectrogramSplit : h;
        double step = o.DbGridStep;
        while (step / (o.MaxDb - o.MinDb) * levelHeight < labelSize * 1.3) step *= 2;
        for (double db = Math.Ceiling(o.MinDb / step) * step; db <= o.MaxDb + 1e-9; db += step)
        {
            string text = Math.Abs(db) < 1e-9 ? "0" : (db < 0 ? "−" : "+") + Math.Abs(db).ToString("0", CultureInfo.InvariantCulture);
            Label(DbAxis, text, dbRight, (o.MaxDb - db) / (o.MaxDb - o.MinDb) * levelHeight, false, DbInk);
        }
    }

    // Meter windows

    private void ToggleMeter(string key, ToggleButton button, Func<MeterWindow> create)
    {
        if (_meters.TryGetValue(key, out var open))
        {
            open.Close();
            return;
        }
        var w = create();
        _meters[key] = w;
        w.Closed += (_, _) =>
        {
            _meters.Remove(key);
            button.IsChecked = false;
        };
        w.Attach(Vm.Pipeline);
        w.Show();
        FollowRack(w);
        button.IsChecked = true;
    }

    /// <summary>Reopens the meter windows that were open when the app last closed.</summary>
    private void RestoreMeters()
    {
        var open = _settings.Get("OpenMeters", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (open.Contains("Led")) ToggleMeter("Led", LedButton, () => new LedSpectrumWindow());
        if (open.Contains("Vu")) ToggleMeter("Vu", VuButton, () => new VuMeterWindow());
        if (open.Contains("Phase")) ToggleMeter("Phase", PhaseButton, () => new PhaseMeterWindow());
        if (open.Contains("Loudness")) ToggleMeter("Loudness", LoudnessButton, () => new LoudnessMeterWindow());
        Activate();
    }

    // Dialogs for the view model

    void IDialogs.ShowInfo(string message) =>
        MessageBox.Show(this, message, "nSpectrum", MessageBoxButton.OK, MessageBoxImage.Information);

    void IDialogs.ShowWarning(string message) =>
        MessageBox.Show(this, message, "nSpectrum", MessageBoxButton.OK, MessageBoxImage.Warning);

    string? IDialogs.AskSaveCsvPath(string initialFolder, string fileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export spectrum",
            Filter = "CSV (comma-separated)|*.csv",
            FileName = fileName,
            InitialDirectory = initialFolder,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    // Shutdown

    private void SaveSettings()
    {
        Vm.SaveSettings();
        _settings.Set("Docked", _docked);
        _settings.Set("Topmost", Topmost);
        if (!_docked && WindowState == WindowState.Normal)
        {
            _settings.Set("Left", Left);
            _settings.Set("Top", Top);
        }
    }

    private void Shutdown()
    {
        CompositionTarget.Rendering -= OnRenderFrame;
        _settings.Set("OpenMeters", string.Join(",", _meters.Keys));
        SaveSettings();
        foreach (var m in _meters.Values.ToList()) m.Close(); // each saves its own settings
        WindowSettings.Save();
        Vm.Dispose();
    }
}
