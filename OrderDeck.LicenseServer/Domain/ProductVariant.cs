namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ürünün tek bir eksen kombinasyonu. Eksensiz üründe de TEK bir satır oluşur
/// (iki değer de null, <see cref="VariantCode"/> = ürün kodu) — böylece stok
/// her zaman aynı yapıdan okunur, özel durum kodu yazılmaz.
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

    /// <summary>Barkoda giren ASCII parça ("YESI"). Elle düzeltilebilir.</summary>
    public string? Axis1Code { get; set; }

    public string? Axis2Value { get; set; }
    public string? Axis2Code { get; set; }

    /// <summary>
    /// Ürün kodu + eksen kod parçaları, "-" ile birleşik (A12-SIYA-M).
    ///
    /// <b>Türetilmiş</b> bir değer: tek kurucusu
    /// <c>Services.Catalog.VariantCodeBuilder</c>. Girdilerinden biri
    /// (<c>Product.Code</c>, <see cref="Axis1Code"/>, <see cref="Axis2Code"/>)
    /// değişince yeniden hesaplanır — yani zaman içinde DEĞİŞİR.
    /// İnsana gösterilen etikettir, kimlik değildir; kimlik
    /// <see cref="Barcode"/>'dur.
    /// </summary>
    public string VariantCode { get; set; } = string.Empty;

    /// <summary>
    /// Faz 1c'de doldurulur (Code128). 1a'da her zaman null.
    ///
    /// Fiziksel kimlik ve <b>değişmez</b>. Spec'e göre yük, varyant kodunun
    /// kendisidir — ama <b>basım anındaki</b> hâlinin kopyası olarak buraya
    /// yazılır ve bir daha değişmez. Gerekçe: <see cref="VariantCode"/>
    /// türetilmiş ve ürün kodu değişince yenileniyor; yük her seferinde yeniden
    /// türetilseydi rafta duran ürüne yapıştırılmış etiket geçersiz olurdu.
    ///
    /// Faz 1c sonucu: okutma <b>bu alandan</b> çözümlenmeli
    /// (<see cref="VariantCode"/> üzerinden değil) ve bu alan kendi indeksini
    /// ister.
    /// </summary>
    public string? Barcode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
