using System;
using System.Globalization;
using System.Windows.Data;

namespace OrderDeck.App.Converters;

/// <summary>
/// Renders the queued-label chip text: 0 → "Yedek+" (call-to-action), N → "👥 N"
/// (count badge). Used by the QueueList row template; one-way only.
/// </summary>
public sealed class BackupChipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i  => i,
            long l => (int)l,
            _ => 0,
        };
        return count <= 0 ? "Yedek+" : $"👥 {count}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
