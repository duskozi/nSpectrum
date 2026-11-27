using System.Windows;
using System.Windows.Controls;

namespace nSpectrum.App.Rack;

/// <summary>
/// The look of a rack push button: dark rounded cap in a bezel, amber lettering and glow when lit,
/// optional status LED. Used inside the Button, ToggleButton and segment (ListBoxItem) templates so
/// they all share one design. Purely visual: the hosting control does the clicking.
/// </summary>
public sealed class RackChrome : ContentControl
{
    public static readonly DependencyProperty LitProperty = DependencyProperty.Register(nameof(Lit), typeof(bool), typeof(RackChrome));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.Register(nameof(Pressed), typeof(bool), typeof(RackChrome));
    public static readonly DependencyProperty HoverProperty = DependencyProperty.Register(nameof(Hover), typeof(bool), typeof(RackChrome));
    public static readonly DependencyProperty ShowLedProperty = DependencyProperty.Register(nameof(ShowLed), typeof(bool), typeof(RackChrome));

    static RackChrome()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(RackChrome), new FrameworkPropertyMetadata(typeof(RackChrome)));
        FocusableProperty.OverrideMetadata(typeof(RackChrome), new FrameworkPropertyMetadata(false));
    }

    public bool Lit { get => (bool)GetValue(LitProperty); set => SetValue(LitProperty, value); }
    public bool Pressed { get => (bool)GetValue(PressedProperty); set => SetValue(PressedProperty, value); }
    public bool Hover { get => (bool)GetValue(HoverProperty); set => SetValue(HoverProperty, value); }
    public bool ShowLed { get => (bool)GetValue(ShowLedProperty); set => SetValue(ShowLedProperty, value); }
}

/// <summary>Attached properties the rack styles read.</summary>
public static class RackProps
{
    /// <summary>Lights a button amber (SPEC, the active option). Toggle buttons bind it to IsChecked.</summary>
    public static readonly DependencyProperty LitProperty = DependencyProperty.RegisterAttached(
        "Lit", typeof(bool), typeof(RackProps), new FrameworkPropertyMetadata(false));

    /// <summary>Shows the small status LED left of the label.</summary>
    public static readonly DependencyProperty LedProperty = DependencyProperty.RegisterAttached(
        "Led", typeof(bool), typeof(RackProps), new FrameworkPropertyMetadata(false));

    /// <summary>Sets a TextBlock's text with hair spaces between the letters, like engraved lettering.</summary>
    public static readonly DependencyProperty EngraveProperty = DependencyProperty.RegisterAttached(
        "Engrave", typeof(string), typeof(RackProps), new PropertyMetadata(null, (d, e) =>
        {
            if (d is TextBlock t) t.Text = string.Join(" ", ((string?)e.NewValue ?? "").ToCharArray());
        }));

    public static bool GetLit(DependencyObject d) => (bool)d.GetValue(LitProperty);
    public static void SetLit(DependencyObject d, bool v) => d.SetValue(LitProperty, v);
    public static bool GetLed(DependencyObject d) => (bool)d.GetValue(LedProperty);
    public static void SetLed(DependencyObject d, bool v) => d.SetValue(LedProperty, v);
    public static string? GetEngrave(DependencyObject d) => (string?)d.GetValue(EngraveProperty);
    public static void SetEngrave(DependencyObject d, string? v) => d.SetValue(EngraveProperty, v);
}
