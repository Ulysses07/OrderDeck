using System.Globalization;

namespace OrderDeck.App.Services.Chat;

/// <summary>
/// Durum satırlarındaki saat yazımı (Instagram ve platform sunucularının ortak yardımcısı).
/// </summary>
internal static class StatusClock
{
    /// <summary>
    /// Anın, <paramref name="now"/>'ın ofsetindeki duvar saati (SS:DD). Makinenin saat dilimi değil çağıranın
    /// verdiği ofset esas alınır: saat dilimi de girdidir, sonuç makineye göre değişmez.
    /// </summary>
    internal static string Clock(DateTimeOffset at, DateTimeOffset now) =>
        at.ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// <see cref="Clock"/> + bulunma eki: "21:20'de". Eki mevcut <see cref="PaymentResultPresenter.Locative"/>
    /// verir; o, anı makinenin yerel saatine çevirip okuduğu için aynı duvar saati yerel ofsetle yeniden kurulup
    /// verilir (an doğrudan verilseydi ek, <paramref name="now"/>'ın ofsetindeki değil makinedeki saate uyardı).
    /// </summary>
    internal static string ClockWithLocative(DateTimeOffset at, DateTimeOffset now)
    {
        var wall = at.ToOffset(now.Offset).DateTime;
        var sameWallClockLocally = new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
        return Clock(at, now) + PaymentResultPresenter.Locative(sameWallClockLocally);
    }

    /// <summary><see cref="Clock"/> + ayrılma eki: "21:03'ten", "21:10'dan". Bulunma ekine "n" eklenir
    /// (-de/-da/-te/-ta → -den/-dan/-ten/-tan).</summary>
    internal static string ClockWithAblative(DateTimeOffset at, DateTimeOffset now) => ClockWithLocative(at, now) + "n";
}
