namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Eksenin rolü: barkod okutunca SABİTLENEN eksen mi, yoksa yorumdan gelmesi
/// beklenen AÇIK eksen mi.
///
/// Sabit "Renk + Beden" adlandırması çantada/kozmetikte kırılıyordu; asıl ayrım
/// eksenin adı değil rolü. Rujda tek eksen var ve rolü <see cref="Viewer"/>.
/// </summary>
public enum AxisRole
{
    /// <summary>Satıcı ekseni — okutulunca sabitlenir (renk, koku).</summary>
    Seller = 1,

    /// <summary>İzleyici ekseni — açık kalır, yorumdan gelir (beden, numara, hacim, ton).</summary>
    Viewer = 2,
}

/// <summary>
/// Katalog kartı (model). Fotoğraf ürün seviyesinde tutulur, varyantta değil —
/// aksi halde görsel sayısı varyant sayısı kadar katlanır.
/// </summary>
public sealed class Product
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Nullable: kart açılır açılmaz, kategori seçmeden kaydedilebilmeli.</summary>
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>Lisans başına benzersiz. Otomatik üretilir (A1, A2…), elle değiştirilebilir.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Yayında değiştirilebilen VARSAYILAN fiyat; siparişe o anki fiyat damgalanır.</summary>
    public decimal DefaultPrice { get; set; }

    /// <summary>Maliyet — ürün bazlı kâr için. Kullanıcı 1a'da kartta istedi.</summary>
    public decimal? Cost { get; set; }

    public string? Axis1Name { get; set; }
    public AxisRole? Axis1Role { get; set; }
    public string? Axis2Name { get; set; }
    public AxisRole? Axis2Role { get; set; }

    // Fotoğraf — BroadcastPost deseniyle birebir (R2, presigned URL).
    public string? PhotoObjectKey { get; set; }
    public string? PhotoContentType { get; set; }
    public long? PhotoSizeBytes { get; set; }
    public int? PhotoWidth { get; set; }
    public int? PhotoHeight { get; set; }

    /// <summary>Faz 1c'de Hangfire işi dolduracak; 1a'da yalnız liste filtresi okur.</summary>
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProductVariant> Variants { get; set; } = new();
}
