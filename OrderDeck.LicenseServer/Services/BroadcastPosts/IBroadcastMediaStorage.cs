namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public interface IBroadcastMediaStorage
{
    Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default);
    Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default);
    Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    /// <summary>
    /// Önek altındaki TÜM nesneleri listeler (sayfalama uygulamanın içinde).
    /// Yetim mutabakatı için var: DB'de hiç yazılmamış anahtarlar ancak kovayı
    /// okuyarak bulunabilir.
    /// </summary>
    Task<IReadOnlyList<MediaObjectListing>> ListAsync(string prefix, CancellationToken ct = default);

    /// <summary>Toplu silme (yayıncı silmesi, KVKK). Fırlatmaz: silinemeyen her anahtar hata metniyle döner, çağıran yeniden dener.
    /// <see cref="DeleteAsync"/> R2'de hatayı yutar; silindiğinin bilinmesi gereken yer bunu kullanır.</summary>
    Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken ct = default);
}

public sealed record MediaObjectInfo(long SizeBytes, string ContentType);

/// <summary>Listeleme satırı. <paramref name="LastModified"/> ödemsiz süre
/// (grace period) kıyasında kullanılıyor — yeni yüklenmiş ama henüz
/// iliştirilmemiş nesne silinmesin diye.</summary>
public sealed record MediaObjectListing(string Key, DateTimeOffset LastModified);

/// <summary>
/// Yayın kovasındaki iki anahtar biçimi. Yazan uçlar, ürün fotoğrafı yetim süpürücüsü ve yayıncı silmesi (KVKK) öneki buradan
/// alır: ayrışsalardı silme sessizce boş liste görür, nesneler kovada kalırdı.
/// <list type="bullet">
/// <item>Yayın paylaşımı medyası <c>{lisans:D}/{paylaşım:D}/media.bin</c> — lisans kimliği TİRELİ (biçim ilk günden böyle,
/// kovada bu biçimde nesneler var; değiştirilirse eski nesneler yeni önekte görünmez).</item>
/// <item>Ürün fotoğrafı <c>{lisans:N}/products/{ürün:N}/{rastgele:N}.img</c> — tiresiz.</item>
/// </list>
/// İkisi ayrık: paylaşım anahtarının ilk parçası tireli, fotoğrafınki tiresiz kimlik; dekont (<c>payments/</c>) ve WhatsApp
/// (<c>wa/</c>) anahtarlarının ilk parçası kimlik değil.
/// </summary>
public static class BroadcastMediaKeys
{
    public static string PostPrefix(Guid licenseId) => $"{licenseId:D}/";

    public static string PostKey(Guid licenseId, Guid postId) => $"{PostPrefix(licenseId)}{postId:D}/media.bin";

    public static string ProductPhotoPrefix(Guid licenseId) => $"{licenseId:N}/products/";

    public static string ProductPhotoPrefix(Guid licenseId, Guid productId) => $"{ProductPhotoPrefix(licenseId)}{productId:N}/";

    /// <summary>Paylaşım medyası anahtarının lisansı; başka biçimde null.</summary>
    public static Guid? PostLicenseOf(string key)
    {
        var parts = key.Split('/', 3);
        return parts.Length == 3 && Guid.TryParseExact(parts[0], "D", out var id) ? id : null;
    }

    /// <summary>Ürün fotoğrafı anahtarının lisansı; başka biçimde null.</summary>
    public static Guid? ProductPhotoLicenseOf(string key)
    {
        var parts = key.Split('/', 3);
        return parts.Length == 3 && parts[1] == "products" && Guid.TryParseExact(parts[0], "N", out var id) ? id : null;
    }
}
