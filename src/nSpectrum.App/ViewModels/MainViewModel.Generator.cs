using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using nSpectrum.Capture;

namespace nSpectrum.App.ViewModels;

/// <summary>The test-signal generator: output device, signal, frequency and level, play and stop.</summary>
public sealed partial class MainViewModel
{
    private GeneratorOutput? _generator;

    public ObservableCollection<AudioDeviceInfo> GenDevices { get; } = [];

    public IReadOnlyList<GeneratorSignal> GenSignals { get; } = Enum.GetValues<GeneratorSignal>();

    private AudioDeviceInfo? _selectedGenDevice;
    public AudioDeviceInfo? SelectedGenDevice
    {
        get => _selectedGenDevice;
        set => SetProperty(ref _selectedGenDevice, value);
    }

    private GeneratorSignal _selectedGenSignal;
    public GeneratorSignal SelectedGenSignal
    {
        get => _selectedGenSignal;
        set => SetProperty(ref _selectedGenSignal, value);
    }

    private string _genFrequencyText = "1000";
    /// <summary>The frequency readout in Hz, as typed. Moves the FREQ knob.</summary>
    public string GenFrequencyText
    {
        get => _genFrequencyText;
        set { if (SetProperty(ref _genFrequencyText, value)) SyncGeneratorKnobs(); }
    }

    private string _genLevelText = "-18";
    /// <summary>The level readout in dBFS, as typed. Moves the LEVEL knob.</summary>
    public string GenLevelText
    {
        get => _genLevelText;
        set { if (SetProperty(ref _genLevelText, value)) SyncGeneratorKnobs(); }
    }

    private double _frequencyKnob;
    /// <summary>The FREQ knob, 0 to 1: 20 Hz to 20 kHz on a log scale. Turning it rewrites the readout.</summary>
    public double FrequencyKnob
    {
        get => _frequencyKnob;
        set
        {
            if (!SetProperty(ref _frequencyKnob, value)) return;
            // Set the field, not the property, so the knob keeps its exact position between rounded readings.
            _genFrequencyText = FormatFrequency(20 * Math.Pow(1000, value));
            OnPropertyChanged(nameof(GenFrequencyText));
        }
    }

    private double _levelKnob;
    /// <summary>The LEVEL knob, 0 to 1: −60 to 0 dBFS. Turning it rewrites the readout.</summary>
    public double LevelKnob
    {
        get => _levelKnob;
        set
        {
            if (!SetProperty(ref _levelKnob, value)) return;
            _genLevelText = Math.Round(-60 + 60 * value).ToString("0", CultureInfo.CurrentCulture);
            OnPropertyChanged(nameof(GenLevelText));
        }
    }

    private string _genStatus = "";
    public string GenStatus { get => _genStatus; private set => SetProperty(ref _genStatus, value); }

    public bool GeneratorPlaying => _generator is not null;

    public string GeneratorButtonText => GeneratorPlaying ? "STOP" : "PLAY";

    public ICommand ToggleGeneratorCommand { get; }

    private void SyncGeneratorKnobs()
    {
        if (TryParseNumber(_genFrequencyText, out double f) && f > 0)
            SetProperty(ref _frequencyKnob, Math.Log(Math.Clamp(f, 20, 20000) / 20) / Math.Log(1000), nameof(FrequencyKnob));
        if (TryParseNumber(_genLevelText, out double l))
            SetProperty(ref _levelKnob, (Math.Clamp(l, -60, 0) + 60) / 60, nameof(LevelKnob));
    }

    private static string FormatFrequency(double f) =>
        (f < 100 ? Math.Round(f, 1) : f < 1000 ? Math.Round(f) : Math.Round(f / 10) * 10).ToString(CultureInfo.CurrentCulture);

    private void ToggleGenerator()
    {
        if (GeneratorPlaying) StopGenerator();
        else StartGenerator();
    }

    /// <summary>Applies a new frequency or level to a playing generator.</summary>
    public void RestartGenerator()
    {
        if (!GeneratorPlaying) return;
        StopGenerator();
        StartGenerator();
    }

    private void StartGenerator()
    {
        if (SelectedGenDevice is not { } dev)
        {
            GenStatus = "No playback device.";
            OnGeneratorChanged();
            return;
        }
        // Accept "1000.5" and the local "1000,5" alike; say so when a box cannot be read.
        if (!TryParseNumber(GenFrequencyText, out double f) || !(f > 0))
        {
            GenStatus = "Enter a frequency in Hz, for example 1000.";
            OnGeneratorChanged();
            return;
        }
        if (!TryParseNumber(GenLevelText, out double level))
        {
            GenStatus = "Enter a level in dBFS, for example -18.";
            OnGeneratorChanged();
            return;
        }
        try
        {
            _generator = new GeneratorOutput(_devices, dev.Id, SelectedGenSignal, f, Math.Clamp(level, -140, 0));
            _generator.Start();
            GenStatus = $"Playing on {dev.Name}. Pick its loopback as the source to read it back.";
        }
        catch (Exception ex)
        {
            _generator?.Dispose();
            _generator = null;
            GenStatus = "Generator failed: " + ex.Message;
        }
        OnGeneratorChanged();
    }

    private void StopGenerator()
    {
        if (_generator is null) return;
        _generator.Dispose();
        _generator = null;
        GenStatus = "";
        OnGeneratorChanged();
    }

    // Also raised when starting fails: the PLAY button lit itself on the click and must go dark again.
    private void OnGeneratorChanged()
    {
        OnPropertyChanged(nameof(GeneratorPlaying));
        OnPropertyChanged(nameof(GeneratorButtonText));
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
