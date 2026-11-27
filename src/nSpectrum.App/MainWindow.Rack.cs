using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using nSpectrum.App.Meters;
using nSpectrum.App.Rack;
using nSpectrum.Core.Meters;
using nSpectrum.Wpf;

namespace nSpectrum.App;

/// <summary>
/// The rack's view-only behaviour: the INPUT knob, the generator knobs' commit, the VU needles,
/// the options panel shown and hidden in place, meter windows sitting on the rack, and docking
/// above the taskbar. The buttons and readouts themselves are bound to the view model.
/// </summary>
public partial class MainWindow
{
    private bool _docked = true;
    private bool _placing;
    private double _visibleTop; // where the visible part of the window starts: below a hidden options panel
    private bool _syncingInputKnob;
    /// <summary>
    /// 0 VU on the rack meters: the peak level of a sine that reads 0 VU. Studio and broadcast work
    /// uses −18 dBFS (EBU R68); mastered music is much louder and would sit in the red, so the
    /// default is −14. Right-click a meter to change it.
    /// </summary>
    private double _vuReference = -14;
    private bool _mini;

    private void WireRack()
    {
        PowerButton.Click += (_, _) => Close();
        MiniPowerButton.Click += (_, _) => Close();
        MiniButton.Click += (_, _) => SetMini(true);
        FullButton.Click += (_, _) => SetMini(false);
        SetMini(_settings.Get("Mini", false));

        // INPUT knob: one detent per source; the source opens when the knob is let go.
        InputKnob.ValueChanged += (_, _) =>
        {
            if (!_syncingInputKnob && InputKnobIndex() < Vm.Sources.Count) Vm.PreviewSource(Vm.Sources[InputKnobIndex()]);
        };
        InputKnob.Committed += (_, _) =>
        {
            if (InputKnobIndex() < Vm.Sources.Count) Vm.SelectedSource = Vm.Sources[InputKnobIndex()];
        };
        Vm.Sources.CollectionChanged += (_, _) => SyncInputKnob();

        // A generator knob let go, or Enter in a readout: play the new frequency or level.
        FreqKnob.Committed += (_, _) => Vm.RestartGenerator();
        LevelKnob.Committed += (_, _) => Vm.RestartGenerator();
        GenFreqBox.KeyDown += OnReadoutKey;
        GenLevelBox.KeyDown += OnReadoutKey;

        // Window: drag the faceplate to move, double-click it to dock above the taskbar, right-click for options.
        MouseLeftButtonDown += OnPlateMouseDown;
        SizeChanged += (_, _) => { if (_docked) PlaceDocked(); UpdateWindowRegion(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(UpdateWindowRegion);
        Loaded += (_, _) => UpdateWindowRegion();
        LocationChanged += (_, _) =>
        {
            if (_docked && !_placing && WindowState == WindowState.Normal) PlaceDocked();
            PlaceMetersOnRack();
        };
        StateChanged += (_, _) => PlaceMetersOnRack();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea) && _docked) Dispatcher.BeginInvoke(PlaceDocked);
        };
        Cabinet.ContextMenu = BuildRackMenu();

        CompositionTarget.Rendering += OnRenderFrame;
        _vuReference = _settings.Get("VuReference", -14.0);
        VuLeft.ContextMenu = VuRight.ContextMenu = BuildVuMenu();
        UpdateVuToolTips();
    }

    /// <summary>The source the INPUT knob points at (it can be past the end while the list is empty).</summary>
    private int InputKnobIndex() => (int)Math.Round(InputKnob.Value * Math.Max(Vm.Sources.Count - 1, 1));

    private void SyncInputKnob()
    {
        int count = Vm.Sources.Count;
        int index = Vm.SelectedSource is { } s ? Vm.Sources.IndexOf(s) : 0;
        _syncingInputKnob = true;
        InputKnob.Steps = Math.Max(count, 2);
        InputKnob.Value = count > 1 ? (double)Math.Max(index, 0) / (count - 1) : 0;
        InputKnob.ToolTip = $"INPUT: {Vm.SelectedSource}. Turn to choose another source.";
        _syncingInputKnob = false;
    }

    private void OnReadoutKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        BindingOperations.GetBindingExpression((TextBox)sender, TextBox.TextProperty)?.UpdateSource();
        Vm.RestartGenerator();
        e.Handled = true;
    }

    /// <summary>Moves the VU needles once per display frame from the meter tap's latest readings.</summary>
    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) return;
        double l = -100, r = -100;
        if (Vm.VuTap is { } tap)
        {
            tap.Output.TryAcquire(out var f);
            if (f.Sequence > 0)
            {
                l = VuBallistics.ToVu(f.Vu[0], _vuReference);
                r = VuBallistics.ToVu(f.Vu[f.Channels > 1 ? 1 : 0], _vuReference);
            }
        }
        VuLeft.SetVu(l);
        VuRight.SetVu(r);
    }

    // Mini rack

    /// <summary>
    /// Switches between the full rack and the mini rack: a slim strip without the title, power and
    /// INPUT knob, with smaller VU meters and screen lettering, so the spectrum runs nearly the
    /// whole width. The window keeps its bottom edge on the taskbar and the LED meter stays on top.
    /// </summary>
    private void SetMini(bool mini)
    {
        _mini = mini;
        _settings.Set("Mini", mini);
        RackPanel.Height = mini ? 176 : 300;
        RackPanel.Padding = mini ? new Thickness(20, 16, 20, 14) : new Thickness(26, 30, 26, 22);
        LeftColumn.Width = new GridLength(mini ? 0 : 196);
        LeftControls.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MeterColumn.Width = new GridLength(mini ? 400 : 560);
        VuMeters.Height = mini ? 108 : 160;
        FrontButtons.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MiniButtons.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        ScreenBezel.Margin = mini ? new Thickness(0, 0, 14, 0) : new Thickness(6, 0, 18, 0);
        ScreenBezel.Padding = new Thickness(mini ? 4 : 8);
        DbColumn.Width = new GridLength(mini ? 46 : 66);
        StatusRow.Height = new GridLength(mini ? 24 : 42);
        FreqRow.Height = new GridLength(mini ? 24 : 36);
        StatusLine.Margin = mini ? new Thickness(8, 0, 6, 3) : new Thickness(10, 0, 6, 8);
        UnitText.Margin = mini ? new Thickness(0, 0, 8, 3) : new Thickness(0, 0, 12, 6);
        DrawAxes();
    }

    // Options panel

    /// <summary>
    /// Shows or hides the options panel without resizing or moving the window. The window always
    /// keeps the full height (panel + rack); a hidden panel is cut off with a window region, so the
    /// rack and its Direct3D screen never move and nothing is redrawn but the panel area. (Changing
    /// the window height made it grow first and jump into place a frame later: a visible blink.)
    /// </summary>
    private void UpdateWindowRegion()
    {
        if (!IsLoaded) return;
        double clipTop = 0;
        if (!Vm.OptionsOpen)
            clipTop = OptionsPanel.TransformToAncestor(this).TransformBounds(new Rect(OptionsPanel.RenderSize)).Bottom;
        WindowRegion.ClipTop(this, clipTop);
        _visibleTop = clipTop;
        PlaceMetersOnRack();
    }

    // Meter windows on the rack

    /// <summary>Keeps a meter window on top of the rack while it is set to sit there.</summary>
    private void FollowRack(MeterWindow w)
    {
        w.SizeChanged += (_, _) => PlaceOnRack(w);
        w.OnRackChanged += (_, _) => PlaceOnRack(w);
        w.Dragged += (_, _) =>
        {
            // Dropped with its bottom edge near the top of the rack: back on the rack.
            w.OnRack = Math.Abs(w.Top + w.ActualHeight - (Top + _visibleTop)) < 24;
            PlaceOnRack(w);
        };
        PlaceOnRack(w);
    }

    private void PlaceMetersOnRack()
    {
        foreach (var w in _meters.Values) PlaceOnRack(w);
    }

    private void PlaceOnRack(MeterWindow w)
    {
        if (!w.OnRack || !IsLoaded || WindowState != WindowState.Normal || w.WindowState != WindowState.Normal) return;
        double height = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        w.Left = Left;
        w.Width = Width;
        w.Top = Top + _visibleTop - height;
    }

    // Window placement

    /// <summary>First placement: docked above the taskbar, or where the user left it.</summary>
    private void PlaceWindow()
    {
        if (_docked)
        {
            PlaceDocked();
            return;
        }
        var area = MonitorArea.WorkAreaOf(this);
        _placing = true;
        Width = area.Width;
        Left = _settings.Get("Left", area.Left);
        Top = _settings.Get("Top", area.Bottom - ActualHeight);
        // Off screen (monitor removed): dock again.
        if (Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40 || Top + 40 < SystemParameters.VirtualScreenTop)
            _docked = true;
        _placing = false;
        if (_docked) PlaceDocked();
    }

    /// <summary>Full width of the monitor's work area, bottom edge on the taskbar.</summary>
    private void PlaceDocked()
    {
        if (WindowState != WindowState.Normal) return;
        var area = MonitorArea.WorkAreaOf(this);
        _placing = true;
        Left = area.Left;
        Width = area.Width;
        UpdateLayout();
        Top = area.Bottom - ActualHeight;
        _placing = false;
    }

    private void Dock()
    {
        _docked = true;
        PlaceDocked();
    }

    private void OnPlateMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            Dock();
            e.Handled = true;
            return;
        }
        _docked = false;
        try { DragMove(); } catch (InvalidOperationException) { /* mouse already released */ }
        // Dropped near the bottom of the screen: snap back into the dock.
        var area = MonitorArea.WorkAreaOf(this);
        if (Math.Abs(Top + ActualHeight - area.Bottom) < 24) Dock();
        else if (Math.Abs(Left - area.Left) > 1 || Math.Abs(Width - area.Width) > 1)
        {
            _placing = true;
            Left = area.Left; // keep the full width of whichever monitor it was dropped on
            Width = area.Width;
            _placing = false;
        }
    }

    /// <summary>True when the click landed on a control rather than on the bare faceplate.</summary>
    private static bool IsInteractive(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ButtonBase or ComboBox or ListBox or TextBox or RackKnob or SpectrumView or ScrollBar) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>Right-click menu of the VU meters: the level that reads 0 VU.</summary>
    private ContextMenu BuildVuMenu()
    {
        var menu = new ContextMenu();
        foreach (var (label, dbfs) in new[]
        {
            ("0 VU = −8 dBFS (loud masters)", -8.0),
            ("0 VU = −10 dBFS", -10.0),
            ("0 VU = −14 dBFS (music)", -14.0),
            ("0 VU = −18 dBFS (EBU R68, studio)", -18.0),
            ("0 VU = −20 dBFS (SMPTE RP155)", -20.0),
        })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, Tag = dbfs };
            item.Click += (_, _) =>
            {
                _vuReference = dbfs;
                _settings.Set("VuReference", dbfs);
                UpdateVuToolTips();
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            foreach (MenuItem item in menu.Items) item.IsChecked = (double)item.Tag == _vuReference;
        };
        return menu;
    }

    private void UpdateVuToolTips() =>
        VuLeft.ToolTip = VuRight.ToolTip = $"0 VU = sine at {_vuReference:0} dBFS peak. Right-click to change.".Replace('-', '−');
}
