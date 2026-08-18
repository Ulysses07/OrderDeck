using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>Medyanın kalıcı hale getirilmiş hali. <see cref="ObjectKey"/> null ise
/// bayt indirilmedi (boyut limiti aşıldı ya da indirme başarısız) — metadata yine
/// de saklanır ki operatör mesajın bir görsel/belge olduğunu görsün.
///
/// <para><see cref="Bytes"/> YALNIZ <c>application/pdf</c> için ve yalnız 10 MB'a
/// kadar doldurulur: dekont ayrıştırıcısı baytları hemen istiyor ve depoda
/// geri-okuma yok. Her türü taşımak 100 MB'lık belge limitiyle belleği
/// patlatırdı; büyük PDF'ler R2'ye yazılır ama ayrıştırılmaz.</para></summary>
public sealed record WhatsAppMediaRef(
    string? ObjectKey, string? MimeType, long? SizeBytes, byte[]? Bytes = null);

/// <summary>
/// Gelen medya mesajlarını kalıcılaştırır: <c>GET /{media-id}</c> ile geçici URL
/// alır, baytları indirir, <see cref="IWhatsAppMediaStore"/>'a yazar.
///
/// <para><b>Neden hemen indiriyoruz:</b> Meta'nın döndüğü medya URL'i
/// <b>5 dakika</b> sonra ölüyor. Bu yüzden indirme webhook işlenirken (inbound
/// job içinde) yapılır, tembel/talep anında değil.</para>
///
/// <para><b>Neden hiç fırlatmıyor:</b> bu iş Hangfire job'ının içinde çalışıyor.
/// Medya indirilemedi diye exception atarsak job retry'a girer ve <i>mesajın
/// kendisi</i> de tekrar tekrar işlenmeye çalışılır. Medya ikincil veri; hata
/// loglanır, mesaj metinsel haliyle kaydedilir.</para>
/// </summary>
public class WhatsAppMediaDownloader
{
    /// <summary>Meta'nın tür başına kabul ettiği üst sınırlar. Aşan medyayı
    /// indirmeyiz — Meta zaten göndertmez, ama webhook'a güvenip 100 MB'ı
    /// belleğe almak istemiyoruz.</summary>
    private static readonly IReadOnlyDictionary<string, long> MaxBytesByType =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["image"] = 5L * 1024 * 1024,
            ["audio"] = 16L * 1024 * 1024,
            ["video"] = 16L * 1024 * 1024,
            ["document"] = 100L * 1024 * 1024,
            ["sticker"] = 512L * 1024,
        };

    /// <summary>Ayrıştırmak üzere baytları geri taşıdığımız PDF üst sınırı.
    /// Belge limiti (100 MB) indirme/R2 için geçerli; PdfPig'e verilecek
    /// baytlar için ayrı ve çok daha dar bir tavan gerekiyor.</summary>
    private const long MaxPdfParseBytes = 10L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> ExtByMime =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
            ["video/mp4"] = ".mp4",
            ["video/3gpp"] = ".3gp",
            ["audio/ogg"] = ".ogg",
            ["audio/mpeg"] = ".mp3",
            ["audio/mp4"] = ".m4a",
            ["audio/amr"] = ".amr",
            ["application/pdf"] = ".pdf",
        };

    private readonly HttpClient _http;
    private readonly IWhatsAppMediaStore _store;
    private readonly WhatsAppOptions _opt;
    private readonly ILogger<WhatsAppMediaDownloader> _log;

    public WhatsAppMediaDownloader(
        HttpClient http,
        IWhatsAppMediaStore store,
        IOptions<WhatsAppOptions> opt,
        ILogger<WhatsAppMediaDownloader> log)
    {
        _http = http;
        _store = store;
        _opt = opt.Value;
        _log = log;
    }

    public virtual async Task<WhatsAppMediaRef?> FetchAsync(
        string mediaId,
        string messageType,
        WhatsAppSendContext ctx,
        Guid licenseId,
        CancellationToken ct = default)
    {
        var meta = await GetMetadataAsync(mediaId, ctx, ct);
        if (meta is null) return null;

        var (url, mime, size) = meta.Value;

        if (MaxBytesByType.TryGetValue(messageType, out var max) && size > max)
        {
            _log.LogWarning(
                "WhatsApp media {MediaId} ({Type}, {Size} bayt) limiti aşıyor, indirilmedi",
                mediaId, messageType, size);
            return new WhatsAppMediaRef(null, mime, size);
        }

        var bytes = await DownloadAsync(url, mediaId, ctx, ct);
        if (bytes is null) return new WhatsAppMediaRef(null, mime, size);

        var key = BuildObjectKey(licenseId, mediaId, mime);
        try
        {
            await _store.SaveAsync(key, bytes, mime ?? "application/octet-stream", ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WhatsApp media {MediaId} R2'ye yazılamadı", mediaId);
            return new WhatsAppMediaRef(null, mime, size);
        }

        _log.LogInformation(
            "WhatsApp media saklandı: {MediaId} → {Key} ({Size} bayt)", mediaId, key, bytes.LongLength);

        // Baytları geri taşımak = PdfPig'i satır içi çalıştırmak: ayrıştırma
        // DbContext ve SQL bağlantısı tutan Hangfire iş parçacığında oluyor.
        // Gerçek bir banka dekontu bir megabaytın altında; 10 MB'ı aşan bir
        // "belge" dekont değildir, ayrıştırmaya çalışmak yalnız işçiyi ve
        // belleği meşgul eder. R2 yüklemesi ve mesaj satırı aynen kalır —
        // atlanan tek şey özet çıkarımı.
        var isPdf = string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase);
        var parseBytes = isPdf && bytes.LongLength <= MaxPdfParseBytes ? bytes : null;
        return new WhatsAppMediaRef(key, mime, bytes.LongLength, parseBytes);
    }

    private async Task<(string Url, string? Mime, long? Size)?> GetMetadataAsync(
        string mediaId, WhatsAppSendContext ctx, CancellationToken ct)
    {
        // phone_number_id opsiyonel ama Meta öneriyor: medyanın gerçekten bu
        // numaraya ait olduğunu doğrular, başka tenant'ın id'siyle çekilemez.
        var url = $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}/{mediaId}" +
                  $"?phone_number_id={Uri.EscapeDataString(ctx.PhoneNumberId)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "WhatsApp media metadata alınamadı ({Status}): {Body}", (int)resp.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("url", out var urlEl)) return null;

            var mediaUrl = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(mediaUrl)) return null;

            var mime = root.TryGetProperty("mime_type", out var mEl) ? mEl.GetString() : null;
            long? size = root.TryGetProperty("file_size", out var sEl) && sEl.TryGetInt64(out var s)
                ? s : null;

            return (mediaUrl, mime, size);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "WhatsApp media metadata isteği başarısız: {MediaId}", mediaId);
            return null;
        }
    }

    private async Task<byte[]?> DownloadAsync(
        string url, string mediaId, WhatsAppSendContext ctx, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        // Meta'nın lookaside CDN'i User-Agent'sız istekleri reddedebiliyor;
        // resmi curl örneğinde de açıkça set ediliyor.
        req.Headers.UserAgent.ParseAdd("OrderDeck/1.0");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "WhatsApp media indirilemedi ({Status}): {MediaId}", (int)resp.StatusCode, mediaId);
                return null;
            }
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "WhatsApp media indirme isteği başarısız: {MediaId}", mediaId);
            return null;
        }
    }

    /// <summary>Anahtar tenant'a ve aya göre bölünür: listeleme makul kalır,
    /// lisans bazlı silme (KVKK) tek prefix taramasıyla yapılabilir.</summary>
    private static string BuildObjectKey(Guid licenseId, string mediaId, string? mime)
    {
        var ext = mime is not null && ExtByMime.TryGetValue(mime, out var e) ? e : ".bin";
        var now = DateTimeOffset.UtcNow;
        return $"wa/{licenseId:N}/{now:yyyy}/{now:MM}/{mediaId}{ext}";
    }
}
