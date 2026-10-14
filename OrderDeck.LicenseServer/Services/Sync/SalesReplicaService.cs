using Microsoft.EntityFrameworkCore;
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
/// <para><b>Protokolsüz istek (≤0.9.10)</b> kopya satırı açmaz; iki kipte de <c>Licenses.LastLegacySalesPushAt</c>
/// yazar (mod kapısının "7 gün eski sürüm izi yok" koşulu, §4.10-3). İz izlenen bir License varlığıyla
/// YAZILMAZ: Licenses'ın RowVersion jetonu var ve ActivationManager onun çakışmasında yeniden deniyor; izlenen
/// yazım hem push'u o çakışmayla 500'e düşürebilir hem de her push'ta etkinleştirmelerle yarışırdı. İlişkisel
/// sağlayıcıda tek bir koşullu UPDATE, saatte en çok bir kez (koşul veritabanında, yarışta da tek yazım).
/// InMemory'de ExecuteUpdate olmadığı için aynı kural izlenen varlığa uygulanır (SecurityDataRetentionJob,
/// WhatsAppInboundJob deseni).</para>
/// </summary>
public sealed class SalesReplicaService
{
    /// <summary>Protokolsüz istek izinin en sık yazılma aralığı. Mod kapısı 7 günlük pencereye baktığı için saat
    /// düzeyinde hassasiyet yeterli; her push'ta yazmak Licenses satırını her 30 saniyede günceller.</summary>
    public static readonly TimeSpan LegacyTraceInterval = TimeSpan.FromHours(1);

    private readonly LicenseDbContext _db;
    private readonly ILogger<SalesReplicaService> _log;

    public SalesReplicaService(LicenseDbContext db, ILogger<SalesReplicaService> log)
    {
        _db = db;
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
    /// Donanım parmak izinde ilk yazan kazanır; farklı bir parmak izi disk klonu uyarısıdır (§8.7). Geri doldurma
    /// ve ödeme geçmişi bayrakları ilk <c>true</c>'da damgalanır, hiç silinmez.
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
                    fingerprintConflict = true;
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

        // Disk klonu (§8.7): aynı veritabanı dosyası iki makinede. İlk parmak izi korunur; makine adı ve parmak
        // izi günlüğe yazılmaz (yalnız kimlikler).
        if (fingerprintConflict)
            _log.LogWarning(
                "sales/hello: kopya {ReplicaId} farklı bir donanım parmak iziyle görüldü — disk klonu şüphesi "
                + "(license={LicenseId}); ilk parmak izi korundu",
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
    /// Geri yüklenmiş kopyanın devralabileceği önceki kopyalar (spec 2026-10-13 §8.7): çağıranın
    /// <c>PreviousReplicaId</c> zinciri boyunca, çağıranın bu lisansta ilk görüldüğü andan
    /// (<see cref="ReplicaHistory.FirstSeenAt"/>) sonra hiç istek göndermemiş kopyalar.
    ///
    /// <para>Zincir ilk konuşan ya da bu lisansta hiç görülmemiş halkada kesilir. Konuşan halka hâlâ açık bir
    /// veritabanıdır: kendi oturumları ve ondan daha eskilerinin oturumları onundur, en yeniye geçmez (iki veritabanı
    /// birden açıksa sahiplik kimseye kendiliğinden geçmez; sahibi dönmeyecek yayını CLI kapatır, §4.10). Görülmemiş
    /// halkanın sessizliği kanıtlanamaz; oturum sahibi zaten her zaman görülmüş bir kopyadır (v2 eklemesi kopya
    /// satırını önce açar).</para>
    /// </summary>
    /// <param name="callerId">Çağıran kopya; zincirde yeniden görünürse döngüdür.</param>
    /// <param name="caller">Çağıranın bu istekten önceki hâli; null = kopya bu istekle doğdu (önceki kimliği yok).</param>
    public async Task<IReadOnlySet<Guid>> SilentPredecessorsAsync(
        Guid licenseId, Guid callerId, ReplicaHistory? caller, CancellationToken ct)
    {
        var silent = new HashSet<Guid>();
        if (caller?.PreviousReplicaId is not { } next) return silent;

        var visited = new HashSet<Guid> { callerId };
        for (var depth = 0; depth < MaxLineageDepth && visited.Add(next); depth++)
        {
            var link = await _db.WpfReplicas.AsNoTracking()
                .Where(r => r.LicenseId == licenseId && r.ReplicaId == next)
                .Select(r => new { r.LastSeenAt, r.PreviousReplicaId })
                .SingleOrDefaultAsync(ct);
            if (link is null || link.LastSeenAt > caller.FirstSeenAt) break;

            silent.Add(next);
            if (link.PreviousReplicaId is not { } previous) break;
            next = previous;
        }

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

    /// <summary>
    /// Pencere yargısı için kopya geçmişi. İkinci parmak izi bugün kalıcı olarak saklanmıyor: WpfReplicas'ta tek
    /// parmak izi kolonu var ve uyuşmazlık yalnız hello anında görülüp günlüğe yazılıyor. Bu yüzden burada
    /// <c>false</c>; S1'de mod açılamadığı için (CLI S3'te) pencerenin bu koşulu henüz hiçbir kararı etkilemez.
    /// Mod kapısından (§4.10-2) önce uyuşmazlığın kalıcı bir izi gerekir.
    /// </summary>
    private static ReplicaHistory HistoryOf(WpfReplica row) => ReplicaHistory.Of(row, fingerprintConflict: false);

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
            written = await _db.Licenses
                .Where(l => l.Id == licenseId
                    && (l.LastLegacySalesPushAt == null || l.LastLegacySalesPushAt < staleBefore))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.LastLegacySalesPushAt, now), ct) > 0;
        }
        else
        {
            var license = await _db.Licenses.SingleAsync(l => l.Id == licenseId, ct);
            written = license.LastLegacySalesPushAt is null || license.LastLegacySalesPushAt < staleBefore;
            if (written)
            {
                license.LastLegacySalesPushAt = now;
                await _db.SaveChangesAsync(ct);
            }
            _db.Entry(license).State = EntityState.Detached;
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
