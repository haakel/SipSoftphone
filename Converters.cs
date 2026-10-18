using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SipSoftphone;

/// <summary>Resolves a resource key (e.g. "OkBrush") to a Brush from application resources.</summary>
public sealed class KeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && System.Windows.Application.Current != null &&
            System.Windows.Application.Current.TryFindResource(key) is System.Windows.Media.Brush b)
            return b;
        return System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null / empty string → Collapsed, otherwise Visible.</summary>
public sealed class NullToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
