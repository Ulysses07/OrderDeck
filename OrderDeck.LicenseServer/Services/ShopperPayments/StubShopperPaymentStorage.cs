using System.Collections.Concurrent;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Services.ShopperPayments;

/// <summary>Test/dev stub. In-memory bytes; download URL = stub://payments/{key}.</summary>
public sealed class StubShopperPaymentStorage : IShopperPaymentStorage
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _store = new();

    public Task<string> UploadAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        _store[objectKey] = (bytes, contentType);
        return Task.FromResult(objectKey);
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default)
        => Task.FromResult($"stub://payments/{objectKey}");

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        _store.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListLicenseObjectsAsync(Guid licenseId, CancellationToken ct = default)
    {
        var prefix = ShopperPaymentKeys.LicensePrefix(licenseId);
        return Task.FromResult<IReadOnlyList<string>>(
            _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
    }

    public Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        foreach (var key in keys) _store.TryRemove(key, out _);
        return Task.FromResult<IReadOnlyList<MediaDeleteFailure>>([]);
    }

    /// <summary>Test inspection only.</summary>
    public bool Contains(string objectKey) => _store.ContainsKey(objectKey);

    /// <summary>Test inspection only. Yetim nesne iddiaları bunun üzerinden ölçülür.</summary>
    public int Count => _store.Count;

    /// <summary>Test inspection only.</summary>
    public byte[]? GetBytes(string objectKey)
        => _store.TryGetValue(objectKey, out var v) ? v.Bytes : null;
}
