using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// In-memory media storage stub for tests. Wraps <see cref="StubBroadcastMediaStorage"/>
/// and records UploadCalls for assertion.
///
/// IMPORTANT: This is shared via <c>IClassFixture&lt;ApiFactory&gt;</c>; UploadCalls
/// accumulates across tests in the same class. Either use order-independent
/// assertions (e.g. <c>Contain</c> with predicate, not <c>HaveCount</c>) or call
/// <see cref="Reset"/> in test constructor.
/// </summary>
public sealed class FakeBroadcastMediaStorage : IBroadcastMediaStorage
{
    public sealed record UploadCall(string Key, string ContentType, long Size);

    private readonly StubBroadcastMediaStorage _inner =
        new(NullLogger<StubBroadcastMediaStorage>.Instance);

    public List<UploadCall> UploadCalls { get; } = new();

    /// <summary>Silinen anahtarlar — yetim temizliği ve inline silme testleri için.</summary>
    public List<string> DeleteCalls { get; } = new();

    public void Seed(string key, long size, string contentType,
        DateTimeOffset? lastModified = null)
        => _inner.Seed(key, size, contentType, lastModified);

    /// <summary>Test izolasyonu için kayıtları sıfırla.</summary>
    public void Reset()
    {
        UploadCalls.Clear();
        DeleteCalls.Clear();
    }

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        UploadCalls.Add(new UploadCall(objectKey, contentType, sizeBytes));
        return _inner.CreateUploadUrlAsync(objectKey, contentType, sizeBytes, ct);
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
        => _inner.CreateDownloadUrlAsync(objectKey, ct);

    public Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
        => _inner.HeadAsync(objectKey, ct);

    public Task<IReadOnlyList<MediaObjectListing>> ListAsync(
        string prefix, CancellationToken ct = default)
        => _inner.ListAsync(prefix, ct);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        DeleteCalls.Add(objectKey);
        return _inner.DeleteAsync(objectKey, ct);
    }
}
