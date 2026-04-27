using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

public sealed class JoinConverter : IValueConverter
{
    public string Separator { get; set; } = ", ";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable e)
            return string.Join(Separator, e.Cast<object>());
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
