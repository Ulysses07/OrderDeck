using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Açılış uyarısı — spec 2026-10-11 §7.2: "Açılışta anahtar boşken havuz kipinde lisans varsa uyarı günlüğe yazılır."
/// Alt sınır boş ya da ayrıştırılamıyorsa VE en az bir lisans havuz kipindeyse TEK uyarı (o lisanslarda preview ve yeni
/// yazım 409 <c>client-update-required</c> döner); aksi hâlde sessiz. InMemory yeterli: tek sayım sorgusu. Son test
/// <c>Program.cs</c> bağlantısını uçtan uca sınar (göçten sonra, bölüme bağlı ayarla çağrılıyor mu).
/// </summary>
public sealed class PersonBalanceStartupTests
{
    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"pb-startup-{Guid.NewGuid():N}").Options);

    /// <summary>Kiracısı ve anahtarı üretilmiş bir lisans; <paramref name="pool"/> ise havuz kipinde.</summary>
    private static async Task SeedLicenseAsync(LicenseDbContext db, bool pool)
    {
        var now = DateTimeOffset.UtcNow;
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            LicenseKey = $"lic-{Guid.NewGuid():N}",
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
            PersonBalancePoolSince = pool ? now.AddDays(-1) : null,
        });
        await db.SaveChangesAsync();
    }

    private static Task<bool> WarnAsync(
        LicenseDbContext db, string? minClientVersion, LogRecorder<PersonBalanceStartupTests> log) =>
        PersonBalanceStartup.WarnIfPoolWithoutMinimumAsync(
            db, new PersonBalanceOptions { MinClientVersion = minClientVersion }, log, CancellationToken.None);

    [Fact]
    public async Task Alt_sinir_tanimsiz_ve_havuz_kipinde_lisans_varsa_tek_uyari()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: true);
        await SeedLicenseAsync(db, pool: true);
        await SeedLicenseAsync(db, pool: false);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        var warned = await WarnAsync(db, null, log);

        warned.Should().BeTrue();
        var entry = log.Entries.Should().ContainSingle("açılışta tek uyarı").Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("havuz kipinde 2 lisans", "satır kipindeki lisans sayılmaz")
            .And.Contain("PersonBalance__MinClientVersion", "uyarı düzeltme yolunu söyler");
    }

    [Fact]
    public async Task Alt_sinir_bozuksa_tanimsiz_sayilir()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: true);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        var warned = await WarnAsync(db, "abc", log);

        warned.Should().BeTrue("ayrıştırılamayan alt sınır tanımsızdır (§7.2)");
        log.Entries.Should().ContainSingle().Which.Message.Should().Contain("havuz kipinde 1 lisans");
    }

    [Fact]
    public async Task Havuz_kipinde_lisans_yoksa_uyari_yok()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: false);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        var warned = await WarnAsync(db, null, log);

        warned.Should().BeFalse("S2 deploy'unda hiçbir lisans havuz kipinde değil; boş alt sınır olağan durum");
        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Alt_sinir_doluysa_uyari_yok()
    {
        using var db = NewDb();
        await SeedLicenseAsync(db, pool: true);
        var log = new LogRecorder<PersonBalanceStartupTests>();

        (await WarnAsync(db, PersonBalanceConfiguredApiFactory.MinClientVersion, log)).Should().BeFalse();

        // Alt sınır doluyken veritabanına hiç gidilmez: kapalı bağlam sorguda atardı.
        var closed = NewDb();
        closed.Dispose();
        (await WarnAsync(closed, PersonBalanceConfiguredApiFactory.MinClientVersion, log))
            .Should().BeFalse("alt sınır dolu → sayım sorgusu koşmaz");
        log.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// <c>Program.cs</c> bağlantısı: host açılırken uyarı göçten SONRA, <c>PersonBalance</c> bölümüne bağlı ayarla
    /// yazılır. Çağrı unutulur, <c>EnsureCreated</c>'dan önceye taşınır ya da başka bir ayar örneği geçirilirse kırmızı.
    /// </summary>
    [Fact]
    public void Program_acilista_havuz_kipindeki_lisans_icin_uyari_yazar()
    {
        using var factory = new PoolAtStartupFactory();

        // İstemci oluşturmak host'u kurar ve Program'ın göç kapsamını koşturur.
        using var client = factory.CreateClient();

        factory.Log.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning && e.Message.Contains("havuz kipinde 1 lisans", StringComparison.Ordinal),
            "alt sınır boş ve havuz kipinde bir lisans var (§7.2)");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>
    /// Alt sınırı boş, açılıştan önce havuz kipinde bir lisansı olan fabrika. Lisans <c>UseSeeding</c> ile yazılır:
    /// EF 10'da Program'ın (eşzamanlı) <c>EnsureCreated</c>'ı tohumlayıcıyı çağırır, yani lisans uyarıdan ÖNCE
    /// depodadır. Eşzamanlı <c>UseSeeding</c> şart — yalnız <c>UseAsyncSeeding</c> verilseydi eşzamanlı
    /// <c>EnsureCreated</c> atardı.
    /// </summary>
    private sealed class PoolAtStartupFactory : ApiFactory
    {
        public LogRecorderProvider Log { get; } = new();

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:MinClientVersion"] = "",
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) =>
            opt.UseSeeding((ctx, _) =>
            {
                ctx.Set<License>().Add(new License
                {
                    Id = Guid.NewGuid(),
                    CustomerId = Guid.NewGuid(),
                    LicenseKey = $"lic-{Guid.NewGuid():N}",
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                    PersonBalancePoolSince = DateTimeOffset.UtcNow.AddDays(-1),
                });
                ctx.SaveChanges();
            });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(l => l.AddProvider(Log));
        }
    }
}
