using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// F08/F09 güvenlik ağı (denetim 2026-09-09): takılı SMS kampanyalarını
/// kurtaran periyodik Hangfire job'ı. Üç takılma sınıfını yakalar:
///
/// 1. "sending" + bayat ClaimedAt — gönderim job'ı süreç ölümüyle yarıda
///    kaldı. Yeniden kuyruğa alınır; <see cref="SmsCampaignSendJob"/> claim'i
///    devralıp yalnız "pending" alıcıları gönderir.
/// 2. "pending" + oluşturulalı belli süre geçmiş — kampanya yazıldı ama
///    Enqueue hiç gerçekleşmedi (Create ile Enqueue arasında çökme, F09'un
///    kayıp-enqueue boşluğu). Kredi rezerve edilmiş ama tek SMS gitmemiş
///    olurdu; yeniden kuyruğa almak boşluğu kapatır.
/// 3. "paused" + bekleyen alıcısı YOK (Görev 15) — gönderim job'ı son alıcıyı
///    yazdıktan sonra tamamlama bloğuna girdi, tam o anda kurulum kapatıldı
///    ve tamamlama yazımı ClaimedAt CAS'ından düştü. Yeniden deneme durum
///    kapısına takılır (paused ne "pending" ne bayat "sending"). Kampanya
///    "duraklatıldı" görünür ama devam edecek hiçbir şeyi yoktur ve
///    başarısızların kredisi iade edilmemiştir.
///
/// Çifte enqueue zararsızdır: gönderim job'ı claim CAS'ı + yalnız-pending
/// alıcı seçimiyle idempotent.
///
/// <para><b>3. sınıf neden kuyruğa ALINMIYOR?</b> Gönderim job'ı "paused"
/// kampanyayı reddediyor ve REDDETMELİ — admin'in kapatma kararı kutsal.
/// Kampanyayı "pending"e çevirmek o kararı sessizce geri almak olurdu:
/// kurulum kapalıyken iş kalan alıcıları "iys-brand-missing" ile failed
/// yazardı. Bugün bekleyen alıcı sıfır olduğu için zararsız görünür, ama
/// kapıyı açmak duraklatmanın bütün gerekçesini deler. Bu yüzden süpürme
/// kampanyayı gönderime sokmadan DOĞRUDAN tamamlar; yaptığı şey gönderim
/// değil kurtarmadır ve "takılmışı bul ve çöz" sözleşmesi zaten burada.</para>
/// </summary>
public sealed class SmsCampaignRecoveryJob
{
    /// <summary>"pending" kampanyanın kayıp-enqueue sayılması için yaşı.
    /// Normal akışta Enqueue, Create yanıtından önce gerçekleşir; 2 dk
    /// yalnız gerçekten kopmuş olanları yakalar.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(2);

    private readonly LicenseDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly LicenseSmsBalanceService _balance;
    private readonly ILogger<SmsCampaignRecoveryJob> _log;

    public SmsCampaignRecoveryJob(
        LicenseDbContext db,
        IBackgroundJobClient jobs,
        LicenseSmsBalanceService balance,
        ILogger<SmsCampaignRecoveryJob> log)
    {
        _db = db;
        _jobs = jobs;
        _balance = balance;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleClaim = now - SmsCampaignSendJob.ClaimLease;
        var pendingCutoff = now - PendingGrace;

        var stuck = await _db.SmsCampaigns
            .Where(c =>
                (c.Status == "sending" && (c.ClaimedAt == null || c.ClaimedAt < staleClaim))
                || (c.Status == "pending" && c.CreatedAt < pendingCutoff))
            .Select(c => new { c.Id, c.Status, c.ClaimedAt, c.CreatedAt })
            .ToListAsync(ct);

        foreach (var c in stuck)
        {
            _log.LogWarning(
                "SmsCampaignRecoveryJob: re-enqueueing stuck campaign {Id} (status={Status}, claimedAt={ClaimedAt}, createdAt={CreatedAt})",
                c.Id, c.Status, c.ClaimedAt, c.CreatedAt);
            _jobs.Enqueue<SmsCampaignSendJob>(j => j.RunAsync(c.Id, CancellationToken.None));
        }

        await CompleteStrandedAsync(ct);
    }

    /// <summary>
    /// 3. takılma sınıfı: gidecek alıcısı kalmamış "paused" kampanyayı
    /// tamamlar ve iadesini yazar. Sınıf doc'undaki gerekçe geçerli —
    /// kampanya gönderime SOKULMAZ, burada bitirilir.
    ///
    /// <para>Ayrı sorgu bilinçli: yukarıdaki <c>stuck</c> listesi Enqueue
    /// ediyor, bu liste etmiyor. Tek sorguda birleştirmek iki farklı eylemi
    /// aynı listeye bindirirdi.</para>
    /// </summary>
    private async Task CompleteStrandedAsync(CancellationToken ct)
    {
        // "Bekleyen alıcısı yok" şartı bu dalın kalbi: bekleyen alıcısı OLAN
        // bir "paused" kampanya GERÇEKTEN duraklatılmıştır. Onu tamamlamak
        // gitmemiş SMS'leri gitmiş saymak, rezervasyonu da iade etmek olurdu —
        // kampanya devam ettirildiğinde aynı kredi ikinci kez harcanır.
        var stranded = await _db.SmsCampaigns
            .Where(c => c.Status == "paused"
                && !_db.SmsCampaignRecipients.Any(
                    r => r.CampaignId == c.Id && r.Status == "pending"))
            .ToListAsync(ct);

        foreach (var campaign in stranded)
        {
            var failedCount = await _db.SmsCampaignRecipients
                .CountAsync(r => r.CampaignId == campaign.Id && r.Status == "failed", ct);

            campaign.Status = "completed";
            campaign.CompletedAt = DateTimeOffset.UtcNow;
            // Damga ilerlemeli: aksi hâlde duraklatmadan önce kampanyayı okumuş
            // bayat bir işçi sahipliği geri kazanır (gerekçe: NextClaimedAt).
            campaign.ClaimedAt = SmsCampaignSendJob.NextClaimedAt(campaign.ClaimedAt);

            // İKİZİ: SmsCampaignSendJob'ın tamamlama bloğu. Formül BİREBİR
            // aynı ve aynı kalmalı — hak edilen toplamın FİİLEN ödenenin
            // üstünde kalan kısmı ödenir. Gönderim işi iadeyi yazmayı başarıp
            // tamamlamayı yazamadıysa fark sıfır çıkar ve ikinci iade olmaz.
            var owed = failedCount * campaign.SegmentsPerMessage;
            var refund = owed - campaign.RefundedCredits;

            _log.LogWarning(
                "SmsCampaignRecoveryJob: completing stranded paused campaign {Id} (failed={Failed}, refund={Refund})",
                campaign.Id, failedCount, refund);

            try
            {
                if (refund > 0)
                {
                    // N05: gerçekleşen iade kampanyaya da yazılır — iade
                    // tx'iyle AYNI SaveChanges'te, yoksa çifte iade penceresi.
                    campaign.RefundedCredits = owed;
                    await _balance.ApplyAndSaveAsync(
                        campaign.LicenseId, refund, "send-refund",
                        reason: $"campaign:{campaign.Id} failed={failedCount}",
                        createdByCustomerId: null, disallowNegative: false, ct);
                }
                else
                {
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                // Bu arada biri kampanyayı yazdı (devam ettirme, yeni bir
                // işçi). Kararımız bayat: düşür ve GERİ KALANA DEVAM ET.
                // Detach şart — kirli kopya bir sonraki kampanyanın
                // SaveChanges'ine binerse tek çakışma bütün süpürmeyi
                // sürekli düşürür. Kaybedilen bir şey yok: kampanya gerçekten
                // asılıysa bir sonraki süpürme taze okumayla yakalar.
                //
                // Burada YALNIZ kampanya detach ediliyor, iade tarafı değil:
                // onu LicenseSmsBalanceService kendi sözleşmesi gereği
                // (DiscardPending) çoktan geri aldı. O sözleşme olmadan iade
                // tx'i `Added` kalır, bir sonraki kampanyanın yazımına biner
                // ve bu kampanya "paused" kaldığı hâlde parası ödenmiş olur —
                // sonraki süpürme onu İKİNCİ kez iade eder.
                _db.Entry(campaign).State = EntityState.Detached;
                _log.LogInformation(
                    "SmsCampaignRecoveryJob: stranded campaign {Id} changed under us, skipping",
                    campaign.Id);
            }
        }
    }
}
