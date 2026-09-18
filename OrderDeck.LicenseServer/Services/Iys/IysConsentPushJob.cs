using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Hangfire işi: <see cref="IysPushState.Pending"/> kayıtları <c>/iys/add</c>
/// ile İYS'ye bildirir.
///
/// <para><b>Bu iş "kabul edildi" kararı VERMEZ.</b> Netgsm'in <c>code 0</c>
/// yanıtı "kuyruğa alındı" demek. Kayıt <see cref="IysPushState.Pushed"/>'e
/// geçer ve doğrulama randevusu alır; kabulü yalnız
/// <see cref="IysConsentVerifyJob"/> yazabilir. 2026-09-17'de 284 onayı bu
/// ayrımı yapmadığımız için kaybettik.</para>
///
/// <para>Süpürme işi olduğu için satır kilidi değil <b>iş kilidi</b> kullanır:
/// <c>[DisableConcurrentExecution]</c>. Aynı anda ikinci bir kopya çalışırsa
/// aynı <c>Pending</c> satırlarını okur ve İYS'ye ikinci kez bildirir — zararsız
/// ama dakikada 10 isteklik kotayı boşa harcar ve olay tablosunu ikizler.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentPushJob
{
    /// <summary>Tek istekte bildirilen kayıt sayısı.</summary>
    public const int BatchSize = 20;

    /// <summary>Netgsm ~10 istek/dk sınırlı; partiler arası bekleme.</summary>
    public static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(6);

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentPushJob> _log;

    public IysConsentPushJob(
        LicenseDbContext db, IIysClient client,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentPushJob> log)
    {
        _db = db;
        _client = client;
        _opt = opt.Value;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.BrandCode))
        {
            _log.LogInformation("İYS push: BrandCode ayarlı değil, boru hattı kapalı");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Süresi dolmuş bekleyenler hiç gönderilmez: 3 iş günü geçtiyse
        // İYS zaten H467 ile reddeder (consent_date çok eski) ve kayıt
        // hukuken geçersiz. Sessizce silmiyoruz — Expired damgası admin
        // listesinde görünür.
        var expired = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Pending
                        && c.PushDeadline != null && c.PushDeadline < now)
            .ToListAsync(ct);
        foreach (var e in expired)
        {
            e.PushState = IysPushState.Expired;
            e.LastError = "push-deadline-passed";
            e.UpdatedAt = now;
        }
        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("İYS push: {Count} kaydın 3 iş günü penceresi doldu", expired.Count);
        }

        var pending = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Pending
                        && (c.PushDeadline == null || c.PushDeadline >= now))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var first = true;
        foreach (var batch in pending.Chunk(BatchSize))
        {
            if (!first) await Task.Delay(BatchDelay, ct);
            first = false;
            await PushBatchAsync(batch, ct);
        }
    }

    private async Task PushBatchAsync(IysConsent[] batch, CancellationToken ct)
    {
        var records = batch.Select(c => new IysConsentRecord(
            c.Recipient, c.RecipientType, c.ChannelType, c.Status,
            c.ConsentDate ?? c.LastLocalEventAt,
            c.SourceCode ?? _opt.IysSourceCode,
            c.Id.ToString("N"))).ToArray();

        IysAddResult result;
        try
        {
            result = await _client.AddAsync(records, ct);
        }
        catch (IysConfigurationException cfg)
        {
            // Kalıcı yapılandırma hatası: her kayıt aynı hatayla düşer.
            // Devam etmek bekleyenleri sırayla harcar → boru hattı durur.
            // Kayıtlara DOKUNULMAZ: Failed yazmak, düzeltilebilir bir ayar
            // hatasını kayıt başına kalıcı yara gibi gösterirdi.
            _log.LogError(cfg, "İYS yapılandırma hatası ({Code}) — push boru hattı durdu", cfg.Code);
            throw;
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var c in batch)
            {
                c.PushState = IysPushState.Failed;
                c.LastError = Truncate(ex.Message, 500);
                c.UpdatedAt = now;
                AddEvent(c, IysConsentEventType.PushAttempt, code: null, body: null, error: ex.GetType().Name);
            }
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(ex, "İYS push: {Count} kayıtlık parti başarısız", batch.Length);
            return;
        }

        var stamp = DateTimeOffset.UtcNow;
        foreach (var c in batch)
        {
            AddEvent(c, IysConsentEventType.PushAttempt, result.Code, result.RawBody, error: null);

            if (result.Queued)
            {
                // KUYRUĞA ALINDI — kabul DEĞİL.
                c.PushState = IysPushState.Pushed;
                c.LastPushedAt = stamp;
                c.VerifyAttempts = 0;
                c.NextVerifyAt = IysVerifySchedule.Next(stamp, 0);
                c.LastError = null;
            }
            else
            {
                c.PushState = IysPushState.Failed;
                c.LastError = Truncate($"iys-add code={result.Code}", 500);
            }
            c.UpdatedAt = stamp;
        }
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "İYS push: {Count} kayıt bildirildi (code={Code}) — doğrulama bekliyor",
            batch.Length, result.Code);
    }

    private void AddEvent(
        IysConsent c, IysConsentEventType type, string? code, string? body, string? error)
        => _db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            Recipient = c.Recipient,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = type,
            Status = c.Status,
            ApiResponseCode = code,
            ApiResponseBody = Truncate(body, 2000),
            ErrorCode = error,
        });

    private static string? Truncate(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];
}
