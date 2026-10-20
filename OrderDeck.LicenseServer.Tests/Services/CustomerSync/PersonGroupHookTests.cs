using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <c>GroupId</c> kancası (spec 2026-10-11 §4.11, M6): numara değişimi para tutan satırın bakiye jetonunu ilerletir —
/// apply üyeliği okuduktan sonra kaydedilen bir ayırma, apply'ın UPDATE'ini tutmaz. Tutar değişmez.
/// </summary>
public sealed class PersonGroupHookTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonGroupHookTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Senkronun_GroupId_degisimi_bakiye_jetonunu_ilerletir_tutara_dokunmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;
        var g2 = Kit.NewGroup();

        (await kit.SyncGroupAsync(a, g2)).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(a)).GroupId.Should().Be(g2);
        var after = (await kit.BalanceRowAsync(a))!;
        after.UpdatedAt.Should().BeAfter(before);
        after.Balance.Should().Be(40m);
    }

    [Fact]
    public async Task Ayni_numarayla_gelen_senkron_jetona_dokunmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;

        (await kit.SyncGroupAsync(a, g)).EnsureSuccessStatusCode();

        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().Be(before);
    }

    [Fact]
    public async Task Yalniz_harf_farki_da_degisimdir_ordinal()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;

        (await kit.SyncGroupAsync(a, g.ToUpperInvariant())).EnsureSuccessStatusCode();

        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public async Task Bakiye_satiri_olmayan_uyede_satir_acilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), balanceRow: false);

        (await kit.SyncGroupAsync(a, Kit.NewGroup())).EnsureSuccessStatusCode();

        (await kit.BalanceRowAsync(a)).Should().BeNull();
    }

    [Fact]
    public async Task Senkron_SaveChanges_yolu_da_jetonu_ilerletir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup(), 40m);
        var before = (await kit.BalanceRowAsync(a))!.UpdatedAt;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.WpfCustomerProjections.SingleAsync(p => p.Id == a)).GroupId = null;
            db.SaveChanges();
        }

        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().BeAfter(before);
    }
}
