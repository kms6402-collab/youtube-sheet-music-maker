using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ScoreCap.Converters;

/// <summary>Visible when the bound value is null (used for "no preview yet" placeholder text).</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
