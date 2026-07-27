using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Gelen WhatsApp medyasının kalıcı deposu.
///
/// <para>Meta'nın verdiği medya URL'i 5 dakikada ölür, dolayısıyla baytları
/// kendimiz saklamak zorundayız. Depo arayüzü ayrı tutuldu ki testler ağa
/// çıkmadan <see cref="InMemoryWhatsAppMediaStore"/> ile çalışabilsin.</para>
/// </summary>
public interface IWhatsAppMediaStore
{
    /// <summary>Baytları yazar ve nesne anahtarını döner.</summary>
    Task<string> SaveAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default);

    /// <summary>Panelde göstermek için kısa ömürlü indirme linki üretir.</summary>
    Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default);
}

/// <summary>
/// Dev/test deposu — R2 yapılandırılmamışken (<c>Provider=stub</c>) devreye girer.
/// Süreç belleğinde tutar; yeniden başlatmada içerik kaybolur, bu yüzden prod'da
/// kullanılmaz.
/// </summary>
public sealed class InMemoryWhatsAppMediaStore : IWhatsAppMediaStore
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _store = new();

    public Task<string> SaveAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        _store[objectKey] = (bytes, contentType);
        return Task.FromResult(objectKey);
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default)
        => Task.FromResult($"memory://wa-media/{objectKey}");

    /// <summary>Testlerin yazılan baytları doğrulaması için.</summary>
    public bool TryGet(string objectKey, out byte[] bytes)
    {
        if (_store.TryGetValue(objectKey, out var e)) { bytes = e.Bytes; return true; }
        bytes = Array.Empty<byte>();
        return false;
    }

    public int Count => _store.Count;
}
