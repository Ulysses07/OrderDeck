using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync status</c> (spec 2026-10-13 §4.10, §6.2, §8.6) gerçek SQL Server'da: lisans düzeyi (mod ve değişim
/// anı, yapılandırılmış <c>MinClientVersion</c>, protokolsüz istek izi, lisans geneli kargo ayarı), lisansın bütün
/// kopyaları (son 14 günde görülenler işaretli; sürüm, protokol, <c>featureLevel</c>, görülen kip — eski sürüme dönüşün
/// kanıtı, §8.6 —, geri doldurma, ayrışma, parmak izi çakışması, bildirilen kargo) ve kopyaların bildirdiği farklı kargo
/// değerleri (§6.2). Salt okunur: hiçbir tablo değişmez. Makine adı ve parmak izi çıktıda hiç yok.
/// </summary>
public sealed partial class SalesSyncRelationalTests
{
    private static readonly DateTimeOffset ModeChangedAt = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Status_kopyalari_surum_ve_gorulen_kip_ile_listeler_makine_adi_yok()
    {
        var seed = await SeedStatusAsync();
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await StatusAsync(seed.LicenseId, new SalesSyncOptions { MinClientVersion = " 0.9.11 " });

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before, "status salt okunur");

        output.Should().Contain("salt okunur").And.Contain($"lisans {seed.LicenseId}");
        output.Should().Contain($"Mod: On, son değişim={U(ModeChangedAt)}");
        output.Should().Contain("MinClientVersion: 0.9.11" + Environment.NewLine);
        output.Should().Contain($"Son protokolsüz satış isteği (LastLegacySalesPushAt): {U(seed.LegacyAt)}");
        output.Should().Contain("Lisans geneli kargo ayarı: sürüm 2, eşik=500.00, ücret=60.00, yazan=cli");
        output.Should().Contain("Kopyalar: 3 (son 14 günde görülen: 2)");

        var current = Block(output, seed.Current.ReplicaId);
        current.Should().Contain("son 14 günde görüldü");
        current.Should().Contain("önceki kopya=yok, sürüm=0.9.11, protokol=2, featureLevel=3");
        current.Should().Contain(
            $"görülen kip=On, kip zamanı={U(seed.Current.SeenModeAt)}, ilk On={U(seed.Current.SeenModeOnAt)}");
        current.Should().Contain($"son görülme={U(seed.Current.LastSeenAt)}, "
            + $"geri doldurma tamam={U(seed.Current.BackfillCompletedAt)}, "
            + $"ödeme geçmişi tamam={U(seed.Current.PaymentHistoryCompletedAt)}");
        current.Should().Contain("geri doldurma ayrışması: yerel kazanan=2, sunucu kazanan=1");
        current.Should().Contain("parmak izi çakışması=yok");
        current.Should().Contain("bildirilen kargo: eşik=500.00, ücret=60.00");

        var restored = Block(output, seed.Restored.ReplicaId);
        restored.Should().Contain("son 14 günde görüldü");
        restored.Should().Contain($"önceki kopya={seed.Stale.ReplicaId}, sürüm=0.9.10, protokol=2, featureLevel=2");
        restored.Should().Contain($"görülen kip=Off, kip zamanı={U(seed.Restored.SeenModeAt)}, ilk On=yok",
            "Off bildiren kopya eski sürüme dönebilir (§8.6)");
        restored.Should().Contain("geri doldurma tamam=yok, ödeme geçmişi tamam=yok");
        restored.Should().Contain($"parmak izi çakışması=VAR ({U(seed.Restored.HardwareFingerprintConflictAt)})");
        restored.Should().Contain("bildirilen kargo: eşik=450.00, ücret=60.00");

        var stale = Block(output, seed.Stale.ReplicaId);
        stale.Should().Contain("14 günden eski");
        stale.Should().Contain("önceki kopya=yok, sürüm=bilinmiyor, protokol=1, featureLevel=0");
        stale.Should().Contain("görülen kip=bildirmedi, kip zamanı=yok, ilk On=yok");
        stale.Should().Contain("bildirilen kargo: eşik=boş, ücret=boş");

        // Bildirilen farklı değerler yalnız son 14 günden (shipping set ile aynı gruplama).
        output.Should().Contain("Kopyaların bildirdiği kargo ayarı (son 14 gün): 2 farklı değer");
        output.Should().Contain($"   eşik=450.00 ücret=60.00: {seed.Restored.ReplicaId}");
        output.Should().Contain($"   eşik=500.00 ücret=60.00: {seed.Current.ReplicaId}");

        output.Should().NotContain(seed.Foreign.ReplicaId.ToString(), "başka lisansın kopyası listelenmez");
        foreach (var secret in seed.MachineNamesAndFingerprints)
            output.Should().NotContain(secret, "çıktıda makine adı ve parmak izi yok");
    }

    /// <summary>Hiç açılmamış lisans: mod Off ve değişim yok, iz yok, ayar yok, kopya yok. <c>MinClientVersion</c>
    /// boşsa ya da ayrıştırılamıyorsa "tanımsız" — mod açılamaz, mod On'da kapı kapalı (§2 "Ws"). Bilinmeyen lisans 2
    /// döner.</summary>
    [Fact]
    public async Task Status_bos_lisansta_tanimsiz_ve_yok_gosterir_bilinmeyen_lisans_2()
    {
        var (licenseId, _) = await SeedAsync(withSettings: false);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfReplicas.RemoveRange(db.WpfReplicas.Where(r => r.LicenseId == licenseId));
            await db.SaveChangesAsync();
        }

        foreach (var (min, expected) in new[]
                 {
                     ((string?)null, "MinClientVersion: tanımsız"),
                     ("  ", "MinClientVersion: tanımsız"),
                     ("sürüm-yok", "MinClientVersion: tanımsız (yazılı değer ayrıştırılamıyor)"),
                 })
        {
            var (exit, output, error) = await StatusAsync(licenseId, new SalesSyncOptions { MinClientVersion = min });

            exit.Should().Be(0, error);
            output.Should().Contain(expected + Environment.NewLine);
            output.Should().Contain("Mod: Off, son değişim=yok");
            output.Should().Contain("Son protokolsüz satış isteği (LastLegacySalesPushAt): yok");
            output.Should().Contain("Lisans geneli kargo ayarı: yok (sürüm 0)");
            output.Should().Contain("Kopyalar: 0 (son 14 günde görülen: 0)");
            output.Should().Contain("Kopyaların bildirdiği kargo ayarı (son 14 gün): 0 farklı değer");
        }

        var before = await TableFingerprintsAsync();
        var unknown = await StatusAsync(Guid.NewGuid(), new SalesSyncOptions { MinClientVersion = "0.9.11" });
        unknown.Exit.Should().Be(2);
        unknown.Error.Should().Contain("Lisans bulunamadı");
        unknown.Output.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before);
    }

    // ── Tohum ve yardımcılar ──────────────────────────────────────────────────

    private sealed record StatusSeed(
        Guid LicenseId,
        DateTimeOffset LegacyAt,
        WpfReplica Current,
        WpfReplica Restored,
        WpfReplica Stale,
        WpfReplica Foreign,
        string[] MachineNamesAndFingerprints);

    /// <summary>Mod On lisansı (değişim anı sabit), protokolsüz istek izi, sürüm 2 kargo ayarı ve kopyalar: güncel
    /// (Ws, mod On'u görmüş, geri doldurmaları bitmiş), geri yüklenmiş (önceki kopyası eski olan; Off bildiriyor, klon
    /// izi var), 20 gün önce görülmüş eski bir kopya (v1) ve başka lisansta bir kopya.</summary>
    private async Task<StatusSeed> SeedStatusAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = Seconds(DateTimeOffset.UtcNow);
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"musteri-{Guid.NewGuid():N}@example.test", Name = "CLI Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = NewLicense(customer.Id, now);
        license.SalesSyncMode = SalesSyncMode.On;
        license.SalesSyncModeChangedAt = ModeChangedAt;
        var otherLicense = NewLicense(customer.Id, now);
        db.Customers.Add(customer);
        db.Licenses.AddRange(license, otherLicense);
        var legacyAt = now.AddDays(-9);
        db.SalesLegacyTraces.Add(new SalesLegacyTrace { LicenseId = license.Id, LastLegacySalesPushAt = legacyAt });
        db.LicenseShippingSettings.Add(new LicenseShippingSettings
        {
            LicenseId = license.Id, Version = 2, FreeShippingThreshold = 500m, ShippingFee = 60m,
            UpdatedAt = now.AddDays(-4), UpdatedBy = LicenseShippingSettings.UpdatedByCli,
        });

        var secrets = new List<string>();
        WpfReplica Replica(Guid licenseId, DateTimeOffset lastSeenAt, Action<WpfReplica> shape)
        {
            var replica = new WpfReplica
            {
                LicenseId = licenseId, ReplicaId = Guid.NewGuid(),
                MachineName = $"PC-{Guid.NewGuid():N}"[..14], HardwareFingerprint = $"fp-{Guid.NewGuid():N}",
                FirstSeenAt = lastSeenAt.AddDays(-30), LastSeenAt = lastSeenAt,
            };
            shape(replica);
            secrets.Add(replica.MachineName);
            secrets.Add(replica.HardwareFingerprint);
            db.WpfReplicas.Add(replica);
            return replica;
        }

        var stale = Replica(license.Id, now.AddDays(-20), r => r.Protocol = 1);
        var current = Replica(license.Id, now.AddHours(-1), r =>
        {
            r.ClientVersion = "0.9.11";
            r.Protocol = 2;
            r.FeatureLevel = 3;
            r.SeenMode = SalesSyncMode.On;
            r.SeenModeAt = now.AddHours(-1);
            r.SeenModeOnAt = ModeChangedAt.AddHours(1);
            r.BackfillCompletedAt = now.AddDays(-6);
            r.PaymentHistoryCompletedAt = now.AddDays(-5);
            r.BackfillDivergedLocal = 2;
            r.BackfillDivergedServer = 1;
            r.ReportedThreshold = 500m;
            r.ReportedFee = 60m;
        });
        var restored = Replica(license.Id, now.AddDays(-2), r =>
        {
            r.PreviousReplicaId = stale.ReplicaId;
            r.ClientVersion = "0.9.10";
            r.Protocol = 2;
            r.FeatureLevel = 2;
            r.SeenMode = SalesSyncMode.Off;
            r.SeenModeAt = now.AddDays(-2);
            r.HardwareFingerprintConflictAt = now.AddDays(-3);
            r.ReportedThreshold = 450m;
            r.ReportedFee = 60m;
        });
        var foreign = Replica(otherLicense.Id, now.AddHours(-1), r =>
        {
            r.ClientVersion = "0.9.11";
            r.Protocol = 2;
            r.ReportedThreshold = 999m;
            r.ReportedFee = 99m;
        });
        await db.SaveChangesAsync();

        return new StatusSeed(license.Id, legacyAt, current, restored, stale, foreign, secrets.ToArray());
    }

    private async Task<(int Exit, string Output, string Error)> StatusAsync(Guid licenseId, SalesSyncOptions options)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesSync.RunAsync(db, new SalesSync.StatusArgs(licenseId), options, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Bir kopyanın bloğu: "- Kopya {id}:" satırından bir sonraki kopyaya ya da bölüm sonuna kadar.</summary>
    private static string Block(string output, Guid replicaId)
    {
        var start = output.IndexOf($"- Kopya {replicaId}:", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"kopya {replicaId} listelenmeli");
        var next = output.IndexOf("- Kopya ", start + 1, StringComparison.Ordinal);
        var section = output.IndexOf("Kopyaların bildirdiği", start, StringComparison.Ordinal);
        var end = new[] { next, section, output.Length }.Where(i => i > start).Min();
        return output[start..end];
    }

    private static string U(DateTimeOffset? at) => at!.Value.ToString("u", CultureInfo.InvariantCulture);

    private static DateTimeOffset Seconds(DateTimeOffset at) =>
        new(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, at.Offset);
}
