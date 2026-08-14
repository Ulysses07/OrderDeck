using System.ComponentModel.DataAnnotations.Schema;

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

    /// <summary>
    /// Stok kodu. Lisans başına benzersiz, <b>sistem üretir</b>
    /// (<c>SK00001</c>, <c>SK00002</c>…) ve bir daha değişmez — elle
    /// düzenlenemez. Yayında söylenen kod bu DEĞİL; o
    /// <see cref="ProductBroadcastCode"/>.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <b>Türetilmiş</b> arama kolonu; tek kaynağı
    /// <c>SearchNormalizer.Normalize(Name)</c>. Elle atanmaz —
    /// <c>LicenseDbContext.SaveChanges</c> her kayıtta yeniden hesaplar.
    ///
    /// Neden var: <c>Name</c> üstünden arama, büyük/küçük harf duyarlılığını
    /// veritabanının collation'ına bırakır (SQL Server duyarsız, PostgreSQL
    /// duyarlı) — yani göç günü arama sessizce bozulurdu. Arama bu kolonun
    /// üstünden koşuyor ve iğne de aynı fonksiyondan geçiyor, böylece davranış
    /// veritabanından bağımsız.
    /// </summary>
    public string NameSearch { get; set; } = string.Empty;

    /// <summary>Yayında değiştirilebilen VARSAYILAN fiyat; siparişe o anki fiyat damgalanır.</summary>
    public decimal DefaultPrice { get; set; }

    /// <summary>Maliyet — ürün bazlı kâr için. Kullanıcı 1a'da kartta istedi.</summary>
    public decimal? Cost { get; set; }

    /// <summary>
    /// Raf/konum. <b>Ürün düzeyinde</b>, varyant düzeyinde değil: aynı ürünün
    /// tüm varyantları pratikte aynı yerde duruyor. Boş bırakılabilir.
    /// </summary>
    public string? ShelfLocation { get; set; }

    public string? Axis1Name { get; set; }
    public AxisRole? Axis1Role { get; set; }
    public string? Axis2Name { get; set; }
    public AxisRole? Axis2Role { get; set; }

    /// <summary>Faz 1c'de Hangfire işi dolduracak; 1a'da yalnız liste filtresi okur.</summary>
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Galeri. Kapak = en küçük <c>SortOrder</c>.</summary>
    public List<ProductPhoto> Photos { get; set; } = new();

    public List<ProductVariant> Variants { get; set; } = new();

    /// <summary>
    /// Bu ürüne verilmiş yayın kodları — emeklileri dahil (satır silinmiyor).
    /// "Güncel" kod, satıcı ekseni değeri başına en yeni <c>CreatedAt</c>.
    /// </summary>
    public List<ProductBroadcastCode> BroadcastCodes { get; set; } = new();

    /// <summary>
    /// Satıcı ekseninin sırası: 1, 2 ya da 0 (satıcı ekseni yok). Satıcı
    /// ekseni barkot okutmayla sabitlenen eksendir; yayın kodu ona bağlanır.
    /// Türetme domain'de duruyor çünkü hangi eksenin satıcı ekseni olduğu
    /// <b>ürünün kendi bilgisi</b>; bir controller'a kopyalansaydı eksen
    /// kuralı değiştiği gün kopya sessizce ayrışırdı.
    /// </summary>
    [NotMapped]
    public int SellerAxis =>
        Axis1Name is not null && Axis1Role == AxisRole.Seller ? 1
        : Axis2Name is not null && Axis2Role == AxisRole.Seller ? 2
        : 0;

    /// <summary>Varyantın satıcı ekseni değeri; satıcı ekseni yoksa null.</summary>
    public string? SellerAxisValueOf(ProductVariant variant) =>
        SellerAxis switch
        {
            1 => variant.Axis1Value,
            2 => variant.Axis2Value,
            _ => null,
        };
}
