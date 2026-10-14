using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// Sipariş metninin KVKK politikası — spec 2026-10-13 §9. Metin = görünen ad (takma ad) ve sohbet yorumu. Kullanıcı
/// adı, kod ve fiyat mali kayıttır ve kalır (yerel politikayla aynı, <c>CustomerRepository.ScrubPersonalData</c>).
/// <list type="bullet">
///   <item><b>Okuma anı maskesi (her zaman):</b> asıl projeksiyonu silinmiş müşterinin siparişi akışta metinsiz gider
///   (<see cref="MasksOnRead"/>). Hiçbir şey silinmez; yalnız metnin yeni bilgisayarlara yayılması önlenir.</item>
///   <item><b>Kalıcı boşaltma (<c>SalesSync:ScrubOrderTextOnPurge</c>, varsayılan kapalı):</b> silmede sipariş
///   satırlarında (<c>ShopperPurgeService</c>) ve silmeden sonra gelen eklemede (<see cref="AppliesOnInsert"/>):
///   <see cref="Apply"/>. Geri dönüşsüz olduğu için açılması sahip kararıdır (§11).</item>
/// </list>
/// </summary>
public static class OrderTextScrub
{
    /// <summary>Akış öğesi metinsiz mi gider: asıl kayıt silinmişse, ayardan bağımsız.</summary>
    public static bool MasksOnRead(DateTimeOffset? canonicalPurgedAt) => canonicalPurgedAt is not null;

    /// <summary>
    /// Eklenen sipariş metinsiz mi yazılır: ayar açık ve asıl kayıt silinmişse. Silmeden sonra gelen geç ekleme
    /// (çevrimdışı bilgisayarın geç gönderimi, geri doldurma) metni geri getirmesin (§4.2, §4.3).
    /// </summary>
    public static bool AppliesOnInsert(bool scrubOrderTextOnPurge, DateTimeOffset? canonicalPurgedAt) =>
        scrubOrderTextOnPurge && canonicalPurgedAt is not null;

    /// <summary>
    /// Metni boşaltır. <c>TextScrubbedAt ??=</c>: boşaltma anı adli kayıttır, tekrar eden silme ilk anı korur
    /// (<see cref="WpfCustomerProjection.MarkPurged"/> ile aynı). Birim değil: <see cref="Order.SyncVersion"/>
    /// (komutların beklenen sürümü) ilerlemez.
    /// </summary>
    public static void Apply(Order order, DateTimeOffset now)
    {
        order.DisplayName = null;
        order.MessageText = "";
        order.TextScrubbedAt ??= now;
    }
}
