using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using nSpectrum.Core.Audio;
using nSpectrum.Visuals;
using nSpectrum.Wpf;

namespace nSpectrum.App.Meters;

/// <summary>
/// A small floating meter window: drag anywhere to move, drag the edges to resize, right-click for
/// options, double-click to show or hide the title bar. Remembers its place, size and options.
/// Subclasses attach a tap to the running pipeline and paint into a <see cref="PixelView"/>.
/// </summary>
internal abstract class MeterWindow : System.Windows.Window
{
    private readonly PixelView _view = new();
    private bool _chrome;

    protected MeterWindow(string key, string baseTitle, double width, double height)
    {
        Key = key;
        BaseTitle = baseTitle;
        Settings = WindowSettings.For(key);
        Title = baseTitle;
        Background = Brushes.Black;
        MinWidth = 120;
        MinHeight = 60;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Content = RackFrame ? RackMeterFrame.Build(this, baseTitle.Replace("nSpectrum ", ""), _view, OpenMenu) : _view;
        if (RackFrame)
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x17, 0x15));
            MinWidth = 360;
            MinHeight = 180;
        }

        Width = Settings.Get("Width", width);
        Height = Settings.Get("Height", height);
        var area = SystemParameters.WorkArea;
        Left = Settings.Get("Left", area.Left + 40);
        Top = Settings.Get("Top", area.Top + 40);
        KeepOnScreen();
        Topmost = Settings.Get("Topmost", true);
        SetChrome(!RackFrame && Settings.Get("Chrome", true));
        OnRack = RackFrame && Settings.Get("OnRack", true);

        _view.Paint += (px, dt) => Paint(px, dt);
        ContextMenu = new ContextMenu();
        ContextMenu.Items.Add(new MenuItem { Header = "…" }); // an empty menu would not open; filled on opening
        ContextMenuOpening += (_, _) => RebuildMenu();
        MouseLeftButtonDown += OnMouseDown;
        Closed += (_, _) => { SaveSettings(); Detach(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && !_chrome && !RackFrame) SetChrome(true); };
    }

    /// <summary>
    /// Shows the window as a small rack unit in the main window's style (wood, brushed metal,
    /// engraved title, gear and close buttons, resize grip) instead of a plain title bar.
    /// </summary>
    protected virtual bool RackFrame => false;

    /// <summary>
    /// The window sits on top of the main window's rack, as wide as it, and follows it (above the
    /// options panel when that is open). Dragging the window away turns this off; dropping it back
    /// on the rack turns it on again.
    /// </summary>
    public bool OnRack { get; set; }

    /// <summary>Raised after the user drags the window to a new place.</summary>
    public event EventHandler? Dragged;

    /// <summary>Raised when "Sit on the rack" is switched in the menu.</summary>
    public event EventHandler? OnRackChanged;

    /// <summary>Opens the options menu under a button (the rack frame's gear).</summary>
    private void OpenMenu(UIElement target)
    {
        RebuildMenu();
        ContextMenu.PlacementTarget = target;
        ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ContextMenu.IsOpen = true;
    }

    /// <summary>Settings key, e.g. "Led".</summary>
    public string Key { get; }

    protected string BaseTitle { get; }

    protected Dictionary<string, string> Settings { get; }

    protected AudioFormat? Format { get; private set; }

    /// <summary>Connects to a running pipeline (or disconnects with null). Called on the UI thread.</summary>
    public void Attach(AnalysisPipeline? pipeline)
    {
        Detach();
        Format = pipeline?.Format;
        if (pipeline is not null) OnAttach(pipeline);
        Title = Format is { } f
            ? $"{BaseTitle}  ·  {f.SampleRate / 1000.0:0.0} kHz / {f.Channels} ch"
            : $"{BaseTitle}  ·  no source";
    }

    private void Detach()
    {
        OnDetach();
        Format = null;
    }

    protected abstract void OnAttach(AnalysisPipeline pipeline);

    protected abstract void OnDetach();

    protected abstract void Paint(PixelBuffer px, double dt);

    /// <summary>Adds this window's own menu items (rebuilt each time the menu opens, so checks are current).</summary>
    protected abstract void BuildMenu(ItemCollection items);

    /// <summary>Writes this window's own options into <see cref="Settings"/>.</summary>
    protected abstract void SaveOptions();

    public void SaveSettings()
    {
        if (WindowState == WindowState.Normal)
        {
            Settings.Set("Left", Left);
            Settings.Set("Top", Top);
            Settings.Set("Width", Width);
            Settings.Set("Height", Height);
        }
        Settings.Set("Topmost", Topmost);
        Settings.Set("Chrome", _chrome);
        Settings.Set("OnRack", OnRack);
        SaveOptions();
        WindowSettings.Save();
    }

    private void KeepOnScreen()
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        Width = Math.Min(Width, vw);
        Height = Math.Min(Height, vh);
        if (Left + 60 > vl + vw || Left + Width - 60 < vl) Left = vl + 40;
        if (Top + 30 > vt + vh || Top < vt) Top = vt + 40;
    }

    private void SetChrome(bool on)
    {
        _chrome = on;
        if (on)
        {
            WindowChrome.SetWindowChrome(this, null);
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.CanResize;
        }
        else
        {
            // Borderless but still resizable from the edges.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(8),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !RackFrame)
        {
            SetChrome(!_chrome);
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { return; }
        Dragged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildMenu()
    {
        var items = ContextMenu.Items;
        items.Clear();
        BuildMenu(items);
        items.Add(new Separator());
        if (!RackFrame) items.Add(Check("Show window chrome", _chrome, () => SetChrome(!_chrome)));
        if (RackFrame) items.Add(Check("Sit on the rack", OnRack, () => { OnRack = !OnRack; OnRackChanged?.Invoke(this, EventArgs.Empty); }));
        items.Add(Check("Float on top", Topmost, () => Topmost = !Topmost));
        var size = new MenuItem { Header = "Size" };
        foreach (var (label, w, h) in Sizes)
            size.Items.Add(Item(label, () => { Width = w; Height = h; }));
        items.Add(size);
        items.Add(new Separator());
        items.Add(Item("Close", Close));
    }

    /// <summary>Preset sizes offered in the Size menu.</summary>
    protected virtual IEnumerable<(string Label, double W, double H)> Sizes =>
        [("Small", Width * 0.6, Height * 0.6), ("Medium", Width, Height), ("Large", Width * 1.5, Height * 1.5)];

    protected static MenuItem Item(string header, Action click)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => click();
        return m;
    }

    protected MenuItem Check(string header, bool isChecked, Action click)
    {
        var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        m.Click += (_, _) => { click(); SaveSettings(); };
        return m;
    }

    /// <summary>A submenu of mutually exclusive choices.</summary>
    protected MenuItem Choice<T>(string header, IEnumerable<(string Label, T Value)> options, T current, Action<T> pick)
    {
        var m = new MenuItem { Header = header };
        foreach (var (label, value) in options)
            m.Items.Add(Check(label, EqualityComparer<T>.Default.Equals(value, current), () => pick(value)));
        return m;
    }

    /// <summary>Inline group of mutually exclusive choices (no submenu), like the band-count items of the menu.</summary>
    protected void Group<T>(ItemCollection items, IEnumerable<(string Label, T Value)> options, T current, Action<T> pick)
    {
        foreach (var (label, value) in options)
            items.Add(Check(label, EqualityComparer<T>.Default.Equals(value, current), () => pick(value)));
    }

    protected static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
