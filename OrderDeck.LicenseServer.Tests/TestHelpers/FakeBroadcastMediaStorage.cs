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

    public void Seed(string key, long size, string contentType)
        => _inner.Seed(key, size, contentType);

    /// <summary>Test izolasyonu için UploadCalls kaydını sıfırla.</summary>
    public void Reset() => UploadCalls.Clear();

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
