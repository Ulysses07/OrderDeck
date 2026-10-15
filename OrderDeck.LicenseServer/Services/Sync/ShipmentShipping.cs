using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Kargoya verme anının bağ kuralı — spec 2026-10-13 §7.2 (sahip kararı Y5), M13: dosya kargoya verilirken ona bağlı ve
/// ŞU ANDA iptal edilmiş siparişlerin bağı çözülür. Ürün kolide değildir; sonradan iptali geri alınırsa sipariş bağsız
/// kalır ve sonraki dosyaya girer. Ardından dosya, türetilmişse, bir kez türetilir ve tutarı o anda donar (DM7) —
/// türetmeyi çağıran yapar, kargoya vermeden ÖNCE (Shipped dosya türetilmez).
///
/// <para>İki yazıcı aynı kuralı kullanır: <c>shipment-decision</c> komutu (§4.6, §7.3) ve açılış penceresindeki kopyanın
/// <c>shipments/sync</c> gönderimi (§7.1, §8.4). Bağ her oturum hâlinde sunucunundur (K4): yayını canlı olan sipariş de
/// çözülür; canlı sahibin aynası değişikliği devirde akıştan alır.</para>
/// </summary>
public static class ShipmentShipping
{
    /// <summary>
    /// Bağlı siparişlerden şu anda iptal edilmiş olanların bağını çözer: <c>ShipmentId</c> boş, <c>ShipmentSetAt</c> =
    /// <paramref name="now"/> (sunucu saati; damga hiç silinmez, §2). Siparişin sürümünü ve <c>UpdatedAt</c>'ini çağıran
    /// ilerletir (komut kapsamı ya da uç). Bağı çözülen siparişler Id sırasıyla döner.
    /// </summary>
    /// <param name="linked">Kargoya verilecek dosyalara bağlı, izlenen siparişler.</param>
    public static IReadOnlyList<Order> UnlinkCancelled(IEnumerable<Order> linked, DateTimeOffset now)
    {
        var unlinked = new List<Order>();
        foreach (var order in linked.OrderBy(o => o.Id))
        {
            if (order.ShipmentId is null || order.CancelledAt is null) continue;
            order.ShipmentId = null;
            order.ShipmentSetAt = now;
            unlinked.Add(order);
        }
        return unlinked;
    }
}
