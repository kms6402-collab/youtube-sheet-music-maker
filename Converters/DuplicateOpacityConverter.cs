using System.Globalization;
using System.Windows.Data;

namespace ScoreCap.Converters;

/// <summary>Dims duplicate-frame thumbnail cards in the capture grid.</summary>
public class DuplicateOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.45 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
