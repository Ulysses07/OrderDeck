using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        (await db.Licenses.SingleAsync(l => l.Id == offLicense)).LastLegacySalesPushAt.Should().NotBeNull();
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
        (await db.WpfReplicas.SingleAsync(r => r.ReplicaId == replicaId)).HardwareFingerprint.Should().Be(original);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task<(SalesRequestContext Context, LogRecorder<SalesReplicaService> Log)> AcceptAsync(
        Guid licenseId, SalesEnvelope envelope, string endpoint = "orders/sync")
    {
        await using var db = Db();
        var log = new LogRecorder<SalesReplicaService>();
        var ctx = await new SalesReplicaService(db, log)
            .AcceptAsync(licenseId, envelope, "OrderDeck-WPF/0.9.11", endpoint, CancellationToken.None);
        db.ChangeTracker.Entries().Should().BeEmpty("kalp atışı ucun değişiklik izleyicisinde kalmaz");
        return (ctx, log);
    }

    private async Task<LogRecorder<SalesReplicaService>> HelloAsync(Guid licenseId, Guid replicaId, string fingerprint)
    {
        await using var db = Db();
        var log = new LogRecorder<SalesReplicaService>();
        await new SalesReplicaService(db, log).HelloAsync(
            licenseId,
            new ReplicaHello(V2(replicaId, SalesSyncMode.Off), null, "PC-KLON", fingerprint, 3,
                ReportsShipping: false, null, null, BackfillCompleted: false, PaymentHistoryCompleted: false),
            "OrderDeck-WPF/0.9.11",
            CancellationToken.None);
        return log;
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
        Guid licenseId, Guid replicaId, DateTimeOffset firstSeenAt, Guid? previousReplicaId = null)
    {
        await using var db = Db();
        db.WpfReplicas.Add(new WpfReplica
        {
            LicenseId = licenseId,
            ReplicaId = replicaId,
            Protocol = 2,
            SeenMode = SalesSyncMode.Off,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = firstSeenAt,
            PreviousReplicaId = previousReplicaId,
        });
        await db.SaveChangesAsync();
    }
}
