namespace OrderDeck.Core.Catalog;

/// <summary>
/// Sunucudaki katalog ürününün yerel salt-okunur kopyası.
/// </summary>
/// <param name="Id">Sunucudaki GUID, "N" biçiminde (32 hane, tiresiz).</param>
/// <param name="Code">Kanonik ürün kodu (sunucu böyle yazıyor).</param>
/// <param name="CodeNormalized">
/// <c>SearchNormalizer.Normalize(Code)</c>. Aranan iğne de aynı fonksiyondan
/// geçtiği için operatör "güzel elbise" yazdığında "GUZEL ELBISE" bulunur.
/// </param>
/// <param name="CoverPhotoKey">R2 nesne anahtarı; fotoğraf önbelleğinin anahtarı.</param>
/// <param name="UpdatedAt">Unix saniye.</param>
public sealed record CatalogProduct(
    string Id,
    string? CategoryId,
    string Code,
    string CodeNormalized,
    string Name,
    decimal DefaultPrice,
    string? ShelfLocation,
    string? Axis1Name,
    int? Axis1Role,
    string? Axis2Name,
    int? Axis2Role,
    string? CoverPhotoKey,
    long UpdatedAt);

/// <summary>Bir ürünün tek varyantı. Eksensiz üründe de tam bir varyant vardır.</summary>
public sealed record CatalogVariant(
    string Id,
    string ProductId,
    string? Axis1Value,
    string? Axis2Value,
    string? Barcode,
    bool IsActive,
    int SortOrder);

/// <summary>
/// Operatörün yayında kullandığı kod. Stok kodundan (<c>SK00001</c>) FARKLI:
/// stok kodunu sunucu üretir ve değişmez; yayın kodunu operatör panelden verir,
/// ürün + <b>satıcı ekseni değeri</b> düzeyindedir ve kalıcı olarak rezervedir.
/// </summary>
/// <param name="SellerAxisValue">
/// Kodun işaret ettiği satıcı ekseni değeri ("Siyah"). Ürünün satıcı ekseni
/// yoksa null — kod o zaman ürünün tamamını gösterir.
/// </param>
/// <param name="CodeNormalized">
/// Sunucunun ürettiği normalize biçim. <b>Yerelde yeniden hesaplanmaz</b>;
/// arama iğnesi <c>SearchNormalizer.Normalize</c> ile üretilip bununla
/// karşılaştırılır.
/// </param>
/// <param name="SortOrder">Sunucunun gönderdiği dizideki konum (en yeni önce).</param>
public sealed record CatalogBroadcastCode(
    string ProductId,
    string? SellerAxisValue,
    string Code,
    string CodeNormalized,
    long CreatedAt,
    int SortOrder);

/// <param name="Path">Id tabanlı yol; sıralaması ağacı ata-önce dizer.</param>
public sealed record CatalogCategory(
    string Id,
    string? ParentCategoryId,
    string Name,
    string Path,
    int SortOrder,
    bool IsActive);
