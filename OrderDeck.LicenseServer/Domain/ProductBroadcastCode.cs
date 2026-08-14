namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir ürünün canlı yayında duyurulan kodu. Ürün kodundan (<c>SK00001</c>)
/// ayrı bir kavram: stok kodu sistemin, yayın kodu operatörün.
///
/// <para><b>Neden ayrı tablo:</b> kardinalitesi varyantla aynı değil. "ATEŞ"
/// = ürün + satıcı ekseni değeri; altında N varyant satırı durur (Siyah·S,
/// Siyah·M, Siyah·L). <c>ProductVariant</c> üstünde bir kolon olsaydı aynı kod
/// N satıra kopyalanır ve benzersizlik kurulamazdı.</para>
///
/// <para><b>Neden satırlar silinmiyor:</b> bir kod bir daha ASLA
/// kullanılamaz — ürün arşivlense, kod değiştirilse bile eski satır durur ve
/// kodu rezerve tutar. Sebebi canlı yayının kendisi: izleyici eski bir yayın
/// videosundaki kodu bugün yoruma yazabilir; kod başka bir ürüne devredilmiş
/// olsaydı sipariş yanlış ürüne düşerdi. Kod değişikliği bu yüzden
/// <b>güncelleme değil, yeni satır</b>; "güncel" olan en yeni
/// <see cref="CreatedAt"/>.</para>
/// </summary>
public sealed class ProductBroadcastCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Kiracı ayracı. <b>Bilerek yalnız skaler</b> — <c>License</c> gezinme
    /// özelliği ve ilişkisi YOK. İlişki kurulsaydı SQL Server iki cascade yolu
    /// görürdü (License→Product→BroadcastCode ve License→BroadcastCode) ve
    /// göç "multiple cascade paths" hatasıyla düşerdi.
    /// <c>ProductVariant</c> ve <c>ProductPhoto</c> aynı kalıpta.
    /// </summary>
    public Guid LicenseId { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>
    /// Kodun bağlandığı satıcı ekseni değeri (örn. "Siyah"). Ürünün satıcı
    /// ekseni yoksa <c>null</c> — kod o zaman ürünün tamamını gösterir.
    /// Ham hâli saklanır; panelde bu metin gösterilecek.
    /// </summary>
    public string? SellerAxisValue { get; set; }

    /// <summary>Operatörün yazdığı kod, kırpılmış ham hâli (görüntü için).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// <c>SearchNormalizer.Normalize(Code)</c>. Türetilmiş — elle YAZILMAZ,
    /// <c>LicenseDbContext.SyncDerivedColumns</c> dolduruyor. Benzersizlik
    /// indeksi ve canlı yorum eşleştirmesi bunun üstünde çalışır.
    /// </summary>
    public string CodeNormalized { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
