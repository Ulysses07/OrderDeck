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

    /// <summary>Ürün kodu + eksen kod parçaları, "-" ile birleşik (A12-SIYA-M).</summary>
    public string VariantCode { get; set; } = string.Empty;

    /// <summary>Faz 1c'de doldurulur (Code128). 1a'da her zaman null.</summary>
    public string? Barcode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
