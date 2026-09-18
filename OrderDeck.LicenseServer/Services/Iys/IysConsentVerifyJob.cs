using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Hangfire işi: <c>/iys/search</c> ile İYS'nin gerçek cevabını okur.
///
/// <para><b>Push'tan AYRI bir adım olması bu tasarımın merkezi.</b> Push
/// "kuyruğa alındı" cevabı döner; kabul yalnız burada görülebilir. Sonuç
/// <see cref="IysConsent.LastVerifiedStatus"/>'e yazılır — yerel
/// <see cref="IysConsent.Status"/> asla ezilmez, çünkü o kişinin bize verdiği
/// onayın ispatı (kural 2).</para>
///
/// <para>Randevu takvimi <see cref="IysVerifySchedule"/>: 15dk → 1sa → 6sa →
/// 24sa. Erken sorgu, henüz işlenmemiş kaydı "RET" sanmaya yol açar.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentVerifyJob
{
    /// <summary>Tek sorguda sorulan alıcı sayısı.</summary>
    public const int BatchSize = 20;

    private readonly LicenseDbContext _db;
    private readonly IIysClient _client;
    private readonly NetgsmOptions _opt;
    private readonly ILogger<IysConsentVerifyJob> _log;

    public IysConsentVerifyJob(
        LicenseDbContext db, IIysClient client,
        IOptions<NetgsmOptions> opt, ILogger<IysConsentVerifyJob> log)
    {
        _db = db;
        _client = client;
        _opt = opt.Value;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.BrandCode)) return;

        var now = DateTimeOffset.UtcNow;
        var due = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Pushed
                        && c.NextVerifyAt != null && c.NextVerifyAt <= now)
            .OrderBy(c => c.NextVerifyAt)
            .Take(BatchSize * 5)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        // Faz 5 Task 8 bunu marka başına döngüyle değiştiriyor.
        var account = new IysAccountContext(
            Guid.Empty, _opt.UserCode, _opt.Password, _opt.BrandCode);

        foreach (var batch in due.Chunk(BatchSize))
        {
            IysSearchResult result;
            try
            {
                result = await _client.SearchAsync(
                    account, batch.Select(c => c.Recipient).ToArray(), ct);
            }
            catch (IysConfigurationException cfg)
            {
                // Boru hattı durur. Randevular olduğu yerde kalır: ayar
                // düzeltildiğinde doğrulama kaldığı yerden devam eder.
                _log.LogError(cfg, "İYS yapılandırma hatası ({Code}) — doğrulama durdu", cfg.Code);
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "İYS doğrulama: {Count} kayıtlık sorgu başarısız", batch.Length);
                continue;  // randevu duruyor, bir sonraki koşuda tekrar denenir
            }

            var stamp = DateTimeOffset.UtcNow;
            foreach (var c in batch)
            {
                // Yanıtta hiç görünmeyen alıcı Unknown — fail-closed.
                var status = result.Statuses.TryGetValue(c.Recipient, out var s)
                    ? s : IysConsentStatus.Unknown;

                c.LastVerifiedStatus = status;
                c.LastVerifiedAt = stamp;
                c.VerifyAttempts++;
                c.UpdatedAt = stamp;

                _db.IysConsentEvents.Add(new IysConsentEvent
                {
                    Id = Guid.NewGuid(),
                    Recipient = c.Recipient,
                    OccurredAt = stamp,
                    EventType = IysConsentEventType.SearchResult,
                    Status = status,
                    ApiResponseCode = result.Code,
                    ApiResponseBody = result.RawBody.Length > 2000 ? result.RawBody[..2000] : result.RawBody,
                });

                if (status == IysConsentStatus.Onay)
                {
                    c.PushState = IysPushState.Confirmed;
                    c.NextVerifyAt = null;
                    c.LastError = null;
                    continue;
                }

                // Henüz ONAY değil. "Kayıt yok" ile "reddetti" ayırt
                // edilemediği için beklemekten başka yapacak bir şey yok;
                // takvim tükenene kadar tekrar sorulur.
                var next = IysVerifySchedule.Next(stamp, c.VerifyAttempts);
                c.NextVerifyAt = next;
                if (next is null)
                {
                    c.PushState = IysPushState.Failed;
                    c.LastError = $"iys-not-confirmed status={status}";
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        var confirmed = due.Count(c => c.PushState == IysPushState.Confirmed);
        _log.LogInformation(
            "İYS doğrulama: {Total} kayıt soruldu, {Confirmed} ONAY", due.Count, confirmed);
    }
}
