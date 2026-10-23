using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

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

    /// <summary>Tek nesne siler; yayıncı silmesinde silinemeyip kuyruğa düşen WhatsApp anahtarını
    /// <c>OrphanedMediaCleanupJob</c> bununla yeniden dener.</summary>
    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    /// <summary>Lisansın WhatsApp önekindeki (<see cref="WhatsAppMediaKeys.LicensePrefix"/>) TÜM anahtarlar; sayfalama içeride.
    /// Yayıncı silmesi (KVKK) için.</summary>
    Task<IReadOnlyList<string>> ListLicenseObjectsAsync(Guid licenseId, CancellationToken ct = default);

    /// <summary>Toplu silme. Fırlatmaz: silinemeyen her anahtar hata metniyle döner, çağıran kuyruğa yazar.</summary>
    Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken ct = default);
}

/// <summary>WhatsApp medya anahtarı <c>wa/{lisans:N}/{yyyy}/{MM}/{medya}{uzantı}</c>. Yazan
/// (<see cref="WhatsAppMediaDownloader"/>) ve yayıncı silmesi öneki buradan alır.</summary>
public static class WhatsAppMediaKeys
{
    /// <summary>Kovadaki bütün WhatsApp nesnelerinin kökü; dekontlar ve yayın medyası aynı kovada başka öneklerde.</summary>
    public const string Root = "wa/";

    public static string LicensePrefix(Guid licenseId) => $"{Root}{licenseId:N}/";
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

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        _store.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListLicenseObjectsAsync(Guid licenseId, CancellationToken ct = default)
    {
        var prefix = WhatsAppMediaKeys.LicensePrefix(licenseId);
        return Task.FromResult<IReadOnlyList<string>>(
            _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
    }

    public Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        foreach (var key in keys) _store.TryRemove(key, out _);
        return Task.FromResult<IReadOnlyList<MediaDeleteFailure>>([]);
    }

    /// <summary>Testlerin yazılan baytları doğrulaması için.</summary>
    public bool TryGet(string objectKey, out byte[] bytes)
    {
        if (_store.TryGetValue(objectKey, out var e)) { bytes = e.Bytes; return true; }
        bytes = Array.Empty<byte>();
        return false;
    }

    public int Count => _store.Count;
}
