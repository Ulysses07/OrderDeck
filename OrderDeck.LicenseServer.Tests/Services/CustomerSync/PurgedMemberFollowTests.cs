using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// Silinmiş üyenin numara izlemesi, senkron yolu (spec 2026-10-11 §4.11, §4.12, K3): grup boşalınca silinmiş üye
/// ayrılanların numarasına geçer; Ayır'da ve dağılan ayrılışta eski numarada kalır.
/// </summary>
public sealed class PurgedMemberFollowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PurgedMemberFollowTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Iki_partide_birlesen_grupta_silinmis_uye_son_canli_uye_ayrilinca_yeni_numaraya_gecer()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var g0 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        await kit.MemberAsync(g0);
        var pBalanceBefore = (await kit.BalanceRowAsync(p))!.UpdatedAt;

        (await kit.SyncGroupAsync(a1, g0)).EnsureSuccessStatusCode();
        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1, "g1'de canlı üye (a2) var");

        (await kit.SyncGroupAsync(a2, g0)).EnsureSuccessStatusCode();
        var moved = await kit.ProjectionAsync(p);
        moved.GroupId.Should().Be(g0, "g1'i boşaltan kayıttaki ayrılanların numarası");
        moved.GroupIdChangedAt.Should().NotBeNull("taşıma damgalı (CustomerGroupUnion.StampAt)");
        moved.PurgedAt.Should().NotBeNull("silinmişlik değişmez");
        (await kit.BalanceRowAsync(p))!.UpdatedAt.Should().BeAfter(pBalanceBefore, "taşıma da bir GroupId değişimi");
        (await kit.BalanceAsync(p)).Should().Be(40m);
    }

    [Fact]
    public async Task Ayir_da_silinmis_uye_eski_numarada_tek_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);

        (await kit.SyncGroupAsync(a, null)).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    [Fact]
    public async Task Canli_uye_kaldikca_silinmis_uye_yerinde_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);

        (await kit.SyncGroupAsync(a1, Kit.NewGroup())).EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    [Fact]
    public async Task Ayrilanlar_ayni_kayitta_farkli_numaralara_giderse_silinmis_uye_yerinde_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g1 = Kit.NewGroup();
        var a1 = await kit.MemberAsync(g1);
        var a2 = await kit.MemberAsync(g1);
        var p = await kit.MemberAsync(g1, 40m, purged: true);
        var stamp = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(HttpMethod.Post, $"/api/v1/licenses/{kit.LicenseId}/wpf-customers/sync", new
        {
            customers = new[]
            {
                await SyncItemAsync(kit, a1, Kit.NewGroup(), stamp),
                await SyncItemAsync(kit, a2, Kit.NewGroup(), stamp),
            },
        });
        resp.EnsureSuccessStatusCode();

        (await kit.ProjectionAsync(p)).GroupId.Should().Be(g1);
    }

    private static async Task<object> SyncItemAsync(Kit kit, Guid id, string groupId, DateTimeOffset at)
    {
        var row = await kit.ProjectionAsync(id);
        return new
        {
            id, platform = row.Platform, username = row.Username, updatedAt = at, format = 2,
            groupId, groupIdChangedAt = at,
        };
    }
}
