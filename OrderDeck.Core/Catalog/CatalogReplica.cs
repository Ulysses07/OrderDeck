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
    string? Axis1Code,
    string? Axis2Value,
    string? Axis2Code,
    string VariantCode,
    string? Barcode,
    bool IsActive,
    int SortOrder);

/// <param name="Path">Id tabanlı yol; sıralaması ağacı ata-önce dizer.</param>
public sealed record CatalogCategory(
    string Id,
    string? ParentCategoryId,
    string Name,
    string Path,
    int SortOrder,
    bool IsActive);
