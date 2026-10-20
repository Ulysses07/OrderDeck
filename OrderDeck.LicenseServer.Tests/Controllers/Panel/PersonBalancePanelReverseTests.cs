using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panel reverse'in S1 tabanı (spec 2026-10-11 §4.9, §7.4, §8.3): S2 biçimindeki satırlar elle kurulur, kip kapalı.
/// Çekim grubuna ait hedef grup olarak geri alınır ya da 409 <c>not-reversible</c>; S2 elle geri alınıp S1'e
/// dönülse bile bir K5 iptal grubu tek satır yoluyla yarım geri alınamaz. <c>disable</c> sonrası panel listesi
/// grubu çapa Id'siyle gösterir: çapa başka satırdayken rota satırındaki bacaktan açılan iptal bulunmalı.
/// </summary>
public sealed class PersonBalancePanelReverseTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePanelReverseTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Capasi_baska_satirda_olan_dusum_grubu_bacagin_satirindan_grup_olarak_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var resp = await kit.PanelReverseAsync(ex.I, ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
    }

    [Fact]
    public async Task Bacak_Id_si_ile_gelen_panel_iptali_de_capaya_cozulur_ikincisi_already_reversed()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        (await kit.PanelReverseAsync(ex.Y, ex.L2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await kit.PanelReverseAsync(ex.T, ex.K);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(again)).Should().Be("already-reversed");
    }

    [Fact]
    public async Task Rota_satirinda_grubun_satiri_yoksa_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var stranger = await kit.MemberAsync(ex.Group, 5m);

        (await kit.PanelReverseAsync(stranger, ex.K)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.ReversalsOfAsync([ex.K])).Should().BeEmpty();
    }

    [Fact]
    public async Task Elle_eksi_grubu_grup_olarak_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var ids = await kit.SeedDrawAsync(Kit.KindManual, [(a, 10m), (b, 20m)], reason: "elle düzeltme");

        (await kit.PanelReverseAsync(b, ids[0])).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue();
    }

    [Fact]
    public async Task K5_iptal_grubu_capadan_da_bacaktan_da_not_reversible()
    {
        // Örnek 4'ün sonucu: A'daki iade R (+80) iptal edilmiş — çapa V (A, −30, reversal, Reverses R), bacak (B, −50).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 30m);
        var b = await kit.MemberAsync(g, 60m);
        var refund = await kit.FundAsync(a, 0m);
        var ids = await kit.SeedDrawAsync(Kit.KindReversal, [(a, 30m), (b, 50m)], reverses: refund);

        var onAnchor = await kit.PanelReverseAsync(a, ids[0]);
        var onLeg = await kit.PanelReverseAsync(b, ids[1]);

        onAnchor.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onAnchor)).Should().Be("not-reversible");
        onLeg.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onLeg)).Should().Be("not-reversible");
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(10m);
    }

    [Fact]
    public async Task Grup_geri_alma_satiri_not_reversible()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);
        var r1 = (await kit.ReversalsOfAsync([ex.L1])).Single();

        var resp = await kit.PanelReverseAsync(ex.I, r1.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("not-reversible", "çok bacaklı 'iptalin iptali' kapalı (M8)");
    }

    [Fact]
    public async Task Eski_reversal_satiri_satir_kipinde_bugunku_gibi_geri_alinabilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var refund = await kit.FundAsync(a, 30m);
        (await kit.PanelReverseAsync(a, refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        var reversal = (await kit.ReversalsOfAsync([refund])).Single();
        reversal.DrawGroupId.Should().BeNull();

        (await kit.PanelReverseAsync(a, reversal.Id)).StatusCode.Should().Be(HttpStatusCode.OK,
            "DrawGroupId NULL reversal satır kipinde bugünkü gibi (§4.9)");
        (await kit.BalanceAsync(a)).Should().Be(80m);
    }
}
