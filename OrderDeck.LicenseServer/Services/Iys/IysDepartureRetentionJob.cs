using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Ayrılış saklama takvimi (§6 + 6563 m.13), günde bir koşar:
///
/// Faz 1 — 30 günü dolan Disabled hesaplar: marka başka canlı hesapta
/// yaşamıyorsa IysConsent satırları silinir ve NetgsmDeparture takvim kaydı
/// açılır; hesap satırı her durumda silinir.
/// Faz 2 — hesabı hiç kalmamış YETİM markaların onayları: silinir + takvim
/// kaydı (LicenseId olay izinden best-effort geri kazanılır).
/// Faz 3 — ayrılış + 3 yılı dolanlar: DÖNEM ispatı (OccurredAt ≤ DepartedAt
/// olayları) ve o lisansın dönem kampanyaları imha edilir, PurgedAt damgalanır.
/// Aydınlatma metni iki kademeyi de söyler; "tamamen sildik" İDDİA EDİLMEZ.
/// </summary>
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class IysDepartureRetentionJob
{
    // public: testin eşiği aşan/aşmayan sınırı doğru sınayabilmesi için bu
    // sabite ihtiyacı var. Sunucu projesinde InternalsVisibleTo YOK (bkz.
    // PanelNetgsmAccountController.IsUniqueIndexConflict dokümantasyonu) —
    // aynı kalıp: internal yerine public.
    public static readonly TimeSpan ConsentRetention = TimeSpan.FromDays(30);

    // m.13: 3 yıl. FromDays(3*365)=1095 gün artık yıllarda 3 takvim yılının
    // GERİSİNDE kalabilir; +2 gün pay süreyi uzatır, asla kısaltmaz.
    public static readonly TimeSpan ProofRetention = TimeSpan.FromDays(3 * 365 + 2);

    private readonly LicenseDbContext _db;
    private readonly ILogger<IysDepartureRetentionJob> _log;

    public IysDepartureRetentionJob(LicenseDbContext db,
        ILogger<IysDepartureRetentionJob> log)
    {
        _db = db;
        _log = log;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await DeleteDepartedConsentsAsync(now, ct);
        await DeleteOrphanConsentsAsync(now, ct);
        await PurgeExpiredProofAsync(now, ct);
    }

    /// <summary>Faz 1 — 30 günü dolan Disabled hesaplar.</summary>
    private async Task DeleteDepartedConsentsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var threshold = now - ConsentRetention;
        var ids = await _db.NetgsmAccounts.AsNoTracking()
            .Where(a => a.Status == NetgsmAccountStatus.Disabled
                        && a.DisabledAt != null && a.DisabledAt <= threshold)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            try
            {
                // Her yinelemede taze oku: önceki yinelemenin hata temizliği
                // (ChangeTracker.Clear) izlenen nesneleri koparmış olabilir.
                var acc = await _db.NetgsmAccounts
                    .FirstOrDefaultAsync(a => a.Id == id, ct);
                // I-3: eşiği TAZE okuma üzerinde yeniden kontrol et. Admin id
                // toplandıktan sonra "Aç" deyip tekrar "Kapat" derse DisabledAt
                // şimdiye damgalanır — eski eşiğe göre "dolmuş" sayıp bu koşuda
                // silmek 30 günlük sayacı atlatırdı.
                if (acc is null || acc.Status != NetgsmAccountStatus.Disabled
                    || acc.DisabledAt is null || acc.DisabledAt > threshold)
                    continue; // admin geri açtı ya da saat yeniden başladı — bu koşuda dokunma.

                // I-2: ayrılışla kapanmamış her satır markayı canlı tutar —
                // sistem kapanışı (§2.4, DisabledAt=null) sahipliği bitirmez;
                // bedel: zombi sistem-kapalı satır eski ayrılanın onaylarını
                // fazla saklatır, yanlış silmeye tercih edilir. Faz 2 ile aynı
                // kural: satır varsa marka sahipli.
                var brandAlive = await _db.NetgsmAccounts.AnyAsync(
                    b => b.Id != acc.Id && b.BrandCode == acc.BrandCode
                         && (b.Status != NetgsmAccountStatus.Disabled || b.DisabledAt == null), ct);

                var consentCount = 0;
                if (!brandAlive)
                {
                    var consents = await _db.IysConsents
                        .Where(c => c.BrandCode == acc.BrandCode).ToListAsync(ct);
                    consentCount = consents.Count;
                    _db.IysConsents.RemoveRange(consents);
                    _db.NetgsmDepartures.Add(new NetgsmDeparture
                    {
                        Id = Guid.NewGuid(),
                        LicenseId = acc.LicenseId,
                        BrandCode = acc.BrandCode,
                        DepartedAt = acc.DisabledAt.Value,
                        ConsentsDeletedAt = now,
                    });
                }

                var (accountId, brandCode) = (acc.Id, acc.BrandCode);
                _db.NetgsmAccounts.Remove(acc);
                await _db.SaveChangesAsync(ct);

                if (!brandAlive)
                    _log.LogInformation(
                        "Ayrılış temizliği: marka {Brand} — {ConsentCount} onay silindi, "
                        + "takvim açıldı (hesap {AccountId})",
                        brandCode, consentCount, accountId);
                else
                    _log.LogInformation(
                        "Ayrılış temizliği: hesap {AccountId} silindi, marka {Brand} başka "
                        + "hesapta canlı — onaylar korundu",
                        accountId, brandCode);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogInformation(ex,
                    "Ayrılış temizliği: araya giren karar (admin Aç/Kapat) — hesap {AccountId} "
                    + "bu koşuda atlandı, sonraki koşu yeniden değerlendirir", id);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Ayrılış temizliği başarısız (hesap {AccountId})", id);
            }
        }
    }

    /// <summary>Faz 2 — hesabı hiç kalmamış yetim markalar.</summary>
    private async Task DeleteOrphanConsentsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // InMemory sağlayıcısı Where içindeki ilişkisel alt sorguyu
        // (_db.NetgsmAccounts.Any(...)) SQL Server gibi çeviremiyor; iki listeyi
        // ayrı çekip bellekte fark alıyoruz. Marka sayısı düşük (yayıncı başına
        // bir kaç yüz onay), bu yüzden bellek maliyeti kabul edilebilir.
        var brandsWithConsents = await _db.IysConsents.AsNoTracking()
            .Select(c => c.BrandCode).Distinct().ToListAsync(ct);
        var brandsWithAccounts = await _db.NetgsmAccounts.AsNoTracking()
            .Select(a => a.BrandCode).Distinct().ToListAsync(ct);
        var orphanBrands = brandsWithConsents.Except(brandsWithAccounts).ToList();

        foreach (var brand in orphanBrands)
        {
            try
            {
                // M-5: liste oluşturulduktan sonra yeni bir hesap bu markayı
                // doğrulamış olabilir (yarış) — taze kontrol olmadan canlı bir
                // markayı yetim sanıp onaylarını silebiliriz.
                if (await _db.NetgsmAccounts.AnyAsync(a => a.BrandCode == brand, ct))
                    continue;

                // I-4: #472-#473 dağıtımı arasında BrandCode hiç yazılmadan
                // kaydedilmiş eski satırlar var. Boş marka OTOMATİK silinmez —
                // hangi yayıncıya ait olduğu makine tarafından çözülemiyor.
                if (string.IsNullOrWhiteSpace(brand))
                {
                    var blankCount = await _db.IysConsents
                        .CountAsync(c => c.BrandCode == brand, ct);
                    _log.LogWarning(
                        "Yetim marka temizliği: boş BrandCode'lu {Count} onay satırı atlandı — "
                        + "#472-#473 arası eski kayıt, elle karar gerekir", blankCount);
                    continue;
                }

                // LicenseId best-effort: hesap yok ama olay izi kalmış olabilir.
                var licenseId = await _db.IysConsentEvents.AsNoTracking()
                    .Where(e => e.BrandCode == brand && e.LicenseId != null)
                    .OrderByDescending(e => e.OccurredAt)
                    .Select(e => e.LicenseId)
                    .FirstOrDefaultAsync(ct);

                // I-1: "zaten bir takvim kaydı var mı" koruması KALDIRILDI.
                // Toplayıcı markayı yalnız Verified bir hesaptan çözer; hesabı
                // olmayan bir markada YENİ onay doğamaz, yani aynı dönemin iki
                // kez tespit edilmesi imkânsız. Bu korumanın tek gerçek etkisi
                // İKİNCİ (gerçek) dönemin imha randevusunu hiç açmamaktı:
                // A ayrıldı → D1 açıldı; marka B'ye geçti, B doğruladı ve
                // topladı; B'nin lisansı KVKK ile silindi → yetim → eski D1
                // hâlâ PurgedAt=null diye yeni satır açılmıyor, B'nin dönem
                // olayları asla imha edilmiyordu.
                var consents = await _db.IysConsents
                    .Where(c => c.BrandCode == brand).ToListAsync(ct);
                var consentCount = consents.Count;
                _db.IysConsents.RemoveRange(consents);
                _db.NetgsmDepartures.Add(new NetgsmDeparture
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    BrandCode = brand,
                    DepartedAt = now, // gerçek ayrılış bilinmiyor — tespit anı
                    ConsentsDeletedAt = now,
                });
                await _db.SaveChangesAsync(ct);

                _log.LogInformation(
                    "Yetim marka temizliği: marka {Brand} — {ConsentCount} onay silindi, "
                    + "takvim açıldı", brand, consentCount);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogInformation(ex,
                    "Yetim marka temizliği: araya giren karar — marka {Brand} bu koşuda "
                    + "atlandı, sonraki koşu yeniden değerlendirir", brand);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Yetim marka temizliği başarısız (marka {Brand})", brand);
            }
        }
    }

    /// <summary>Faz 3 — m.13 süresi dolan dönem ispatının imhası.</summary>
    private async Task PurgeExpiredProofAsync(DateTimeOffset now, CancellationToken ct)
    {
        var threshold = now - ProofRetention;
        var ids = await _db.NetgsmDepartures.AsNoTracking()
            .Where(d => d.PurgedAt == null && d.DepartedAt <= threshold)
            .Select(d => d.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            try
            {
                var dep = await _db.NetgsmDepartures
                    .FirstOrDefaultAsync(d => d.Id == id, ct);
                if (dep is null || dep.PurgedAt is not null) continue;

                // DÖNEM sınırı: yalnız ayrılış ANINA KADAR olan olaylar.
                // Marka devredildiyse yeni dönemin ispatı bu imhaya KARIŞMAZ.
                var events = await _db.IysConsentEvents
                    .Where(e => e.BrandCode == dep.BrandCode
                                && e.OccurredAt <= dep.DepartedAt)
                    .ToListAsync(ct);
                _db.IysConsentEvents.RemoveRange(events);

                var campaignCount = 0;
                var recipientCount = 0;
                if (dep.LicenseId is { } licenseId)
                {
                    var campaigns = await _db.SmsCampaigns
                        .Where(c => c.LicenseId == licenseId
                                    && c.CreatedAt <= dep.DepartedAt)
                        .ToListAsync(ct);
                    var campaignIds = campaigns.Select(c => c.Id).ToList();
                    // InMemory'de ExecuteDeleteAsync yok — açık RemoveRange.
                    var recipients = await _db.SmsCampaignRecipients
                        .Where(r => campaignIds.Contains(r.CampaignId))
                        .ToListAsync(ct);
                    campaignCount = campaigns.Count;
                    recipientCount = recipients.Count;
                    _db.SmsCampaignRecipients.RemoveRange(recipients);
                    _db.SmsCampaigns.RemoveRange(campaigns);
                }

                var (departureId, brandCode) = (dep.Id, dep.BrandCode);
                dep.PurgedAt = now;
                await _db.SaveChangesAsync(ct);

                _log.LogInformation(
                    "m.13 imhası: ayrılış {DepartureId} marka {Brand} — {EventCount} olay, "
                    + "{CampaignCount} kampanya, {RecipientCount} alıcı imha edildi",
                    departureId, brandCode, events.Count, campaignCount, recipientCount);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogInformation(ex,
                    "m.13 imhası: araya giren karar — ayrılış {DepartureId} bu koşuda atlandı, "
                    + "sonraki koşu yeniden değerlendirir", id);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "m.13 imhası başarısız (ayrılış {DepartureId})", id);
            }
        }
    }
}
