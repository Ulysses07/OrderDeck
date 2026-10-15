using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Kargoya verme anının bağ kuralı — spec 2026-10-13 §7.2 (sahip kararı Y5), M13: dosya kargoya verilirken ona bağlı ve
/// ŞU ANDA iptal edilmiş siparişlerin bağı çözülür. Ürün kolide değildir; sonradan iptali geri alınırsa sipariş bağsız
/// kalır ve sonraki dosyaya girer. Ardından dosya, türetilmişse, bir kez türetilir ve tutarı o anda donar (DM7) —
/// türetmeyi çağıran yapar, kargoya vermeden ÖNCE (Shipped dosya türetilmez).
///
/// <para><b>Yalnız türetilmiş dosyada.</b> Kural türetilmiş tutara dayanır: iptal türetilmiş dosyanın tutarından zaten
/// düşmüştür, bağın çözülmesi tutarı değiştirmez. Kabul edilmiş (<c>--accept-shipment</c>, <c>AmountDerivedAt</c> boş,
/// §4.10-6) dosyanın saklanan tutarı iptalle düşmez ve o siparişi içerir; bağı çözülseydi sipariş geri alınınca
/// bağsız kalır, sonraki dosyaya girer ve aynı tutar iki dosyada sayılırdı. Kabul edilmiş dosyada bağ kalır, tutar
/// saklanan değeriyle donar (mod Off'taki kuralın aynısı, §7.4).</para>
///
/// <para>İki yazıcı aynı kuralı kullanır: <c>shipment-decision</c> komutu (§4.6, §7.3) ve açılış penceresindeki kopyanın
/// <c>shipments/sync</c> gönderimi (§7.1, §8.4). Bağ her oturum hâlinde sunucunundur (K4): yayını canlı olan sipariş de
/// çözülür; canlı sahibin aynası değişikliği devirde akıştan alır.</para>
/// </summary>
public static class ShipmentShipping
{
    /// <summary>
    /// Kargoya verilecek türetilmiş dosyalara bağlı siparişlerden şu anda iptal edilmiş olanların bağını çözer:
    /// <c>ShipmentId</c> boş, <c>ShipmentSetAt</c> = <paramref name="now"/> (sunucu saati; damga hiç silinmez, §2).
    /// Kabul edilmiş (türetilmemiş) dosyanın ve <paramref name="shipping"/>'te olmayan dosyanın siparişine dokunulmaz.
    /// Siparişin sürümünü ve <c>UpdatedAt</c>'ini çağıran ilerletir (komut kapsamı ya da uç). Bağı çözülen siparişler Id
    /// sırasıyla döner.
    /// </summary>
    /// <param name="linked">Kargoya verilecek dosyalara bağlı, izlenen siparişler.</param>
    /// <param name="shipping">Kargoya verilecek dosyalar, durumları değişmeden önceki hâlleriyle.</param>
    public static IReadOnlyList<Order> UnlinkCancelled(
        IEnumerable<Order> linked, IEnumerable<Shipment> shipping, DateTimeOffset now)
    {
        var derived = shipping.Where(s => s.AmountDerivedAt is not null).Select(s => s.Id).ToHashSet();
        var unlinked = new List<Order>();
        foreach (var order in linked.OrderBy(o => o.Id))
        {
            if (order.ShipmentId is not { } shipmentId || !derived.Contains(shipmentId) || order.CancelledAt is null)
                continue;
            order.ShipmentId = null;
            order.ShipmentSetAt = now;
            unlinked.Add(order);
        }
        return unlinked;
    }
}
