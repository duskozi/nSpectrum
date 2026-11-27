using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace nSpectrum.App.Rack;

/// <summary>Cuts off the top of a borderless window with a window region (clicks there reach the desktop).</summary>
internal static class WindowRegion
{
    private static readonly Dictionary<nint, int> Applied = [];

    /// <summary>Hides everything above <paramref name="topDip"/> (WPF units from the window's top); 0 shows the whole window.</summary>
    public static void ClipTop(Window window, double topDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var target = PresentationSource.FromVisual(window)?.CompositionTarget;
        if (hwnd == 0 || target is null) return;

        var toDevice = target.TransformToDevice;
        int top = (int)Math.Round(toDevice.Transform(new Point(0, topDip)).Y);
        var size = toDevice.Transform(new Point(window.ActualWidth, window.ActualHeight));
        int w = (int)Math.Ceiling(size.X) + 1, h = (int)Math.Ceiling(size.Y) + 1;

        // Same cut as last time: nothing to do (avoids a redraw on every size notification).
        int key = top <= 0 ? 0 : top * 65536 ^ w * 31 ^ h;
        if (Applied.TryGetValue(hwnd, out int last) && last == key) return;
        Applied[hwnd] = key;

        if (top <= 0)
        {
            SetWindowRgn(hwnd, 0, true);
            return;
        }
        nint rgn = CreateRectRgn(0, top, w, h);
        if (rgn != 0 && SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn); // on success the system owns it
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint rgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
