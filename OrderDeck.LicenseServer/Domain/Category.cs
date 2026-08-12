namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Kategori ağacı (örn. Erkek &gt; Üst Giyim &gt; Tişört). Ürün ağacın herhangi
/// bir seviyesine bağlanabilir; yaprak olma zorunluluğu yok.
///
/// Derinlik <see cref="CatalogLimits.CategoryMaxDepth"/> seviye ile sınırlı —
/// "sınırsız" DEĞİL: <see cref="Path"/> seviye başına 33 karakter büyüyor ve
/// kolon 512 karakter.
/// </summary>
public sealed class Category
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid? ParentCategoryId { get; set; }
    public Category? ParentCategory { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Id tabanlı yol: kökte <c>/{id:N}/</c>, altta <c>/{parentId:N}/{id:N}/</c>.
    ///
    /// Neden id tabanlı ve neden <c>"N"</c> (küçük harf, tiresiz): alt ağaç
    /// filtresi tek <c>StartsWith</c> oluyor, recursive CTE gerekmiyor; ve yol
    /// her zaman aynı biçimde ÜRETİLDİĞİ için hiçbir yerde harf duyarsız
    /// karşılaştırma gerekmiyor → PostgreSQL göçünde davranış değişmez.
    /// İsim tabanlı yol olsaydı göçte arama sessizce değişirdi.
    /// </summary>
    public string Path { get; set; } = "/";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
