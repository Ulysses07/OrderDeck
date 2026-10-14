using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// <c>sales-release-repair</c> (spec 2026-10-13 §4.9, §10.1-3, §10.1-19), gerçek SQL Server: devir ve damga koşullu
/// UPDATE'tir, rowversion ve parti deyimi InMemory'de yok. S1 öncesi imajın yazımları (geri alma penceresi, §8.6)
/// o imajın kolon listeleriyle ham SQL'le taklit edilir — eski imaj yeni kolonları tanımaz, NULL bırakır.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesReleaseRepairJobTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private HookFactory _factory = null!;

    public SalesReleaseRepairJobTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new HookFactory(_cs);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// §10.1-3: miras oturum (S1 öncesi imajın açıp kapattığı) devredilir ve siparişi aynı devir anıyla damgalanır;
    /// eski imajın geri alma penceresinde devredilmiş bir yayına eklediği sipariş o yayının (işin değil) devir
    /// anıyla damgalanır. Devrediliyor hâlindeki v2 oturumuna, canlı miras oturuma ve oturumsuz siparişe
    /// dokunulmaz. İkinci koşu hiçbir satırın ChangeSeq'ini değiştirmez.
    /// </summary>
    [Fact]
    public async Task Miras_oturum_ve_geri_alma_penceresindeki_siparis_onarilir_v2_devrediliyor_oturuma_dokunulmaz()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);

        var legacyEnded = Guid.NewGuid();
        await InsertPreS1SessionAsync(licenseId, legacyEnded, endedAt: T0.AddHours(2));
        var legacyOrder = await InsertPreS1OrderAsync(licenseId, legacyEnded);

        // S1 imajının devrettiği yayın; ardından geri alma penceresinde eski imaj ona sipariş ekler.
        var released = Guid.NewGuid();
        await InsertPreS1SessionAsync(licenseId, released, endedAt: T0.AddDays(-1));
        var releasedAt = T0.AddDays(-1).AddMinutes(5);
        await ReleaseAsync(licenseId, released, releasedAt);
        var windowOrder = await InsertPreS1OrderAsync(licenseId, released);

        // v2 sahibinin bitirdiği ama etiketleri gönderilmeyi bekleyen yayın (devrediliyor).
        var handingOver = Guid.NewGuid();
        var handingOverOrder = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.StreamSessions.Add(new StreamSession
            {
                Id = handingOver, LicenseId = licenseId, Title = "v2", StartedAt = T0, EndedAt = T0.AddHours(1),
                Platforms = "tiktok", UpdatedAt = T0, OwnerReplicaId = Guid.NewGuid(), UnsentLabelCount = 3,
            });
            db.Orders.Add(NewOrder(licenseId, handingOver, handingOverOrder));
            await db.SaveChangesAsync();
        });

        var liveLegacy = Guid.NewGuid();
        await InsertPreS1SessionAsync(licenseId, liveLegacy, endedAt: null);
        var liveOrder = await InsertPreS1OrderAsync(licenseId, liveLegacy);
        var sessionless = await InsertPreS1OrderAsync(licenseId, sessionId: null);

        var untouchedBefore = await ChangeSeqsAsync();

        var report = await RunJobAsync();

        report.Should().Be(new SalesReleaseRepairJob.Report(
            SessionsReleased: 1, OrdersStamped: 1, OrderBatches: 1, ShipmentsRederived: 0, ShipmentsConflicted: 0));

        var legacy = await SessionAsync(legacyEnded);
        legacy.ReleasedAt.Should().NotBeNull("miras oturum EndedAt dolu ve sahipsiz: devredilir (§4.9-a)");
        var legacyOrderRow = await OrderAsync(legacyOrder);
        legacyOrderRow.SessionReleasedAt.Should().Be(legacy.ReleasedAt, "devir siparişleri aynı anla damgalar (DM3)");
        legacyOrderRow.ChangeSeq.Should().BeGreaterThan(legacy.ChangeSeq, "akış oturumu siparişlerinden önce görür");
        legacyOrderRow.SyncVersion.Should().Be(1, "damga birim değildir; komutların beklenen sürümü ilerlemez");

        var window = await OrderAsync(windowOrder);
        window.SessionReleasedAt.Should().Be(releasedAt, "kaçmış damga yayının kendi devir anıdır, işin saati değil");
        window.SyncVersion.Should().Be(1);
        (await SessionAsync(released)).ReleasedAt.Should().Be(releasedAt, "devir anı bir daha değişmez");

        (await SessionAsync(handingOver)).ReleasedAt.Should().BeNull(
            "v2 oturumunu iş devretmez: etiket kuyruğu boşalmadan devir yok (M2)");
        (await OrderAsync(handingOverOrder)).SessionReleasedAt.Should().BeNull();
        (await SessionAsync(liveLegacy)).ReleasedAt.Should().BeNull("canlı oturum devredilmez");
        (await OrderAsync(liveOrder)).SessionReleasedAt.Should().BeNull();
        (await OrderAsync(sessionless)).SessionReleasedAt.Should().BeNull();

        var after = await ChangeSeqsAsync();
        foreach (var id in new[] { handingOver, handingOverOrder, liveLegacy, liveOrder, sessionless })
            after[id].Should().Be(untouchedBefore[id], $"{id}: dokunulmayan satır akışta ilerlemez");

        var second = await RunJobAsync();

        second.Should().Be(new SalesReleaseRepairJob.Report(0, 0, 0, 0, 0), "iş idempotent");
        (await ChangeSeqsAsync()).Should().Equal(after, "ikinci koşu hiçbir satırı yazmaz");
    }

    /// <summary>
    /// Sahipsizlik koşulu devrin koşullu UPDATE'inde: aday okunduktan sonra geri doldurmanın sahip yazdığı oturum
    /// (artık v2) iş tarafından devredilmez. Aynı oturumu sahibin kapanış yolu (<see cref="SessionReleaser.ReleaseAsync"/>)
    /// devreder — fark yalnız bu koşul.
    /// </summary>
    [Fact]
    public async Task Sahip_yazilmis_oturum_sahipsiz_devirle_devredilmez()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var sessionId = Guid.NewGuid();
        await InsertPreS1SessionAsync(licenseId, sessionId, endedAt: T0.AddHours(1));
        var orderId = await InsertPreS1OrderAsync(licenseId, sessionId);
        await ExecAsync("UPDATE StreamSessions SET OwnerReplicaId = @owner WHERE Id = @id",
            ("@owner", Guid.NewGuid()), ("@id", sessionId));

        IReadOnlyList<Guid> unowned = [];
        await WithScopeAsync(async sp => unowned = await sp.GetRequiredService<SessionReleaser>()
            .ReleaseUnownedAsync(licenseId, [sessionId], DateTimeOffset.UtcNow, default));

        unowned.Should().BeEmpty();
        (await SessionAsync(sessionId)).ReleasedAt.Should().BeNull();
        (await OrderAsync(orderId)).SessionReleasedAt.Should().BeNull();

        IReadOnlyList<Guid> owned = [];
        await WithScopeAsync(async sp => owned = await sp.GetRequiredService<SessionReleaser>()
            .ReleaseAsync(licenseId, [sessionId], DateTimeOffset.UtcNow, default));
        owned.Should().Equal(sessionId);
    }

    /// <summary>(b) parti döngüsü küçük parti boyuyla: 5 sipariş, parti 2 → üç parti (2 + 2 + 1), hepsi damgalı.</summary>
    [Fact]
    public async Task Kacmis_damgalar_partilerle_basilir()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var sessionId = Guid.NewGuid();
        await InsertPreS1SessionAsync(licenseId, sessionId, endedAt: T0.AddHours(1));
        var releasedAt = T0.AddHours(1).AddMinutes(1);
        await ReleaseAsync(licenseId, sessionId, releasedAt);
        var orders = new List<Guid>();
        for (var i = 0; i < 5; i++) orders.Add(await InsertPreS1OrderAsync(licenseId, sessionId));

        var report = await RunJobAsync(orderBatchSize: 2);

        report.OrdersStamped.Should().Be(5);
        report.OrderBatches.Should().Be(3);
        foreach (var id in orders)
            (await OrderAsync(id)).SessionReleasedAt.Should().Be(releasedAt);
        (await RunJobAsync(orderBatchSize: 2)).OrdersStamped.Should().Be(0);
    }

    /// <summary>(b) varsayılan parti boyu (5 bin, spec §4.9) aşılınca ikinci parti koşar; hiçbir satır kalmaz.</summary>
    [Fact]
    public async Task Varsayilan_parti_bes_bini_asinca_ikinci_parti_kosar()
    {
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var sessionId = Guid.NewGuid();
        await InsertPreS1SessionAsync(licenseId, sessionId, endedAt: T0.AddHours(1));
        var releasedAt = T0.AddHours(1).AddMinutes(1);
        await ReleaseAsync(licenseId, sessionId, releasedAt);
        const int count = 5_001;
        await ExecAsync($"""
            WITH n AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO Orders ({string.Join(", ", SalesSyncS1MigrationScenario.PreS1OrderColumns)})
            SELECT NEWID(), @licenseId, @sessionId, CONCAT(N'c', i), N'tiktok', CONCAT(N'kullanici', i), NULL,
                   N'A1', N'A1', 100.00, @now, NULL, NULL, NULL, 0, 0, 0, NULL, NULL, @now, 1
            FROM n
            """, ("@count", count), ("@licenseId", licenseId), ("@sessionId", sessionId), ("@now", T0));

        var report = await RunJobAsync();

        report.OrdersStamped.Should().Be(count);
        report.OrderBatches.Should().Be(2);
        (await ScalarAsync<int>(
                "SELECT COUNT(*) FROM Orders WHERE SessionId = @id AND (SessionReleasedAt IS NULL OR SessionReleasedAt <> @r)",
                ("@id", sessionId), ("@r", releasedAt)))
            .Should().Be(0);
    }

    /// <summary>
    /// §10.1-19 (onarım kısmı), DM7: mod On lisansta türetilmiş ve Shipped olmayan dosyanın yanlış tutarı bağlı
    /// siparişlerden yeniden türetilir (iptal ve geçici yedek düşer, kargo ücreti ve basılmamış dahil); Shipped
    /// dosyanın donmuş tutarına, kabul edilmiş (türetilmemiş) dosyaya ve tutarı zaten doğru dosyaya dokunulmaz.
    /// Mod Off lisansın dosyası hiç okunmaz. İkinci koşu etkisiz.
    /// </summary>
    [Fact]
    public async Task Mod_On_turetilmis_dosya_duzeltilir_Shipped_kabul_edilmis_ve_mod_Off_dosyaya_dokunulmaz()
    {
        var on = await SeedLicenseAsync(SalesSyncMode.On);
        var off = await SeedLicenseAsync(SalesSyncMode.Off);
        var derivedAt = T0.AddDays(-2);

        var wrong = NewShipment(on, ShipmentStatus.Held, amount: 999m, derivedAt);
        var shipped = NewShipment(on, ShipmentStatus.Shipped, amount: 999m, derivedAt);
        var accepted = NewShipment(on, ShipmentStatus.Pending, amount: 999m, derivedAt: null);
        var correct = NewShipment(on, ShipmentStatus.Pending, amount: 70m, derivedAt);
        var offDerived = NewShipment(off, ShipmentStatus.Pending, amount: 999m, derivedAt);

        await WithDbAsync(async db =>
        {
            db.Shipments.AddRange(wrong, shipped, accepted, correct, offDerived);
            db.Orders.AddRange(
                NewOrder(on, null, Guid.NewGuid(), price: 100m, shipment: wrong.Id, printed: true),
                NewOrder(on, null, Guid.NewGuid(), price: 50m, shipment: wrong.Id, printed: false),
                NewOrder(on, null, Guid.NewGuid(), price: 25m, shipment: wrong.Id, shippingFee: true),
                NewOrder(on, null, Guid.NewGuid(), price: 30m, shipment: wrong.Id, cancelled: true),
                NewOrder(on, null, Guid.NewGuid(), price: 20m, shipment: wrong.Id, tentative: true),
                NewOrder(on, null, Guid.NewGuid(), price: 40m, shipment: shipped.Id),
                NewOrder(on, null, Guid.NewGuid(), price: 40m, shipment: accepted.Id),
                NewOrder(on, null, Guid.NewGuid(), price: 70m, shipment: correct.Id),
                NewOrder(off, null, Guid.NewGuid(), price: 10m, shipment: offDerived.Id));
            await db.SaveChangesAsync();
        });
        var before = await ShipmentsAsync();

        var report = await RunJobAsync();

        report.ShipmentsRederived.Should().Be(1);
        report.ShipmentsConflicted.Should().Be(0);
        var after = await ShipmentsAsync();
        after[wrong.Id].CumulativeAmount.Should().Be(175m, "100 + 50 + kargo ücreti 25; iptal ve geçici yedek düşer");
        after[wrong.Id].Version.Should().Be(before[wrong.Id].Version + 1, "sunucunun her dosya yazımı sürümü artırır");
        after[wrong.Id].UpdatedAt.Should().BeAfter(before[wrong.Id].UpdatedAt);
        foreach (var id in new[] { shipped.Id, accepted.Id, correct.Id, offDerived.Id })
        {
            after[id].CumulativeAmount.Should().Be(before[id].CumulativeAmount, $"{id}: dokunulmaz");
            after[id].ChangeSeq.Should().Be(before[id].ChangeSeq, $"{id}: yazılmaz");
        }
        after[shipped.Id].CumulativeAmount.Should().Be(999m, "Shipped dosyanın tutarı donmuştur (§7.2)");

        (await RunJobAsync()).ShipmentsRederived.Should().Be(0);
        (await ShipmentsAsync())[wrong.Id].ChangeSeq.Should().Be(after[wrong.Id].ChangeSeq, "ikinci koşu yazmaz");
    }

    /// <summary>
    /// (c) eşzamanlı yazım: iş dosyayı okuduktan sonra başka bir yazıcı (K7 türetmesi) aynı dosyanın sürümünü
    /// artırırsa işin yazımı jetona çarpar, dosya o koşuda atlanır ve sayılır; öbür yazım ezilmez. Sonraki koşu
    /// düzeltir.
    /// </summary>
    [Fact]
    public async Task Esszamanli_yazimda_dosya_atlanir_oteki_yazim_ezilmez()
    {
        var on = await SeedLicenseAsync(SalesSyncMode.On);
        var shipment = NewShipment(on, ShipmentStatus.Pending, amount: 999m, T0);
        await WithDbAsync(async db =>
        {
            db.Shipments.Add(shipment);
            db.Orders.Add(NewOrder(on, null, Guid.NewGuid(), price: 60m, shipment: shipment.Id));
            await db.SaveChangesAsync();
        });

        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;
            await ExecAsync("UPDATE Shipments SET Version = Version + 1, CumulativeAmount = 555 WHERE Id = @id",
                ("@id", shipment.Id));
        };

        var report = await RunJobAsync();

        report.ShipmentsRederived.Should().Be(0);
        report.ShipmentsConflicted.Should().Be(1);
        (await ShipmentsAsync())[shipment.Id].CumulativeAmount.Should().Be(555m, "öbür yazım ezilmez");

        (await RunJobAsync()).ShipmentsRederived.Should().Be(1);
        (await ShipmentsAsync())[shipment.Id].CumulativeAmount.Should().Be(60m);
    }

    /// <summary>Takvim (spec §4.9): 04:50 UTC günlük ve açılışta bir kez kuyruğa.</summary>
    [Fact]
    public void Is_0450_UTC_takvimiyle_kaydolur_ve_acilista_bir_kez_kuyruga_alinir()
    {
        var storage = new MemoryStorage();

        Program.ScheduleSalesSyncJobs(new RecurringJobManager(storage), new BackgroundJobClient(storage));

        using var connection = storage.GetConnection();
        var job = connection.GetRecurringJobs().Should().ContainSingle().Subject;
        (job.Id, job.Cron, job.TimeZoneId).Should().Be(("sales-release-repair", "50 4 * * *", "UTC"));
        job.Job.Type.Should().Be(typeof(SalesReleaseRepairJob));
        storage.GetMonitoringApi().EnqueuedCount("default").Should().Be(1);
    }

    /// <summary>Hangfire işi DI kapsamından çözer.</summary>
    [Fact]
    public void Is_DI_kapsamindan_cozulur()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SalesReleaseRepairJob>().Should().NotBeNull();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task<SalesReleaseRepairJob.Report> RunJobAsync(int? orderBatchSize = null)
    {
        using var scope = _factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<SalesReleaseRepairJob>();
        if (orderBatchSize is { } size) job.OrderBatchSize = size;
        return await job.RunAsync(default);
    }

    private async Task ReleaseAsync(Guid licenseId, Guid sessionId, DateTimeOffset releasedAt) =>
        await WithScopeAsync(async sp =>
            (await sp.GetRequiredService<SessionReleaser>().ReleaseAsync(licenseId, [sessionId], releasedAt, default))
            .Should().Equal(sessionId));

    private async Task WithScopeAsync(Func<IServiceProvider, Task> body)
    {
        using var scope = _factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }

    private Task WithDbAsync(Func<LicenseDbContext, Task> body) =>
        WithScopeAsync(sp => body(sp.GetRequiredService<LicenseDbContext>()));

    private async Task<Guid> SeedLicenseAsync(SalesSyncMode mode)
    {
        var licenseId = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            var customer = new Customer
            {
                Id = Guid.NewGuid(), Email = $"yayinci-{Guid.NewGuid():N}@example.test", Name = "Onarım Testi",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Customers.Add(customer);
            db.Licenses.Add(new License
            {
                Id = licenseId, CustomerId = customer.Id, LicenseKey = "srr-" + Guid.NewGuid().ToString("N")[..12],
                SkuCode = "STD", ActivationSlots = 2, IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(1), SalesSyncMode = mode,
                SalesSyncModeChangedAt = mode == SalesSyncMode.On ? T0.AddDays(-30) : null,
            });
            await db.SaveChangesAsync();
        });
        return licenseId;
    }

    /// <summary>S1 öncesi imajın oturum INSERT'i: yalnız onun bildiği kolonlar (sahip, devir NULL kalır).</summary>
    private Task InsertPreS1SessionAsync(Guid licenseId, Guid sessionId, DateTimeOffset? endedAt) => ExecAsync($"""
        INSERT INTO StreamSessions ({string.Join(", ", SalesSyncS1MigrationScenario.PreS1StreamSessionColumns)})
        VALUES (@id, @licenseId, N'Eski imaj yayını', @started, @ended, N'tiktok', NULL, @started)
        """, ("@id", sessionId), ("@licenseId", licenseId), ("@started", T0), ("@ended", (object?)endedAt ?? DBNull.Value));

    /// <summary>S1 öncesi imajın sipariş INSERT'i (devir damgası, kaynak kopya ve birim damgaları NULL kalır).</summary>
    private async Task<Guid> InsertPreS1OrderAsync(Guid licenseId, Guid? sessionId)
    {
        var id = Guid.NewGuid();
        await ExecAsync($"""
            INSERT INTO Orders ({string.Join(", ", SalesSyncS1MigrationScenario.PreS1OrderColumns)})
            VALUES (@id, @licenseId, @sessionId, @customerId, N'tiktok', @username, NULL, N'A1', N'A1', 100.00,
                    @now, NULL, NULL, NULL, 0, 0, 0, NULL, NULL, @now, 1)
            """,
            ("@id", id), ("@licenseId", licenseId), ("@sessionId", (object?)sessionId ?? DBNull.Value),
            ("@customerId", Guid.NewGuid().ToString("N")), ("@username", $"u-{Guid.NewGuid():N}"), ("@now", T0));
        return id;
    }

    private static Order NewOrder(
        Guid licenseId, Guid? sessionId, Guid id, decimal price = 100m, Guid? shipment = null, bool printed = true,
        bool cancelled = false, bool tentative = false, bool shippingFee = false) => new()
    {
        Id = id, LicenseId = licenseId, SessionId = sessionId, CustomerId = Guid.NewGuid().ToString("N"),
        Platform = "tiktok", Username = $"u-{Guid.NewGuid():N}", MessageText = "A1", Code = "A1", Price = price,
        AddedAt = T0, PrintedAt = printed ? T0 : null, CancelledAt = cancelled ? T0 : null,
        IsTentativeBackup = tentative, IsShippingFee = shippingFee, ShipmentId = shipment, UpdatedAt = T0,
        SyncVersion = 1, OriginReplicaId = Guid.NewGuid(),
    };

    private static Shipment NewShipment(Guid licenseId, ShipmentStatus status, decimal amount, DateTimeOffset? derivedAt)
        => new()
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = Guid.NewGuid().ToString("N"), Status = status,
            CumulativeAmount = amount, CreatedAt = T0, UpdatedAt = T0, AmountDerivedAt = derivedAt,
            ShippedAt = status == ShipmentStatus.Shipped ? T0 : null,
        };

    private async Task<StreamSession> SessionAsync(Guid id)
    {
        StreamSession row = null!;
        await WithDbAsync(async db => row = await db.StreamSessions.AsNoTracking().SingleAsync(s => s.Id == id));
        return row;
    }

    private async Task<Order> OrderAsync(Guid id)
    {
        Order row = null!;
        await WithDbAsync(async db => row = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id));
        return row;
    }

    private async Task<Dictionary<Guid, Shipment>> ShipmentsAsync()
    {
        Dictionary<Guid, Shipment> rows = null!;
        await WithDbAsync(async db => rows = await db.Shipments.AsNoTracking().ToDictionaryAsync(s => s.Id));
        return rows;
    }

    /// <summary>Üç akış tablosunun bütün satırlarının ChangeSeq'i (Id'ler tablolar arasında çakışmaz).</summary>
    private async Task<Dictionary<Guid, long>> ChangeSeqsAsync()
    {
        var result = new Dictionary<Guid, long>();
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, CAST(ChangeSeq AS bigint) FROM StreamSessions
            UNION ALL SELECT Id, CAST(ChangeSeq AS bigint) FROM Orders
            UNION ALL SELECT Id, CAST(ChangeSeq AS bigint) FROM Shipments
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetGuid(0), reader.GetInt64(1));
        return result;
    }

    private async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary><see cref="RelationalApiFactory"/> + kaydetme kancası (eşzamanlı yazımı sıraya dayalı kurmak için).</summary>
    private sealed class HookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public SaveHookInterceptor Hook { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
