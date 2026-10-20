using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Grup geri alması (spec 2026-10-11 §4.5): çekimin HER bacağı için bir <c>reversal</c> satırı, parası bacağın
/// GÜNCEL satırına döner; eski tek bacaklı düşüm bugünkü biçimde (DrawGroupId NULL) geri alınır (M13).
/// </summary>
public sealed class PersonBalanceGroupReversalTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceGroupReversalTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ornek_2_her_bacak_kendi_satirina_doner_tek_grup_tek_CreatedAt()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var tBefore = (await kit.BalanceRowAsync(ex.T))!.UpdatedAt;

        var resp = await kit.ReverseAsync(ex.K);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2]);
        rows.Should().HaveCount(3);
        var r0 = rows.Single(r => r.ReversesTransactionId == ex.K);
        rows.Should().OnlyContain(r => r.DrawGroupId == r0.Id && r.Kind == Kit.KindReversal && r.CreatedAt == r0.CreatedAt);
        r0.Should().BeEquivalentTo(new { WpfCustomerId = ex.T, Amount = 0m, Reason = $"Reverse of {ex.K:N}" });
        rows.Single(r => r.ReversesTransactionId == ex.L1).Should().BeEquivalentTo(
            new { WpfCustomerId = ex.I, Amount = 100m, Reason = (string?)null });
        rows.Single(r => r.ReversesTransactionId == ex.L2).Should().BeEquivalentTo(
            new { WpfCustomerId = ex.Y, Amount = 20m, Reason = (string?)null });

        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        (await kit.BalanceRowAsync(ex.T))!.UpdatedAt.Should().Be(tBefore, "tutarı 0 olan bacakta bakiye satırına dokunulmaz");
    }

    [Fact]
    public async Task Bacak_Id_si_ile_gelen_reverse_capaya_cozulur_ikincisi_already_reversed()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        (await kit.ReverseAsync(ex.L2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await kit.ReverseAsync(ex.K);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(again)).Should().Be("already-reversed");
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3, "grup bir kez geri alınır");
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
    }

    [Fact]
    public async Task Eski_tek_bacakli_dusum_bugunku_bicimde_DrawGroupId_null_geri_alinir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 80m);
        var d = await kit.SeedLegacyDeductionAsync(a, 30m, 50m, Kit.Session42);

        (await kit.ReverseAsync(d)).StatusCode.Should().Be(HttpStatusCode.OK);

        var reversal = (await kit.ReversalsOfAsync([d])).Should().ContainSingle().Subject;
        reversal.Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = 30m, Kind = Kit.KindReversal, DrawGroupId = (Guid?)null,
            Reason = $"Reverse of {d:N}",
        });
        (await kit.BalanceAsync(a)).Should().Be(80m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue();
    }

    [Fact]
    public async Task Bacagin_guncel_satirinda_bakiye_satiri_yoksa_acilir()
    {
        // Kimlik birleşmesi bacağı (ve iadesini) asıl kayda taşımış, asıl kaydın bakiye satırı yok
        // (birleştirici kopyanın 0'ını eklemez): geri alma parayı GÜNCEL satıra yazar, satırı açar.
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var canonical = await kit.MemberAsync(ex.Group, balanceRow: false, platform: "instagram");
        await kit.Sales.WithDbAsync(async db =>
        {
            var moved = await db.CustomerBalanceTransactions.Where(t => t.WpfCustomerId == ex.I).ToListAsync();
            foreach (var t in moved) t.WpfCustomerId = canonical;
            (await db.WpfCustomerProjections.SingleAsync(p => p.Id == ex.I)).MergedIntoId = canonical;
        });

        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.ReversalsOfAsync([ex.L1])).Single().WpfCustomerId.Should().Be(canonical);
        (await kit.BalanceAsync(canonical)).Should().Be(100m);
        (await kit.LedgerHoldsAsync(canonical)).Should().BeTrue();
        (await kit.BalanceAsync(ex.I)).Should().Be(0m, "kopyaya para yazılmaz");
    }

    [Fact]
    public async Task Iade_ve_elle_ayar_satiri_WPF_reverse_ile_geri_alinamaz_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null);
        var refund = await kit.FundAsync(a, 40m);
        var manual = (await kit.SeedDrawAsync(Kit.KindManual, [(a, 10m)], reason: "elle"))[0];

        (await kit.ReverseAsync(refund)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await kit.ReverseAsync(manual)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Birlesmeden_sonra_ayni_satira_dusen_iki_bacak_tek_bakiye_satirinda_toplanir(bool canonicalHasRow)
    {
        // Spec §4.12: aynı çekimin iki bacağı birleşmeden sonra aynı satıra düşebilir. Geri alma ikinci bacakta
        // satırı izlenenlerde bulmalı: yoksa ikinci bir bakiye satırı açar (SQL'de tekil ihlal, her denemede).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 20m);
        var ids = await kit.SeedDrawAsync(Kit.KindDeduction, [(t, 0m), (i, 100m), (y, 20m)],
            originalAmount: 120m, saleScope: Kit.Session42);
        var canonical = await kit.MemberAsync(g, canonicalHasRow ? 5m : 0m, balanceRow: false);
        await kit.Sales.WithDbAsync(async db =>
        {
            // I ve Y aynı asıl kayda birleşti; ikisinin de satırı 0'da kaldığı için birleştirici asıl kayda satır açmaz.
            var moved = await db.CustomerBalanceTransactions.Where(x => x.WpfCustomerId == i || x.WpfCustomerId == y).ToListAsync();
            foreach (var x in moved) x.WpfCustomerId = canonical;
            foreach (var p in await db.WpfCustomerProjections.Where(p => p.Id == i || p.Id == y).ToListAsync())
                p.MergedIntoId = canonical;
        });

        (await kit.ReverseAsync(ids[0])).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await kit.Sales.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == kit.LicenseId && b.WpfCustomerId == canonical).ToListAsync());
        rows.Should().ContainSingle("iki bacak aynı satıra döner").Which.Balance.Should().Be(canonicalHasRow ? 125m : 120m);
        (await kit.ReversalsOfAsync([ids[1], ids[2]])).Should().OnlyContain(r => r.WpfCustomerId == canonical);
        (await kit.LedgerHoldsAsync(canonical)).Should().BeTrue("D1");
        (await kit.BalanceAsync(i)).Should().Be(0m);
        (await kit.BalanceAsync(y)).Should().Be(0m);
    }

    [Fact]
    public async Task Capasi_olmayan_yetim_bacak_404_ve_hicbir_sey_yazmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var orphan = Guid.NewGuid();
        await kit.Sales.WithDbAsync(async db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = orphan, LicenseId = kit.LicenseId, WpfCustomerId = a, Amount = -20m, Kind = Kit.KindDeduction,
                DrawGroupId = Guid.NewGuid(), CreatedByCustomerId = kit.CustomerId, CreatedAt = DateTimeOffset.UtcNow,
            });
            (await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a)).Balance -= 20m;
        });

        (await kit.ReverseAsync(orphan)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await kit.ReversalsOfAsync([orphan])).Should().BeEmpty();
        (await kit.BalanceAsync(a)).Should().Be(30m);
    }

    [Fact]
    public async Task Capasi_dusum_olmayan_bacak_404_ve_hicbir_sey_yazmaz()
    {
        // D4 bozuk veri: bacak düşüm, çapası elle ayar. WPF ucu düşüm dışı bir grubu geri almaz.
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var b = await kit.MemberAsync(null, 50m);
        var ids = await kit.SeedDrawAsync(Kit.KindManual, [(a, 10m), (b, 20m)], reason: "elle");
        await kit.Sales.WithDbAsync(async db =>
            (await db.CustomerBalanceTransactions.SingleAsync(x => x.Id == ids[1])).Kind = Kit.KindDeduction);

        (await kit.ReverseAsync(ids[1])).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await kit.ReversalsOfAsync(ids)).Should().BeEmpty();
        (await kit.BalanceAsync(a)).Should().Be(40m);
        (await kit.BalanceAsync(b)).Should().Be(30m);
    }

    [Fact]
    public async Task Tek_bacakli_S2_grubunun_geri_almasi_kendi_capasidir()
    {
        // S2 biçimli tek bacaklı çekim (DrawGroupId = Id): geri alma da grup biçiminde, kendi çapası (§3.2).
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 80m);
        var d = (await kit.SeedDrawAsync(Kit.KindDeduction, [(a, 30m)], originalAmount: 50m, saleScope: Kit.Session42))[0];

        (await kit.ReverseAsync(d)).StatusCode.Should().Be(HttpStatusCode.OK);

        var reversal = (await kit.ReversalsOfAsync([d])).Should().ContainSingle().Subject;
        reversal.DrawGroupId.Should().Be(reversal.Id);
        reversal.Should().BeEquivalentTo(new { WpfCustomerId = a, Amount = 30m, Reason = $"Reverse of {d:N}" });
        (await kit.BalanceAsync(a)).Should().Be(80m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
    }
}
