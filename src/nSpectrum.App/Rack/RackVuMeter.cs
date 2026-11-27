using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace nSpectrum.App.Rack;

/// <summary>
/// An amber-lit analog VU meter for the rack: classic −20…+3 VU scale with the red zone, channel
/// letter, "VU" legend and a black needle. The face is drawn once per size; only the needle moves.
/// The needle's IEC 60268-17 ballistics run in the DSP (<see cref="Core.Meters.MeterTap"/>).
/// </summary>
public sealed class RackVuMeter : FrameworkElement
{
    // Drawing space of the face; scaled to the control's size.
    private const double W = 270, H = 146, Cx = 135, Cy = 210, R0 = 150, HalfSweep = 43;

    private readonly DrawingVisual _face = new();
    private readonly DrawingVisual _needle = new();
    private readonly RotateTransform _rotate = new(-HalfSweep - 3, Cx, Cy);
    private readonly ScaleTransform _scale = new(1, 1);
    private double _shown = double.NaN;

    public RackVuMeter()
    {
        ClipToBounds = true; // the needle swings past the face edges at the end stops
        AddVisualChild(_face);
        AddVisualChild(_needle);
        _face.Transform = _scale;
        var group = new TransformGroup();
        group.Children.Add(_rotate);
        group.Children.Add(_scale);
        _needle.Transform = group;
        SizeChanged += (_, _) => DrawFace();
        Loaded += (_, _) => { DrawFace(); DrawNeedle(); };
    }

    /// <summary>Channel letter on the face ("L", "R").</summary>
    public string Channel { get; set; } = "L";

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _face : _needle;

    /// <summary>Moves the needle to a VU reading (0 = reference level). Below −20 it rests on the left stop.</summary>
    public void SetVu(double vu)
    {
        double a = Angle(vu);
        if (Math.Abs(a - _shown) < 0.02) return;
        _shown = a;
        _rotate.Angle = a;
    }

    /// <summary>
    /// Needle angle in degrees for a VU value. As on a real meter the deflection is proportional to
    /// voltage, from zero at the rest position to +3 VU (141 %) at full scale, so 0 VU sits at 71 %
    /// of the sweep and −20 VU just right of the rest position.
    /// </summary>
    private static double Angle(double vu)
    {
        double frac = Math.Pow(10, Math.Clamp(vu, -60, 6) / 20) / Math.Pow(10, 3 / 20.0);
        frac = Math.Clamp(frac, -0.02, 1.07); // end stops
        return -HalfSweep + 2 * HalfSweep * frac;
    }

    private static Point P(double deg, double r)
    {
        double a = deg * Math.PI / 180;
        return new Point(Cx + r * Math.Sin(a), Cy - r * Math.Cos(a));
    }

    private static readonly Color Ink = Color.FromRgb(0x2A, 0x1A, 0x08);
    private static readonly Color Red = Color.FromRgb(0xC8, 0x16, 0x0E);

    private void DrawFace()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _scale.ScaleX = ActualWidth / W;
        _scale.ScaleY = ActualHeight / H;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        using var dc = _face.RenderOpen();
        var rect = new Rect(0, 0, W, H);
        dc.PushClip(new RectangleGeometry(rect, 5, 5));

        // Back-lit amber paper.
        var paper = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.7), GradientOrigin = new Point(0.5, 0.7), RadiusX = 0.75, RadiusY = 0.75,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xFF, 0xE9, 0xA8), 0),
                new GradientStop(Color.FromRgb(0xFF, 0xC4, 0x5A), 0.55),
                new GradientStop(Color.FromRgb(0xD9, 0x86, 0x1E), 1),
            },
        };
        dc.DrawRectangle(paper, null, rect);

        var ink = new SolidColorBrush(Ink);
        var red = new SolidColorBrush(Red);
        var inkPen = new Pen(ink, 1.4);
        // Main arc and the red zone (0 to +3 VU).
        dc.DrawGeometry(null, inkPen, Arc(-20, 3, R0));
        dc.DrawGeometry(null, new Pen(red, 7), Arc(0, 3, R0 + 4));

        double[] marks = [-20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3];
        double[] labelled = [-20, -10, -7, -5, -3, -1, 0, 1, 3];
        var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (double d in marks)
        {
            bool big = labelled.Contains(d);
            double a = Angle(d);
            var brush = d > 0 ? red : ink;
            dc.DrawLine(new Pen(brush, big ? 2 : 1.4), P(a, R0), P(a, R0 + (big ? 13 : 8)));
            if (big) Text(dc, d > 0 ? "+" + d.ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture), P(a, R0 + 24), 12, brush, tf, dip);
        }
        for (double d = -17.5; d < -10; d += 2.5)
        {
            double a = Angle(d);
            dc.DrawLine(new Pen(ink, 1), P(a, R0), P(a, R0 + 6));
        }

        Text(dc, Channel, new Point(Cx, 86), 15, new SolidColorBrush(Color.FromRgb(0x7A, 0x44, 0x10)), tf, dip);
        Text(dc, "VU", new Point(Cx, 112), 25, new SolidColorBrush(Color.FromRgb(0x1D, 0x12, 0x06)),
            new Typeface(tf.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), dip);
        Text(dc, "−", new Point(26, 116), 11, new SolidColorBrush(Color.FromRgb(0x5A, 0x34, 0x10)), tf, dip);
        Text(dc, "+", new Point(244, 116), 11, red, tf, dip);

        // Needle pivot housing at the bottom.
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2A, 0x1D, 0x12)), null, new Point(Cx, H + 4), 34, 12);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x80, 0xCF, 0x9A, 0x45)), null, new Point(Cx, H + 2), 30, 9);

        // Vignette and the glass reflection on the upper half.
        dc.DrawRectangle(new RadialGradientBrush
        {
            RadiusX = 0.7, RadiusY = 0.7,
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.6), new GradientStop(Color.FromArgb(0x8C, 0x3A, 0x18, 0x00), 1) },
        }, null, rect);
        dc.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 0.45),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.46),
            },
        }, null, rect);
        dc.Pop();
    }

    private void DrawNeedle()
    {
        using var dc = _needle.RenderOpen();
        // Drawn pointing straight up; the rotate transform turns it about the pivot.
        var tip = new Point(Cx, Cy - (R0 + 10));
        var tail = new Point(Cx, Cy - 70);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x5A, 0x5A, 0x2C, 0x00)), 2.4), new Point(tail.X + 2, tail.Y + 3), new Point(tip.X + 2, tip.Y + 3));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x10)), 2), tail, tip);
    }

    private static PathGeometry Arc(double d1, double d2, double r)
    {
        var g = new PathGeometry();
        var f = new PathFigure { StartPoint = P(Angle(d1), r), IsClosed = false };
        f.Segments.Add(new ArcSegment(P(Angle(d2), r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        g.Figures.Add(f);
        g.Freeze();
        return g;
    }

    private static void Text(DrawingContext dc, string s, Point centre, double size, Brush brush, Typeface tf, double dip)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, brush, dip);
        dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y - ft.Height / 2));
    }
}
