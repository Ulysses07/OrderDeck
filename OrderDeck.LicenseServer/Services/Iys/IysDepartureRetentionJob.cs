using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Iys;

/// <summary>
/// Ayrılış saklama takvimi (§6 + 6563 m.13), günde bir koşar:
///
/// Faz 1 — 30 günü dolan Disabled hesaplar: marka başka canlı hesapta
/// yaşamıyorsa IysConsent satırları silinir ve NetgsmDeparture takvim kaydı
/// açılır; hesap satırı her durumda silinir.
/// Faz 2 — hesabı hiç kalmamış YETİM markaların onayları: son onay hareketi
/// üzerinden 30 günlük ödemsiz süre dolunca silinir + takvim kaydı açılır
/// (LicenseId olay izinden best-effort geri kazanılır).
/// Faz 2b — MARKASIZ yayıncının ispatı (sahip kararı 10): lisansı silinmiş
/// yayıncının BrandCode'suz olayları için markasız takvim kaydı açılır.
/// Faz 3 — ayrılış + 3 yılı dolanlar: DÖNEM ispatı (OccurredAt ≤ DepartedAt
/// olayları) ve o lisansın dönem kampanyaları imha edilir, PurgedAt damgalanır,
/// imha kaydı (DataDestructionRecords) aynı kayıtta yazılır.
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

    // İmha kaydının (DataDestructionRecords) ölçütü — kişisel veri, marka kodu yok.
    private const string BrandProofCriterion = "ayrılış+3 yıl (6563 m.13), marka dönemi";
    private const string NoBrandProofCriterion = "lisansın silinişi+3 yıl (6563 m.13), markasız dönem";

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
        await ScheduleNoBrandDeparturesAsync(now, ct);
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

                // I-2 (round 2): ayrılışla kapanmamış her satır markayı canlı
                // tutar — sistem kapanışı (§2.4, DisabledAt=null) sahipliği
                // bitirmez. I-2 (round 3): kendi 30 günü dolmamış AYRILMIŞ
                // kardeş satır da markayı hâlâ canlı tutar — aksi hâlde A'nın
                // 31. günü B'nin dışa aktarım penceresini 19 gün kısaltırdı.
                // Bedel: zombi satır eski ayrılanın onaylarını fazla saklatır,
                // yanlış silmeye tercih edilir. Faz 2 ile aynı kural: satır
                // varsa (ve kendi penceresi dolmadıysa) marka sahipli.
                var brandAlive = await _db.NetgsmAccounts.AnyAsync(
                    b => b.Id != acc.Id && b.BrandCode == acc.BrandCode
                         && (b.Status != NetgsmAccountStatus.Disabled
                             || b.DisabledAt == null || b.DisabledAt > threshold), ct);

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
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                _log.LogInformation(
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

                // I-1: aydınlatma metnindeki "30 gün" sözü HER silme yolunda
                // geçerli — yetim marka ayrılış OLMADAN da doğar (broadcaster
                // BrandCode'u UpsertAsync'te değiştirir, ya da lisans/müşteri
                // cascade ile silinir). Faz 1'deki aynı ödemsiz süreyi burada
                // da uyguluyoruz; yoksa ilk prod koşusu (merge'de otomatik
                // deploy) o ana kadarki HER yetim markayı anında silerdi ve o
                // satırlar hiç dışa aktarılamazdı. İlk koşu bu yüzden yapısal
                // olarak no-op: markalı satırların tamamı #473 sonrası,
                // 30 günden GENÇ.
                var newest = await _db.IysConsents.AsNoTracking()
                    .Where(c => c.BrandCode == brand)
                    .MaxAsync(c => (DateTimeOffset?)c.UpdatedAt, ct);
                if (newest > now - ConsentRetention)
                {
                    _log.LogInformation(
                        "Yetim marka {Brand}: son onay hareketi {Newest} — 30 günlük "
                        + "ödemsiz süre dolmadı, bekliyor", brand, newest);
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
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                _log.LogInformation(
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

    /// <summary>
    /// Faz 2b — markasız yayıncının ispat takvimi (sahip kararı 10, 2026-10-23).
    ///
    /// <para>Doğrulanmış markası olmayan yayıncıda toplayıcı olayı BrandCode=null
    /// yazar (<c>no-brand</c>, <c>invalid-phone</c>) ve kayıt satırı açmaz; olay
    /// lisansa bağlıdır, FK'sız olduğu için lisansla kaskad GİTMEZ. Faz 3 olayları
    /// markayla eşlediği için bu olaylar hiçbir takvime girmiyordu.</para>
    ///
    /// <para><b>Ayrılış = lisansın silinmesi.</b> Lisans yaşadıkça dönem bitmez:
    /// markasız RET'ler lisansın her yeni doğrulamasında yeniden oynatılır
    /// (<see cref="IysConsentCollector.StageReplayNoBrandEventsAsync"/>), yani
    /// olaylar belirli bir markanın dönemine ait değil, lisansın kendi kaydıdır.
    /// Marka ayrılışında (Faz 1/2) bunları da imha etmek, aynı lisansın sonraki
    /// markasına oynatılmış ispatı o marka yaşarken silerdi. Markalı yolun
    /// "hesap/lisans gitti" anlamındaki ayrılışı burada lisansın silinmesidir;
    /// aydınlatma metnindeki "yayıncı platformdan ayrılırsa" da budur.</para>
    ///
    /// <para>Silinme anı bilinmiyor (lisans satırı yok): Faz 2 gibi TESPİT anı
    /// yazılır; süre uzar, asla kısalmaz. Olaylar 3 yıl yaşadığı için aynı dönemi
    /// her gece yeniden açmamak gerekir: lisansın en yeni markasız olayı mevcut
    /// bir markasız randevunun ayrılış anından eskiyse dokunulmaz. LicenseId'si
    /// de null olan eski olaylar (kimin olduğu bilinmiyor) otomatik imha edilmez —
    /// Faz 2'nin boş marka kuralının aynısı.</para>
    /// </summary>
    private async Task ScheduleNoBrandDeparturesAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Faz 2'deki InMemory notu: alt sorgu yerine ayrı listeler, fark bellekte.
        var newestByLicense = await _db.IysConsentEvents.AsNoTracking()
            .Where(e => e.BrandCode == null && e.LicenseId != null)
            .GroupBy(e => e.LicenseId)
            .Select(g => new { LicenseId = g.Key, Newest = g.Max(e => e.OccurredAt) })
            .ToListAsync(ct);
        if (newestByLicense.Count == 0) return;

        var candidateIds = newestByLicense.Select(x => x.LicenseId!.Value).ToList();
        var aliveLicenses = (await _db.Licenses.AsNoTracking()
                .Where(l => candidateIds.Contains(l.Id))
                .Select(l => l.Id)
                .ToListAsync(ct))
            .ToHashSet();
        // Markasız randevular az (silinen lisans başına bir) — hepsi okunur.
        var scheduled = (await _db.NetgsmDepartures.AsNoTracking()
                .Where(d => d.BrandCode == null && d.LicenseId != null)
                .Select(d => new { d.LicenseId, d.DepartedAt })
                .ToListAsync(ct))
            .GroupBy(d => d.LicenseId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(d => d.DepartedAt));

        foreach (var x in newestByLicense)
        {
            var licenseId = x.LicenseId!.Value;
            if (aliveLicenses.Contains(licenseId)) continue;
            if (scheduled.TryGetValue(licenseId, out var departedAt) && departedAt >= x.Newest)
                continue; // bu olaylar zaten açık (ya da imha edilmiş) bir randevunun dönemi içinde

            try
            {
                _db.NetgsmDepartures.Add(new NetgsmDeparture
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    BrandCode = null, // markasız dönem — imha LicenseId ile
                    DepartedAt = now, // gerçek silinme anı bilinmiyor — tespit anı
                    ConsentsDeletedAt = now, // markasız dönemde silinecek durum satırı yok
                });
                await _db.SaveChangesAsync(ct);

                _log.LogInformation(
                    "Markasız ispat takvimi: lisans {LicenseId} silinmiş — markasız olaylar için "
                    + "3 yıllık imha randevusu açıldı", licenseId);
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Markasız ispat takvimi açılamadı (lisans {LicenseId})", licenseId);
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
                //
                // İki dal AYRI kalmalı: `e.BrandCode == dep.BrandCode` markasız
                // randevuda (null) IS NULL'a çevrilir ve BÜTÜN lisansların
                // markasız olaylarını imha ederdi.
                List<IysConsentEvent> events;
                if (dep.BrandCode is { } brand)
                    events = await _db.IysConsentEvents
                        .Where(e => e.BrandCode == brand
                                    && e.OccurredAt <= dep.DepartedAt)
                        .ToListAsync(ct);
                else if (dep.LicenseId is { } noBrandLicenseId)
                    // Markasız dönem (Faz 2b): o lisansın BrandCode'suz olayları.
                    // Aynı lisansın markalı olayları kendi marka randevusunda.
                    events = await _db.IysConsentEvents
                        .Where(e => e.LicenseId == noBrandLicenseId
                                    && e.BrandCode == null
                                    && e.OccurredAt <= dep.DepartedAt)
                        .ToListAsync(ct);
                else
                    events = []; // markasız VE lisanssız randevu yazılmaz; yazılmışsa kimin ispatı belirsiz — dokunma
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

                // Kalıcı imha kaydı (KVKK Ö-13): lisans ve tablo başına, imhayla aynı
                // SaveChanges'te (Record izleyiciye ekler; sıfır satır yazılmaz). İki dönem
                // türü aynı kaydı bırakır, yalnız ölçüt ayrışır.
                var criterion = dep.BrandCode is null ? NoBrandProofCriterion : BrandProofCriterion;
                var recordLicenseId = dep.LicenseId;
                void Record(string target, int count) => DataDestruction.Record(_db, recordLicenseId,
                    target, DataDestruction.Actions.Deleted, count, criterion,
                    DataDestruction.Triggers.Retention, DataDestruction.SystemActor);
                Record("IysConsentEvents", events.Count);
                Record("SmsCampaigns", campaignCount);
                Record("SmsCampaignRecipients", recipientCount);

                var (departureId, brandCode) = (dep.Id, dep.BrandCode);
                dep.PurgedAt = now;
                await _db.SaveChangesAsync(ct);

                _log.LogInformation(
                    "m.13 imhası: ayrılış {DepartureId} marka {Brand} lisans {LicenseId} — "
                    + "{EventCount} olay, {CampaignCount} kampanya, {RecipientCount} alıcı imha edildi",
                    departureId, brandCode ?? "(markasız)", recordLicenseId, events.Count,
                    campaignCount, recipientCount);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                _log.LogInformation(
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
