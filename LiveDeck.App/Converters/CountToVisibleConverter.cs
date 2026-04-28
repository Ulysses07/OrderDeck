using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>int (or long) > 0 → Visible, otherwise Collapsed. Used to gate sections that
/// should disappear when their bound collection is empty.</summary>
public sealed class CountToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i)  return i > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (value is long l) return l > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int (or long) > 0 → Collapsed, otherwise Visible. Inverse of <see cref="CountToVisibleConverter"/>.</summary>
public sealed class CountToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i)  return i > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (value is long l) return l > 0 ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
