using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// Webhook'tan kuyruklanan live_comments işleyicisi. "!kayıt" tetiğinde
/// tokenlı kayıt linkini private reply DM'iyle gönderir.
///
/// <para><b>Retry etme:</b> private reply penceresi yayın süresiyle sınırlı ve
/// yorum başına 1 hak var — düşen gönderim loglanır, yeniden denenmez.
/// Bu yüzden metot exception fırlatmaz (Hangfire retry'ı tetiklemesin).</para>
/// </summary>
public sealed class InstagramLiveCommentJob
{
    private static readonly TimeSpan DmCooldown = TimeSpan.FromHours(1);

    private readonly LicenseDbContext _db;
    private readonly InstagramAccountService _accounts;
    private readonly InstagramPrivateReplyClient _reply;
    private readonly IntakeIgTokenService _tokens;
    private readonly IMemoryCache _cache;
    private readonly ILogger<InstagramLiveCommentJob> _log;

    public InstagramLiveCommentJob(
        LicenseDbContext db, InstagramAccountService accounts,
        InstagramPrivateReplyClient reply, IntakeIgTokenService tokens,
        IMemoryCache cache, ILogger<InstagramLiveCommentJob> log)
    {
        _db = db; _accounts = accounts; _reply = reply;
        _tokens = tokens; _cache = cache; _log = log;
    }

    /// <summary>Yorum tetik mi? Normalize: trim + invariant küçük harf +
    /// noktasız ı→i (Türkçe klavye/otomatik düzeltme her iki biçimi üretir).</summary>
    public static bool IsTrigger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var norm = text.Trim().ToLowerInvariant().Replace('ı', 'i');
        return norm == "!kayit";
    }

    public async Task ProcessAsync(string rawBody, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawBody); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Instagram webhook gövdesi ayrıştırılamadı.");
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entry", out var entries)) return;
            foreach (var entry in entries.EnumerateArray())
            {
                var igUserId = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (igUserId is null || !entry.TryGetProperty("changes", out var changes)) continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (change.TryGetProperty("field", out var f) && f.GetString() == "live_comments"
                        && change.TryGetProperty("value", out var value))
                        await HandleCommentAsync(igUserId, value, ct);
                }
            }
        }
    }

    private async Task HandleCommentAsync(string igUserId, JsonElement value, CancellationToken ct)
    {
        var text = value.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (!IsTrigger(text)) return;

        var commentId = value.TryGetProperty("id", out var c) ? c.GetString() : null;
        string? fromId = null, fromUsername = null;
        if (value.TryGetProperty("from", out var from))
        {
            fromId = from.TryGetProperty("id", out var fi) ? fi.GetString() : null;
            fromUsername = from.TryGetProperty("username", out var fu) ? fu.GetString() : null;
        }
        if (commentId is null || fromId is null || string.IsNullOrWhiteSpace(fromUsername)) return;

        var acc = await _db.InstagramAccounts
            .FirstOrDefaultAsync(a => a.IgUserId == igUserId && a.Status == "active", ct);
        if (acc is null) return; // tanımadığımız hesap — bot kapatılmış olabilir

        var config = await _db.Set<IntakeFormConfig>()
            .Where(cfg => cfg.IsActive && cfg.InstagramDmBotEnabled)
            .Join(_db.Licenses.Where(l => l.Id == acc.LicenseId),
                cfg => cfg.CustomerId, l => l.CustomerId, (cfg, _) => cfg)
            .FirstOrDefaultAsync(ct);
        if (config is null) return;

        // Hız sınırı: aynı izleyiciye saatte 1 DM (spec §3). Süreç içi cache
        // yeter — pencere kısa, restart'ta sıfırlanması kabul edilebilir.
        var cooldownKey = $"igdm:{igUserId}:{fromId}";
        if (_cache.TryGetValue(cooldownKey, out _)) return;
        _cache.Set(cooldownKey, true, DmCooldown);

        var token = _tokens.Create(config.Slug, fromUsername);
        var link = $"https://orderdeckapp.com/musteri-kayit/{Uri.EscapeDataString(config.Slug)}?ig={token}";
        var pageToken = _accounts.UnprotectPageToken(acc);
        if (pageToken is null) return;

        var ok = await _reply.SendAsync(acc.PageId, commentId,
            $"Merhaba! Kayıt formun hazır, Instagram hesabın otomatik bağlanacak: {link}", pageToken,
            ct);
        if (!ok)
        {
            acc.LastError = $"private reply düştü ({DateTimeOffset.UtcNow:O})";
            // Best-effort tanı yazımı: iptal/DB hatası no-throw sözleşmesini
            // delmesin (Hangfire retry'ı tetiklenmemeli).
            try { await _db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "IG LastError yazılamadı."); }
        }
        else
        {
            _log.LogInformation("IG kayıt DM'i gönderildi — slug={Slug}, viewer={Viewer}",
                config.Slug, fromUsername);
        }
    }
}
