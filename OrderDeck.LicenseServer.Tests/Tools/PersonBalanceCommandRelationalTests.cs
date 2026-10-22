using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>person-balance verify --capture-baseline --apply</c>'ın eşzamanlı yazım yolu gerçek SQL Server'da: süzgeç
/// birincil anahtar ihlalini gerçek iletisinden tanır, kayıt atomiktir. InMemory'de anahtar yarışı ve atomik kayıt yok.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceCommandRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RaceRelationalApiFactory _factory = null!;

    public PersonBalanceCommandRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RaceRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Hook.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Eszamanli_yakalama_ayni_lisansin_isaretini_yazdiysa_1_doner_hicbir_satir_yazilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var negative = await kit.MemberAsync(null, -10m);
        // Rakip koşu, bu koşunun okumasından sonra ve kaydından hemen önce aynı lisansın işaretini yazar.
        _factory.Hook.Once(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.PersonBalanceBaselines.Add(new PersonBalanceBaseline
            {
                LicenseId = kit.LicenseId, WpfCustomerId = PersonBalanceVerifier.LicenseMarker,
                CapturedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        int exit;
        var output = new StringWriter();
        var error = new StringWriter();
        using (var scope = _factory.Services.CreateScope())
        {
            PersonBalanceCommand.TryParseArgs(
                    ["person-balance", "verify", "--license", kit.LicenseId.ToString(), "--capture-baseline", "--apply"],
                    out var parsed)
                .Should().BeTrue();
            exit = await PersonBalanceCommand.RunAsync(
                scope.ServiceProvider.GetRequiredService<LicenseDbContext>(), parsed!, output, error, default);
        }

        exit.Should().Be(1, $"birincil anahtar yarışı (çıktı: {output}; hata: {error})");
        error.ToString().Should().Contain("başka bir koşuyla yazıldı");
        using (var scope = _factory.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PersonBalanceBaselines
                .AsNoTracking().Where(b => b.LicenseId == kit.LicenseId).ToListAsync();
            rows.Should().ContainSingle("yalnız rakibin işareti; bu koşunun eksi satırı da yazılmadı (tek işlem)")
                .Which.WpfCustomerId.Should().Be(PersonBalanceVerifier.LicenseMarker);
            rows.Should().NotContain(b => b.WpfCustomerId == negative);
        }
    }
}
