using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace nSpectrum.App.Rack;

/// <summary>
/// Lays its child out at least <see cref="DesignWidth"/> wide. Wider than that, the child simply
/// stretches (the spectrum screen takes the extra room); narrower, the whole child is scaled down
/// uniformly, so the rack keeps its proportions on small or high-DPI screens.
/// </summary>
public sealed class ScaleToWidth : Decorator
{
    private readonly ScaleTransform _scale = new(1, 1);

    public double DesignWidth { get; set; } = 1920;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        double avail = double.IsInfinity(constraint.Width) ? DesignWidth : constraint.Width;
        double w = Math.Max(DesignWidth, avail);
        Child.Measure(new Size(w, double.PositiveInfinity));
        double s = Math.Min(1, avail / w);
        return new Size(avail, Child.DesiredSize.Height * s);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null) return arrangeSize;
        double w = Math.Max(DesignWidth, arrangeSize.Width);
        double s = Math.Min(1, arrangeSize.Width / w);
        _scale.ScaleX = _scale.ScaleY = s;
        Child.RenderTransform = s < 1 ? _scale : Transform.Identity;
        Child.Arrange(new Rect(0, 0, w, Child.DesiredSize.Height));
        return new Size(arrangeSize.Width, Child.DesiredSize.Height * s);
    }
}
