using System.Collections.Concurrent;
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

    /// <summary>Silinemeyen anahtarlar (yayıncı silmesi, KVKK): toplu silme hata metniyle döndürür; tek silme
    /// <see cref="R2BroadcastMediaStorage.DeleteAsync"/> gibi hatayı YUTAR, nesne kalır.</summary>
    public ConcurrentDictionary<string, byte> FailingKeys { get; } = new();

    /// <summary>Listelenemeyen önekler (R2 kesintisi taklidi): bunlarla örtüşen önekte <see cref="ListAsync"/> fırlatır.</summary>
    public ConcurrentDictionary<string, byte> UnlistablePrefixes { get; } = new();

    public void Seed(string key, long size, string contentType,
        DateTimeOffset? lastModified = null)
        => _inner.Seed(key, size, contentType, lastModified);

    /// <summary>Nesne kovada mı (test denetimi).</summary>
    public bool Contains(string key) => _inner.HeadAsync(key).GetAwaiter().GetResult() is not null;

    /// <summary>Test izolasyonu için kayıtları sıfırla.</summary>
    public void Reset()
    {
        UploadCalls.Clear();
        DeleteCalls.Clear();
        FailingKeys.Clear();
        UnlistablePrefixes.Clear();
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
        => UnlistablePrefixes.Keys.Any(p => prefix.StartsWith(p, StringComparison.Ordinal)
                || p.StartsWith(prefix, StringComparison.Ordinal))
            ? throw new IOException("kova listelenemiyor")
            : _inner.ListAsync(prefix, ct);

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        DeleteCalls.Add(objectKey);
        return FailingKeys.ContainsKey(objectKey) ? Task.CompletedTask : _inner.DeleteAsync(objectKey, ct);
    }

    public async Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys,
        CancellationToken ct = default)
    {
        var failures = new List<MediaDeleteFailure>();
        foreach (var key in keys)
        {
            DeleteCalls.Add(key);
            if (FailingKeys.ContainsKey(key)) failures.Add(new MediaDeleteFailure(key, "AccessDenied: nesne kilitli"));
            else await _inner.DeleteAsync(key, ct);
        }
        return failures;
    }
}
