using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LiveDeck.App.Converters;

/// <summary>true → soft red (rgba 80%); false → Transparent. Used for ListBoxItem backgrounds.</summary>
public sealed class BlacklistToBrushConverter : IValueConverter
{
    private static readonly Brush BlacklistedBrush =
        new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x66, 0x66));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? BlacklistedBrush : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
