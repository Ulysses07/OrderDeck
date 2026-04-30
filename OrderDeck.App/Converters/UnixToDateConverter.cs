using System;
using System.Globalization;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>UNIX-seconds long? → "yyyy-MM-dd HH:mm" local string. Null → "".</summary>
public sealed class UnixToDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long unix && unix > 0)
            return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
