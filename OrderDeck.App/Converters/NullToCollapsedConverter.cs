using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OrderDeck.App.Converters;

/// <summary>Null/empty string → Collapsed; non-empty → Visible. Used by validation banners.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
