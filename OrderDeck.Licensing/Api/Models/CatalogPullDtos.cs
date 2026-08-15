namespace OrderDeck.Licensing.Api.Models;

/// <summary>Sunucudan çekilen tek bir katalog ürünü (tel modeli).</summary>
/// <param name="CoverPhotoKey">
/// R2 nesne anahtarı; <b>kalıcı</b> önbellek anahtarı. Fotoğraf yoksa null.
/// </param>
/// <param name="CoverPhotoUrl">
/// Aynı nesnenin 5 dakika geçerli imzalı adresi. <b>Saklanmaz</b> — yalnız
/// bu çekme turunda indirmek için kullanılır.
/// <para><c>CoverPhotoKey</c> dolu iken bunun null gelmesi <b>meşru bir
/// durumdur</b>: sunucu imzalama başarısız olursa sayfayı düşürmez, URL'i null
/// bırakıp anahtarı yine gönderir. Önbellek bu durumda mevcut yerel dosyayı
/// KORUMALI, "fotoğraf silinmiş" sayıp atmamalıdır.</para>
/// </param>
/// <param name="Variants">
/// Ürünün varyantları. <b>Dizideki konum sıralamanın kendisidir</b> — sunucu
/// normalize eksen değerlerine göre <b>ordinal</b> sıralayıp gönderiyor
/// (sıralama sunucuda bellekte yapılıyor; veritabanı collation'ına bağlı
/// DEĞİL). Yerelde yeniden SIRALANMAZ, dizi indeksi <c>SortOrder</c> olarak
/// taşınır (bkz. <c>025_catalog_replica.sql</c>).
/// </param>
/// <param name="BroadcastCodes">
/// Ürüne verilmiş yayın kodları; sunucu <b>en yeni önce</b> sıralayıp gönderiyor.
/// Dizideki konum sıralamanın kendisidir (bkz. <c>027_catalog_broadcast_codes.sql</c>).
/// </param>
public sealed record CatalogProductPullItem(
    Guid Id,
    Guid? CategoryId,
    string Code,
    string Name,
    string NameSearch,
    decimal DefaultPrice,
    string? ShelfLocation,
    string? Axis1Name,
    int? Axis1Role,
    string? Axis2Name,
    int? Axis2Role,
    DateTimeOffset UpdatedAt,
    string? CoverPhotoKey,
    string? CoverPhotoUrl,
    List<CatalogVariantPullItem> Variants,
    List<CatalogBroadcastCodePullItem> BroadcastCodes);

/// <summary>Bir ürünün tek varyantı; sırası taşıyıcı dizinin konumundan gelir.</summary>
public sealed record CatalogVariantPullItem(
    Guid Id,
    string? Axis1Value,
    string? Axis2Value,
    string? Barcode,
    bool IsActive);

/// <summary>
/// Ürüne verilmiş bir yayın kodu. <c>SellerAxisValue</c> null ise kod ürünün
/// tamamını gösterir (ürünün satıcı ekseni yok).
/// </summary>
public sealed record CatalogBroadcastCodePullItem(
    string? SellerAxisValue,
    string Code,
    string CodeNormalized,
    DateTimeOffset CreatedAt);

/// <summary>Kategori ağacının tek düğümü; <c>Path</c> sırası ata-önce dizer.</summary>
public sealed record CatalogCategoryPullItem(
    Guid Id,
    Guid? ParentCategoryId,
    string Name,
    string Path,
    int SortOrder,
    bool IsActive);
