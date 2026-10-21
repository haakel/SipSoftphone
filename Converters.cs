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

/// <summary>true → Collapsed, false/null → Visible (opposite of NullToVis for booleans).</summary>
public sealed class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>non-empty string → Collapsed, null/empty → Visible (inverse of NullToVis).</summary>
public sealed class InverseNullToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>count &gt; 0 → true (enabled), 0/null → false.</summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is int c && c > 0;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>0 → Visible (empty state), any other count → Collapsed.</summary>
public sealed class CountZeroToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int c && c == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-zero count → Visible, zero → Collapsed (e.g. contact list body).</summary>
public sealed class CountToVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int c && c > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Resolves a resource key (e.g. "IcoArrowUpRight") to a Geometry from application resources.</summary>
public sealed class KeyToGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && key.Length > 0 &&
            System.Windows.Application.Current != null &&
            System.Windows.Application.Current.TryFindResource(key) is System.Windows.Media.Geometry g)
            return g;
        return System.Windows.Media.Geometry.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
