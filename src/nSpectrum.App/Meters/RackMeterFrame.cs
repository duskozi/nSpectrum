using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using nSpectrum.App.Rack;

namespace nSpectrum.App.Meters;

/// <summary>
/// Dresses a meter window as a small rack unit matching the main window: walnut cheeks, a
/// brushed-metal faceplate with an engraved title, gear and close buttons, the meter behind
/// dark glass, and a resize grip in the corner. Every edge resizes the window too.
/// </summary>
internal static class RackMeterFrame
{
    public static FrameworkElement Build(Window window, string title, UIElement meter, Action<UIElement> openMenu)
    {
        var res = Application.Current.Resources;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

        var left = new ContentControl { Style = (Style)res["RackCheek"], Tag = "L", Width = 22 };
        var right = new ContentControl { Style = (Style)res["RackCheek"], Tag = "R", Width = 22 };
        Grid.SetColumn(right, 2);
        root.Children.Add(left);
        root.Children.Add(right);

        var plate = new ContentControl { Style = (Style)res["RackPlate"], Padding = new Thickness(30, 18, 30, 16) };
        Grid.SetColumn(plate, 1);
        root.Children.Add(plate);

        var body = new DockPanel();
        plate.Content = body;

        // Header: engraved title on the left, gear (options menu) and close on the right.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        DockPanel.SetDock(header, Dock.Top);
        var name = new TextBlock { Style = (Style)res["RackHeader"], Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        RackProps.SetEngrave(name, title.ToUpperInvariant());
        DockPanel.SetDock(name, Dock.Left);
        header.Children.Add(name);

        var close = SmallButton(res, "✕", "Close");
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        var gear = SmallButton(res, "⚙", "Options");
        gear.Margin = new Thickness(0, 0, 10, 0);
        gear.Click += (_, _) => openMenu(gear);
        DockPanel.SetDock(gear, Dock.Right);
        header.Children.Add(gear);
        body.Children.Add(header);

        // The meter behind dark glass, in the same bezel as the main spectrum screen.
        var bezel = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x09, 0x08)),
            BorderThickness = new Thickness(2),
            Background = new LinearGradientBrush(Color.FromRgb(0x1A, 0x18, 0x17), Color.FromRgb(0x27, 0x24, 0x22), 90),
        };
        var glass = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x06)),
            ClipToBounds = true,
            Child = meter,
        };
        bezel.Child = glass;

        // Resize grip: three engraved diagonal lines in the bottom-right corner of the plate.
        var grip = new Canvas
        {
            Width = 16, Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -24, -12),
            Background = Brushes.Transparent, Cursor = Cursors.SizeNWSE,
            ToolTip = "Drag to resize",
        };
        for (int i = 0; i < 3; i++)
        {
            double o = 4 + i * 4;
            grip.Children.Add(new Line { X1 = o, Y1 = 16, X2 = 16, Y2 = o, Stroke = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), StrokeThickness = 1.5 });
            grip.Children.Add(new Line { X1 = o + 1, Y1 = 16, X2 = 16, Y2 = o + 1, Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 });
        }
        grip.MouseEnter += (_, _) => grip.Cursor = OnRack(window) ? Cursors.SizeNS : Cursors.SizeNWSE;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            // Hand the drag to Windows as a border drag: native live resize. Sitting on the rack,
            // the bottom edge stays on the rack, so the drag changes the height from the top.
            var hwnd = new WindowInteropHelper(window).Handle;
            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, OnRack(window) ? HTTOP : HTBOTTOMRIGHT, 0);
            e.Handled = true;
        };
        // The grip sits over the plate's bottom-right padding, next to the screen.
        var fill = new Grid();
        fill.Children.Add(bezel);
        fill.Children.Add(grip);
        body.Children.Add(fill);
        return root;
    }

    private static Button SmallButton(ResourceDictionary res, string text, string tip) => new()
    {
        Style = (Style)res["RackSmallButton"],
        Content = text,
        Height = 28,
        MinWidth = 34,
        Padding = new Thickness(8, 0, 8, 0),
        FontSize = 13,
        ToolTip = tip,
    };

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTTOP = 12;
    private const int HTBOTTOMRIGHT = 17;

    private static bool OnRack(Window window) => window is MeterWindow { OnRack: true };

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);
}
