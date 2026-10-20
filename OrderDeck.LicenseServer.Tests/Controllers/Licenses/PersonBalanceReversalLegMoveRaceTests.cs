using System.Data.Common;
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Grup geri alması ∥ kimlik birleşmesi, birleşme bacak OKUNDUKTAN SONRA ama bacağın bakiye satırı okunmadan ÖNCE
/// commit ederken (spec 2026-10-11 §4.5, §4.12, M12). Birleştirici bacağı asıl kayda taşır ve kopyanın sıfır bakiyeli
/// satırını damgalar — ikisi tek işlemde. Geri alma kopyanın satırını birleşme SONRASI jetonla okur; damga bu sırada
/// geri almayı korumaz (UPDATE tutar). Koruyan, bakiye satırları okunduktan sonra bacak sahiplerinin yeniden
/// okunmasıdır: sahip değiştiyse grup baştan kurulur ve para bacağın güncel (asıl) satırına yazılır.
///
/// <para>Birleşmenin <c>SaveChanges</c>'te, yani bütün okumalardan sonra koştuğu yarış (§8.2-5) jetonla kapanır; bu
/// sınıf ondan önceki pencereyi sınar. Yarış sırayla kurulur: sorgu kancası geri almanın ilk bakiye satırı okumasının
/// hemen önünde birleşmeyi ayrı bir bağlamda commit ettirir. InMemory'de komut kancası yok. Canlı yayın sırasında
/// ÇALIŞTIRILMAZ.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceReversalLegMoveRaceTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private HookFactory _factory = null!;

    public PersonBalanceReversalLegMoveRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new HookFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Bakiye_satiri_okunmadan_once_birlesen_bacagin_parasi_asil_kayda_yazilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var canonical = await kit.MemberAsync(ex.Group, balanceRow: false, platform: "instagram");
        var merged = false;
        _factory.Queries.Arm(
            sql => sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal),
            async () =>
            {
                // Sync ucunun ve birleştirme işinin birleşmesi: başvurular taşınır, kopya yönlenir, tek kayıt.
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
                var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
                await merger.RepointReferencesAsync(kit.LicenseId, ex.I, canonical, default);
                (await db.WpfCustomerProjections.SingleAsync(p => p.Id == ex.I)).MergedIntoId = canonical;
                await db.SaveChangesAsync();
                merged = true;
            });

        var resp = await kit.ReverseAsync(ex.K);
        _factory.Queries.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        merged.Should().BeTrue("kanca birleşmeyi geri almanın bakiye satırı okumasının önünde koşturmalı");
        (await kit.ReversalsOfAsync([ex.L1])).Single().WpfCustomerId.Should().Be(canonical,
            "bacak geri alma sırasında asıl kayda taşınmıştı");
        (await kit.BalanceAsync(canonical)).Should().Be(100m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m, "para görünmez kopyada kalmaz");
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m, "baştan kurulan grupta delta bir kez");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
        foreach (var row in new[] { ex.T, ex.I, ex.Y, canonical })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Sorgu kancası: <c>when</c>'i tutan ilk okuma komutunun hemen öncesinde <c>before</c>'u bir kez koşturur.
    /// Yeniden girmez (kanca gövdesinin sorguları onu tetiklemez).
    /// </summary>
    private sealed class QueryHook : DbCommandInterceptor
    {
        private Func<string, bool>? _when;
        private Func<Task>? _before;
        private bool _running;

        public void Arm(Func<string, bool> when, Func<Task> before)
        {
            _when = when;
            _before = before;
        }

        public void Disarm() => _before = null;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_before is { } hook && !_running && _when!(command.CommandText))
            {
                _before = null;
                _running = true;
                try { await hook(); }
                finally { _running = false; }
            }
            return result;
        }
    }

    private sealed class HookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public QueryHook Queries { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Queries);
    }
}
