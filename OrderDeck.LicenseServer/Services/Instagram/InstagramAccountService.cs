using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Facebook;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// FB OAuth exchange'inden çağrılır (FacebookOAuthController). Müşterinin
/// IntakeFormConfig.InstagramDmBotEnabled bayrağı AÇIKSA: uzun ömürlü kullanıcı
/// token'ından sayfaları çeker, IG professional hesabı bağlı ilk sayfayı seçer,
/// Page token'ı şifreli saklar ve sayfayı live_comments webhook'una abone eder.
/// Bayrak kapalıysa HİÇBİR ŞEY yapmaz — exchange'in "token saklamaz" sözü
/// varsayılan davranış olarak korunur.
///
/// <para>Hata exchange'i DÜŞÜRMEZ: masaüstü Facebook bağlantısı bot yüzünden
/// kırılamaz. Çağıran try/catch'ler, biz de kendi içimizde loglarız.</para>
/// </summary>
public sealed class InstagramAccountService
{
    public const string ProtectorPurpose = "InstagramAccount.PageToken.v1";

    private readonly LicenseDbContext _db;
    private readonly HttpClient _http;
    private readonly FacebookOptions _fb;
    private readonly IDataProtector _protector;
    private readonly ILogger<InstagramAccountService> _log;

    public InstagramAccountService(
        LicenseDbContext db, HttpClient http, IOptions<FacebookOptions> fb,
        IDataProtectionProvider protection, ILogger<InstagramAccountService> log)
    {
        _db = db;
        _http = http;
        _fb = fb.Value;
        _protector = protection.CreateProtector(ProtectorPurpose);
        _log = log;
    }

    public string? UnprotectPageToken(InstagramAccount acc)
    {
        try { return _protector.Unprotect(acc.PageTokenProtected); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Instagram Page token çözülemedi — hesap={IgUserId}", acc.IgUserId);
            return null;
        }
    }

    public async Task TryConnectAsync(Guid customerId, string longLivedUserToken, CancellationToken ct)
    {
        var config = await _db.Set<IntakeFormConfig>()
            .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.InstagramDmBotEnabled, ct);
        if (config is null) return; // opt-in yok — varsayılan: sakla-MA

        var now = DateTimeOffset.UtcNow;
        var license = await _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderByDescending(l => l.IssuedAt)
            .FirstOrDefaultAsync(ct);
        if (license is null)
        {
            _log.LogWarning("IG DM botu açık ama aktif lisans yok — customer={CustomerId}", customerId);
            return;
        }

        var root = $"{_fb.GraphBaseUrl.TrimEnd('/')}/{_fb.GraphApiVersion}";

        using var pagesReq = new HttpRequestMessage(HttpMethod.Get,
            $"{root}/me/accounts?fields=id,access_token,instagram_business_account%7Bid,username%7D");
        pagesReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", longLivedUserToken);
        using var pagesRes = await _http.SendAsync(pagesReq, ct);
        if (!pagesRes.IsSuccessStatusCode)
        {
            _log.LogWarning("IG bağlama: /me/accounts düştü — status={Status}", (int)pagesRes.StatusCode);
            return;
        }

        using var doc = JsonDocument.Parse(await pagesRes.Content.ReadAsStringAsync(ct));
        string? pageId = null, pageToken = null, igUserId = null, igUsername = null;
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var page in data.EnumerateArray())
            {
                if (!page.TryGetProperty("instagram_business_account", out var ig)) continue;
                pageId = page.GetProperty("id").GetString();
                pageToken = page.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                igUserId = ig.GetProperty("id").GetString();
                igUsername = ig.TryGetProperty("username", out var u) ? u.GetString() : "";
                break; // IG'li ilk sayfa
            }
        }
        if (pageId is null || pageToken is null || igUserId is null)
        {
            _log.LogInformation("IG DM botu: IG professional hesabı bağlı sayfa yok — customer={CustomerId}",
                customerId);
            return;
        }

        var acc = await _db.InstagramAccounts.FirstOrDefaultAsync(a => a.IgUserId == igUserId, ct)
            ?? _db.InstagramAccounts.Add(new InstagramAccount { Id = Guid.NewGuid(), IgUserId = igUserId }).Entity;
        acc.LicenseId = license.Id;
        acc.PageId = pageId;
        acc.IgUsername = igUsername ?? "";
        acc.PageTokenProtected = _protector.Protect(pageToken);
        acc.Status = "active";
        acc.LastError = null;
        acc.ConnectedAt = now;
        await _db.SaveChangesAsync(ct);

        // Webhook aboneliği — idempotent, her bağlamada tekrar çağrılabilir.
        using var subReq = new HttpRequestMessage(HttpMethod.Post, $"{root}/{pageId}/subscribed_apps")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["subscribed_fields"] = "live_comments"
            })
        };
        subReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageToken);
        using var subRes = await _http.SendAsync(subReq, ct);
        if (!subRes.IsSuccessStatusCode)
        {
            acc.LastError = $"subscribed_apps {(int)subRes.StatusCode}";
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("IG webhook aboneliği düştü — page={PageId}, status={Status}",
                pageId, (int)subRes.StatusCode);
        }

        _log.LogInformation("IG DM botu bağlandı — ig={IgUsername} ({IgUserId}), license={LicenseId}",
            igUsername, igUserId, license.Id);
    }
}
