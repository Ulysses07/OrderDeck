using System;
using System.Globalization;
using System.Windows.Data;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Converters;

/// <summary>Maps a CustomerGiveawayRow to a localized status text.
/// IsWinner → "🏆 Kazandı"; cancelled giveaway → "İptal edildi"; otherwise "Katıldı".</summary>
public sealed class GiveawayParticipationStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CustomerGiveawayRow row) return "";
        if (row.IsWinner) return "🏆 Kazandı";
        if (row.GiveawayCancelledAt is not null) return "İptal edildi";
        return "Katıldı";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
