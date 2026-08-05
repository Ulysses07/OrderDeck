using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Bağlı Facebook oturumunda Instagram izinlerinin verilip verilmediğini
/// kontrol eder.
///
/// <para><b>Neden gerekli:</b> IG izinleri Facebook Login for Business
/// yapılandırmasına 2026-08-05'te eklendi. Daha önce bağlanmış operatörlerin
/// token'ında bu izinler yok ve Graph, izin eksikliğini <b>hata olarak değil</b>
/// alanı yanıttan düşürerek bildiriyor — yani resmi API sessizce hiç yorum
/// getirmez. Ayarlar'da açık bir "yeniden bağlan" uyarısı olmazsa operatör
/// bunu asla anlayamaz.</para>
/// </summary>
public sealed class InstagramPermissionProbe
{
    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";

    /// <summary>Yorum okumak için ikisi de şart: <c>instagram_basic</c>
    /// olmadan hesap uçları kapalı, <c>instagram_manage_comments</c> olmadan
    /// yorumlarda <c>username</c> gelmiyor (Meta, 27 Ağustos 2024).</summary>
    private static readonly string[] Required =
        { "instagram_basic", "instagram_manage_comments" };

    private readonly HttpClient _http;
    private readonly ILogger<InstagramPermissionProbe> _log;

    public InstagramPermissionProbe(HttpClient http, ILogger<InstagramPermissionProbe> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// <c>GET /me/permissions</c> — <paramref name="userAccessToken"/>
    /// <b>kullanıcı</b> token'ı olmalı. Ağ hatasında null döner ("bilinmiyor"),
    /// böylece geçici bir kesinti yanlışlıkla "izin yok" uyarısına dönüşmez.
    /// </summary>
    public async Task<bool?> HasInstagramPermissionsAsync(
        string userAccessToken, CancellationToken ct = default)
    {
        var url = $"{GraphBase}/me/permissions" +
                  $"?access_token={Uri.EscapeDataString(userAccessToken)}";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // 400 + code 190 (token süresi dolmuş) da geçerli bir cevap: izin yok.
            if (!resp.IsSuccessStatusCode && body.Contains("\"error\"", StringComparison.Ordinal))
                return HasInstagramPermissions(body);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("[InstagramPermissionProbe] {Status}", (int)resp.StatusCode);
                return null;
            }
            return HasInstagramPermissions(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[InstagramPermissionProbe] permission check failed");
            return null;
        }
    }

    /// <summary>Saf ayrıştırıcı — <c>{"data":[{"permission":..,"status":..}]}</c>.
    /// Gerekli izinlerin <b>hepsi</b> <c>granted</c> ise true.</summary>
    public static bool HasInstagramPermissions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var required in Required)
            {
                bool found = false;
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = item.TryGetProperty("permission", out var p) ? p.GetString() : null;
                    if (!string.Equals(name, required, StringComparison.Ordinal)) continue;
                    var status = item.TryGetProperty("status", out var s) ? s.GetString() : null;
                    found = string.Equals(status, "granted", StringComparison.Ordinal);
                    break;
                }
                if (!found) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
