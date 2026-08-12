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
}

public sealed record MediaObjectInfo(long SizeBytes, string ContentType);

/// <summary>Listeleme satırı. <paramref name="LastModified"/> ödemsiz süre
/// (grace period) kıyasında kullanılıyor — yeni yüklenmiş ama henüz
/// iliştirilmemiş nesne silinmesin diye.</summary>
public sealed record MediaObjectListing(string Key, DateTimeOffset LastModified);
