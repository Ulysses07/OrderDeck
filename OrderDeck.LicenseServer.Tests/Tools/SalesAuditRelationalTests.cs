using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-audit</c> (spec 2026-10-13 §4.10) gerçek SQL Server'da: her kalem için tohumlanmış durum beklenen sayıyı
/// verir, başka lisansın satırları sayılmaz; komut hiçbir tabloya yazmaz (bütün tabloların satır sayısı ve sağlama
/// toplamı, rowversion'lar dahil, aynı kalır); çıktıda tohumlanan kullanıcı adı, görünen ad ya da mesaj metni yoktur;
/// bilinmeyen lisans 2 döner. Ayrıştırma <see cref="SalesAuditTests"/>'te. Gövde, bağlantısı ve yazıcıları verilmiş
/// <see cref="SalesAudit.RunAsync(LicenseDbContext, Guid, TextWriter, TextWriter, CancellationToken)"/>'ten girilir
/// (<see cref="GroupCustomersRelationalTests"/> deseni).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesAuditRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;
    private readonly List<string> _personal = new();

    public SalesAuditRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

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

    private sealed record Seed(
        Guid LicenseId,
        Guid StaleLive,
        Guid StaleHandingOver,
        Guid ReleasedSession,
        Guid ProjectionHead,
        string GhostCustomer,
        Guid[] Deductions,
        Guid ShipmentWithoutOrigin,
        Guid ShipmentWithoutLinks,
        Guid ReplicaA,
        Guid ReplicaB);

    [Fact]
    public async Task Tohumlanan_her_kalem_beklenen_sayiyi_verir_baska_lisans_sayilmaz()
    {
        var seed = await SeedAsync();

        SalesAuditReport.Result r = null!;
        await WithDbAsync(async db => r = (await SalesAuditReport.BuildAsync(db, seed.LicenseId, DateTimeOffset.UtcNow, default))!);

        r.StaleSessions.Should().Equal(
            new SalesAuditReport.StaleSession(seed.StaleHandingOver, Live: false, seed.ReplicaA),
            new SalesAuditReport.StaleSession(seed.StaleLive, Live: true, null));
        r.OrdersWithoutSession.Should().Be(2);
        r.OrdersWithoutSessionSample.Should().HaveCount(2);
        r.CustomerIdsWithoutProjection.Should().Equal(seed.GhostCustomer);
        r.OrdersWithNonGuidCustomer.Should().Be(1);
        r.OrdersMissingReleaseStamp.Should().Be(1);
        r.ReportedShipping.Should().BeEquivalentTo(new[]
        {
            new SalesAuditReport.ReplicaShipping(seed.ReplicaA, 500m, 60m),
            new SalesAuditReport.ReplicaShipping(seed.ReplicaB, 400m, 60m),
        });
        r.DistinctReportedShipping.Should().Be(2);
        r.ReplicasWithoutShipping.Should().Be(1);
        r.PriceViolations.Should().Equal(new SalesAuditReport.PriceViolation(seed.ReleasedSession, 1));
        r.DuplicateDeductions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { WpfCustomerId = seed.ProjectionHead, TransactionIds = seed.Deductions });
        r.ShipmentsWithoutOrigin.Should().Equal(seed.ShipmentWithoutOrigin);
        r.ShipmentsWithAmountWithoutLinks.Should().Equal(seed.ShipmentWithoutLinks);
        r.OrdersWithUnknownLink.Should().Be(4);
        r.UnknownLinkCandidates.Should().Be(1);
        r.UnknownLinkCandidateAmount.Should().Be(70m);
        r.BackfillDivergence.Should().BeEquivalentTo(new[]
        {
            new SalesAuditReport.ReplicaDivergence(seed.ReplicaA, 2, 0),
            new SalesAuditReport.ReplicaDivergence(seed.ReplicaB, 0, 1),
        });
    }

    [Fact]
    public async Task Komut_0_doner_hicbir_seye_yazmaz_ciktida_kisisel_veri_yok()
    {
        var seed = await SeedAsync();
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await RunCliAsync(seed.LicenseId);

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        (await TableFingerprintsAsync()).Should().Equal(before, "rapor salt okunur: hiçbir satır, rowversion dahil, değişmez");

        output.Should().Contain($"lisans {seed.LicenseId}");
        output.Should().Contain("1. 24 saatten eski canlı ya da devrediliyor oturum: 2")
            .And.Contain($"{seed.StaleLive} canlı")
            .And.Contain($"{seed.StaleHandingOver} devrediliyor");
        output.Should().Contain("2. SessionId'si boş sipariş: 2");
        output.Should().Contain("3. Projeksiyonu olmayan CustomerId: 1").And.Contain(seed.GhostCustomer)
            .And.Contain("CustomerId'si GUID olmayan sipariş: 1");
        output.Should().Contain("4. Oturumu devredilmiş ama SessionReleasedAt'i boş sipariş: 1");
        output.Should().Contain("FARKLI (2 farklı değer)").And.Contain($"{seed.ReplicaA} eşik=500.00 ücret=60.00");
        output.Should().Contain("6. Tek fiyat ihlali (DM5): 1 kod, 1 yayında").And.Contain($"{seed.ReleasedSession} kod=1");
        output.Should().Contain("7. Birden çok geri alınmamış düşümü olan müşteri satırı × kapsam: 1")
            .And.Contain(string.Join(", ", seed.Deductions));
        output.Should().Contain("8. Shipped olmayan, OriginReplicaId'si boş dosya: 1")
            .And.Contain("tutarı > 0 ve bağlı siparişi olmayan dosya: 1");
        output.Should().Contain("sipariş: 4;").And.Contain(": 1, tutar 70.00");
        output.Should().Contain("10. Geri doldurma birim ayrışması: yerel kazanan 2, sunucu kazanan 1");
        output.Should().Contain("disk klonu şüphesi").And.Contain("K5 istisnasıyla").And.Contain("açılış penceresi dışında");

        foreach (var text in _personal)
            output.Should().NotContain(text, "çıktıda kişisel veri yok: yalnız sayı, tutar ve Id");
    }

    [Fact]
    public async Task Bilinmeyen_lisans_2_doner_rapor_yazmaz()
    {
        await SeedAsync();
        var before = await TableFingerprintsAsync();

        var (exit, output, error) = await RunCliAsync(Guid.NewGuid());

        exit.Should().Be(2);
        error.Should().Contain("Lisans bulunamadı");
        output.Should().BeEmpty("yazım hatalı Id sıfırlı, 'temiz' bir rapor üretmemeli");
        (await TableFingerprintsAsync()).Should().Equal(before);
    }

    // ── Tohum ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Her kalem için en az bir sayılan ve bir sayılmayan durum. Kişisel görünen her metin üretilir ve
    /// <see cref="_personal"/>'a eklenir (çıktıda hiçbirinin geçmediği denetlenir).
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var accountId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var otherLicenseId = Guid.NewGuid();
        var replicaA = Guid.NewGuid();
        var replicaB = Guid.NewGuid();
        var replicaC = Guid.NewGuid();

        var staleLive = Guid.NewGuid();
        var staleHandingOver = Guid.NewGuid();
        var recentHandingOver = Guid.NewGuid();
        var freshLive = Guid.NewGuid();
        var released = Guid.NewGuid();
        var releasedAt = now.AddHours(-48);

        var head = NewProjection(licenseId);
        var copy = NewProjection(licenseId);
        copy.MergedIntoId = head.Id;
        var ghost = Guid.NewGuid().ToString("N");

        var shipped = NewShipment(licenseId, ShipmentStatus.Shipped, 80m, origin: null);
        var withoutOrigin = NewShipment(licenseId, ShipmentStatus.Pending, 0m, origin: null);
        var withoutLinks = NewShipment(licenseId, ShipmentStatus.Held, 150m, origin: replicaA);
        var linked = NewShipment(licenseId, ShipmentStatus.Pending, 40m, origin: replicaA);

        var deductions = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await WithDbAsync(async db =>
        {
            db.Customers.Add(new Customer
            {
                Id = accountId, Email = $"yayinci-{Guid.NewGuid():N}@example.test", Name = "Denetim Testi",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
            });
            foreach (var id in new[] { licenseId, otherLicenseId })
                db.Licenses.Add(new License
                {
                    Id = id, CustomerId = accountId, LicenseKey = "sa-" + Guid.NewGuid().ToString("N")[..12],
                    SkuCode = "STD", ActivationSlots = 2, IssuedAt = now, ExpiresAt = now.AddYears(1),
                });

            db.WpfCustomerProjections.AddRange(head, copy);
            db.WpfReplicas.AddRange(
                NewReplica(licenseId, replicaA, 500m, 60m, local: 2, server: 0, now),
                NewReplica(licenseId, replicaB, 400m, 60m, local: 0, server: 1, now),
                NewReplica(licenseId, replicaC, null, null, local: 0, server: 0, now),
                NewReplica(otherLicenseId, Guid.NewGuid(), 999m, 1m, local: 5, server: 5, now));

            db.StreamSessions.AddRange(
                NewSession(licenseId, staleLive, now.AddHours(-30), endedAt: null, owner: null),
                NewSession(licenseId, staleHandingOver, now.AddHours(-40), now.AddHours(-26), replicaA),
                // Devir beklemesi bitişten sayılır: uzun yayın, iki saat önce bitti.
                NewSession(licenseId, recentHandingOver, now.AddHours(-30), now.AddHours(-2), replicaA),
                NewSession(licenseId, freshLive, now.AddHours(-1), endedAt: null, owner: replicaA),
                NewSession(licenseId, released, now.AddHours(-50), now.AddHours(-48), replicaA, releasedAt),
                NewSession(otherLicenseId, Guid.NewGuid(), now.AddHours(-30), endedAt: null, owner: null));

            db.Shipments.AddRange(shipped, withoutOrigin, withoutLinks, linked);

            var customer = head.Id.ToString("N");
            db.Orders.AddRange(
                // DM5: A1 iki düzeltilebilir fiyat (harf farkı aynı kod), B2 iptalli fark, C3 kargolanmış fark.
                NewOrder(licenseId, released, customer, "A1", 100m, releasedAt),
                NewOrder(licenseId, released, customer, "a1", 120m, sessionReleasedAt: null),
                NewOrder(licenseId, released, customer, "B2", 100m, releasedAt),
                NewOrder(licenseId, released, customer, "B2", 50m, releasedAt, cancelled: true),
                NewOrder(licenseId, released, customer, "C3", 100m, releasedAt),
                NewOrder(licenseId, released, customer, "C3", 80m, releasedAt, shipment: shipped.Id),
                // Oturumsuz.
                NewOrder(licenseId, null, customer, null, 10m, null),
                NewOrder(licenseId, null, customer, null, 10m, null),
                // Müşteri: kopyaya bağlı (projeksiyonu var), projeksiyonsuz GUID (iki sipariş, tek Id), GUID değil.
                NewOrder(licenseId, freshLive, copy.Id.ToString("N"), null, 10m, null),
                NewOrder(licenseId, freshLive, ghost, null, 10m, null),
                NewOrder(licenseId, freshLive, ghost, null, 10m, null),
                NewOrder(licenseId, freshLive, $"yerel-{Random.Shared.Next(1000)}", null, 10m, null),
                // Kargo bağı bilinmeyen: aday olurdu, iptal, bağlı, geçici yedek.
                NewOrder(licenseId, freshLive, customer, null, 70m, null, origin: false),
                NewOrder(licenseId, freshLive, customer, null, 30m, null, origin: false, cancelled: true),
                NewOrder(licenseId, freshLive, customer, null, 40m, null, origin: false, shipment: linked.Id),
                NewOrder(licenseId, freshLive, customer, null, 20m, null, origin: false, tentative: true),
                // Başka lisans: hiçbir kalemde sayılmaz.
                NewOrder(otherLicenseId, null, ghost, null, 10m, null, origin: false));

            var scope = $"session:{released:N}";
            var cumulative = Guid.NewGuid();
            db.CustomerBalanceTransactions.AddRange(
                NewDeduction(licenseId, head.Id, deductions[0], scope, now.AddMinutes(-20), accountId),
                NewDeduction(licenseId, head.Id, deductions[1], scope.ToUpperInvariant(), now.AddMinutes(-10), accountId),
                NewDeduction(licenseId, head.Id, cumulative, "cumulative", now.AddMinutes(-9), accountId),
                NewDeduction(licenseId, head.Id, Guid.NewGuid(), "cumulative", now.AddMinutes(-8), accountId),
                new CustomerBalanceTransaction
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = head.Id, Amount = 25m, Kind = "reversal",
                    ReversesTransactionId = cumulative, CreatedByCustomerId = accountId, CreatedAt = now.AddMinutes(-7),
                });

            await db.SaveChangesAsync();
        });

        return new Seed(licenseId, staleLive, staleHandingOver, released, head.Id, ghost, deductions,
            withoutOrigin.Id, withoutLinks.Id, replicaA, replicaB);
    }

    private string Personal(string prefix)
    {
        var text = $"{prefix}-{Guid.NewGuid():N}";
        _personal.Add(text);
        return text;
    }

    private WpfCustomerProjection NewProjection(Guid licenseId) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = Personal("kullanici"),
        FullName = Personal("Ad Soyad"), DisplayName = Personal("Gorunen"), UpdatedAt = DateTimeOffset.UtcNow,
    };

    private Order NewOrder(
        Guid licenseId, Guid? sessionId, string customerId, string? code, decimal price,
        DateTimeOffset? sessionReleasedAt, bool origin = true, bool cancelled = false, bool tentative = false,
        Guid? shipment = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, SessionId = sessionId, CustomerId = customerId,
        Platform = "tiktok", Username = Personal("kullanici"), DisplayName = Personal("Gorunen"),
        MessageText = Personal("mesaj"), Code = code, Price = price, AddedAt = DateTimeOffset.UtcNow.AddHours(-1),
        PrintedAt = DateTimeOffset.UtcNow.AddHours(-1), CancelledAt = cancelled ? DateTimeOffset.UtcNow : null,
        IsTentativeBackup = tentative, ShipmentId = shipment, SessionReleasedAt = sessionReleasedAt,
        OriginReplicaId = origin ? Guid.NewGuid() : null, UpdatedAt = DateTimeOffset.UtcNow, SyncVersion = 1,
    };

    private static StreamSession NewSession(
        Guid licenseId, Guid id, DateTimeOffset startedAt, DateTimeOffset? endedAt, Guid? owner,
        DateTimeOffset? releasedAt = null) => new()
    {
        Id = id, LicenseId = licenseId, Title = "Yayın", StartedAt = startedAt, EndedAt = endedAt,
        Platforms = "tiktok", UpdatedAt = startedAt, OwnerReplicaId = owner, ReleasedAt = releasedAt,
    };

    private static Shipment NewShipment(Guid licenseId, ShipmentStatus status, decimal amount, Guid? origin) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = Guid.NewGuid().ToString("N"), Status = status,
        CumulativeAmount = amount, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        OriginReplicaId = origin, ShippedAt = status == ShipmentStatus.Shipped ? DateTimeOffset.UtcNow : null,
    };

    private static WpfReplica NewReplica(
        Guid licenseId, Guid replicaId, decimal? threshold, decimal? fee, int local, int server, DateTimeOffset now)
        => new()
        {
            LicenseId = licenseId, ReplicaId = replicaId, Protocol = 2, FeatureLevel = 1, FirstSeenAt = now,
            LastSeenAt = now, ReportedThreshold = threshold, ReportedFee = fee,
            BackfillDivergedLocal = local, BackfillDivergedServer = server,
        };

    private static CustomerBalanceTransaction NewDeduction(
        Guid licenseId, Guid customerId, Guid id, string scope, DateTimeOffset at, Guid createdBy) => new()
    {
        Id = id, LicenseId = licenseId, WpfCustomerId = customerId, Amount = -25m, Kind = "purchase-deduction",
        OriginalAmount = 100m, SaleScope = scope, CreatedByCustomerId = createdBy, CreatedAt = at,
    };

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task WithDbAsync(Func<LicenseDbContext, Task> body)
    {
        using var scope = _factory.Services.CreateScope();
        await body(scope.ServiceProvider.GetRequiredService<LicenseDbContext>());
    }

    private async Task<(int Exit, string Output, string Error)> RunCliAsync(Guid license)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SalesAudit.RunAsync(db, license, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Veritabanındaki her kullanıcı tablosunun satır sayısı ve satır sağlama toplamı. rowversion kolonları da
    /// toplama girer: bir UPDATE değeri aynı yazsa bile toplam değişir.
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
}
