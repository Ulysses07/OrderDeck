using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OrderDeck.App.Converters;

/// <summary>
/// platform string → Visibility. Used to hide the YouTube-specific moderation
/// menu items ("YT'de mesajı sil", "YT'de kullanıcıyı banla") on chat rows
/// from other platforms (IG, TikTok, Facebook). One-way only.
/// </summary>
public sealed class PlatformIsYouTubeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string platform &&
            string.Equals(platform, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
