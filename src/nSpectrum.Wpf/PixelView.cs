using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using nSpectrum.Visuals;

namespace nSpectrum.Wpf;

/// <summary>
/// Shows a software-rendered <see cref="PixelBuffer"/> at the display's native pixel size and
/// repaints it on every WPF frame (CompositionTarget.Rendering) while visible. Unlike the Direct3D
/// <see cref="SpectrumView"/> it composes like any WPF element, so it works in borderless,
/// transparent-free floating windows and can be overlapped.
/// </summary>
public sealed class PixelView : FrameworkElement
{
    private readonly PixelBuffer _buffer = new(1, 1);
    private WriteableBitmap? _bitmap;
    private TimeSpan _lastRender = TimeSpan.MinValue;
    private bool _hooked;

    public PixelView()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SnapsToDevicePixels = true;
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
        IsVisibleChanged += (_, _) => Hook(IsVisible && IsLoaded);
    }

    /// <summary>Called once per frame with the buffer (already sized to the element) and the seconds since the last frame.</summary>
    public event Action<PixelBuffer, double>? Paint;

    /// <summary>Last frame's paint time in milliseconds.</summary>
    public double PaintMs { get; private set; }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var time = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (time == _lastRender) return; // Rendering can fire more than once per frame
        double dt = _lastRender == TimeSpan.MinValue ? 1 / 60.0 : Math.Clamp((time - _lastRender).TotalSeconds, 0, 0.25);
        _lastRender = time;

        var dpi = VisualTreeHelper.GetDpi(this);
        int w = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
            InvalidateVisual();
        }
        _buffer.Resize(w, h);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Paint?.Invoke(_buffer, dt);
        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _buffer.Pixels, w * 4, 0);
        PaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap is not null) dc.DrawImage(_bitmap, new Rect(RenderSize));
    }
}
