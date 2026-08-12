namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Kategori ağacının id tabanlı yol hesapları. Hepsi saf fonksiyon — DB'ye
/// dokunmaz, controller sonucu uygular.
///
/// Yol biçimi: <c>/{id:N}/{childId:N}/</c>. Küçük harf, tiresiz ve HER ZAMAN
/// buradan üretiliyor → hiçbir karşılaştırma harf duyarsız olmak zorunda değil,
/// PostgreSQL göçünde davranış değişmez.
/// </summary>
public static class CategoryPathService
{
    public const string RootPath = "/";

    public static string BuildPath(string? parentPath, Guid id)
        => (parentPath ?? RootPath) + id.ToString("N") + "/";

    /// <summary>
    /// Bir kategori kendi alt ağacına taşınamaz. Yeni ebeveynin yolu, taşınan
    /// kategorinin yoluyla başlıyorsa bu bir döngüdür (kategorinin kendisi de
    /// dahil — kendi altına taşımak da yasak).
    /// </summary>
    public static bool WouldCreateCycle(string movedPath, string? newParentPath)
        => newParentPath is not null
           && newParentPath.StartsWith(movedPath, StringComparison.Ordinal);

    /// <summary>
    /// Alt ağaçtaki bir düğümün yolunu yeni ebeveyne göre yeniden yazar.
    /// <paramref name="path"/> mutlaka <paramref name="oldPrefix"/> ile
    /// başlamalıdır (çağıran zaten öyle filtreliyor).
    /// </summary>
    public static string Rebase(string path, string oldPrefix, string newPrefix)
        => newPrefix + path[oldPrefix.Length..];
}
