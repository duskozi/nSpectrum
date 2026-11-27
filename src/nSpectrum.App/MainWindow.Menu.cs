using System.Windows;
using System.Windows.Controls;
using nSpectrum.App.Meters;

namespace nSpectrum.App;

/// <summary>
/// The rack's right-click menu: every option of the options panel, the meter windows, the
/// generator and the window itself, so nothing needs the panel open. Rebuilt each time it opens,
/// so the ticks and the source list are current.
/// </summary>
public partial class MainWindow
{
    private ContextMenu BuildRackMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "…" }); // an empty menu would not open
        menu.Opened += (_, _) => FillRackMenu(menu.Items);
        return menu;
    }

    private void FillRackMenu(ItemCollection items)
    {
        items.Clear();

        // Input and analysis
        items.Add(Pick("Source", Vm.Sources, Vm.SelectedSource, v => Vm.SelectedSource = v));
        items.Add(Pick("Channel", Vm.Routes, Vm.SelectedRoute ?? default, v => Vm.SelectedRoute = v));
        items.Add(Pick("FFT size", Vm.FftSizes, Vm.SelectedFft, v => Vm.SelectedFft = v));
        items.Add(Pick("Window", Vm.Windows, Vm.SelectedWindow, v => Vm.SelectedWindow = v));
        items.Add(Pick("Averaging", Vm.Averagings, Vm.SelectedAveraging, v => Vm.SelectedAveraging = v));
        items.Add(Pick("Reading", Vm.Readings, Vm.SelectedReading, v => Vm.SelectedReading = v));
        items.Add(Pick("Weighting", Vm.Weightings, Vm.SelectedWeighting, v => Vm.SelectedWeighting = v));
        items.Add(Pick("Rate", Vm.Rates, Vm.SelectedRate, v => Vm.SelectedRate = v));
        items.Add(new Separator());

        // Display
        items.Add(Pick("Look", Vm.Looks, Vm.SelectedLook, v => Vm.SelectedLook = v));
        var bands = Pick("Bands", Vm.BandOptions, Vm.SelectedBands, v => Vm.SelectedBands = v);
        bands.IsEnabled = Vm.BandsEnabled;
        items.Add(bands);
        items.Add(Check("Glow", Vm.Glow, v => Vm.Glow = v));
        items.Add(Check("Peak hold", Vm.PeakHold, v => Vm.PeakHold = v));
        items.Add(Check("Freeze (Space)", Vm.Frozen, v => Vm.Frozen = v));
        items.Add(Action("Reset averaging and peaks", () => Vm.ResetCommand.Execute(null)));
        items.Add(Action("Export CSV… (Ctrl+S)", () => Vm.ExportCommand.Execute(null)));
        items.Add(new Separator());

        // Meter windows and generator
        var meters = new MenuItem { Header = "Meter windows" };
        meters.Items.Add(Check("LED spectrum", _meters.ContainsKey("Led"), _ => ToggleMeter("Led", LedButton, () => new LedSpectrumWindow())));
        meters.Items.Add(Check("VU / peak", _meters.ContainsKey("Vu"), _ => ToggleMeter("Vu", VuButton, () => new VuMeterWindow())));
        meters.Items.Add(Check("Phase", _meters.ContainsKey("Phase"), _ => ToggleMeter("Phase", PhaseButton, () => new PhaseMeterWindow())));
        meters.Items.Add(Check("Loudness (LUFS)", _meters.ContainsKey("Loudness"), _ => ToggleMeter("Loudness", LoudnessButton, () => new LoudnessMeterWindow())));
        items.Add(meters);
        var generator = new MenuItem { Header = "Generator" };
        generator.Items.Add(Action(Vm.GeneratorPlaying ? "Stop" : $"Play  ({Vm.GenFrequencyText} Hz, {Vm.GenLevelText} dBFS)",
            () => Vm.ToggleGeneratorCommand.Execute(null)));
        generator.Items.Add(Pick("Signal", Vm.GenSignals, Vm.SelectedGenSignal, v => Vm.SelectedGenSignal = v));
        generator.Items.Add(Pick("Output", Vm.GenDevices, Vm.SelectedGenDevice, v => Vm.SelectedGenDevice = v));
        items.Add(generator);
        items.Add(new Separator());

        // Window
        items.Add(Check("Options panel", Vm.OptionsOpen, v => Vm.OptionsOpen = v));
        items.Add(Check("Mini rack", _mini, SetMini));
        items.Add(Check("Always on top", Topmost, v => Topmost = v));
        var dock = Action("Dock above the taskbar", Dock);
        dock.IsEnabled = !_docked;
        items.Add(dock);
        items.Add(Action("Minimize", () => WindowState = WindowState.Minimized));
        items.Add(new Separator());
        items.Add(Action("Power off", Close));
    }

    /// <summary>A submenu of mutually exclusive choices, the current one ticked.</summary>
    private static MenuItem Pick<T>(string header, IEnumerable<T> options, T current, Action<T> pick)
    {
        var m = new MenuItem { Header = header };
        foreach (var option in options)
        {
            var item = new MenuItem { Header = option?.ToString(), IsCheckable = true, IsChecked = EqualityComparer<T>.Default.Equals(option, current) };
            item.Click += (_, _) => pick(option);
            m.Items.Add(item);
        }
        m.IsEnabled = m.Items.Count > 0;
        return m;
    }

    private static MenuItem Check(string header, bool isChecked, Action<bool> set)
    {
        var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        m.Click += (_, _) => set(m.IsChecked);
        return m;
    }

    private static MenuItem Action(string header, Action click)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => click();
        return m;
    }
}
