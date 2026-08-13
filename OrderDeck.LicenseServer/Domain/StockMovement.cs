namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir stok hareketinin gerekçesi. Sayı değerleri kalıcıdır (kolonda saklanır),
/// asla değiştirilmez.
/// </summary>
public enum StockMovementReason
{
    /// <summary>Yayında satış — WPF sipariş senkronundan türer.</summary>
    Sale = 1,

    /// <summary>Satışın iptali veya iadesi — satışın ters işaretlisi.</summary>
    CancelReturn = 2,

    /// <summary>Mal kabul / stok girişi — panelden elle.</summary>
    Entry = 3,

    /// <summary>Sayım düzeltmesi — sayılan ile defterin farkı kadar.</summary>
    CountAdjustment = 4,
}

/// <summary>
/// Stok defterinin tek satırı. <b>Bakiye hiçbir yerde saklanmaz</b> — bu
/// satırların işaretli toplamıdır.
///
/// Neden mutlak bakiye kolonu yok: bakiye kolonu, aynı ürüne aynı anda yazan iki
/// yol (yayın senkronu + panel girişi) olduğu anda kilit ister; kilit de yayın
/// hızını vurur. Toplam ise çakışmasız ve geçmişe dönük düzeltilebilir. Bunun
/// bedeli her okumada bir <c>SUM</c>; katalog ölçeğinde (lisans başına yüzler
/// mertebesi ürün) bu bedel önemsiz.
///
/// Satırlar <b>asla silinmez veya güncellenmez</b>. İptal, ters işaretli yeni bir
/// satırdır — defter denetlenebilir kalsın diye.
/// </summary>
public sealed class StockMovement
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>
    /// Null ise düşüm/giriş <b>ürün seviyesindedir</b>: hangi varyant olduğu
    /// bilinmiyor. Spec bunu açıkça kabul ediyor — yayında hız, kırılım
    /// doğruluğuna feda edilmez. Sonucu: "A12'den 10 sattım" doğru, "kaçı M'di"
    /// bilinmez.
    /// </summary>
    public Guid? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    /// <summary>
    /// <b>İşaretli</b> miktar: satış negatif, giriş/iade pozitif. Sıfır satır
    /// hiç yazılmaz (mutabakat sıfır farkı üretmez).
    /// </summary>
    public int Quantity { get; set; }

    public StockMovementReason Reason { get; set; }

    /// <summary>
    /// Kaynak sipariş — <see cref="StockMovementReason.Sale"/> ve
    /// <see cref="StockMovementReason.CancelReturn"/> için dolu, elle girişlerde
    /// null. FK <b>değil</b>: <c>Order</c> ile <c>StockMovement</c> aynı işlemde
    /// yazılıyor ve sipariş kimliği WPF'ten geliyor; sert bağ kurmak senkronu
    /// kırılgan yapardı. Mutabakat bu kolonu indeksten okur.
    /// </summary>
    public Guid? OrderId { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// <b>İş zamanı</b> — hareketin gerçekte olduğu an. Geçmişe dönük olabilir:
    /// WPF çevrimdışıyken satılan sipariş kendi <c>AddedAt</c> damgasıyla saatler
    /// sonra ulaşır.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// <b>Sunucuya yazılma anı</b> — monoton artar. Çekme imleci (WPF stok
    /// senkronu) bunun üstünden koşar. <see cref="OccurredAt"/> üstünden koşsaydı
    /// geç ulaşan çevrimdışı satışlar imlecin gerisinde kalıp sessizce atlanırdı.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Elle girişlerde işlemi yapan operatör; senkrondan gelenlerde null.</summary>
    public Guid? CreatedByOperatorId { get; set; }
}
