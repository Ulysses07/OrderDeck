using System.Collections.Concurrent;

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

    /// <summary>Test inspection only.</summary>
    public bool Contains(string objectKey) => _store.ContainsKey(objectKey);

    /// <summary>Test inspection only.</summary>
    public byte[]? GetBytes(string objectKey)
        => _store.TryGetValue(objectKey, out var v) ? v.Bytes : null;
}
