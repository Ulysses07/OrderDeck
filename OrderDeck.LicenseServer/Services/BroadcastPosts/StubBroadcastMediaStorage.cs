using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class StubBroadcastMediaStorage : IBroadcastMediaStorage
{
    private readonly ConcurrentDictionary<string, MediaObjectInfo> _objects = new();
    private readonly ILogger<StubBroadcastMediaStorage> _log;

    public StubBroadcastMediaStorage(ILogger<StubBroadcastMediaStorage> log) => _log = log;

    public void Seed(string objectKey, long sizeBytes, string contentType)
        => _objects[objectKey] = new MediaObjectInfo(sizeBytes, contentType);

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        _log.LogDebug("Stub upload-url: {Key} ({Size} bytes, {Mime})", objectKey, sizeBytes, contentType);
        return Task.FromResult($"https://stub.local/{objectKey}?upload=1");
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
        => Task.FromResult($"https://stub.local/{objectKey}?get=1");

    public Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
        => Task.FromResult(_objects.TryGetValue(objectKey, out var info) ? info : null);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        _objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }
}
