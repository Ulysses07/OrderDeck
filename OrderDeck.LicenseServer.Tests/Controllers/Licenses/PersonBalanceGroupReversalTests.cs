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
}
