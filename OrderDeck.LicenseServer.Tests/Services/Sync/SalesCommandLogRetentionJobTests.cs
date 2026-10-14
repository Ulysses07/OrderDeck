using FluentAssertions;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// <c>sales-command-log-retention</c> — spec 2026-10-13 §3.1 (90 gün), §4.9 (InMemory: izlenen varlıklarla silme yolu,
/// takvim ve DI). <c>ExecuteDelete</c> yolu gerçek SQL Server'da (<see cref="SalesCommandLogRetentionJobRelationalTests"/>).
/// </summary>
public sealed class SalesCommandLogRetentionJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SalesCommandLogRetentionJobTests(ApiFactory factory) => _factory = factory;

    /// <summary>90 günden eski satırlar (bütün lisanslarda) partilerle silinir; daha yenileri kalır.</summary>
    [Fact]
    public async Task Doksan_gunden_eski_satirlar_partilerle_silinir_yenileri_kalir()
    {
        var first = await SalesCommandTestKit.CreateAsync(_factory);
        var second = await first.SecondLicenseAsync();
        var now = DateTimeOffset.UtcNow;
        var old = new[]
        {
            await SalesCommandLogSeed.AddAsync(first, now.AddDays(-91)),
            await SalesCommandLogSeed.AddAsync(first, now.AddDays(-200)),
            await SalesCommandLogSeed.AddAsync(second, now.AddDays(-90).AddMinutes(-1)),
        };
        var kept = new[]
        {
            await SalesCommandLogSeed.AddAsync(first, now.AddDays(-89)),
            await SalesCommandLogSeed.AddAsync(second, now.AddDays(-90).AddMinutes(5)),
            await SalesCommandLogSeed.AddAsync(second, now),
        };

        var deleted = await SalesCommandLogSeed.RunJobAsync(_factory, batchSize: 2);

        deleted.Should().Be(old.Length);
        var remaining = await SalesCommandLogSeed.IdsAsync(_factory, old.Concat(kept));
        remaining.Should().BeEquivalentTo(kept);
        (await SalesCommandLogSeed.RunJobAsync(_factory, batchSize: 2)).Should().Be(0, "ikinci koşu etkisiz");
    }

    /// <summary>Takvim (spec §4.9): 04:54 UTC günlük; açılışta kuyruğa alınmaz.</summary>
    [Fact]
    public void Is_0454_UTC_takvimiyle_kaydolur()
    {
        var storage = new MemoryStorage();

        Program.ScheduleSalesSyncJobs(new RecurringJobManager(storage), new BackgroundJobClient(storage));

        using var connection = storage.GetConnection();
        var job = connection.GetRecurringJobs().Should().ContainSingle(j => j.Id == "sales-command-log-retention").Subject;
        (job.Cron, job.TimeZoneId).Should().Be(("54 4 * * *", "UTC"));
        job.Job.Type.Should().Be(typeof(SalesCommandLogRetentionJob));
        job.Job.Method.Name.Should().Be(nameof(SalesCommandLogRetentionJob.RunAsync));
    }

    /// <summary>Hangfire işi DI kapsamından çözer.</summary>
    [Fact]
    public void Is_DI_kapsamindan_cozulur()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SalesCommandLogRetentionJob>().Should().NotBeNull();
    }
}

/// <summary>
/// <c>sales-command-log-retention</c>'ın gerçek SQL Server yolu: <c>ExecuteDelete</c> partileri (§4.9). InMemory'de
/// <c>ExecuteDelete</c> yok.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesCommandLogRetentionJobRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public SalesCommandLogRetentionJobRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Doksan_gunden_eski_satirlar_ExecuteDelete_partileriyle_silinir_yenileri_kalir()
    {
        var kit = await SalesCommandTestKit.CreateAsync(_factory);
        var now = DateTimeOffset.UtcNow;
        var old = new[]
        {
            await SalesCommandLogSeed.AddAsync(kit, now.AddDays(-91)),
            await SalesCommandLogSeed.AddAsync(kit, now.AddDays(-120)),
            await SalesCommandLogSeed.AddAsync(kit, now.AddDays(-365)),
        };
        var kept = new[]
        {
            await SalesCommandLogSeed.AddAsync(kit, now.AddDays(-89)),
            await SalesCommandLogSeed.AddAsync(kit, now),
        };

        (await SalesCommandLogSeed.RunJobAsync(_factory, batchSize: 2)).Should().Be(old.Length);

        (await SalesCommandLogSeed.IdsAsync(_factory, old.Concat(kept))).Should().BeEquivalentTo(kept);
        (await SalesCommandLogSeed.RunJobAsync(_factory, batchSize: 2)).Should().Be(0);
    }
}

internal static class SalesCommandLogSeed
{
    public static async Task<Guid> AddAsync(SalesCommandTestKit kit, DateTimeOffset createdAt)
    {
        var commandId = Guid.NewGuid();
        await kit.WithDbAsync(db =>
        {
            db.SalesCommandLogs.Add(new SalesCommandLog
            {
                CommandId = commandId,
                LicenseId = kit.LicenseId,
                ReplicaId = kit.ReplicaId,
                Kind = "cancel",
                RequestHash = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(),
                Status = SalesCommandLog.StatusApplied,
                ResultJson = "{\"changed\":[],\"skipped\":[],\"orders\":[],\"shipments\":[]}",
                CreatedAt = createdAt,
            });
            return Task.CompletedTask;
        });
        return commandId;
    }

    public static async Task<int> RunJobAsync(ApiFactory factory, int batchSize)
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<SalesCommandLogRetentionJob>();
        job.BatchSize = batchSize;
        return await job.RunAsync(default);
    }

    /// <summary>Verilenlerden hâlâ duranlar.</summary>
    public static async Task<List<Guid>> IdsAsync(ApiFactory factory, IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.SalesCommandLogs.AsNoTracking()
            .Where(c => list.Contains(c.CommandId))
            .Select(c => c.CommandId)
            .ToListAsync();
    }
}
