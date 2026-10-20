using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// §8.2-12: senkronun <c>GroupId</c> değişikliği bakiye jetonunu gerçek SQL Server'da ilerletir; okuma ile yazma
/// arasına giren bir grup değişimi bakiye yazımını çatıştırır, apply'ın F02 döngüsü taze okumayla tamamlar. Canlı
/// yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonGroupHookRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RaceRelationalApiFactory _factory = null!;

    public PersonGroupHookRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RaceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    [Fact]
    public async Task Okunmus_bakiye_satiri_grup_degisiminden_sonra_yazilamaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 100m);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a);

        (await kit.SyncGroupAsync(a, null)).EnsureSuccessStatusCode(); // Ayır

        balance.Balance -= 30m;
        var write = async () => await db.SaveChangesAsync();
        await write.Should().ThrowAsync<DbUpdateConcurrencyException>("kanca ayırmada para tutan satırın jetonunu ilerletti");
    }

    [Fact]
    public async Task Apply_okumasi_ile_yazimi_arasina_giren_grup_degisimi_cakisir_F02_taze_okumayla_tamamlar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 100m);
        var g2 = Kit.NewGroup();
        _factory.Hook.Once(async () => (await kit.SyncGroupAsync(a, g2)).EnsureSuccessStatusCode());

        var resp = await kit.ApplyAsync(a, 30m, 200m, key: Guid.NewGuid(), saleScope: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<ApplyDto>())!.AppliedAmount.Should().Be(30m);
        _factory.Hook.ConcurrencyFailures.Should().BeGreaterThan(0, "apply'ın ilk UPDATE'i ilerlemiş jetona çarptı");
        (await kit.ProjectionAsync(a)).GroupId.Should().Be(g2);
        (await kit.BalanceAsync(a)).Should().Be(70m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
    }
}
