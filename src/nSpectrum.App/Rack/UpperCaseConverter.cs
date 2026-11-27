using System.Globalization;
using System.Windows.Data;

namespace nSpectrum.App.Rack;

/// <summary>Shows an item's text in capitals, as engraved on the rack buttons ("Mid" → "MID").</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpper(CultureInfo.CurrentCulture) ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
