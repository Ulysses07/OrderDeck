namespace OrderDeck.Licensing.Api.Models;

/// <param name="CoverPhotoKey">
/// R2 nesne anahtarı; <b>kalıcı</b> önbellek anahtarı. Fotoğraf yoksa null.
/// </param>
/// <param name="CoverPhotoUrl">
/// Aynı nesnenin 5 dakika geçerli imzalı adresi. <b>Saklanmaz</b> — yalnız
/// bu çekme turunda indirmek için kullanılır.
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
    List<CatalogVariantPullItem> Variants);

public sealed record CatalogVariantPullItem(
    Guid Id,
    string? Axis1Value,
    string? Axis1Code,
    string? Axis2Value,
    string? Axis2Code,
    string VariantCode,
    string? Barcode,
    bool IsActive);

public sealed record CatalogCategoryPullItem(
    Guid Id,
    Guid? ParentCategoryId,
    string Name,
    string Path,
    int SortOrder,
    bool IsActive);
