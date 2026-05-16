using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public sealed class FakeBroadcastMediaStorage : IBroadcastMediaStorage
{
    public sealed record UploadCall(string Key, string ContentType, long Size);

    private readonly StubBroadcastMediaStorage _inner =
        new(NullLogger<StubBroadcastMediaStorage>.Instance);

    public List<UploadCall> UploadCalls { get; } = new();

    public void Seed(string key, long size, string contentType)
        => _inner.Seed(key, size, contentType);

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        UploadCalls.Add(new UploadCall(objectKey, contentType, sizeBytes));
        return _inner.CreateUploadUrlAsync(objectKey, contentType, sizeBytes, ct);
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
        => _inner.CreateDownloadUrlAsync(objectKey, ct);

    public Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
        => _inner.HeadAsync(objectKey, ct);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        => _inner.DeleteAsync(objectKey, ct);
}
