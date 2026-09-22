using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Güvenlik ağı (spec: boru hattı adım 5). İki iş yapar:
///
/// <para>1. <b>Düşmüş kayıtları geri alır.</b> <see cref="IysPushState.Failed"/>
/// damgası geçici hatadan gelir (ağ, zaman aşımı, oran sınırı) ve onu kimse geri
/// almaz — push işi yalnız <c>Pending</c> tarar. Son tarih dolmamışsa ve düşüşün
/// üstünden <see cref="FailedRetryGrace"/> geçmişse kayıt <c>Pending</c>'e döner.
/// Bekleme şart: onsuz push-düşer-recovery-geri-alır sıcak döngüsü Netgsm'in
/// dakikada 10 isteklik kotasını dakikalar içinde tüketir.</para>
///
/// <para>2. <b>Sessiz kalmayı engeller.</b> <see cref="IysConsent.PushDeadline"/>'a
/// 24 saatten az kalmış ve hâlâ onaylanmamış kayıt sayısını günlüğe yazar. Bu
/// kayıtlar son tarihi kaçırırsa onay hukuken geçersiz olur; admin sayfası
/// (çekme) yetmez, günlük de (itme) uyarmalı.</para>
///
/// <para>Son tarihi zaten geçmiş <c>Failed</c> kayıt yeniden denenmez —
/// <see cref="IysPushState.Expired"/> olur. ONAY için İYS o kayda <c>H467</c>
/// ("consent_date 3 günden eski") döndürmekten başka bir şey yapamaz; RET için
/// bu, sınırlı deneme penceresinin dolmasıdır (bkz. <see cref="IysConsent.PushDeadline"/>).</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 120)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysConsentRecoveryJob
{
    /// <summary>Düşen kaydın yeniden denenmeden önce bekleyeceği süre.</summary>
    public static readonly TimeSpan FailedRetryGrace = TimeSpan.FromMinutes(20);

    /// <summary>Bu süreden az kalmışsa uyarı verilir.</summary>
    public static readonly TimeSpan DeadlineWarning = TimeSpan.FromHours(24);

    private readonly LicenseDbContext _db;
    private readonly ILogger<IysConsentRecoveryJob> _log;

    public IysConsentRecoveryJob(LicenseDbContext db, ILogger<IysConsentRecoveryJob> log)
    {
        _db = db;
        _log = log;
    }

    /// <returns>Son tarihi yaklaşan, onaylanmamış kayıt sayısı (test için).</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var retryCutoff = now - FailedRetryGrace;

        var failed = await _db.IysConsents
            .Where(c => c.PushState == IysPushState.Failed && c.UpdatedAt < retryCutoff)
            .ToListAsync(ct);

        var revived = 0;
        var expired = 0;
        foreach (var c in failed)
        {
            if (c.PushDeadline != null && c.PushDeadline < now)
            {
                c.PushState = IysPushState.Expired;
                c.LastError = "push-deadline-passed";
                expired++;
            }
            else
            {
                c.PushState = IysPushState.Pending;
                c.LastError = null;
                revived++;
            }
            c.UpdatedAt = now;
        }

        var warnCutoff = now + DeadlineWarning;
        var approaching = await _db.IysConsents
            .CountAsync(c => c.PushState != IysPushState.Confirmed
                             && c.PushState != IysPushState.Expired
                             && c.PushDeadline != null
                             && c.PushDeadline > now
                             && c.PushDeadline <= warnCutoff, ct);

        if (failed.Count > 0) await _db.SaveChangesAsync(ct);

        if (revived > 0 || expired > 0)
        {
            _log.LogWarning(
                "İYS kurtarma: {Revived} kayıt yeniden kuyruğa alındı, {Expired} kayıt süresi doldu",
                revived, expired);
        }

        if (approaching > 0)
        {
            _log.LogWarning(
                "İYS son tarih uyarısı: {Count} onayın İYS penceresine 24 saatten az kaldı ve hâlâ doğrulanmadı",
                approaching);
        }

        return approaching;
    }
}
