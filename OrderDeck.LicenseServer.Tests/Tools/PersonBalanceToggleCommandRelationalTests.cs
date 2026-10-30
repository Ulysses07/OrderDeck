using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;
using ToggleArgs = OrderDeck.LicenseServer.Tools.PersonBalanceCommand.ToggleArgs;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>person-balance enable|disable --apply</c> (spec 2026-10-11 §7.2, §7.4) gerçek SQL Server'da: tek koşullu UPDATE
/// bayrağı yazar ya da boşaltır, ikinci <c>enable</c> etkisizdir (S2 planı fark 18e), <c>disable</c> hiçbir hareket ya da
/// bakiye satırına dokunmaz. Eşzamanlı bir rakip bayrağı UPDATE'ten hemen önce değiştirirse 0 satır → 1, rakibin değeri
/// kalır. CLI'ın bağlamı fabrikanınkinden ayrı bir bağlantıdır (komutun gerçek kurulumu gibi); kanca yalnız ona takılır.
/// InMemory'de <c>ExecuteUpdateAsync</c> ve atomik koşullu yazım yok. Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceToggleCommandRelationalTests : IAsyncLifetime
{
    private const string Min = "0.9.11";
    private static readonly PersonBalanceOptions Pb = new() { MinClientVersion = Min };
    private static readonly SalesSyncOptions Ss = new() { MinClientVersion = Min };

    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public PersonBalanceToggleCommandRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

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

    // ── Kurulum ───────────────────────────────────────────────────────────────

    /// <summary>Dört koşulu tutan lisans: geri tarihli, incelenmiş taban işareti, alt sınırda tek etkin PC, verisiz.</summary>
    private async Task<Kit> PassingLicenseAsync()
    {
        var kit = await Kit.CreateAsync(_factory);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        await kit.Sales.WithDbAsync(async db =>
        {
            (await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId)).IssuedAt = DateTimeOffset.UtcNow.AddYears(-1);
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = PersonBalanceVerifier.LicenseMarker,
                CapturedAt = at, ReviewedAt = at,
            });
            db.Activations.Add(new Activation
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, HardwareFingerprint = $"hw-{Guid.NewGuid():N}",
                ActivatedAt = DateTimeOffset.UtcNow.AddDays(-3), LastSeenAt = DateTimeOffset.UtcNow, AppVersion = Min,
            });
        });
        return kit;
    }

    private static Task<DateTimeOffset?> PoolSinceAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
            .Where(l => l.Id == kit.LicenseId)
            .Select(l => l.PersonBalancePoolSince)
            .SingleAsync());

    /// <summary>CLI'ın kendi bağlamı (<c>RunAsync(string[])</c> gibi doğrudan <c>UseSqlServer</c>), isteğe bağlı kancayla.</summary>
    private async Task<(int Exit, string Output, string Error)> RunAsync(ToggleArgs args, IInterceptor? hook = null)
    {
        var builder = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs);
        if (hook is not null) builder.AddInterceptors(hook);
        await using var db = new LicenseDbContext(builder.Options);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = args.Enable
            ? await PersonBalanceCommand.RunEnableAsync(db, args, Pb, Ss, output, error, default)
            : await PersonBalanceCommand.RunDisableAsync(db, args, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    // ── enable ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kosullar_tutunca_enable_apply_bayragi_yazar()
    {
        var kit = await PassingLicenseAsync();

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: true, kit.LicenseId, Apply: true));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance enable").And.Contain("Sonuç: bütün koşullar tutuyor")
            .And.Contain("UYGULANDI: havuz kipi açık, PersonBalancePoolSince=");
        var since = await PoolSinceAsync(kit);
        since.Should().NotBeNull();
        since!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        // İkinci koşu etkisiz (S2 planı fark 18e): 0, kapı basılmaz, tarih korunur.
        var again = await RunAsync(new ToggleArgs(Enable: true, kit.LicenseId, Apply: true));
        again.Exit.Should().Be(0, again.Error);
        again.Output.Should().Contain("Havuz kipi zaten açık").And.NotContain("Koşul 1").And.NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().Be(since);
    }

    /// <summary>Kapının değerlendirmesinden sonra, koşullu UPDATE'ten hemen önce rakip bir enable bayrağı yazar: UPDATE
    /// 0 satır günceller → 1, rakibin değeri kalır.</summary>
    [Fact]
    public async Task Enable_apply_esanli_degisiklikte_1()
    {
        var kit = await PassingLicenseAsync();
        var rival = DateTimeOffset.UtcNow.AddDays(-2);
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[PersonBalancePoolSince]", StringComparison.Ordinal),
            before: () => kit.Sales.WithDbAsync(db => db.Licenses.Where(l => l.Id == kit.LicenseId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, rival))));

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: true, kit.LicenseId, Apply: true), hook);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1, output);
        error.Should().Contain("Havuz kipi bu arada değişti (başka bir enable?)").And.Contain("yeniden çalıştır");
        output.Should().Contain("Sonuç: bütün koşullar tutuyor").And.NotContain("UYGULANDI");
        var since = await PoolSinceAsync(kit);
        since.Should().NotBeNull();
        since!.Value.Should().BeCloseTo(rival, TimeSpan.FromSeconds(1), "rakibin yazdığı değer korunur");
    }

    // ── disable ───────────────────────────────────────────────────────────────

    /// <summary>Kill switch (§7.2, §7.4): bayrak boşalır; Örnek 1'in çok bacaklı grubu ve bakiyeler olduğu gibi kalır.</summary>
    [Fact]
    public async Task Disable_apply_bayragi_bosaltir_veriye_dokunmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync(DateTimeOffset.UtcNow.AddDays(-5));
        var before = await LedgerShapeAsync(kit);

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: false, kit.LicenseId, Apply: true));

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().Contain("UYGULAMA — person-balance disable").And.Contain(
            "UYGULANDI: havuz kipi kapalı (satır kipi); çok bacaklı geçmiş okunur ve geri alınır, veri işlemi yapılmadı.");
        (await PoolSinceAsync(kit)).Should().BeNull();
        (await LedgerShapeAsync(kit)).Should().Be(before, "disable veri işlemi yapmaz");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    [Fact]
    public async Task Disable_apply_esanli_degisiklikte_1()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync(DateTimeOffset.UtcNow.AddDays(-5));
        var hook = new BeforeNonQueryHook(
            when: sql => sql.Contains("[PersonBalancePoolSince]", StringComparison.Ordinal),
            before: () => kit.Sales.WithDbAsync(db => db.Licenses.Where(l => l.Id == kit.LicenseId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, (DateTimeOffset?)null))));

        var (exit, output, error) = await RunAsync(new ToggleArgs(Enable: false, kit.LicenseId, Apply: true), hook);

        hook.Ran.Should().BeTrue();
        exit.Should().Be(1, output);
        error.Should().Contain("Havuz kipi bu arada değişti (başka bir disable?)").And.Contain("yeniden çalıştır");
        output.Should().NotContain("UYGULANDI");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    /// <summary>Lisansın defteri ve bakiyeleri: hareket sayısı, bacak sayısı (DrawGroupId dolu), hareket toplamı,
    /// bakiye satırı sayısı ve bakiye toplamı.</summary>
    private static Task<(int Transactions, int Legs, decimal LedgerTotal, int Balances, decimal BalanceTotal)>
        LedgerShapeAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(async db =>
        {
            var amounts = await db.CustomerBalanceTransactions.AsNoTracking()
                .Where(t => t.LicenseId == kit.LicenseId)
                .Select(t => new { t.Amount, Leg = t.DrawGroupId != null })
                .ToListAsync();
            var balances = await db.CustomerBalances.AsNoTracking()
                .Where(b => b.LicenseId == kit.LicenseId)
                .Select(b => b.Balance)
                .ToListAsync();
            return (amounts.Count, amounts.Count(a => a.Leg), amounts.Sum(a => a.Amount), balances.Count, balances.Sum());
        });

    // ── Kanca ─────────────────────────────────────────────────────────────────

    /// <summary>Okuyucusuz komut kancası: <c>when</c>'i tutan ilk komutun hemen öncesinde <c>before</c>'u bir kez
    /// koşturur (<see cref="SalesSyncEnableRelationalTests"/>'teki kancanın kopyası; o sınıf içi özel).</summary>
    private sealed class BeforeNonQueryHook(Func<string, bool> when, Func<Task> before) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Ran && when(command.CommandText))
            {
                Ran = true;
                await before();
            }
            return result;
        }
    }
}
