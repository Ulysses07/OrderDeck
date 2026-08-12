using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class StubBroadcastMediaStorage : IBroadcastMediaStorage
{
    /// <summary>Nesnenin içeriği + zaman damgası. Damga <c>ListAsync</c>'in
    /// ödemsiz süre kıyası için gerekli; <c>MediaObjectInfo</c>'ya eklemek yerine
    /// burada sarmalanıyor ki HeadAsync sözleşmesi değişmesin.</summary>
    private sealed record StoredObject(MediaObjectInfo Info, DateTimeOffset LastModified);

    private readonly ConcurrentDictionary<string, StoredObject> _objects = new();
    private readonly ILogger<StubBroadcastMediaStorage> _log;

    public StubBroadcastMediaStorage(ILogger<StubBroadcastMediaStorage> log) => _log = log;

    public void Seed(string objectKey, long sizeBytes, string contentType,
        DateTimeOffset? lastModified = null)
        => _objects[objectKey] = new StoredObject(
            new MediaObjectInfo(sizeBytes, contentType),
            lastModified ?? DateTimeOffset.UtcNow);

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        _log.LogDebug("Stub upload-url: {Key} ({Size} bytes, {Mime})", objectKey, sizeBytes, contentType);
        return Task.FromResult($"https://stub.local/{objectKey}?upload=1");
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
        => Task.FromResult($"https://stub.local/{objectKey}?get=1");

    public Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
        => Task.FromResult(_objects.TryGetValue(objectKey, out var stored) ? stored.Info : null);

    public Task<IReadOnlyList<MediaObjectListing>> ListAsync(
        string prefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MediaObjectListing>>(
            _objects
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new MediaObjectListing(kv.Key, kv.Value.LastModified))
                .ToList());

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        _objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }
}
