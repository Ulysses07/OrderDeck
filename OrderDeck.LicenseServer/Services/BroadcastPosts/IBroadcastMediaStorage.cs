namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public interface IBroadcastMediaStorage
{
    Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default);
    Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default);
    Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default);
    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}

public sealed record MediaObjectInfo(long SizeBytes, string ContentType);
