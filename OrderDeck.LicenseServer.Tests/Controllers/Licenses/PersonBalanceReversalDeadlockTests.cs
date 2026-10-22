using System.Data.Common;
using System.Net;
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
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Grup geri almasının kilitlenme kurbanı (1205) dalı, gerçek EF + SQL Server hattında ve belirlenimli (spec 2026-10-11
/// §4.5). Paralel yarış (§8.2-3, <see cref="PersonBalanceReversalRaceTests"/>) kilitlenmeyi yalnız bazı koşularda
/// doğurur; burada komut kancası seçilen komutta BİR KEZ <c>SqlException(1205)</c> fırlatır, komut sunucuya gitmez.
/// <list type="bullet">
/// <item><b>Kayıt:</b> geri almanın ilk yazım komutu. EF onu <see cref="DbUpdateException"/>'a, varsayılan yürütme
/// stratejisi de <see cref="InvalidOperationException"/>'a sarar. Süzgeç yeniden <see cref="DbUpdateException"/>'a
/// daraltılırsa düşer.</item>
/// <item><b>Okuma:</b> "geri alındı mı" hızlı yolu (N01 anahtarı). Yazarlar N01 anahtarlarında birbirini kilitleyince
/// aynı anahtarın kuyruğunda bekleyen okuyucu da döngüye girer ve kurban seçilebilir (en ucuzu odur; yarış testinde
/// görüldü). Strateji onu <see cref="InvalidOperationException"/>'a sarar. Denemenin okumaları kilitlenme süzgecinin
/// dışında kalırsa düşer.</item>
/// </list>
/// InMemory'de komut kancası yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceReversalDeadlockTests : IAsyncLifetime
{
    public const string AtSave = "kayıt";
    public const string AtReversedCheck = "okuma";

    private readonly SqlServerContainerFixture _sql;
    private DeadlockFactory _factory = null!;

    public PersonBalanceReversalDeadlockTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new DeadlockFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Deadlock.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(AtSave)]
    [InlineData(AtReversedCheck)]
    public async Task Kilitlenme_kurbani_grubu_bastan_kurar_ve_geri_alma_basarir(string at)
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        _factory.Deadlock.Arm(at == AtSave ? DeadlockOnce.IsFirstWrite : DeadlockOnce.IsReversedCheck);

        var resp = await kit.ReverseAsync(ex.K);
        _factory.Deadlock.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        _factory.Deadlock.Injected.Should().Be(1, "kanca seçilen komutta ({0}) bir kez fırlatmalı", at);
        _factory.Log.Entries.Where(e => e.Message.Contains("kilitlenme (1205)", StringComparison.Ordinal))
            .Should().ContainSingle("tek yeniden deneme, nedeni kilitlenme")
            .Which.Level.Should().Be(LogLevel.Information, "baştan kurma satırı; balance-busy uyarısı değil");

        var rows = await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2]);
        rows.Should().HaveCount(3, "ya hepsi ya hiçbiri (D5): ilk denemeden hiçbir şey kalmadı, ikinci tam yazdı");
        rows.Select(r => r.DrawGroupId).Distinct().Should().ContainSingle("tek grup geri alması");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m, "baştan kurulan grupta delta bir kez");
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>
    /// Kurulduktan sonra <c>when</c>'i tutan ilk komutta bir kez SQL Server kilitlenme kurbanı hatası fırlatır; komut
    /// sunucuya hiç gitmez.
    /// </summary>
    private sealed class DeadlockOnce : DbCommandInterceptor
    {
        private Func<string, bool>? _when;
        private int _injected;

        public int Injected => Volatile.Read(ref _injected);

        /// <summary>Geri almanın ilk yazım komutu: defter INSERT'i ya da bakiye UPDATE'i.</summary>
        public static bool IsFirstWrite(string sql) =>
            sql.Contains("INSERT INTO [CustomerBalanceTransactions]", StringComparison.Ordinal)
            || sql.Contains("MERGE [CustomerBalanceTransactions]", StringComparison.Ordinal)
            || sql.Contains("UPDATE [CustomerBalances]", StringComparison.Ordinal);

        /// <summary>"Geri alındı mı" hızlı yolu: N01 anahtarında eşitlik.</summary>
        public static bool IsReversedCheck(string sql) =>
            sql.Contains("[ReversesTransactionId] = @", StringComparison.Ordinal);

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

    private sealed class DeadlockFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public DeadlockOnce Deadlock { get; } = new();

        /// <summary>Yeniden deneme nedeni yalnız günlükte: <see cref="PersonBalance"/>'ın satırları.</summary>
        public LogRecorder<PersonBalance> Log { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Deadlock);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ILogger<PersonBalance>>(Log));
        }
    }
}
