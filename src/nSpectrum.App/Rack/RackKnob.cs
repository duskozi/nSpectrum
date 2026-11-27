using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace nSpectrum.App.Rack;

/// <summary>
/// A hi-fi rotary knob: knurled black skirt, brushed aluminium cap and a pointer, with an optional
/// scale of ticks around it. <see cref="Value"/> runs from 0 to 1 over 270 degrees. Drag up/down
/// or use the mouse wheel; with <see cref="Steps"/> above 1 it moves in detents.
/// </summary>
public sealed class RackKnob : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RackKnob),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((RackKnob)d).ValueChanged?.Invoke(d, EventArgs.Empty), (_, v) => Math.Clamp((double)v, 0, 1)));

    public static readonly DependencyProperty ShowScaleProperty = DependencyProperty.Register(
        nameof(ShowScale), typeof(bool), typeof(RackKnob), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Sweep = 270;
    private Point _dragStart;
    private double _dragValue;
    private bool _dragging;

    static RackKnob()
    {
        FocusableProperty.OverrideMetadata(typeof(RackKnob), new FrameworkPropertyMetadata(true));
        CursorProperty.OverrideMetadata(typeof(RackKnob), new FrameworkPropertyMetadata(Cursors.SizeNS));
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Draws the tick scale around the knob (the INPUT knob on the rack).</summary>
    public bool ShowScale
    {
        get => (bool)GetValue(ShowScaleProperty);
        set => SetValue(ShowScaleProperty, value);
    }

    /// <summary>Number of detent positions; 0 or 1 turns smoothly.</summary>
    public int Steps { get; set; }

    /// <summary>Raised when the value changes, by the user or by code.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Raised when the user lets go of the knob (or after a wheel notch).</summary>
    public event EventHandler? Committed;

    private double Snap(double v) => Steps > 1 ? Math.Round(v * (Steps - 1)) / (Steps - 1) : v;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragStart = e.GetPosition(this);
        _dragValue = Value;
        _dragging = CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        // 200 px of travel for the full sweep; sideways movement counts too.
        double delta = ((_dragStart.Y - p.Y) + (p.X - _dragStart.X) * 0.5) / 200;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) delta *= 0.15; // fine adjustment
        Value = Snap(_dragValue + delta);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double step = Steps > 1 ? 1.0 / (Steps - 1) : (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.002 : 0.01);
        Value = Snap(Value + Math.Sign(e.Delta) * step);
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = Steps > 1 ? 1.0 / (Steps - 1) : 0.01;
        if (e.Key is Key.Up or Key.Right) Value = Snap(Value + step);
        else if (e.Key is Key.Down or Key.Left) Value = Snap(Value - step);
        else return;
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // Drawing

    private static readonly Pen TickPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE2, 0xD8)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen TickPenMajor = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE2, 0xD8)), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Brush SkirtDark = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x11, 0x10)));
    private static readonly Brush SkirtLight = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x36, 0x33)));
    private static readonly Pen Outline = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x0C, 0x0B, 0x0A)), 2));
    private static readonly Pen CapRim = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x18, 0x17)), 2));
    private static readonly Brush PointerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x19, 0x18)));
    private static readonly Brush ShadowBrush = Frozen(new RadialGradientBrush(Color.FromArgb(0xB0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)));

    // Brushed aluminium cap: a sweep of light and dark bands, as an anisotropic highlight would give.
    private static readonly Brush CapBrush = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromRgb(0x4F, 0x4A, 0x45), 0.00),
        new(Color.FromRgb(0xE2, 0xDD, 0xD5), 0.22),
        new(Color.FromRgb(0x8A, 0x84, 0x7C), 0.40),
        new(Color.FromRgb(0x5A, 0x55, 0x50), 0.52),
        new(Color.FromRgb(0xEB, 0xE6, 0xDD), 0.72),
        new(Color.FromRgb(0x8D, 0x87, 0x80), 0.88),
        new(Color.FromRgb(0x4B, 0x46, 0x42), 1.00),
    }, new Point(0, 0), new Point(1, 1)));

    private static readonly Brush CapSheen = Frozen(new RadialGradientBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF))
    { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.4, 0.35), RadiusX = 0.6, RadiusY = 0.6 });

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override Size MeasureOverride(Size availableSize) => new(Width is > 0 ? Width : 56, Height is > 0 ? Height : 56);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double scalePad = ShowScale ? Math.Max(10, size * 0.16) : 0;
        double r = size / 2 - scalePad;

        if (ShowScale)
        {
            for (int i = 0; i <= 10; i++)
            {
                double a = (-135 + i * 27) * Math.PI / 180;
                bool major = i % 5 == 0;
                double r1 = r + 5, r2 = r + (major ? 15 : 11) * size / 136;
                dc.DrawLine(major ? TickPenMajor : TickPen, P(c, a, r1), P(c, a, Math.Max(r2, r1 + 4)));
            }
        }

        // Drop shadow under the knob.
        dc.DrawEllipse(ShadowBrush, null, new Point(c.X, c.Y + r * 0.12), r * 1.12, r * 1.12);

        // Knurled skirt: alternating wedges.
        int teeth = Math.Clamp((int)(r * 1.6), 36, 90);
        dc.DrawEllipse(SkirtDark, Outline, c, r, r);
        var knurl = new StreamGeometry();
        using (var g = knurl.Open())
        {
            for (int i = 0; i < teeth; i++)
            {
                double a0 = i * 2 * Math.PI / teeth, a1 = a0 + Math.PI / teeth;
                g.BeginFigure(c, true, true);
                g.LineTo(P(c, a0, r - 1), false, false);
                g.LineTo(P(c, a1, r - 1), false, false);
            }
        }
        knurl.Freeze();
        dc.DrawGeometry(SkirtLight, null, knurl);

        // Cap.
        double rc = r * 0.68;
        dc.DrawEllipse(CapBrush, CapRim, c, rc, rc);
        dc.DrawEllipse(CapSheen, null, c, rc, rc);

        // Pointer: a dark engraved line from near the centre to the cap edge.
        double angle = (-Sweep / 2 + Value * Sweep) * Math.PI / 180;
        var tip = P(c, angle, rc * 0.92);
        var tail = P(c, angle, rc * 0.25);
        var pen = new Pen(PointerBrush, Math.Max(2.2, r * 0.07)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawLine(pen, tail, tip);
    }

    private static Point P(Point c, double a, double r) => new(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
}
