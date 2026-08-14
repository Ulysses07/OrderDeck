namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ürünün tek bir eksen kombinasyonu. Eksensiz üründe de TEK bir satır oluşur
/// (iki değer de null) — böylece stok her zaman aynı yapıdan okunur, özel
/// durum kodu yazılmaz.
///
/// <para>Varyantın <b>kodu yoktur</b>: kimliği <see cref="Id"/>, kullanıcıya
/// görünen adı eksen değerleridir ("Siyah · M"). Yayında söylenen kod ürün +
/// satıcı ekseni seviyesinde ve ayrı bir kaynakta
/// (<c>ProductBroadcastCode</c>) yaşıyor.</para>
/// </summary>
public sealed class ProductVariant
{
    public Guid Id { get; set; }

    /// <summary>Product üzerinden türetilebilir; kiracı filtresini tek kural
    /// tutmak için denormalize edildi (spec: varyant da LicenseId alır).</summary>
    public Guid LicenseId { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Görünen değer — serbest, Türkçe karakter içerebilir ("Yeşil").</summary>
    public string? Axis1Value { get; set; }

    public string? Axis2Value { get; set; }

    /// <summary>
    /// <c>SearchNormalizer.Normalize(Axis1Value)</c>. Türetilmiş — elle YAZILMAZ,
    /// <c>LicenseDbContext.SyncDerivedColumns</c> dolduruyor.
    ///
    /// <para>Varyantın kimliği bu iki kolon. Ham değerlerin üstüne benzersizlik
    /// kurmak iki yerde sessizce bozulurdu: harf duyarlılığı veritabanının
    /// collation'ına kalırdı (SQL Server duyarsız, PostgreSQL duyarlı) ve
    /// NULL'lar indekste birbirinden farklı sayılırdı. Eksen yoksa değer
    /// <b>boş dize</b>, null değil.</para>
    /// </summary>
    public string Axis1ValueNorm { get; set; } = string.Empty;

    /// <summary><see cref="Axis1ValueNorm"/>'un ikinci eksen karşılığı.</summary>
    public string Axis2ValueNorm { get; set; } = string.Empty;

    /// <summary>
    /// Faz 1c'de doldurulur (Code128). 1a'da her zaman null.
    ///
    /// Fiziksel kimlik ve <b>değişmez</b>: yük <b>basım anında</b> buraya
    /// yazılıp dondurulur, okutma da bu alandan çözümlenir. Türetilmiş bir
    /// değerden her seferinde yeniden hesaplansaydı, girdisi değiştiği anda
    /// rafta duran ürüne yapıştırılmış etiket geçersiz olurdu.
    ///
    /// Faz 1c'de bu alan kendi indeksini ister.
    /// </summary>
    public string? Barcode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
