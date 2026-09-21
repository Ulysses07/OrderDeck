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
public sealed class IysDepartureRetentionJob
{
    internal static readonly TimeSpan ConsentRetention = TimeSpan.FromDays(30);

    // m.13: 3 yıl. FromDays(3*365)=1095 gün artık yıllarda 3 takvim yılının
    // GERİSİNDE kalabilir; +2 gün pay süreyi uzatır, asla kısaltmaz.
    //
    // public: testin eşiği aşan/aşmayan sınırı doğru sınayabilmesi için bu
    // sabite ihtiyacı var. Sunucu projesinde InternalsVisibleTo YOK (bkz.
    // PanelNetgsmAccountController.IsUniqueIndexConflict dokümantasyonu) —
    // aynı kalıp: internal yerine public.
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
                if (acc is null || acc.Status != NetgsmAccountStatus.Disabled
                    || acc.DisabledAt is null)
                    continue; // admin bu arada geri açtı — sayaç iptal.

                // Marka canlı başka hesapta yaşıyor mu? (BrandCode'un tekil
                // indeksi yalnız Verified'ı kapsar — aynı marka Disabled
                // kopyalarda da durabilir.)
                var brandAlive = await _db.NetgsmAccounts.AnyAsync(
                    b => b.Id != acc.Id && b.BrandCode == acc.BrandCode
                         && b.Status != NetgsmAccountStatus.Disabled, ct);

                if (!brandAlive)
                {
                    var consents = await _db.IysConsents
                        .Where(c => c.BrandCode == acc.BrandCode).ToListAsync(ct);
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

                _db.NetgsmAccounts.Remove(acc);
                await _db.SaveChangesAsync(ct);
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
                // LicenseId best-effort: hesap yok ama olay izi kalmış olabilir.
                var licenseId = await _db.IysConsentEvents.AsNoTracking()
                    .Where(e => e.BrandCode == brand && e.LicenseId != null)
                    .OrderByDescending(e => e.OccurredAt)
                    .Select(e => e.LicenseId)
                    .FirstOrDefaultAsync(ct);

                var hasSchedule = await _db.NetgsmDepartures
                    .AnyAsync(d => d.BrandCode == brand && d.PurgedAt == null, ct);
                if (!hasSchedule)
                {
                    _db.NetgsmDepartures.Add(new NetgsmDeparture
                    {
                        Id = Guid.NewGuid(),
                        LicenseId = licenseId,
                        BrandCode = brand,
                        DepartedAt = now, // gerçek ayrılış bilinmiyor — tespit anı
                        ConsentsDeletedAt = now,
                    });
                }

                var consents = await _db.IysConsents
                    .Where(c => c.BrandCode == brand).ToListAsync(ct);
                _db.IysConsents.RemoveRange(consents);
                await _db.SaveChangesAsync(ct);
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
                    _db.SmsCampaignRecipients.RemoveRange(recipients);
                    _db.SmsCampaigns.RemoveRange(campaigns);
                }

                dep.PurgedAt = now;
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "m.13 imhası başarısız (ayrılış {DepartureId})", id);
            }
        }
    }
}
