using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// <see cref="SalesReplicaService"/>'in uçlara verdiği bağlam ve günlükleri — spec 2026-10-13 §4.0. Açılış penceresi
/// kopyanın bu istekten ÖNCEKİ hâli üzerinden yargılanır; pencere dışında kalan <c>SeenMode = 0</c> istekleri mod
/// On'da günlüğe sayılır (ad yok, yalnız kimlik). Protokolsüz istek uyarısı yalnız mod On'da ve iz sıklığında.
/// </summary>
public sealed class SalesReplicaServiceTests
{
    private static readonly DateTimeOffset ModeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);

    private readonly string _dbName = Guid.NewGuid().ToString();

    private LicenseDbContext Db() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static SalesEnvelope V2(Guid replicaId, SalesSyncMode seen) => new(SalesEnvelope.V2, replicaId, seen);

    /// <summary>
    /// Mod açılmadan önce var olan, modu henüz görmemiş kopyanın 0'ı pencerededir. Aynı kopya bir kez 1 bildirince
    /// sonraki 0'ı pencere değildir — 1'in yazdığı damga ancak bir SONRAKİ isteğin "önceki hâli"nde görünür. Pencere
    /// dışındaki 0 günlüğe yazılır.
    /// </summary>
    [Fact]
    public async Task Pencere_istegin_onceki_satiri_uzerinden_yargilanir()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.On);
        var replicaId = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, replicaId, firstSeenAt: ModeChangedAt.AddDays(-2));

        var (first, firstLog) = await AcceptAsync(licenseId, V2(replicaId, SalesSyncMode.Off));
        first.OpeningWindow.Should().BeTrue();
        first.ReplicaBefore!.FirstSeenAt.Should().Be(ModeChangedAt.AddDays(-2));
        firstLog.Entries.Should().BeEmpty();

        var (sawOn, _) = await AcceptAsync(licenseId, V2(replicaId, SalesSyncMode.On));
        sawOn.OpeningWindow.Should().BeFalse("1 bildiren istek pencere değildir");
        sawOn.ReplicaBefore!.SeenModeOnAt.Should().BeNull("damga bu isteğin dokunuşuyla yazıldı, öncesinde yoktu");

        var (after, afterLog) = await AcceptAsync(licenseId, V2(replicaId, SalesSyncMode.Off));
        after.OpeningWindow.Should().BeFalse("kopya bu açılıştan beri modu gördü");
        afterLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message.Contains(replicaId.ToString()) && e.Message.Contains("orders/sync"));
    }

    /// <summary>Bu istekle doğan kopya (mod açıldıktan sonra kurulmuş) ya da yedekten geri yüklenmiş kopya pencerede
    /// değildir; 0'ları On kurallarıyla yargılanır ve sayılır.</summary>
    [Fact]
    public async Task Yeni_ve_geri_yuklenmis_kopya_pencerede_degil()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.On);

        var (fresh, freshLog) = await AcceptAsync(licenseId, V2(Guid.NewGuid(), SalesSyncMode.Off));
        fresh.ReplicaBefore.Should().BeNull();
        fresh.OpeningWindow.Should().BeFalse();
        freshLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information);

        var restoredId = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, restoredId, firstSeenAt: ModeChangedAt.AddDays(-2),
            previousReplicaId: Guid.NewGuid());
        var (restored, _) = await AcceptAsync(licenseId, V2(restoredId, SalesSyncMode.Off));
        restored.OpeningWindow.Should().BeFalse();
    }

    /// <summary>Mod Off'ta pencere yoktur ve 0 bildirimi olağandır: günlük satırı düşmez.</summary>
    [Fact]
    public async Task Mod_Off_ta_pencere_yok_gunluk_yok()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var replicaId = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, replicaId, firstSeenAt: ModeChangedAt.AddDays(-2));

        var (ctx, log) = await AcceptAsync(licenseId, V2(replicaId, SalesSyncMode.Off));

        ctx.OpeningWindow.Should().BeFalse();
        ctx.Mode.Should().Be(SalesSyncMode.Off);
        log.Entries.Should().BeEmpty();
    }

    /// <summary>Protokolsüz istek: mod On'da uyarı, iz sıklığında (saatte bir); mod Off'ta iz yazılır ama uyarı
    /// yok. Uyarıda lisans kimliği ve uç var, ad yok.</summary>
    [Fact]
    public async Task Protokolsuz_istek_uyarisi_yalniz_mod_On_da_ve_iz_sikliginda()
    {
        var onLicense = await SeedLicenseAsync(SalesSyncMode.On);
        var (first, firstLog) = await AcceptAsync(onLicense, SalesEnvelope.Legacy, "shipments/sync");
        first.IsV2.Should().BeFalse();
        firstLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning
            && e.Message.Contains(onLicense.ToString()) && e.Message.Contains("shipments/sync"));

        var (_, secondLog) = await AcceptAsync(onLicense, SalesEnvelope.Legacy, "shipments/sync");
        secondLog.Entries.Should().BeEmpty("iz pencere içinde yeniden yazılmadı");

        var offLicense = await SeedLicenseAsync(SalesSyncMode.Off);
        var (_, offLog) = await AcceptAsync(offLicense, SalesEnvelope.Legacy, "orders/sync");
        offLog.Entries.Should().BeEmpty();
        await using var db = Db();
        (await db.SalesLegacyTraces.SingleAsync(t => t.LicenseId == offLicense)).LastLegacySalesPushAt.Should().BeAfter(DateTimeOffset.MinValue);
        (await db.WpfReplicas.AnyAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Disk klonu (§8.7): aynı ReplicaId farklı bir donanım parmak iziyle hello gönderir. İlk parmak izi korunur ve
    /// uyarı düşer; uyarıda yalnız kimlikler var — parmak izi ve makine adı günlüğe yazılmaz. Aynı parmak iziyle
    /// tekrar hello olağandır.
    /// </summary>
    [Fact]
    public async Task Hello_farkli_parmak_izi_klon_uyarisi_yazar_ilk_izi_korur()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var replicaId = Guid.NewGuid();
        var original = $"fp-{Guid.NewGuid():N}";
        var clone = $"fp-{Guid.NewGuid():N}";

        (await HelloAsync(licenseId, replicaId, original)).Entries.Should().BeEmpty();
        (await HelloAsync(licenseId, replicaId, original)).Entries.Should().BeEmpty("aynı makinenin yeniden hello'su");

        var cloneLog = await HelloAsync(licenseId, replicaId, clone);

        var warning = cloneLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain(replicaId.ToString()).And.Contain(licenseId.ToString());
        warning.Message.Should().NotContain(original).And.NotContain(clone).And.NotContain("PC-KLON");
        await using var db = Db();
        var row = await db.WpfReplicas.AsNoTracking().SingleAsync(r => r.ReplicaId == replicaId);
        row.HardwareFingerprint.Should().Be(original);
        row.HardwareFingerprintConflictAt.Should().NotBeNull("uyuşmazlık kalıcı iz bırakır, yalnız günlüğe düşmez");

        // İz bir kez yazılır, hiç silinmez: özgün makinenin sonraki hello'su onu temizlemez ya da ilerletmez.
        await HelloAsync(licenseId, replicaId, original);
        await HelloAsync(licenseId, replicaId, clone);
        (await db.WpfReplicas.AsNoTracking().SingleAsync(r => r.ReplicaId == replicaId))
            .HardwareFingerprintConflictAt.Should().Be(row.HardwareFingerprintConflictAt);
    }

    /// <summary>
    /// Disk klonu açılış penceresini kapatır (§4.0 koşul 3, §8.7): mod açılmadan önce var olan, modu görmemiş kopya
    /// pencerededir; aynı ReplicaId farklı parmak iziyle hello gönderdikten sonra hiçbir isteği pencere değildir ve
    /// uca verilen geçmiş klonu taşır (geri doldurmadaki yerel kazanır kararı da bunu okur, §4.3).
    /// </summary>
    [Fact]
    public async Task Klon_izi_pencereyi_kapatir_ve_gecmise_tasinir()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.On);
        var replicaId = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, replicaId, firstSeenAt: ModeChangedAt.AddDays(-2));
        await HelloAsync(licenseId, replicaId, $"fp-{Guid.NewGuid():N}");

        var (beforeClone, _) = await AcceptAsync(licenseId, V2(replicaId, SalesSyncMode.Off));
        beforeClone.OpeningWindow.Should().BeTrue("kontrol: klon görülmeden kopya pencerededir");
        beforeClone.ReplicaBefore!.FingerprintConflict.Should().BeFalse();

        await HelloAsync(licenseId, replicaId, $"fp-{Guid.NewGuid():N}");

        var (afterClone, _) = await AcceptAsync(licenseId, V2(replicaId, SalesSyncMode.Off));
        afterClone.ReplicaBefore!.FingerprintConflict.Should().BeTrue();
        afterClone.OpeningWindow.Should().BeFalse("hangi makinenin özgün olduğu bilinemez");
    }

    // ── Geri yüklenmiş kopyanın devralması (§8.7, §11 Y7, Y8) ─────────────────

    /// <summary>
    /// Y8: başka ya da bilinmeyen makineye geri yüklenen kopya, sahibi iki saattir sussa da devrediliyor oturumu
    /// devralmaz — yayının etiketleri sahipte bekliyor. Parmak izi her iki taraftan da bilinmeyebilir (hello'suz
    /// kopya). Engel oturum başına bir kez günlüğe düşer; günlükte yalnız kimlikler var, parmak izi yok.
    /// </summary>
    [Theory]
    [InlineData("A", "B")]
    [InlineData(null, "B")]
    [InlineData("A", null)]
    [InlineData(null, null)]
    public async Task Baska_ya_da_bilinmeyen_makinede_devrediliyor_oturum_devralinmaz(
        string? previousMachine, string? restoredMachine)
    {
        var lineage = await SeedLineageAsync(
            silence: TimeSpan.FromHours(2), Fingerprint(previousMachine), Fingerprint(restoredMachine));
        var sessionId = Guid.NewGuid();
        var log = new LogRecorder<SalesReplicaService>();
        await using var db = Db();
        var service = Service(db, log);

        (await lineage.CanInheritAsync(service, TakeoverPhase.HandingOver, sessionId)).Should().BeFalse();
        (await lineage.CanInheritAsync(service, TakeoverPhase.HandingOver, sessionId)).Should().BeFalse();

        var entry = log.Entries.Should().ContainSingle("aynı oturumun engeli istek başına bir kez yazılır").Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain(sessionId.ToString())
            .And.Contain(lineage.Previous.ToString())
            .And.Contain(lineage.Restored.ToString());
        foreach (var fingerprint in new[] { lineage.PreviousFingerprint, lineage.RestoredFingerprint })
            if (fingerprint is not null) entry.Message.Should().NotContain(fingerprint);
    }

    /// <summary>
    /// Y7, Y8: başka makinedeki kopya canlı oturumu yalnız sahip <c>ForceEndSilenceMinutes</c> (varsayılan 15)
    /// sustuktan sonra devralır; eşik ayardan okunur.
    /// </summary>
    [Theory]
    [InlineData(5, null, false)]
    [InlineData(14, null, false)]
    [InlineData(20, null, true)]
    [InlineData(20, 30, false)]
    [InlineData(40, 30, true)]
    public async Task Baska_makinede_canli_oturum_yalniz_sessizlik_esiginden_sonra_devralinir(
        int silentMinutes, int? thresholdMinutes, bool inherits)
    {
        var lineage = await SeedLineageAsync(TimeSpan.FromMinutes(silentMinutes), Fingerprint("A"), Fingerprint("B"));
        var options = new SalesSyncOptions();
        if (thresholdMinutes is { } minutes) options.ForceEndSilenceMinutes = minutes;
        await using var db = Db();

        (await lineage.CanInheritAsync(Service(db, options: options), TakeoverPhase.Live, Guid.NewGuid()))
            .Should().Be(inherits);
    }

    /// <summary>Sahip kararı (2026-10-14): aynı makinede geri yüklenen kopya her hâlde hemen devralır — önceki
    /// veritabanı dosyası artık yok.</summary>
    [Theory]
    [InlineData(TakeoverPhase.Live)]
    [InlineData(TakeoverPhase.HandingOver)]
    public async Task Ayni_makinede_hemen_devralir(TakeoverPhase phase)
    {
        var fingerprint = Fingerprint("A");
        var lineage = await SeedLineageAsync(TimeSpan.FromMinutes(1), fingerprint, fingerprint);
        await using var db = Db();

        (await lineage.CanInheritAsync(Service(db), phase, Guid.NewGuid())).Should().BeTrue();
    }

    /// <summary>
    /// Klon izi (§8.7): aynı parmak izi taşısa da kopyalardan birinde ikinci parmak izi görülmüşse hangi makinenin
    /// özgün olduğu bilinemez — çağıran başka makine sayılır: devrediliyor oturumu ve kısa süre susan sahibin canlı
    /// oturumunu devralmaz.
    /// </summary>
    [Theory]
    [InlineData(true, false, TakeoverPhase.HandingOver)]
    [InlineData(true, false, TakeoverPhase.Live)]
    [InlineData(false, true, TakeoverPhase.HandingOver)]
    [InlineData(false, true, TakeoverPhase.Live)]
    public async Task Klon_izi_varsa_ayni_parmak_izi_baska_makine_sayilir(
        bool restoredCloned, bool previousCloned, TakeoverPhase phase)
    {
        var fingerprint = Fingerprint("A");
        var lineage = await SeedLineageAsync(TimeSpan.FromMinutes(1), fingerprint, fingerprint,
            previousCloned: previousCloned, restoredCloned: restoredCloned);
        await using var db = Db();

        (await lineage.CanInheritAsync(Service(db), phase, Guid.NewGuid())).Should().BeFalse();
    }

    /// <summary>S1'in koşulu önce gelir: önceki kopya yenisinin ilk görüldüğü andan sonra konuştuysa aynı makinede ve
    /// uzun sessizlikte de devralma yok; önceki kimliği olmayan kopya hiçbir şey devralmaz.</summary>
    [Fact]
    public async Task Konusan_onceki_kopyanin_ve_onceki_kimligi_olmayanin_devralmasi_yok()
    {
        var fingerprint = Fingerprint("A");
        var lineage = await SeedLineageAsync(TimeSpan.FromHours(2), fingerprint, fingerprint, previousSpokeLater: true);
        await using var db = Db();
        var service = Service(db);

        (await lineage.CanInheritAsync(service, TakeoverPhase.Live, Guid.NewGuid())).Should().BeFalse();
        (await lineage.CanInheritAsync(service, TakeoverPhase.HandingOver, Guid.NewGuid())).Should().BeFalse();
        (await service.CanInheritAsync(lineage.LicenseId, Guid.NewGuid(), caller: null, Guid.NewGuid(),
            lineage.Previous, TakeoverPhase.Live, DateTimeOffset.UtcNow, CancellationToken.None)).Should().BeFalse();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task<(SalesRequestContext Context, LogRecorder<SalesReplicaService> Log)> AcceptAsync(
        Guid licenseId, SalesEnvelope envelope, string endpoint = "orders/sync")
    {
        await using var db = Db();
        var log = new LogRecorder<SalesReplicaService>();
        var ctx = await Service(db, log)
            .AcceptAsync(licenseId, envelope, "OrderDeck-WPF/0.9.11", endpoint, CancellationToken.None);
        db.ChangeTracker.Entries().Should().BeEmpty("kalp atışı ucun değişiklik izleyicisinde kalmaz");
        return (ctx, log);
    }

    private async Task<LogRecorder<SalesReplicaService>> HelloAsync(Guid licenseId, Guid replicaId, string fingerprint)
    {
        await using var db = Db();
        var log = new LogRecorder<SalesReplicaService>();
        await Service(db, log).HelloAsync(
            licenseId,
            new ReplicaHello(V2(replicaId, SalesSyncMode.Off), null, "PC-KLON", fingerprint, 3,
                ReportsShipping: false, null, null, BackfillCompleted: false, PaymentHistoryCompleted: false),
            "OrderDeck-WPF/0.9.11",
            CancellationToken.None);
        return log;
    }

    private static SalesReplicaService Service(
        LicenseDbContext db, LogRecorder<SalesReplicaService>? log = null, SalesSyncOptions? options = null) =>
        new(db, Options.Create(options ?? new SalesSyncOptions()), log ?? new LogRecorder<SalesReplicaService>());

    // Makine harfi → bu testin parmak izi (üretilmiş; null = bilinmiyor).
    private readonly Dictionary<string, string> _fingerprints = new();

    private string? Fingerprint(string? machine)
    {
        if (machine is null) return null;
        if (!_fingerprints.TryGetValue(machine, out var fingerprint))
            _fingerprints[machine] = fingerprint = $"fp-{Guid.NewGuid():N}";
        return fingerprint;
    }

    /// <summary>Geri yüklenmiş kopya ve önceki kopyası. Önceki kopya <paramref name="silence"/> kadar önce son kez
    /// konuştu; yeni kopya ondan sonra ilk kez görüldü (S1'in "sustu" koşulu tutar), ya da
    /// <paramref name="previousSpokeLater"/> ile ondan önce görüldü (önceki kopya ondan sonra konuştu).</summary>
    private async Task<Lineage> SeedLineageAsync(
        TimeSpan silence,
        string? previousFingerprint,
        string? restoredFingerprint,
        bool previousCloned = false,
        bool restoredCloned = false,
        bool previousSpokeLater = false)
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var now = DateTimeOffset.UtcNow;
        var previous = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var previousLastSeen = now - silence;
        var restoredFirstSeen = previousSpokeLater
            ? previousLastSeen.AddMinutes(-1)
            : previousLastSeen + (now - previousLastSeen) / 2;
        await SeedReplicaAsync(licenseId, previous, firstSeenAt: now.AddDays(-3), lastSeenAt: previousLastSeen,
            fingerprint: previousFingerprint, conflictAt: previousCloned ? now.AddDays(-2) : null);
        await SeedReplicaAsync(licenseId, restored, firstSeenAt: restoredFirstSeen, previousReplicaId: previous,
            fingerprint: restoredFingerprint, conflictAt: restoredCloned ? restoredFirstSeen : null);
        await using var db = Db();
        var caller = ReplicaHistory.Of(await db.WpfReplicas.AsNoTracking()
            .SingleAsync(r => r.LicenseId == licenseId && r.ReplicaId == restored));
        return new Lineage(licenseId, previous, restored, caller, previousFingerprint, restoredFingerprint);
    }

    private sealed record Lineage(
        Guid LicenseId,
        Guid Previous,
        Guid Restored,
        ReplicaHistory Caller,
        string? PreviousFingerprint,
        string? RestoredFingerprint)
    {
        /// <summary>Geri yüklenmiş kopya, önceki kopyanın oturumunu devralabilir mi?</summary>
        public Task<bool> CanInheritAsync(SalesReplicaService service, TakeoverPhase phase, Guid sessionId) =>
            service.CanInheritAsync(LicenseId, Restored, Caller, sessionId, Previous, phase, DateTimeOffset.UtcNow,
                CancellationToken.None);
    }

    private async Task<Guid> SeedLicenseAsync(SalesSyncMode mode)
    {
        await using var db = Db();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-KOPYA-" + Guid.NewGuid().ToString("N"),
            CustomerId = Guid.NewGuid(),
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? ModeChangedAt : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private async Task SeedReplicaAsync(
        Guid licenseId,
        Guid replicaId,
        DateTimeOffset firstSeenAt,
        Guid? previousReplicaId = null,
        DateTimeOffset? lastSeenAt = null,
        string? fingerprint = null,
        DateTimeOffset? conflictAt = null)
    {
        await using var db = Db();
        db.WpfReplicas.Add(new WpfReplica
        {
            LicenseId = licenseId,
            ReplicaId = replicaId,
            Protocol = 2,
            SeenMode = SalesSyncMode.Off,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = lastSeenAt ?? firstSeenAt,
            PreviousReplicaId = previousReplicaId,
            HardwareFingerprint = fingerprint,
            HardwareFingerprintConflictAt = conflictAt,
        });
        await db.SaveChangesAsync();
    }
}
