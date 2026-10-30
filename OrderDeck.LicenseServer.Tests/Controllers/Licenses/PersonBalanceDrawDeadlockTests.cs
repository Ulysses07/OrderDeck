using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using ApplyResponse = OrderDeck.LicenseServer.Controllers.Licenses.LicensesCustomerBalanceApplyController.ApplyResponse;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Havuzlu apply'ın kilitlenme kurbanı (1205) dalı, gerçek EF + SQL Server hattında ve belirlenimli (spec 2026-10-11
/// §4.2 adım 6 "S2 uygulama notu"). Paralel yarış (<see cref="PersonBalanceDrawRaceTests"/>) kilitlenmeyi yalnız bazı
/// koşularda doğurur; burada komut kancası seçilen komutta BİR KEZ <c>SqlException(1205)</c> fırlatır, komut sunucuya
/// gitmez.
/// <list type="bullet">
/// <item><b>Kayıt:</b> çekimin ilk yazım komutu. Varsayılan yürütme stratejisi onu
/// <see cref="InvalidOperationException"/>'a sarar; yalnız <see cref="DbUpdateException"/>'a bakan süzgeç kurbanı
/// 500'e düşürürdü.</item>
/// <item><b>Okuma:</b> denemenin ilk bakiye okuması (<c>ResolveDrawSetAsync</c>). <c>DrawAsync</c>'in <c>try</c>'ı
/// okumaları da kapsar; kapsamasaydı kurban 500 alırdı.</item>
/// </list>
/// InMemory'de komut kancası yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceDrawDeadlockTests : IAsyncLifetime
{
    public const string AtSave = "kayıt";
    public const string AtBalanceRead = "okuma";

    private readonly SqlServerContainerFixture _sql;
    private DeadlockFactory _factory = null!;

    public PersonBalanceDrawDeadlockTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new DeadlockFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Deadlock.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(AtSave)]
    [InlineData(AtBalanceRead)]
    public async Task Kilitlenme_kurbani_apply_bastan_kurar_ve_basarir(string at)
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        await kit.EnablePoolAsync();
        var k = Guid.NewGuid();
        _factory.Deadlock.Arm(at == AtSave ? DeadlockOnce.IsFirstWrite : DeadlockOnce.IsBalanceRead);

        var resp = await kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]);
        _factory.Deadlock.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ApplyResponse>()).Should()
            .Be(new ApplyResponse(k, 120m, 10m), "Örnek 1'in yanıtı: (K, 120, 10)");
        _factory.Deadlock.Injected.Should().Be(1, "kanca seçilen komutta ({0}) bir kez fırlatmalı", at);
        _factory.Log.Entries.Where(e => e.Message.Contains("kilitlenme (1205)", StringComparison.Ordinal))
            .Should().ContainSingle("tek yeniden deneme, nedeni kilitlenme")
            .Which.Level.Should().Be(LogLevel.Information, "baştan kurma satırı; balance-busy uyarısı değil");

        var rows = await kit.GroupRowsAsync(k);
        rows.Should().HaveCount(3, "ilk denemeden hiçbir şey kalmadı, ikinci tam yazdı");
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should()
            .BeEquivalentTo(new[] { (t, 0m), (i, -100m), (y, -20m) }, "Örnek 1'in satırları");
        (await kit.BalanceAsync(t)).Should().Be(0m);
        (await kit.BalanceAsync(i)).Should().Be(0m);
        (await kit.BalanceAsync(y)).Should().Be(10m, "baştan kurulan çekimde Y'den bir kez");
        foreach (var row in new[] { t, i, y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Kurulduktan sonra <c>when</c>'i tutan ilk komutta bir kez SQL Server kilitlenme kurbanı hatası fırlatır; komut
    /// sunucuya hiç gitmez (emsal <c>PersonBalanceReversalDeadlockTests.DeadlockOnce</c>).
    /// </summary>
    private sealed class DeadlockOnce : DbCommandInterceptor
    {
        private Func<string, bool>? _when;
        private int _injected;

        public int Injected => Volatile.Read(ref _injected);

        /// <summary>Çekimin ilk yazım komutu: defter INSERT'i ya da bakiye UPDATE'i (EF tek grupta gönderir).</summary>
        public static bool IsFirstWrite(string sql) =>
            sql.Contains("INSERT INTO [CustomerBalanceTransactions]", StringComparison.Ordinal)
            || sql.Contains("MERGE [CustomerBalanceTransactions]", StringComparison.Ordinal)
            || sql.Contains("UPDATE [CustomerBalances]", StringComparison.Ordinal);

        /// <summary>Denemenin ilk bakiye okuması (çekim kümesinin izlemesiz bakiyeleri).</summary>
        public static bool IsBalanceRead(string sql) =>
            sql.Contains("FROM [CustomerBalances]", StringComparison.Ordinal);

        public void Arm(Func<string, bool> when) => Volatile.Write(ref _when, when);

        public void Disarm() => Volatile.Write(ref _when, null);

        private void ThrowOnce(DbCommand command)
        {
            if (Volatile.Read(ref _when) is not { } when || !when(command.CommandText)) return;
            if (Interlocked.Exchange(ref _when, null) is null) return;
            Interlocked.Increment(ref _injected);
            throw SqlExceptionFactory.Create(1205, "Transaction was deadlocked and has been chosen as the deadlock victim.");
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Havuz kipi için yapılandırılmış ilişkisel fabrika (iki sürüm kapısı da kitin <c>User-Agent</c>'ını kabul eder) +
    /// komut kancası + <see cref="PersonBalance"/>'ın günlüğü.
    /// </summary>
    private sealed class DeadlockFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public DeadlockOnce Deadlock { get; } = new();

        /// <summary>Yeniden deneme nedeni yalnız günlükte: <see cref="PersonBalance"/>'ın satırları.</summary>
        public LogRecorder<PersonBalance> Log { get; } = new();

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["PersonBalance:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
            ["SalesSync:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Deadlock);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ILogger<PersonBalance>>(Log));
        }
    }
}
