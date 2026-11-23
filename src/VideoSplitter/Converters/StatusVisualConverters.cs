using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VideoSplitter.ViewModels;

namespace VideoSplitter.Converters;

public sealed class BoolToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string raw = parameter as string ?? string.Empty;
        string[] parts = raw.Split('|');
        string trueText = parts.Length > 0 ? parts[0] : string.Empty;
        string falseText = parts.Length > 1 ? parts[1] : string.Empty;

        return value is true ? trueText : falseText;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public sealed class BoolToKeyFrameBrushConverter : IValueConverter
{
    private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? SuccessBrush : WarningBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public sealed class ExportStatusToGeometryConverter : IValueConverter
{
    private static readonly Geometry PendingGeometry = Geometry.Parse("M 2 6 L 10 6");
    private static readonly Geometry RunningGeometry = Geometry.Parse("M 3 2 L 9 6 L 3 10 Z");
    private static readonly Geometry DoneGeometry = Geometry.Parse("M 1.5 6 L 4.5 9 L 10.5 3");
    private static readonly Geometry CrossGeometry = Geometry.Parse("M 2 2 L 10 10 M 10 2 L 2 10");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ExportItemStatus status
            ? status switch
            {
                ExportItemStatus.EnCours => RunningGeometry,
                ExportItemStatus.Termine => DoneGeometry,
                ExportItemStatus.Erreur => CrossGeometry,
                ExportItemStatus.Annule => CrossGeometry,
                _ => PendingGeometry
            }
            : PendingGeometry;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public sealed class ExportStatusToStrokeConverter : IValueConverter
{
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x66, 0x6E, 0x7D));
    private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly IBrush DangerBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ExportItemStatus status
            ? status switch
            {
                ExportItemStatus.EnCours => null,
                ExportItemStatus.Termine => SuccessBrush,
                ExportItemStatus.Erreur => DangerBrush,
                ExportItemStatus.Annule => WarningBrush,
                _ => MutedBrush
            }
            : MutedBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public sealed class ExportStatusToFillConverter : IValueConverter
{
    private static readonly IBrush AccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ExportItemStatus.EnCours ? AccentBrush : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public sealed class ExportStatusToTextBrushConverter : IValueConverter
{
    private static readonly IBrush SecondaryBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
    private static readonly IBrush AccentHoverBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly IBrush DangerBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ExportItemStatus status
            ? status switch
            {
                ExportItemStatus.EnCours => AccentHoverBrush,
                ExportItemStatus.Termine => SuccessBrush,
                ExportItemStatus.Erreur => DangerBrush,
                ExportItemStatus.Annule => WarningBrush,
                _ => SecondaryBrush
            }
            : SecondaryBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
