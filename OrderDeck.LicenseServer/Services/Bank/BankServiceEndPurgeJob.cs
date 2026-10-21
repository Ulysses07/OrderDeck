using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Hizmet sonu + <see cref="GracePeriod"/> banka verisi silmesi (KVKK K-6 öneri 2; İYS "ayrılış + 30 gün" kalıbı, Silme
/// Yönetmeliği md. 11/3'teki 3 ayın içinde). Günde bir, sırayla:
/// <list type="number">
/// <item>Tarihsiz Disabled bugünle damgalanır ve yönetici durdurması sayılır: kod Disabled'ı hep <c>DisabledAt</c>'le yazar,
/// tarihsiz satırı ancak bir insan SQL ile yazmıştır (P0 öncesi). Damgalanmasa hizmet sonu silmesine hiç girmezdi; sonraki adım
/// onu lisans geçerli diye kendiliğinden açmaz.</item>
/// <item>Lisansı yenilenen bağlantı silinmez: kendiliğinden durdurulmuşsa Unverified'a döner
/// (<see cref="ObifinConnectionService.ReviveRenewedLicensesAsync"/>; yerel verisi silinmişse çapa açılış gününe çekilir, karar
/// 28), yöneticiye "Doğrula" e-postası gider. Yöneticinin durdurduğu açılmaz.</item>
/// <item>Lisansı biten bağlantı Disabled'a çekilir (<see cref="ObifinConnectionService.DisableEndedLicensesAsync"/>): çekim
/// anahtarsız zamanlanmıyor olabilir, sayaç burada da başlasın.</item>
/// <item><c>DisabledAt</c>'i <see cref="GracePeriod"/>'u geçen, henüz işlenmemiş (<c>BankDataPurgedAt</c> boş) ve lisansı
/// yeniden geçerli olmayan (yönetici durdurduysa lisans önemsiz) bağlantının banka verisi
/// <see cref="BankConnectionTerminator.PurgeLocalDataAsync"/> ile YALNIZ YERELDE silinir; "Kaldır ve sil"in sahiplik kanıtı
/// (Obifin kimliği, yerel banka bağlantıları, IBAN'sız onaylı hesaplar) kalır. Obifin'e GİDİLMEZ (plan karar 12):
/// <c>bankaapi/sil</c>'i yalnız yöneticinin "Kaldır ve sil"i çağırır (lisansa özgü, paylaşılan kullanıcı korumalı); insan onayı
/// olmadan paylaşılan bir Obifin kullanıcısının altındaki bağlantı silinmesin. Bir lisansın hatası sıradakini bekletmez. Seçimden
/// sonra yönetici sayfası (kilidi almaz) bağlantıyı etkinleştirdiyse ya da "Kaldır ve sil" ile kaldırdıysa lisans "atlandı"
/// sayılır, iş hatası değildir.</item>
/// <item>Bir şey silindiyse, atlandıysa ya da düştüyse yöneticiye özet e-posta: her lisans için admin/obifin'de "Kaldır ve sil"
/// ve Obifin'den yazılı silme teyidi (Obifin API'sinde hareket silme ucu yok).</item>
/// <item>Obifin tarafının takibi (KVKK Ö-9 öneri 3, K-6 öneri 1; plan karar 21): bu koşudan ÖNCE yerelde silinmiş ama "Kaldır ve
/// sil" bekleyen bağlantılar ve yazılı silme teyidi gelmemiş Obifin kopyaları (<c>ObifinCopyDeletions</c>; Obifin'de elle
/// yapılacak işiyle) her gün tek hatırlatma e-postasında; biri <see cref="BankConnectionTerminator.ObifinFollowUpOverdue"/>'yu
/// geçtiyse konu "GECİKTİ". Hatırlatma uyarı yoludur: gönderim düşerse ertesi gün yeniden gelir.</item>
/// </list>
/// Başarılı koşu silecek bağlantı olmasa da bir koşu kaydı bırakır (<see cref="DataDestruction.RecordRun"/>). İş düşerse yöneticiye
/// e-posta gider ve istisna Hangfire'a çıkar (saklama işlerinin kalıbı). Çekimle aynı kilit (<see cref="ObifinPollJob.LockResource"/>):
/// silme ile çekim yarışmaz. <see cref="BankHasher"/> istemez; anahtardan bağımsız HEP zamanlanır. E-postalara kişisel veri girmez:
/// lisans kimlikleri, sayılar, istisna türleri.
/// </summary>
[DisableConcurrentExecution(ObifinPollJob.LockResource, 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class BankServiceEndPurgeJob
{
    public const string JobId = "bank-service-end-purge";
    /// <summary>05:17 UTC: 5 dakikalık ızgaranın ve diğer günlük işlerin dışında.</summary>
    public const string Cron = "17 5 * * *";
    /// <summary>Durdurmadan (lisans bitişi ya da yönetici) yerel silmeye kadar gün (plan karar 1; öneri, avukat onaylamalı). Yönetici
    /// sayfasının "silinecek" tarihi ve "Durdur" bildirimi de bunu kullanır.</summary>
    public const int GracePeriodDays = 30;
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(GracePeriodDays);
    public const string DoneSubject = "[OrderDeck] Hizmet sonu banka silmesi yapıldı";
    public const string FailedSubject = "[OrderDeck] Hizmet sonu banka silmesi düştü";
    public const string RenewedSubject = "[OrderDeck] Lisans yenilendi: banka bağlantısını doğrulayın";
    public const string ObifinPendingSubject = "[OrderDeck] Obifin tarafı bekliyor: Kaldır ve sil / yazılı silme teyidi";
    public const string ObifinOverdueSubject = "[OrderDeck] GECİKTİ: Obifin tarafı süresini geçti";

    private readonly LicenseDbContext _db;
    private readonly BankConnectionTerminator _terminator;
    private readonly AdminJobAlerts _alerts;
    private readonly ILogger<BankServiceEndPurgeJob> _log;

    public BankServiceEndPurgeJob(LicenseDbContext db, BankConnectionTerminator terminator, AdminJobAlerts alerts,
        ILogger<BankServiceEndPurgeJob> log)
    { _db = db; _terminator = terminator; _alerts = alerts; _log = log; }

    /// <summary>Verisi yerelde silinen lisans sayısı. Bir lisansın silmesi düştüyse özet e-postası gider ve
    /// <see cref="AggregateException"/> fırlar (koşu Failed, koşu kaydı yazılmaz; ertesi gün kalanı siler). Başka bir adım
    /// düşerse yöneticiye e-posta gider ve istisna yukarı çıkar.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        (int Deleted, List<Exception> Failures) outcome;
        try
        {
            outcome = await RunCoreAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Hizmet sonu banka silmesi işi düştü");
            await _alerts.SendAsync(FailedSubject,
                $"{JobId} düştü: {ex.GetType().Name}. Hangfire panosuna bakın; iş ertesi gün yeniden koşar, o güne kadar hizmet " +
                "sonu silmesi ve Obifin tarafının hatırlatması gecikir.", CancellationToken.None);
            throw;
        }
        if (outcome.Failures.Count > 0)
            throw new AggregateException($"Hizmet sonu banka silmesi {outcome.Failures.Count} lisansta düştü.", outcome.Failures);
        return outcome.Deleted;
    }

    private async Task<(int Deleted, List<Exception> Failures)> RunCoreAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await StampUndatedAsync(now, ct);
        var renewed = await ObifinConnectionService.ReviveRenewedLicensesAsync(_db, now, _log, ct);
        if (renewed.Count > 0)
            await _alerts.SendAsync(RenewedSubject, string.Join("\n", renewed.Select(id =>
                    $"lisans {id}: lisans yeniden geçerli; hizmet sonu silmesi iptal, bağlantı Unverified. admin/obifin'de Doğrula'ya basın."))
                + "\n\nYöneticinin durdurduğu bağlantılar kendiliğinden açılmaz (admin/obifin'de Etkinleştir).", CancellationToken.None);
        await ObifinConnectionService.DisableEndedLicensesAsync(_db, now, _log, ct);

        // Obifin tarafının takibi: silmeden ÖNCE okunur — bu koşuda silinenler "yapıldı" e-postasında; yenilenen lisansın işareti
        // yukarıda boşaldı. İşaret yalnız Disabled bağlantıda yazılır ve Disabled'dan çıkan her yolda boşalır (Görev 13).
        var awaitingRemoval = await _db.ObifinConnections.AsNoTracking().Where(c => c.BankDataPurgedAt != null)
            .Select(c => new { c.LicenseId, Since = c.BankDataPurgedAt!.Value }).ToListAsync(ct);
        var awaitingConfirmation = await _db.ObifinCopyDeletions.AsNoTracking().Where(d => d.ConfirmedAt == null)
            .Select(d => new { d.LicenseId, Since = d.RequestedAt, d.ManualAction }).ToListAsync(ct);

        var cutoff = now - GracePeriod;
        var activeLicenseIds = _db.Licenses.Where(ObifinConnectionService.LicenseActiveAt(now)).Select(l => l.Id);
        var due = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.Status == ObifinConnectionStatus.Disabled && c.DisabledAt != null && c.DisabledAt <= cutoff
                && c.BankDataPurgedAt == null
                // Savunma (yenilenme adımının kuralı, eşzamanlı bir uzatmaya karşı): lisansı bugün geçerli ve yönetici
                // durdurmamışsa silinmez.
                && (c.DisabledByAdmin || !activeLicenseIds.Contains(c.LicenseId)))
            .OrderBy(c => c.DisabledAt).Select(c => c.LicenseId).ToListAsync(ct);

        var deleted = 0;
        var failures = new List<Exception>();
        var notes = new List<string>();
        foreach (var licenseId in due)
        {
            try
            {
                var r = await _terminator.PurgeLocalDataAsync(licenseId, DataDestruction.Triggers.ServiceEnd,
                    DataDestruction.SystemActor, ct);
                deleted++;
                notes.Add($"lisans {licenseId}: {r.Transactions} hareket, {r.Matches} eşleşme, {r.Memories} IBAN bağı, {r.Gaps} gap, " +
                    $"{r.Accounts} onaysız hesap yerelde silindi; {r.AccountsScrubbed} onaylı hesabın IBAN'ı boşaltıldı (satır " +
                    $"'Kaldır ve sil'e kadar kalır). {r.ManualActionText()}");
            }
            catch (ObifinValidationException ex) when (ex.Message is BankConnectionTerminator.NotStoppedMessage
                or BankConnectionTerminator.NoConnectionMessage)
            {
                // Seçimden sonra yönetici sayfası (obifin-poll kilidini almaz) bağlantıyı etkinleştirdi ya da "Kaldır ve sil" ile
                // kaldırdı. İş hatası değil: Failed sayılmaz, "DÜŞTÜ" denmez. Etkinleştirmede silinen partiler imha kaydında.
                _db.ChangeTracker.Clear();
                var reason = ex.Message == BankConnectionTerminator.NotStoppedMessage
                    ? "bağlantı bu sırada etkinleştirildi; yerel silme yapılmadı ya da yarıda bırakıldı (silinen satırlar imha kaydında)"
                    : "bağlantı bu sırada 'Kaldır ve sil' ile kaldırıldı; verisi o yolla silindi";
                _log.LogInformation("Hizmet sonu banka silmesi atlandı — lisans={LicenseId}: {Reason}", licenseId, reason);
                notes.Add($"lisans {licenseId}: atlandı — {reason}.");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // İkinci jeton çakışması (BankDeletionIncompleteException) da buraya düşer: ertesi gün kalanı siler.
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Hizmet sonu banka silmesi düştü — lisans={LicenseId}", licenseId);
                failures.Add(ex);
                notes.Add($"lisans {licenseId}: DÜŞTÜ ({ex.GetType().Name})");
            }
        }
        if (notes.Count > 0)
        {
            var body = string.Join("\n", notes);
            if (deleted > 0)
                body += "\n\nObifin'e gidilmedi. Yerelde silinen her lisans için admin/obifin'de 'Kaldır ve sil'e basın (paylaşılan " +
                    "Obifin kullanıcısı ya da sahibi doğrulanamayan bağlantı varsa sayfa söyler) ve Obifin'den hareket kopyasının " +
                    "silindiğine dair YAZILI teyit isteyin (Obifin API'sinde hareket silme ucu yok).";
            if (failures.Count > 0)
                body += "\n\nDüşen lisansların kalan verisi ertesi günkü koşuda yeniden silinir; Hangfire panosuna bakın.";
            await _alerts.SendAsync(failures.Count > 0 ? FailedSubject : DoneSubject, body, CancellationToken.None);
        }

        if (awaitingRemoval.Count + awaitingConfirmation.Count > 0)
        {
            var overdueAt = now - BankConnectionTerminator.ObifinFollowUpOverdue;
            string Line(Guid licenseId, DateTimeOffset since, string what, string? manualAction = null)
                => $"lisans {licenseId}: {what} — {since.UtcDateTime:yyyy-MM-dd}'den beri{(since < overdueAt ? " (GECİKTİ)" : "")}" +
                    (string.IsNullOrWhiteSpace(manualAction) ? "" : $"; elle iş: {manualAction}");
            var lines = awaitingRemoval.OrderBy(x => x.Since).Select(x => Line(x.LicenseId, x.Since,
                    "banka verisi yerelde silindi, admin/obifin'de 'Kaldır ve sil' bekliyor"))
                .Concat(awaitingConfirmation.OrderBy(x => x.Since).Select(x => Line(x.LicenseId, x.Since,
                    "Obifin'den yazılı silme teyidi bekleniyor (admin/obifin → Teyit alındı)", x.ManualAction)));
            var overdue = awaitingRemoval.Any(x => x.Since < overdueAt) || awaitingConfirmation.Any(x => x.Since < overdueAt);
            await _alerts.SendAsync(overdue ? ObifinOverdueSubject : ObifinPendingSubject, string.Join("\n", lines) +
                "\n\nObifin kendi kopyasını silip yazılı teyit verene kadar bankadan çekmeyi ve saklamayı sürdürebilir. Bu " +
                $"hatırlatma her gün gelir; {BankConnectionTerminator.ObifinFollowUpOverdue.TotalDays:0} günü geçen satır gecikmiş " +
                "işaretlenir: Obifin'i yeniden yazılı uyarın.", CancellationToken.None);
        }

        if (failures.Count > 0) return (deleted, failures);
        // Başarılı koşunun kaydı, silinecek lisans olmasa da (Görev 9 incelemesi; iç imha prosedürü §4.2-3).
        DataDestruction.RecordRun(_db, JobId, $"durdurma+{GracePeriodDays}g, yalnız yerel");
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return (deleted, failures);
    }

    /// <summary>Tarihsiz Disabled: P0'dan önce elle (SQL ile) durdurulmuş satır. Lisans bitişi zaten Disabled olanı atlar, tarih
    /// yazmaz; damgalanmasa hizmet sonu silmesine hiç girmezdi. Yazan bir insandır: yönetici durdurması sayılır, lisans yenilenme
    /// adımı onu açmaz (açılması gerekiyorsa admin/obifin → Etkinleştir).</summary>
    private async Task StampUndatedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var undated = await _db.ObifinConnections
            .Where(c => c.Status == ObifinConnectionStatus.Disabled && c.DisabledAt == null).ToListAsync(ct);
        if (undated.Count == 0) return;
        foreach (var c in undated) { c.DisabledAt = now; c.DisabledByAdmin = true; c.UpdatedAt = now; }
        await _db.SaveChangesAsync(ct);
        _log.LogWarning("Hizmet sonu: tarihsiz {Count} Disabled bağlantı bugünle damgalandı, yönetici durdurması sayıldı — " +
            "lisanslar={LicenseIds}", undated.Count, string.Join(",", undated.Select(c => c.LicenseId)));
    }
}
