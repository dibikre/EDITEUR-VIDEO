using System.Globalization;
using System.Windows.Data;

namespace VideoSplitter.Converters;

public sealed class EnumBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        if (parameter is string paramString && Enum.TryParse(value.GetType(), paramString, out object? parsed))
        {
            return Equals(value, parsed);
        }

        return Equals(value, parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        if (parameter is string paramString)
        {
            return Enum.Parse(targetType, paramString);
        }

        return parameter;
    }
}
