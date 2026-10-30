using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panel reverse, havuz kipi (ortak bakiye spec 2026-10-11 §4.9, K5, M8, M13; plan farkları 10, 18d). Hedef
/// S(rota)'nın herhangi bir üyesinde olabilir: çekim grubu S(rota) üyeliğiyle bulunur; eski eksi hedef grup geri
/// almasıyla kendi satırına döner; artı hareketin iptali çekim türü iptaldir (çapa hedefin satırında, kalan öbür
/// üyelerden, hep-ya-hiç — Örnek 4); eski <c>reversal</c> satırı 409 <c>not-reversible</c>. Bayrak boşken S1'in
/// koşulları aynen. InMemory: yarış iddiası yok (N01 hakemi Görev 16'da Testcontainers'ta).
/// </summary>
public sealed class PersonBalancePoolReverseTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolReverseTests(ApiFactory factory) => _factory = factory;

    private sealed record Example4(Guid A, Guid B, Guid R);

    /// <summary>
    /// Spec Örnek 4'ün kurulumu: kişi {A, B}; A'ya iade R (+80), sonra eski bir düşüm A'dan 50 → A = 30;
    /// B'nin kendi iadesi <paramref name="b"/>.
    /// </summary>
    private static async Task<Example4> Example4Async(Kit kit, decimal b)
    {
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var refund = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var bRow = await kit.MemberAsync(g, b);
        return new Example4(a, bRow, refund);
    }

    private static Task<int> ReversalRowCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindReversal));

    [Fact]
    public async Task Ornek4_baska_uyenin_iadesini_iptal()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(ex.B, ex.R);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var anchor = (await kit.ReversalsOfAsync([ex.R])).Should().ContainSingle().Which;
        var rows = await kit.GroupRowsAsync(anchor.Id);
        rows.Should().HaveCount(2);
        rows[0].Id.Should().Be(anchor.Id);
        anchor.WpfCustomerId.Should().Be(ex.A, "çapa iptal edilen hareketin kendi satırında, rota satırında değil (§4.9)");
        anchor.Amount.Should().Be(-30m);
        anchor.Kind.Should().Be(Kit.KindReversal);
        anchor.DrawGroupId.Should().Be(anchor.Id);
        anchor.Reason.Should().Be($"Reverse of {ex.R:N}");
        var leg = rows[1];
        leg.WpfCustomerId.Should().Be(ex.B);
        leg.Amount.Should().Be(-50m);
        leg.Kind.Should().Be(Kit.KindReversal);
        leg.ReversesTransactionId.Should().BeNull("çekim türü iptalde hedefi yalnız çapa taşır (§3.2)");
        leg.DrawGroupId.Should().Be(anchor.Id);
        leg.Reason.Should().BeNull("açıklama çapada durur");
        leg.CreatedAt.Should().Be(anchor.CreatedAt, "grup tek SaveChanges'te, ortak CreatedAt ile");
        (await kit.BalanceAsync(ex.A)).Should().Be(0m);
        (await kit.BalanceAsync(ex.B)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(ex.A)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(ex.B)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Ornek4_B20_havuz_yetmez_409_hicbir_sey_yazilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 20m);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(ex.B, ex.R);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("insufficient-balance");
        (await ReversalRowCountAsync(kit)).Should().Be(0, "hep-ya-hiç: havuz 50 < 80, kısmi iptal yok");
        (await kit.BalanceAsync(ex.A)).Should().Be(30m);
        (await kit.BalanceAsync(ex.B)).Should().Be(20m);
    }

    [Fact]
    public async Task Ayni_iade_ikinci_kez_iptal_409_already_reversed()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);
        await kit.EnablePoolAsync();
        (await kit.PanelReverseAsync(ex.B, ex.R)).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await kit.PanelReverseAsync(ex.A, ex.R);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(again)).Should().Be("already-reversed");
        (await kit.ReversalsOfAsync([ex.R])).Should().ContainSingle();
        (await kit.BalanceAsync(ex.A)).Should().Be(0m);
        (await kit.BalanceAsync(ex.B)).Should().Be(10m);
    }

    [Fact]
    public async Task Ornek4_satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);

        var fromOther = await kit.PanelReverseAsync(ex.B, ex.R);
        var fromOwn = await kit.PanelReverseAsync(ex.A, ex.R);

        fromOther.StatusCode.Should().Be(HttpStatusCode.NotFound, "satır kipinde hedef rota satırının olmalı");
        fromOwn.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(fromOwn)).Should().Be("insufficient-balance", "satır kipinde bugünkü gibi: A = 30 < 80");
        (await ReversalRowCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(ex.A)).Should().Be(30m);
        (await kit.BalanceAsync(ex.B)).Should().Be(60m);
    }

    [Fact]
    public async Task Havuz_kipinde_baska_uyedeki_cekim_grubu_bulunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var z = await kit.MemberAsync(ex.Group);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(z, ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "grubun satırları S(rota)'da (§4.9)");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        (await kit.BalanceAsync(z)).Should().Be(0m);
    }

    [Fact]
    public async Task Satir_kipinde_rota_disindaki_grup_404_bacagin_satirindan_bulunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var z = await kit.MemberAsync(ex.Group);
        await kit.EnablePoolAsync();
        await kit.DisablePoolAsync();

        var fromStranger = await kit.PanelReverseAsync(z, ex.K);

        fromStranger.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "bayrak boş → S1 koşulu: grubun bir satırı rota satırında olmalı");
        (await kit.ReversalsOfAsync([ex.K])).Should().BeEmpty();
        (await kit.PanelReverseAsync(ex.I, ex.K)).StatusCode.Should().Be(HttpStatusCode.OK,
            "disable sonrası düzeltme yolu: bacağın satırından");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3);
    }

    [Fact]
    public async Task Eski_eksi_hedef_baska_uyede_kendi_satirina_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 50m);
        var target = await kit.SeedLegacyDeductionAsync(b, 20m, 20m);
        (await kit.PanelReverseAsync(a, target)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "satır kipinde koşul aynen");
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(a, target);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reversal = (await kit.ReversalsOfAsync([target])).Should().ContainSingle().Which;
        reversal.WpfCustomerId.Should().Be(b, "para hedefin kendi satırına döner (K1), rota satırına değil");
        reversal.Amount.Should().Be(20m);
        reversal.Kind.Should().Be(Kit.KindReversal);
        reversal.DrawGroupId.Should().BeNull("eski tek bacaklı çekimin geri alması bugünkü biçimde (M13)");
        reversal.Reason.Should().Be($"Reverse of {target:N}");
        (await kit.BalanceAsync(b)).Should().Be(50m);
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Eski_eksi_hedef_S_disinda_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(Kit.NewGroup());
        var stranger = await kit.MemberAsync(Kit.NewGroup(), 50m);
        var target = await kit.SeedLegacyDeductionAsync(stranger, 20m, 20m);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(a, target);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "hedef başka kişinin satırında");
        (await kit.ReversalsOfAsync([target])).Should().BeEmpty();
        (await kit.BalanceAsync(stranger)).Should().Be(30m);
    }

    [Fact]
    public async Task Eski_reversal_havuz_kipinde_409_not_reversible()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var refund = await kit.FundAsync(a, 30m);
        (await kit.PanelReverseAsync(a, refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        var reversal = (await kit.ReversalsOfAsync([refund])).Should().ContainSingle().Which;
        reversal.DrawGroupId.Should().BeNull("satır kipinde yazılan eski biçim");
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(a, reversal.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("not-reversible", "eski reversal havuz kipinde iptal edilemez (M8)");
        (await kit.ReversalsOfAsync([reversal.Id])).Should().BeEmpty();
        (await kit.BalanceAsync(a)).Should().Be(50m);
    }

    [Fact]
    public async Task Havuz_kipinde_K5_cekim_grubunun_geri_almasi_grup_satirlariyla()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();
        var reason = $"r-{Guid.NewGuid():N}";
        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);
        var anchorId = await kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindManual && t.Reason == reason)
            .Select(t => t.Id)
            .SingleAsync());
        var drawn = await kit.GroupRowsAsync(anchorId);
        drawn.Should().HaveCount(2);

        var resp = await kit.PanelReverseAsync(a, anchorId);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reversals = await kit.ReversalsOfAsync(drawn.Select(t => t.Id));
        reversals.Should().HaveCount(2, "her bacak için bir geri alma satırı (§4.5)");
        var reversalAnchor = reversals.Single(r => r.ReversesTransactionId == anchorId);
        reversals.Should().OnlyContain(r => r.DrawGroupId == reversalAnchor.Id && r.Kind == Kit.KindReversal);
        reversals.Select(r => r.CreatedAt).Distinct().Should().ContainSingle();
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Havuz_kipinde_cekim_turu_iptal_grubu_409_not_reversible()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await Example4Async(kit, 60m);
        await kit.EnablePoolAsync();
        (await kit.PanelReverseAsync(ex.B, ex.R)).StatusCode.Should().Be(HttpStatusCode.OK);
        var v = (await kit.ReversalsOfAsync([ex.R])).Should().ContainSingle().Which;
        var leg = (await kit.GroupRowsAsync(v.Id))[1];

        var onAnchor = await kit.PanelReverseAsync(ex.A, v.Id);
        var onLeg = await kit.PanelReverseAsync(ex.A, leg.Id);

        onAnchor.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onAnchor)).Should().Be("not-reversible", "çok bacaklı 'iptalin iptali' kapalı (M8)");
        onLeg.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(onLeg)).Should().Be("not-reversible");
        (await kit.ReversalsOfAsync([v.Id, leg.Id])).Should().BeEmpty();
        (await kit.BalanceAsync(ex.A)).Should().Be(0m);
        (await kit.BalanceAsync(ex.B)).Should().Be(10m);
    }

    [Fact]
    public async Task Tek_bacakli_havuz_cekiminin_geri_almasi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, 20m);
        var ids = await kit.SeedDrawAsync(Kit.KindDeduction, [(a, 30m)], originalAmount: 30m, saleScope: Kit.Session42);
        await kit.EnablePoolAsync();

        var resp = await kit.PanelReverseAsync(b, ids[0]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "çapa S(rota)'da");
        var reversal = (await kit.ReversalsOfAsync([ids[0]])).Should().ContainSingle().Which;
        reversal.WpfCustomerId.Should().Be(a);
        reversal.Amount.Should().Be(30m);
        reversal.DrawGroupId.Should().Be(reversal.Id,
            "havuz kipinde yazılmış tek bacaklı çekimin geri alması grup biçiminde; NULL yalnız eski satırda (M13)");
        (await kit.BalanceAsync(a)).Should().Be(50m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
    }
}
