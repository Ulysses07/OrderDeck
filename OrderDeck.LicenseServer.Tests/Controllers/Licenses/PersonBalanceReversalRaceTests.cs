using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Grup geri almasının eşzamanlılık sözleşmesi gerçek SQL Server'da (spec 2026-10-11 §8.2-3, -4, -5; §4.5, §4.12):
/// ya hepsi ya hiçbiri (D5), bacak başına tek geri alma (N01), çakışmada grup baştan kurulur (delta iki kez eklenmez),
/// para bacağın GÜNCEL satırına yazılır. InMemory'de tekil indeks ve atomik kayıt yok. Canlı yayın sırasında
/// ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceReversalRaceTests : IAsyncLifetime
{
    private const int Parallel = 8;

    private readonly SqlServerContainerFixture _sql;
    private RaceRelationalApiFactory _factory = null!;

    public PersonBalanceReversalRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RaceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>TestServer, sunucu tarafında yakalanmayan istisnayı istemciye fırlatır; prod'da 500 olurdu.</summary>
    private static async Task<HttpStatusCode> StatusAsync(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            return (await call()).StatusCode;
        }
        catch (Exception)
        {
            return HttpStatusCode.InternalServerError;
        }
    }

    private static async Task ShouldBeReversedOnceAsync(Kit kit, Kit.Example1 ex)
    {
        var rows = await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2]);
        rows.Should().HaveCount(3, "ya hepsi ya hiçbiri (D5), bacak başına tek geri alma (N01)");
        rows.Select(r => r.DrawGroupId).Distinct().Should().ContainSingle("tek grup geri alması");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>§8.2-3a: WPF ∥ WPF — aynı çok bacaklı çekimi 8 paralel geri alma.</summary>
    [Fact]
    public async Task WPF_paralel_grup_geri_almasindan_yalniz_biri_kazanir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        // Isınma: rota + EF JIT (var olmayan hareket → 404), gerçek turda istekler ayrışmasın.
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.ReverseAsync(Guid.NewGuid())));

        var statuses = await Task.WhenAll(Enumerable.Range(0, Parallel)
            .Select(_ => StatusAsync(() => kit.ReverseAsync(ex.K))));

        var seen = string.Join(", ", statuses);
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1, seen);
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(Parallel - 1,
            "kaybedenler already-reversed — 500 varsa N01/kilitlenme yolu eşleşmiyor ({0})", seen);
        await ShouldBeReversedOnceAsync(kit, ex);
    }

    /// <summary>§8.2-3b: WPF ∥ panel — aynı grubu iki yüzey birlikte geri alır.</summary>
    [Fact]
    public async Task WPF_ve_panel_paralel_grup_geri_almasindan_yalniz_biri_kazanir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => kit.PanelReverseAsync(ex.I, Guid.NewGuid())));

        var statuses = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(i => i % 2 == 0
            ? StatusAsync(() => kit.ReverseAsync(ex.K))
            : StatusAsync(() => kit.PanelReverseAsync(ex.I, ex.K))));

        var seen = string.Join(", ", statuses);
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1, seen);
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(Parallel - 1, seen);
        await ShouldBeReversedOnceAsync(kit, ex);
    }

    /// <summary>
    /// §8.2-4: grup geri alması ∥ bacak satırlarından birine eşzamanlı artı yazım (panel iadesi). İlk deneme I'nin
    /// jetonuna çarpar; eski "deltayı yeniden uygula" kalıbı çakışmayan Y'ye 20'yi iki kez eklerdi (Y = 50).
    /// </summary>
    [Fact]
    public async Task Bacak_satirina_eszamanli_iade_gelince_grup_bastan_kurulur_delta_iki_kez_eklenmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        _factory.Hook.Once(async () => (await kit.PanelRefundAsync(ex.I, 50m)).EnsureSuccessStatusCode());

        var resp = await kit.ReverseAsync(ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0, "ilk deneme iadenin ilerlettiği jetona çarpmalı");
        (await kit.BalanceAsync(ex.I)).Should().Be(150m, "0 + 50 iade + 100 geri alma");
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m, "çakışmayan satıra delta bir kez");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
    }

    /// <summary>
    /// §8.2-5: grup geri alması ∥ kimlik birleşmesi ("@" ikizi), bacak satırı I 0'da kalmışken kopyaya dönüyor.
    /// Birleştirici sıfır bakiyeli kopya satırını damgalar (M12) → geri alma çakışır, bacağın güncel (asıl) satırıyla
    /// yeniden kurulur; para asıl kayda yazılır, görünmez kopyada kalmaz.
    /// </summary>
    [Fact]
    public async Task Kimlik_birlesmesiyle_carpisan_grup_geri_almasi_parayi_asil_kayda_yazar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var name = $"u{Guid.NewGuid():N}"[..16];
        var ex = await kit.Example1Async(iUsername: "@" + name);
        var canonical = await kit.MemberAsync(null, balanceRow: false, platform: "instagram", username: name);
        _factory.Hook.Once(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<CustomerIdentityMergeJob>();
            (await job.RunAsync(kit.LicenseId, apply: true, default, atTwins: true)).FailedGroups.Should().Be(0);
        });

        var resp = await kit.ReverseAsync(ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.ProjectionAsync(ex.I)).MergedIntoId.Should().Be(canonical, "'@'lı satır '@'sız asıl kayda birleşti");
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0, "sıfır bakiyeli kopya satırı damgalandı (M12)");
        (await kit.ReversalsOfAsync([ex.L1])).Single().WpfCustomerId.Should().Be(canonical);
        (await kit.BalanceAsync(canonical)).Should().Be(100m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m, "para görünmez kopyada kalmaz");
        (await kit.LedgerHoldsAsync(canonical)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(ex.I)).Should().BeTrue("D1");
    }

    /// <summary>§4.5: üç denemede de çakışan grup geri alması 503 <c>balance-busy</c>; işlem bütün ya da hiç.</summary>
    [Fact]
    public async Task Uc_denemede_de_cakisan_grup_geri_almasi_503_balance_busy_ve_hicbir_sey_yazmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        _factory.Hook.Always(() => TouchBalanceAsync(ex.I));

        var resp = await kit.ReverseAsync(ex.K);
        _factory.Hook.Disarm();

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Kit.TitleAsync(resp)).Should().Be("balance-busy");
        _factory.Hook.ConcurrencyFailures.Should().Be(PersonBalance.MaxAttempts);
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().BeEmpty("SaveChanges tek işlem");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>Kilit dokunuşu: satıra yalnız "değişti" işareti, jetonu StampVersions ilerletir.</summary>
    private async Task TouchBalanceAsync(Guid row)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == row);
        db.Entry(balance).Property(b => b.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync();
    }
}
