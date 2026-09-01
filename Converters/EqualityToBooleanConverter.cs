using System.Globalization;
using System.Windows.Data;

namespace ScoreCap.Converters;

/// <summary>Drives radio-button "chip" groups (paper size, page-layout columns, frame interval) off a plain
/// enum/int/double property: IsChecked is true when value.ToString() == ConverterParameter.ToString().</summary>
public class EqualityToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
            return Binding.DoNothing;

        var text = parameter.ToString()!;
        if (targetType.IsEnum)
            return Enum.Parse(targetType, text);

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return System.Convert.ChangeType(text, underlying, culture);
    }
}
