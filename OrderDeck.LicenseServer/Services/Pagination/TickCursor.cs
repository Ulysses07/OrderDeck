namespace OrderDeck.LicenseServer.Services.Pagination;

/// <summary>
/// Composite cursor encode/decode helper'ı. Çoğu shopper-facing list endpoint
/// `{UtcTicks}|{Guid:N}` formatında stable cursor üretiyor — sort field
/// (CreatedAt / AddedAt) + tiebreak id.
///
/// Önceden 3 controller'da (ShopperFeed, ShopperBroadcasters'ta 2 yerde)
/// private static helper olarak tekrarlanıyordu. Burada konsolide edildi.
/// </summary>
public static class TickCursor
{
    /// <summary>
    /// `{UtcTicks}|{Guid:N}` formatında cursor üretir. Caller next-page sort
    /// + tiebreak için kullanır.
    /// </summary>
    public static string Encode(DateTimeOffset sortValue, Guid id)
        => $"{sortValue.UtcTicks}|{id:N}";

    /// <summary>
    /// Cursor'u parse eder. Boş / format hatası → false (caller cursor'suz
    /// devam eder). True dönerse <paramref name="ticks"/> ve <paramref name="id"/>
    /// set'lidir.
    /// </summary>
    public static bool TryDecode(string? cursor, out long ticks, out Guid id)
    {
        ticks = 0;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(cursor)) return false;
        var parts = cursor.Split('|', 2);
        return parts.Length == 2
            && long.TryParse(parts[0], out ticks)
            && Guid.TryParse(parts[1], out id);
    }
}
