using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Bir satış isteğinin sunucudaki bağlamı: zarf, lisansın kipi ve (v2'de) kopya satırının bu istekten önceki hâli.
/// Uçlar birim kararlarını bununla verir (K4, K5-ii, §7.1).
/// </summary>
/// <param name="Envelope">Doğrulanmış zarf; v1 için <see cref="SalesEnvelope.Legacy"/>.</param>
/// <param name="Mode">Lisansın şimdiki kipi.</param>
/// <param name="ModeChangedAt">Kipin son değiştiği an.</param>
/// <param name="ReplicaBefore">Kopya satırının bu istekten önceki hâli; v1'de ve kopya bu istekle doğduysa null.</param>
/// <param name="OpeningWindow">İstek açılış penceresinde mi (§4.0); yalnız v2'de true olabilir.</param>
public sealed record SalesRequestContext(
    SalesEnvelope Envelope,
    SalesSyncMode Mode,
    DateTimeOffset? ModeChangedAt,
    ReplicaHistory? ReplicaBefore,
    bool OpeningWindow)
{
    public bool IsV2 => Envelope.IsV2;
}

/// <summary>Devralınmak istenen oturumun bu push'tan önceki sunucu hâli (spec 2026-10-13 §2 tablosu, §8.7). Devredilmiş
/// oturum devralınmaz; birim yazıcısı artık sunucu.</summary>
public enum TakeoverPhase
{
    /// <summary><c>EndedAt</c> boş: canlı yayın. Başka makinedeki kopya yalnız sahip yeterince sustuysa devralır (Y7).</summary>
    Live,

    /// <summary><c>EndedAt</c> dolu, <c>ReleasedAt</c> boş: etiketleri sahipte bekliyor. Başka makinedeki kopya
    /// devralmaz (Y8).</summary>
    HandingOver,
}

/// <summary><c>sales/hello</c>'nun kopya satırına yazdığı alanlar (spec 2026-10-13 §4.5).</summary>
public sealed record ReplicaHello(
    SalesEnvelope Envelope,
    Guid? PreviousReplicaId,
    string? MachineName,
    string? HardwareFingerprint,
    byte FeatureLevel,
    bool ReportsShipping,
    decimal? ReportedThreshold,
    decimal? ReportedFee,
    bool BackfillCompleted,
    bool PaymentHistoryCompleted);

/// <summary>
/// Kopya kaydı (<see cref="WpfReplica"/>) ve protokolsüz istek izi — spec 2026-10-13 §4.0, §4.5.
///
/// <para><b>v2 isteği</b> kopya satırını ekler ya da günceller: <c>FirstSeenAt</c> yalnız eklemede,
/// <c>LastSeenAt</c> her istekte (kalp atışı; sahip sessizliği bununla ölçülür, §4.6), istemci sürümü
/// <c>User-Agent</c>'tan, görülen kip ve anı. Kip 1 bildirildiğinde <c>SeenModeOnAt</c> boşsa ya da mod son
/// açılmadan önce yazılmışsa yeniden yazılır (açılış penceresi, §4.0). Kopya yazımı ucun kendi SaveChanges'inden
/// ÖNCE ve ayrı kaydedilir: ucun 409'u kalp atışını geri almaz, kalp atışındaki bir yarış da paketi düşürmez.</para>
///
/// <para><b>Eşzamanlı ilk dokunuş.</b> Yeni kopyanın iki isteği aynı anda gelirse ikisi de satırı yok görür ve
/// ekler; ikincisi birincil anahtara çarpar. Yazma hatası "şimdi bakınca satır var mı" diye sınıflandırılır
/// (ShopperPaymentSubmissionService deseni): varsa güncellemeye dönülür, yoksa hata olduğu gibi yukarı gider —
/// bilinmeyen bir yazma hatası yarış diye yutulmaz.</para>
///
/// <para><b>Protokolsüz istek (≤0.9.10)</b> kopya satırı açmaz; iki kipte de lisansın
/// <see cref="SalesLegacyTrace"/> satırını yazar (mod kapısının "7 gün eski sürüm izi yok" koşulu, §4.10-3). İz
/// Licenses satırında DEĞİL: Licenses'ın RowVersion'ı eşzamanlılık jetonudur ve satıra yapılan her UPDATE —
/// izlenmeyen ExecuteUpdate dahil — onu ilerletir; License'ı okuyup kaydeden ve çakışmayı yakalamayan bugünkü
/// yollar (payment-account, shopper-code) eski sürümün push'uyla yarışıp 500 alırdı. İlişkisel sağlayıcıda tek
/// bir koşullu <c>MERGE</c>, saatte en çok bir yazım (koşul veritabanında, yarışta da tek yazım; SaveChanges'e
/// girmez). InMemory'de ham SQL olmadığı için aynı kural izlenen varlığa uygulanır (SecurityDataRetentionJob,
/// WhatsAppInboundJob deseni).</para>
/// </summary>
public sealed class SalesReplicaService
{
    /// <summary>Protokolsüz istek izinin en sık yazılma aralığı. Mod kapısı 7 günlük pencereye baktığı için saat
    /// düzeyinde hassasiyet yeterli; her push'ta yazmak Licenses satırını her 30 saniyede günceller.</summary>
    public static readonly TimeSpan LegacyTraceInterval = TimeSpan.FromHours(1);

    private readonly LicenseDbContext _db;
    private readonly SalesSyncOptions _options;
    private readonly ILogger<SalesReplicaService> _log;

    // Devralma yargısının istek içi belleği (servis istek kapsamlı, Program.cs): çağıranın sessiz önceki kopyaları
    // istek başına bir kez okunur; Y8 engeli oturum başına bir kez günlüğe düşer (200 satırlık paket 200 satır
    // yazmasın).
    private readonly Dictionary<(Guid LicenseId, Guid CallerId), IReadOnlyDictionary<Guid, SilentPredecessor>>
        _lineages = new();
    private readonly HashSet<(Guid LicenseId, Guid SessionId)> _loggedTakeoverBlocks = new();

    public SalesReplicaService(
        LicenseDbContext db, IOptions<SalesSyncOptions> options, ILogger<SalesReplicaService> log)
    {
        _db = db;
        _options = options.Value;
        _log = log;
    }

    private sealed record LicenseMode(SalesSyncMode Mode, DateTimeOffset? ChangedAt);

    /// <summary>
    /// Bir satış isteğini kaydeder: v1 ise protokolsüz istek izi, v2 ise kopya satırının kalp atışı. Lisans
    /// sahipliği çağıran uçta denetlenmiş olmalı.
    /// </summary>
    /// <param name="endpoint">Günlük için ucun adı ("orders/sync").</param>
    public async Task<SalesRequestContext> AcceptAsync(
        Guid licenseId, SalesEnvelope envelope, string? userAgent, string endpoint, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await ReadModeAsync(licenseId, ct);

        if (!envelope.IsV2)
        {
            await TraceLegacyAsync(licenseId, license.Mode, endpoint, now, ct);
            return new SalesRequestContext(envelope, license.Mode, license.ChangedAt, null, false);
        }

        return await TouchAsync(licenseId, envelope, userAgent, endpoint, license, now, apply: null, ct);
    }

    /// <summary>
    /// <c>sales/hello</c> (§4.5): kalp atışına ek olarak kopyanın kimlik ve durum alanları. Makine adı, yetenek
    /// düzeyi ve bildirilen kargo ayarı her hello'da güncellenir. <c>PreviousReplicaId</c> bir kez yazılır.
    /// Donanım parmak izinde ilk yazan kazanır; farklı bir parmak izi disk klonudur (§8.7): uyarı düşer ve
    /// <see cref="WpfReplica.HardwareFingerprintConflictAt"/> bir kez yazılır, hiç silinmez. Geri doldurma ve ödeme
    /// geçmişi bayrakları ilk <c>true</c>'da damgalanır, hiç silinmez.
    /// </summary>
    public async Task HelloAsync(Guid licenseId, ReplicaHello hello, string? userAgent, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await ReadModeAsync(licenseId, ct);
        var fingerprintConflict = false;

        await TouchAsync(licenseId, hello.Envelope, userAgent, "sales/hello", license, now, row =>
        {
            fingerprintConflict = false;
            if (hello.MachineName is not null) row.MachineName = hello.MachineName;
            if (!string.IsNullOrEmpty(hello.HardwareFingerprint))
            {
                if (row.HardwareFingerprint is null)
                    row.HardwareFingerprint = hello.HardwareFingerprint;
                else if (!string.Equals(row.HardwareFingerprint, hello.HardwareFingerprint, StringComparison.Ordinal))
                {
                    fingerprintConflict = true;
                    row.HardwareFingerprintConflictAt ??= now;
                }
            }
            row.FeatureLevel = hello.FeatureLevel;
            if (row.PreviousReplicaId is null && hello.PreviousReplicaId is { } previous)
                row.PreviousReplicaId = previous;
            if (hello.ReportsShipping)
            {
                row.ReportedThreshold = hello.ReportedThreshold;
                row.ReportedFee = hello.ReportedFee;
            }
            if (hello.BackfillCompleted) row.BackfillCompletedAt ??= now;
            if (hello.PaymentHistoryCompleted) row.PaymentHistoryCompletedAt ??= now;
        }, ct);

        // Disk klonu (§8.7): aynı veritabanı dosyası iki makinede. İlk parmak izi korunur, iz kopya satırında kalır;
        // makine adı ve parmak izi günlüğe yazılmaz (yalnız kimlikler).
        if (fingerprintConflict)
            _log.LogWarning(
                "sales/hello: kopya {ReplicaId} farklı bir donanım parmak iziyle görüldü — disk klonu şüphesi "
                + "(license={LicenseId}); ilk parmak izi korundu, kopya artık açılış penceresine girmez ve geri "
                + "doldurmada yerel kazanmaz",
                hello.Envelope.ReplicaId, licenseId);
    }

    /// <summary>
    /// Geri doldurmadaki birim ayrışması sayılarını kopya satırına ekler (spec 2026-10-13 §4.3, M20): yerel kazanan
    /// ve sunucu kazanan satırlar. <c>sales-audit</c>, <c>sales-sync status</c> ve <c>enable</c> kuru koşusu gösterir.
    /// Birikimli sayaçtır: aynı partinin yeniden gönderimi yeniden sayılır (satır başına iz tutulmaz; sayı tanı
    /// amaçlıdır, karar vermez).
    ///
    /// <para>İlişkisel sağlayıcıda tek bir artırım UPDATE'i (<c>X = X + @n</c>): aynı kopyanın eşzamanlı iki partisi
    /// birbirinin artışını ezmez. Çağıranın açık işlemi varsa ona katılır — partinin yazımı geri alınırsa sayı da
    /// geri alınır. InMemory'de izlenen varlıkla aynı kural (<see cref="TraceLegacyAsync"/> deseni). Kopya satırı
    /// yoksa (kalp atışı yazılmadan çağrıldı) hiçbir şey yazılmaz.</para>
    /// </summary>
    public async Task AddBackfillDivergenceAsync(
        Guid licenseId, Guid replicaId, int localWins, int serverWins, CancellationToken ct)
    {
        if (localWins == 0 && serverWins == 0) return;

        if (_db.Database.IsRelational())
        {
            await _db.WpfReplicas
                .Where(r => r.LicenseId == licenseId && r.ReplicaId == replicaId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.BackfillDivergedLocal, r => r.BackfillDivergedLocal + localWins)
                    .SetProperty(r => r.BackfillDivergedServer, r => r.BackfillDivergedServer + serverWins), ct);
            return;
        }

        var row = await FindAsync(licenseId, replicaId, ct);
        if (row is null) return;
        row.BackfillDivergedLocal += localWins;
        row.BackfillDivergedServer += serverWins;
        await _db.SaveChangesAsync(ct);
        _db.Entry(row).State = EntityState.Detached;
    }

    /// <summary>Geri yükleme zincirinde izlenen en çok halka; döngüye ya da bozuk veriye karşı sınır.</summary>
    public const int MaxLineageDepth = 16;

    /// <summary>
    /// Zorla bitirilmiş oturumun bitirildiği andaki hâli — K5 istisnası (i)'de geri yüklenmiş kopyanın sahip sayılıp
    /// sayılmayacağının yargısı (spec 2026-10-13 §8.7, Y7, Y8). Zorla bitirme devri bitirmeyle aynı anla yazar: canlı
    /// oturumda <c>EndedAt</c> = <c>ReleasedAt</c> (<c>force-end</c> komutu; CLI de canlı oturumda aynı anı yazar,
    /// §4.10). Devrediliyor oturumu yalnız CLI bitirir ve <c>EndedAt</c>'e dokunmaz: o sahibin kapanış anıdır, devir
    /// anından farklıdır. İkisi farklıysa oturum bitirilirken devrediliyordu — başka makinedeki kopya onu hiç devralmaz
    /// (Y8); eşitse canlıydı (M18) — yalnız sahip yeterince sustuysa devralır (Y7). Ayrı bir kolon gerekmez; tekdüze
    /// <c>EndedAt</c> (§4.1) ve hiç değişmeyen <c>ReleasedAt</c> bu bilgiyi korur.
    /// </summary>
    public static TakeoverPhase PhaseAtForceEnd(DateTimeOffset? endedAt, DateTimeOffset? releasedAt) =>
        endedAt is { } ended && releasedAt is { } released && ended != released
            ? TakeoverPhase.HandingOver
            : TakeoverPhase.Live;

    /// <summary>
    /// Geri yüklenmiş kopya, başka kopyanın devredilmemiş oturumunu devralabilir mi (spec 2026-10-13 §8.7, §11 Y7,
    /// Y8)? <c>sessions/sync</c> (sahiplik geçer), <c>orders/sync</c> (başka kopyanın canlı yayınına ekleme ve K5
    /// istisnası i) ve <c>sales/backfill</c> (canlı yayına ekleme) bu tek yoldan yargılar.
    ///
    /// <para><b>1. Sessiz önceki kopya (S1).</b> Sahip, çağıranın <c>PreviousReplicaId</c> zincirinde ve çağıranın
    /// bu lisansta ilk görüldüğü andan (<see cref="ReplicaHistory.FirstSeenAt"/>) beri hiç istek göndermemiş olmalı.
    /// Zincir ilk konuşan ya da bu lisansta hiç görülmemiş halkada kesilir. Konuşan halka hâlâ açık bir veritabanıdır:
    /// kendi oturumları ve ondan daha eskilerinin oturumları onundur, en yeniye geçmez (iki veritabanı birden açıksa
    /// sahiplik kimseye kendiliğinden geçmez; sahibi dönmeyecek yayını CLI kapatır, §4.10). Görülmemiş halkanın
    /// sessizliği kanıtlanamaz; oturum sahibi zaten her zaman görülmüş bir kopyadır (v2 eklemesi kopya satırını önce
    /// açar).</para>
    ///
    /// <para><b>2. Makine (Y8, sahip kararı 2026-10-14).</b> Çağıranın ve sahibin donanım parmak izi ikisi de
    /// biliniyor ve ordinal eşitse, ikisinde de klon izi yoksa geri yükleme <b>aynı makinededir</b>: önceki veritabanı
    /// dosyası artık yoktur, kopya her hâlde hemen devralır. Aksi hâlde (farklı ya da bilinmeyen parmak izi, klon izi)
    /// çağıran <b>öbür bilgisayardır</b>: canlı oturumu yalnız sahip <see cref="SalesSyncOptions.ForceEndSilenceMinutes"/>
    /// susmuşsa devralır (Y7: yayın ortasında çöken bilgisayar); devrediliyor oturumu hiç devralmaz — yayının
    /// etiketleri sahipte bekliyor, devralma eksik etiketle ödeme isteği ve koli üretirdi (Y8). Sahip hiç dönmeyecekse
    /// yayını Burak'ın CLI'ı kapatır (§4.10). Sahipteki klon izi spec'in "aynı makine" tanımına ek bir koşuldur: o
    /// zaman yayının hangi makinede açıldığı bilinemez ve belirsizlikte Y8 korunur (§8.7).</para>
    ///
    /// <para>Engellenen devralma oturum başına bir kez günlüğe yazılır (yalnız kimlikler; makine adı ve parmak izi
    /// yazılmaz). Sessiz önceki kopyalar istek başına bir kez okunur.</para>
    /// </summary>
    /// <param name="callerId">Çağıran kopya; zincirde yeniden görünürse döngüdür.</param>
    /// <param name="caller">Çağıranın bu istekten önceki hâli; null = kopya bu istekle doğdu (önceki kimliği yok).</param>
    /// <param name="sessionId">Devralınmak istenen oturum (yalnız günlük için).</param>
    /// <param name="owner">Oturumun sunucudaki sahibi.</param>
    /// <param name="phase">Oturumun bu push'tan ÖNCEKİ sunucu hâli.</param>
    /// <param name="now">İsteğin sunucu anı; sessizlik buna göre ölçülür.</param>
    public async Task<bool> CanInheritAsync(
        Guid licenseId,
        Guid callerId,
        ReplicaHistory? caller,
        Guid sessionId,
        Guid owner,
        TakeoverPhase phase,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (caller is null) return false;
        var lineage = await SilentLineageAsync(licenseId, callerId, caller, ct);
        if (!lineage.TryGetValue(owner, out var predecessor)) return false;

        if (SameMachine(caller, predecessor)) return true;
        if (phase == TakeoverPhase.Live && predecessor.LastSeenAt <= now - _options.ForceEndSilence) return true;

        if (_loggedTakeoverBlocks.Add((licenseId, sessionId)))
            _log.LogInformation(
                "Y8: geri yüklenmiş kopya {ReplicaId}, önceki kopya {PreviousReplicaId}'in oturumu {SessionId}'i "
                + "devralmadı — başka ya da bilinmeyen makine, oturum {Phase} (canlı oturum için sessizlik eşiği "
                + "{SilenceMinutes} dk; license={LicenseId})",
                callerId, owner, sessionId, phase, _options.ForceEndSilenceMinutes, licenseId);
        return false;
    }

    /// <summary>
    /// Kopyanın kendisi ve <c>PreviousReplicaId</c> zinciri boyunca geri yüklendiği önceki kopyalar (§8.7), en çok
    /// <see cref="MaxLineageDepth"/> halka. Kayıtlı bir öncülü olan halka dahil edilir; zincir öncülü bilinmeyen ya da
    /// bu lisansta görülmemiş halkada biter (o halka yine dahildir: kopyanın bildirdiği önceki kimliktir). Sessizlik
    /// aranmaz (devralmadan farkı, <see cref="CanInheritAsync"/>): yalnız "bu kopya o kopyanın soyundan mı?" sorusu
    /// içindir — ör. önceki kopyanın açtığı ödeme kirasını geri yüklenmiş kopyanın bitirmesi (<c>finish</c>, §6.4).
    /// </summary>
    /// <param name="previousReplicaId">Kopyanın kayıtlı önceki kimliği (<see cref="ReplicaHistory.PreviousReplicaId"/>).</param>
    public async Task<IReadOnlySet<Guid>> LineageAsync(
        Guid licenseId, Guid replicaId, Guid? previousReplicaId, CancellationToken ct)
    {
        var lineage = new HashSet<Guid> { replicaId };
        var next = previousReplicaId;
        for (var depth = 0; depth < MaxLineageDepth && next is { } link && lineage.Add(link); depth++)
            next = await _db.WpfReplicas.AsNoTracking()
                .Where(r => r.LicenseId == licenseId && r.ReplicaId == link)
                .Select(r => r.PreviousReplicaId)
                .SingleOrDefaultAsync(ct);
        return lineage;
    }

    /// <summary>Sessiz önceki kopyanın devralma yargısına giren hâli.</summary>
    private sealed record SilentPredecessor(
        DateTimeOffset LastSeenAt, string? HardwareFingerprint, bool FingerprintConflict);

    /// <summary>Aynı makinede geri yükleme: iki parmak izi de biliniyor, ordinal eşit, iki tarafta da klon izi yok.</summary>
    private static bool SameMachine(ReplicaHistory caller, SilentPredecessor owner) =>
        !caller.FingerprintConflict
        && !owner.FingerprintConflict
        && !string.IsNullOrEmpty(caller.HardwareFingerprint)
        && !string.IsNullOrEmpty(owner.HardwareFingerprint)
        && string.Equals(caller.HardwareFingerprint, owner.HardwareFingerprint, StringComparison.Ordinal);

    /// <summary>
    /// Çağıranın <c>PreviousReplicaId</c> zinciri boyunca, çağıranın ilk görüldüğü andan sonra hiç istek göndermemiş
    /// kopyalar (koşul 1, <see cref="CanInheritAsync"/>); istek başına bir kez okunur.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, SilentPredecessor>> SilentLineageAsync(
        Guid licenseId, Guid callerId, ReplicaHistory caller, CancellationToken ct)
    {
        if (_lineages.TryGetValue((licenseId, callerId), out var cached)) return cached;

        var silent = new Dictionary<Guid, SilentPredecessor>();
        if (caller.PreviousReplicaId is { } next)
        {
            var visited = new HashSet<Guid> { callerId };
            for (var depth = 0; depth < MaxLineageDepth && visited.Add(next); depth++)
            {
                var link = await _db.WpfReplicas.AsNoTracking()
                    .Where(r => r.LicenseId == licenseId && r.ReplicaId == next)
                    .Select(r => new
                    {
                        r.LastSeenAt,
                        r.PreviousReplicaId,
                        r.HardwareFingerprint,
                        Conflict = r.HardwareFingerprintConflictAt != null,
                    })
                    .SingleOrDefaultAsync(ct);
                if (link is null || link.LastSeenAt > caller.FirstSeenAt) break;

                silent[next] = new SilentPredecessor(link.LastSeenAt, link.HardwareFingerprint, link.Conflict);
                if (link.PreviousReplicaId is not { } previous) break;
                next = previous;
            }
        }

        _lineages[(licenseId, callerId)] = silent;
        return silent;
    }

    private async Task<LicenseMode> ReadModeAsync(Guid licenseId, CancellationToken ct) =>
        await _db.Licenses.AsNoTracking()
            .Where(l => l.Id == licenseId)
            .Select(l => new LicenseMode(l.SalesSyncMode, l.SalesSyncModeChangedAt))
            .SingleAsync(ct);

    private async Task<SalesRequestContext> TouchAsync(
        Guid licenseId,
        SalesEnvelope envelope,
        string? userAgent,
        string endpoint,
        LicenseMode license,
        DateTimeOffset now,
        Action<WpfReplica>? apply,
        CancellationToken ct)
    {
        var clientVersion = ClientVersion.RawFromUserAgent(userAgent);

        var row = await FindAsync(licenseId, envelope.ReplicaId, ct);
        var before = row is null ? null : HistoryOf(row);

        if (row is null)
        {
            row = new WpfReplica
            {
                LicenseId = licenseId,
                ReplicaId = envelope.ReplicaId,
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            Heartbeat(row, envelope, clientVersion, license, now);
            apply?.Invoke(row);
            _db.WpfReplicas.Add(row);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Aynı kopyanın eşzamanlı ilk isteği satırı bizden önce ekledi mi? Eklediyse kazananın satırı
                // güncellenir; bu isteğin "önceki hâli" o satırdır.
                _db.Entry(row).State = EntityState.Detached;
                var winner = await FindAsync(licenseId, envelope.ReplicaId, ct);
                if (winner is null) throw;
                row = winner;
                before = HistoryOf(row);
                Heartbeat(row, envelope, clientVersion, license, now);
                apply?.Invoke(row);
                await _db.SaveChangesAsync(ct);
            }
        }
        else
        {
            Heartbeat(row, envelope, clientVersion, license, now);
            apply?.Invoke(row);
            await _db.SaveChangesAsync(ct);
        }

        // Ucun kendi değişiklik izleyicisi kalp atışını taşımasın.
        _db.Entry(row).State = EntityState.Detached;

        var window = SalesOpeningWindow.Applies(license.Mode, license.ChangedAt, envelope.SeenMode, before);
        if (license.Mode == SalesSyncMode.On && envelope.SeenMode == SalesSyncMode.Off && !window)
            _log.LogInformation(
                "{Endpoint}: SeenMode = 0 açılış penceresi dışında, On kurallarıyla yargılanır "
                + "(license={LicenseId}, replica={ReplicaId})",
                endpoint, licenseId, envelope.ReplicaId);

        return new SalesRequestContext(envelope, license.Mode, license.ChangedAt, before, window);
    }

    private Task<WpfReplica?> FindAsync(Guid licenseId, Guid replicaId, CancellationToken ct) =>
        _db.WpfReplicas.SingleOrDefaultAsync(r => r.LicenseId == licenseId && r.ReplicaId == replicaId, ct);

    /// <summary>Pencere ve yerel kazanır yargısı için kopya geçmişi (klon izi dahil, §4.0, §4.3).</summary>
    private static ReplicaHistory HistoryOf(WpfReplica row) => ReplicaHistory.Of(row);

    private static void Heartbeat(
        WpfReplica row, SalesEnvelope envelope, string? clientVersion, LicenseMode license, DateTimeOffset now)
    {
        if (row.LastSeenAt < now) row.LastSeenAt = now;
        if (clientVersion is not null) row.ClientVersion = clientVersion;
        row.Protocol = (byte)envelope.Protocol;
        row.SeenMode = envelope.SeenMode;
        row.SeenModeAt = now;
        // Kopyanın modu ilk gördüğü an; mod yeniden açıldıysa (Off → On) yeni dönemin ilk anı.
        if (envelope.SeenMode == SalesSyncMode.On
            && (row.SeenModeOnAt is null
                || (license.ChangedAt is { } changedAt && row.SeenModeOnAt < changedAt)))
            row.SeenModeOnAt = now;
    }

    private async Task TraceLegacyAsync(
        Guid licenseId, SalesSyncMode mode, string endpoint, DateTimeOffset now, CancellationToken ct)
    {
        var staleBefore = now - LegacyTraceInterval;
        bool written;

        if (_db.Database.IsRelational())
        {
            // Tek deyim, koşul veritabanında: satır yoksa eklenir, eşikten eskiyse güncellenir, yoksa dokunulmaz.
            // HOLDLOCK aynı lisansın iki eski PC'sinin eşzamanlı ilk izini sıraya sokar (birincil anahtar yarışı
            // yok). SaveChanges'e girmez: ucun değişiklik izleyicisine ve kaydetme kancalarına dokunmaz.
            written = await _db.Database.ExecuteSqlInterpolatedAsync($"""
                MERGE SalesLegacyTraces WITH (HOLDLOCK) AS t
                USING (VALUES ({licenseId})) AS s (LicenseId) ON t.LicenseId = s.LicenseId
                WHEN MATCHED AND t.LastLegacySalesPushAt < {staleBefore} THEN
                    UPDATE SET LastLegacySalesPushAt = {now}
                WHEN NOT MATCHED THEN
                    INSERT (LicenseId, LastLegacySalesPushAt) VALUES ({licenseId}, {now});
                """, ct) > 0;
        }
        else
        {
            var trace = await _db.SalesLegacyTraces.SingleOrDefaultAsync(t => t.LicenseId == licenseId, ct);
            written = trace is null || trace.LastLegacySalesPushAt < staleBefore;
            if (trace is null)
                _db.SalesLegacyTraces.Add(trace = new SalesLegacyTrace { LicenseId = licenseId });
            if (written)
            {
                trace.LastLegacySalesPushAt = now;
                await _db.SaveChangesAsync(ct);
            }
            _db.Entry(trace).State = EntityState.Detached;
        }

        // Uyarı yalnız mod On'da (§4.0) ve izle aynı sıklıkta: eski sürüm her 30–60 sn'de push'luyor, her istekte
        // bir uyarı satırı günlüğü boğardı.
        if (written && mode == SalesSyncMode.On)
            _log.LogWarning(
                "{Endpoint}: mod On'da protokolsüz (≤0.9.10) satış isteği (license={LicenseId}); eski sürüm bir "
                + "bilgisayar dönmüş olabilir",
                endpoint, licenseId);
    }
}
