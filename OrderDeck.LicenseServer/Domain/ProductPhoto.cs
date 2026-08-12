namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ürün galerisindeki tek fotoğraf. Fotoğraf <b>ürün</b> düzeyinde tutulur,
/// varyantta değil — aksi halde görsel sayısı varyant sayısı kadar katlanır.
///
/// <b>Kapak = en küçük <see cref="SortOrder"/>.</b> Ayrı bir <c>IsCover</c>
/// bayrağı bilerek yok: iki ayrı doğruluk kaynağı olsaydı sıralama ile bayrak
/// er geç ayrışır ve "kapak hangisi" sorusunun iki farklı cevabı olurdu.
/// </summary>
public sealed class ProductPhoto
{
    public Guid Id { get; set; }

    /// <summary>
    /// Product üzerinden türetilebilir; kiracı filtresini tek kural tutmak için
    /// denormalize edildi. <b>FK yok</b> — <c>ProductVariant.LicenseId</c> ile
    /// aynı desen. Hem Product hem Photo lisansa cascade ile bağlansaydı SQL
    /// Server çoklu cascade yolu hatası verirdi; ürün zaten lisansla birlikte
    /// gidiyor, fotoğraf da ürünle.
    /// </summary>
    public Guid LicenseId { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>R2 anahtarı: <c>{licenseId:N}/products/{productId:N}/{guid:N}.img</c></summary>
    public string ObjectKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>0 tabanlı sıra. Sürükle-bırak bunu yeniden yazar.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
