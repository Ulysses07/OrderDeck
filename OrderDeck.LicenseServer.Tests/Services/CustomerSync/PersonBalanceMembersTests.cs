using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Sunucu kişisi S(a) — spec 2026-10-11 §2, §4.0, §15-5: istekteki Id'nin asıl kaydı gruplu değilse yalnız kendisi;
/// gruplu ise lisansta numarası ORDİNAL eşit bütün asıl kayıtlar (silinmişler dahil, kopyalar hariç). Ödeme isteği
/// kirasıyla (<c>payment-requests/begin</c>) aynı kaynak.
/// </summary>
public sealed class PersonBalanceMembersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceMembersTests(ApiFactory factory) => _factory = factory;

    private async Task<PersonBalance.Person> MembersAsync(PersonBalanceTestKit kit, Guid a)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PersonBalance>().MembersAsync(kit.LicenseId, a, default);
    }

    [Fact]
    public async Task Gruplu_satirin_kisisi_ayni_numarali_asil_kayitlar_silinmisler_dahil_kopyalar_haric()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var g = PersonBalanceTestKit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, balanceRow: false);
        var p = await kit.MemberAsync(g, 40m, purged: true);
        await kit.MemberAsync(PersonBalanceTestKit.NewGroup(), 99m);              // başka kişi
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: a);                 // kopya: kişide değil
        await kit.Sales.WithDbAsync(async db =>
            (await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(x => x.Id == copy)).GroupId = g);

        var person = await MembersAsync(kit, a);

        person.GroupId.Should().Be(g);
        person.Members.Should().BeEquivalentTo(new[]
        {
            new PersonBalance.Member(a, 10m, false),
            new PersonBalance.Member(b, 0m, false),
            new PersonBalance.Member(p, 40m, true),
        });
    }

    [Fact]
    public async Task Grupsuz_satir_tek_basina_kisidir()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 5m);
        await kit.MemberAsync(null, 7m);

        (await MembersAsync(kit, a)).Members.Should().Equal(new PersonBalance.Member(a, 5m, false));
    }

    [Fact]
    public async Task Yalniz_bosluktan_olusan_numara_grupsuz_sayilir_kira_ile_ayni_kural()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var a = await kit.MemberAsync("   ", 5m);
        await kit.MemberAsync("   ", 7m);

        (await MembersAsync(kit, a)).Members.Select(m => m.WpfCustomerId).Should().Equal(a);
        PaymentPersonKey.IsGrouped("   ").Should().Be(PersonMembership.IsGrouped("   "));
    }

    [Fact]
    public async Task Odeme_istegi_kirasinin_sunucu_grubu_ile_ayni_uye_kumesi()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var g = PersonBalanceTestKit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g);
        await kit.MemberAsync(g, 3m, purged: true);

        using var scope = _factory.Services.CreateScope();
        var lease = await scope.ServiceProvider.GetRequiredService<PaymentPersonResolver>()
            .ServerGroupAsync(kit.LicenseId, g, default);
        var person = await MembersAsync(kit, a);

        person.Members.Select(m => m.WpfCustomerId.ToString("N")).Should().BeEquivalentTo(lease);
    }
}

/// <summary>
/// Ordinal süzgecin gerçek SQL Server kanıtı (spec §4.13, M5): BIN2 harf farkını ayırır ama SQL '=' sondaki boşluğu
/// yine yutar; kişi sorgusu sonucu bellekte ordinal süzmeli. InMemory zaten ordinal karşılaştırdığı için iddiayı
/// kanıtlamaz.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceMembersRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public PersonBalanceMembersRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task g1_g1bosluk_ve_G1_ayri_kisilerdir()
    {
        var kit = await PersonBalanceTestKit.CreateAsync(_factory);
        var stem = PersonBalanceTestKit.NewGroup();
        var a = await kit.MemberAsync(stem, 10m);
        var d = await kit.MemberAsync(stem, 1m);
        var spaced = await kit.MemberAsync(stem + " ", 20m);
        var upper = await kit.MemberAsync(stem.ToUpperInvariant(), 30m);

        using var scope = _factory.Services.CreateScope();
        var person = scope.ServiceProvider.GetRequiredService<PersonBalance>();
        (await person.MembersAsync(kit.LicenseId, a, default)).Members.Select(m => m.WpfCustomerId)
            .Should().BeEquivalentTo(new[] { a, d });
        (await person.MembersAsync(kit.LicenseId, spaced, default)).Members.Select(m => m.WpfCustomerId)
            .Should().Equal(spaced);
        (await person.MembersAsync(kit.LicenseId, upper, default)).Members.Select(m => m.WpfCustomerId)
            .Should().Equal(upper);
        (await scope.ServiceProvider.GetRequiredService<PaymentPersonResolver>().ServerGroupAsync(kit.LicenseId, stem, default))
            .Should().BeEquivalentTo(a.ToString("N"), d.ToString("N"));
    }
}
