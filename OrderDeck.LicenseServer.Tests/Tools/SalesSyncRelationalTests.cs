using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync shipping set</c> (spec 2026-10-13 §4.8, §4.10, §6.2) gerçek SQL Server'da: kuru koşu hiçbir tabloya
/// yazmaz (bütün tabloların satır sayısı ve sağlama toplamı aynı kalır) ama şu anki satırı ve son 14 günde kopyaların
/// bildirdiği değerleri gösterir; <c>--apply</c> ekler, sonra günceller (<c>UpdatedBy = cli</c>, sürüm bir artar);
/// <c>--off</c> iki değeri boş yazar; bilinmeyen lisans 2 döner ve hiçbir şey yazmaz; eşzamanlı bir yazım kazanırsa
/// 1 döner ve üzerine yazmaz. Çıktıda makine adı yok. Ayrıştırma <see cref="SalesSyncTests"/>'te; gövde, bağlantısı
/// ve yazıcıları verilmiş <see cref="SalesSync.RunAsync(LicenseDbContext, SalesSync.ShippingSetArgs, TextWriter, TextWriter, CancellationToken)"/>'ten
/// girilir (<see cref="SalesAuditRelationalTests"/> deseni).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public SalesSyncRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Kuru koşu: şu anki satır (sürüm, değerler, yazan), son 14 günde görülmüş kopyaların bildirdiği farklı
    /// değerler (kopya Id'siyle; makine adı yok) ve yazılacak değer basılır; veritabanı değişmez.</summary>
    [Fact]
    public async Task Kuru_kosu_hicbir_seye_yazmaz_mevcut_satiri_ve_kopya_degerlerini_gosterir()
    {
        var (licenseId, replicas) = await SeedAsync(withSettings: true);
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await RunAsync(new SalesSync.ShippingSetArgs(licenseId, 750m, 75.5m, Apply: false));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before, "kuru koşu hiçbir satırı, rowversion dahil, değiştirmez");
        (await RowAsync(licenseId))!.Version.Should().Be(3);

        output.Should().Contain("KURU ÇALIŞTIRMA").And.Contain($"lisans {licenseId}").And.Contain("Mod: Off");
        output.Should().Contain("Şu anki ayar: sürüm 3, eşik=400.00, ücret=50.00, yazan=cli");
        output.Should().Contain("Kopyaların bildirdiği kargo ayarı (son 14 gün): 3 farklı değer");
        var sameLine = output.Split('\n').Single(l => l.Contains("eşik=500.00 ücret=60.00:"));
        sameLine.Should().Contain(replicas.SameA.ToString()).And.Contain(replicas.SameB.ToString());
        output.Should().Contain($"eşik=450.00 ücret=60.00: {replicas.Different}");
        output.Should().Contain($"eşik=boş ücret=boş: {replicas.Silent}");
        output.Should().NotContain(replicas.Stale.ToString(), "14 günden eski kopya listelenmez");
        output.Should().NotContain(replicas.OtherLicense.ToString(), "başka lisansın kopyası listelenmez");
        output.Should().Contain("Yazılacak: eşik=750.00, ücret=75.50, sürüm 4, yazan=cli");
        output.Should().Contain("hiçbir şey yazılmadı");
        foreach (var machine in replicas.MachineNames)
            output.Should().NotContain(machine, "çıktıda makine adı yok");
    }

    /// <summary><c>--apply</c>: satır yokken sürüm 1 ile eklenir, sonra sürüm bir artarak güncellenir; ikisinde de
    /// <c>UpdatedBy = cli</c>. Kip fark etmez (mod On'da da yazar).</summary>
    [Fact]
    public async Task Apply_once_ekler_sonra_gunceller_yazan_cli()
    {
        var (licenseId, _) = await SeedAsync(withSettings: false);
        (await RunAsync(new SalesSync.ShippingSetArgs(licenseId, 500m, 60m, Apply: false))).Exit.Should().Be(0);
        (await RowAsync(licenseId)).Should().BeNull("kuru koşu satır açmaz");

        var before = DateTimeOffset.UtcNow;
        var (exit, output, error) = await RunAsync(new SalesSync.ShippingSetArgs(licenseId, 500m, 60m, Apply: true));

        exit.Should().Be(0, error);
        output.Should().Contain("Şu anki ayar: yok (sürüm 0)").And.Contain("UYGULANDI").And.Contain("sürüm 1");
        var inserted = (await RowAsync(licenseId))!;
        inserted.Should().BeEquivalentTo(new
        {
            Version = 1, FreeShippingThreshold = 500m, ShippingFee = 60m, UpdatedBy = LicenseShippingSettings.UpdatedByCli,
        });
        inserted.UpdatedAt.Should().BeOnOrAfter(before.AddSeconds(-1));

        await SetModeAsync(licenseId, SalesSyncMode.On);
        (exit, output, error) = await RunAsync(new SalesSync.ShippingSetArgs(licenseId, 650.25m, 45m, Apply: true));

        exit.Should().Be(0, error);
        output.Should().Contain("Mod: On").And.Contain("sürüm 2");
        (await RowAsync(licenseId)).Should().BeEquivalentTo(new
        {
            Version = 2, FreeShippingThreshold = 650.25m, ShippingFee = 45m, UpdatedBy = "cli",
        });
    }

    /// <summary><c>--off</c>: iki değer boş (kargo kapalı) yazılır; kopyanın yazdığı satırın üstüne de, sürüm artar.
    /// Satır yokken de açılır — mod kapısının 4. koşulu (satır var, <c>UpdatedBy</c> dolu) böyle karşılanabilir.</summary>
    [Fact]
    public async Task Off_iki_degeri_bos_yazar()
    {
        var (licenseId, _) = await SeedAsync(withSettings: true);

        var (exit, output, error) = await RunAsync(new SalesSync.ShippingSetArgs(licenseId, null, null, Apply: true));

        exit.Should().Be(0, error);
        output.Should().Contain("Yazılacak: kargo kapalı (eşik ve ücret boş), sürüm 4, yazan=cli");
        (await RowAsync(licenseId)).Should().BeEquivalentTo(new
        {
            Version = 4, FreeShippingThreshold = (decimal?)null, ShippingFee = (decimal?)null, UpdatedBy = "cli",
        });

        var (fresh, _) = await SeedAsync(withSettings: false);
        (await RunAsync(new SalesSync.ShippingSetArgs(fresh, null, null, Apply: true))).Exit.Should().Be(0);
        (await RowAsync(fresh)).Should().BeEquivalentTo(new
        {
            Version = 1, FreeShippingThreshold = (decimal?)null, ShippingFee = (decimal?)null, UpdatedBy = "cli",
        });
    }

    /// <summary>Yazım hatalı lisans Id'si "yazıldı" sanılmasın: 2, hiçbir tabloya yazılmaz, çıktı boş.</summary>
    [Fact]
    public async Task Bilinmeyen_lisans_2_doner_hicbir_sey_yazmaz()
    {
        await SeedAsync(withSettings: true);
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await RunAsync(new SalesSync.ShippingSetArgs(Guid.NewGuid(), 500m, 60m, Apply: true));

        exit.Should().Be(2);
        error.Should().Contain("Lisans bulunamadı");
        output.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before);
    }

    /// <summary>Komut okuduğu sürümü bekler: okuma ile yazma arasında başka bir yazım (WPF'in PUT'u) gelirse onun
    /// üzerine yazmaz, 1 döner — yeniden çalıştırılınca güncel satırı görür. Sıra kancayla kurulur.</summary>
    [Fact]
    public async Task Eszamanli_yazim_kazanirsa_1_doner_uzerine_yazmaz()
    {
        var (licenseId, _) = await SeedAsync(withSettings: true);
        var hook = new BeforeShippingSaveHook(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var other = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            await other.LicenseShippingSettings.Where(s => s.LicenseId == licenseId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Version, 4)
                    .SetProperty(x => x.FreeShippingThreshold, 900m)
                    .SetProperty(x => x.UpdatedBy, "baska-kopya"));
        });
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).AddInterceptors(hook).Options);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await SalesSync.RunAsync(db, new SalesSync.ShippingSetArgs(licenseId, 500m, 60m, Apply: true), output, error, default);

        hook.Ran.Should().BeTrue("kanca yazımdan önce koşmalı");
        exit.Should().Be(1);
        error.ToString().Should().Contain("sürüm 4").And.Contain("yeniden çalıştır");
        (await RowAsync(licenseId)).Should().BeEquivalentTo(new
        {
            Version = 4, FreeShippingThreshold = 900m, UpdatedBy = "baska-kopya",
        });
    }

    // ── Tohum ve yardımcılar ──────────────────────────────────────────────────

    private sealed record Replicas(
        Guid SameA, Guid SameB, Guid Different, Guid Silent, Guid Stale, Guid OtherLicense, string[] MachineNames);

    /// <summary>Bir lisans (mod Off), isteğe bağlı sürüm 3 ayar satırı ve kopyalar: ikisi aynı değeri, biri farklı
    /// değeri bildirmiş, biri hiç bildirmemiş; biri 20 gün önce görülmüş; biri başka lisansta.</summary>
    private async Task<(Guid LicenseId, Replicas Replicas)> SeedAsync(bool withSettings)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"musteri-{Guid.NewGuid():N}@example.test", Name = "CLI Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = NewLicense(customer.Id, now);
        var otherLicense = NewLicense(customer.Id, now);
        db.Customers.Add(customer);
        db.Licenses.AddRange(license, otherLicense);

        var machineNames = new List<string>();
        WpfReplica Replica(Guid licenseId, decimal? threshold, decimal? fee, DateTimeOffset lastSeenAt)
        {
            var machine = $"PC-{Guid.NewGuid():N}"[..12];
            machineNames.Add(machine);
            var replica = new WpfReplica
            {
                LicenseId = licenseId, ReplicaId = Guid.NewGuid(), MachineName = machine, Protocol = 2,
                FirstSeenAt = lastSeenAt.AddDays(-1), LastSeenAt = lastSeenAt,
                ReportedThreshold = threshold, ReportedFee = fee,
            };
            db.WpfReplicas.Add(replica);
            return replica;
        }

        var sameA = Replica(license.Id, 500m, 60m, now.AddHours(-1));
        var sameB = Replica(license.Id, 500m, 60m, now.AddDays(-13));
        var different = Replica(license.Id, 450m, 60m, now.AddDays(-2));
        var silent = Replica(license.Id, null, null, now.AddDays(-3));
        var stale = Replica(license.Id, 300m, 30m, now.AddDays(-20));
        var foreign = Replica(otherLicense.Id, 999m, 99m, now.AddHours(-1));

        if (withSettings)
            db.LicenseShippingSettings.Add(new LicenseShippingSettings
            {
                LicenseId = license.Id, Version = 3, FreeShippingThreshold = 400m, ShippingFee = 50m,
                UpdatedAt = now.AddDays(-5), UpdatedBy = LicenseShippingSettings.UpdatedByCli,
            });
        await db.SaveChangesAsync();

        return (license.Id, new Replicas(
            sameA.ReplicaId, sameB.ReplicaId, different.ReplicaId, silent.ReplicaId, stale.ReplicaId,
            foreign.ReplicaId, machineNames.ToArray()));
    }

    private static License NewLicense(Guid customerId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), CustomerId = customerId, LicenseKey = "ssc-" + Guid.NewGuid().ToString("N")[..12],
        SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1),
    };

    private async Task SetModeAsync(Guid licenseId, SalesSyncMode mode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await db.Licenses.Where(l => l.Id == licenseId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.SalesSyncMode, mode));
    }

    private async Task<LicenseShippingSettings?> RowAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.LicenseShippingSettings.AsNoTracking().SingleOrDefaultAsync(s => s.LicenseId == licenseId);
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(SalesSync.ShippingSetArgs args)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(db, args, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Veritabanındaki her kullanıcı tablosunun satır sayısı ve satır sağlama toplamı (<see cref="SalesAuditRelationalTests"/>
    /// ile aynı). rowversion kolonları da toplama girer: bir UPDATE değeri aynı yazsa bile toplam değişir.
    /// </summary>
    private async Task<Dictionary<string, (long Rows, int Checksum)>> TableFingerprintsAsync()
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY name";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        var result = new Dictionary<string, (long, int)>();
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT_BIG(*), ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(*)), 0) FROM [{table}]";
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            result[table] = (reader.GetInt64(0), reader.GetInt32(1));
        }
        return result;
    }

    /// <summary>Ayar satırını yazan kaydetmenin hemen öncesinde bir kez koşar.</summary>
    private sealed class BeforeShippingSaveHook(Func<Task> body) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran && eventData.Context!.ChangeTracker.Entries<LicenseShippingSettings>()
                    .Any(e => e.State is EntityState.Added or EntityState.Modified))
            {
                Ran = true;
                await body();
            }
            return result;
        }
    }
}
