using System.Globalization;
using System.Windows.Data;

namespace VideoSplitter.Converters;

public sealed class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts)
        {
            return "00:00:00";
        }

        if (ts < TimeSpan.Zero)
        {
            ts = TimeSpan.Zero;
        }

        bool includeMilliseconds = string.Equals(parameter as string, "ms", StringComparison.OrdinalIgnoreCase);
        int totalHours = (int)ts.TotalHours;

        return includeMilliseconds
            ? $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
            : $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
