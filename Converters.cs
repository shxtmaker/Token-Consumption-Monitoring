using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TokenConsumptionMonitoring.Models;
using Color = System.Windows.Media.Color;

namespace TokenConsumptionMonitoring.Converters;

public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (AlertLevel)value switch
        {
            AlertLevel.Critical => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x48)),
            AlertLevel.Warn => new SolidColorBrush(Color.FromRgb(0xE0, 0xA5, 0x2B)),
            _ => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x5F)),
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ConnectionToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (ConnectionStatus)value switch
        {
            ConnectionStatus.Ok => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x5F)),
            ConnectionStatus.Warn => new SolidColorBrush(Color.FromRgb(0xE0, 0xA5, 0x2B)),
            ConnectionStatus.Critical => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x48)),
            ConnectionStatus.AuthError => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x48)),
            ConnectionStatus.Offline => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B)),
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ProgressConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int p && p > 0 ? Math.Min(100, p) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
