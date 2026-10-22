using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>Panel GET, satır kipi (spec 2026-10-11 §4.6): gruplara toplanmış liste, grup sayan take,
/// <c>Members[]</c> (S üzerinden), <c>UpdatedAt</c> = son para hareketi (M9).</summary>
public sealed class PersonBalancePanelListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePanelListTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(Guid WpfCustomerId, Guid LicenseId, decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, Guid? ReversesTransactionId, DateTimeOffset CreatedAt);
    private sealed record MemberDto(Guid WpfCustomerId, decimal Balance, bool Purged);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions, MemberDto[] Members);

    private static async Task<Response> GetAsync(Kit kit, Guid row, int take = 50) =>
        (await (await kit.PanelGetAsync(row, take)).Content.ReadFromJsonAsync<Response>())!;

    [Fact]
    public async Task Ornek_1_bacak_satirinda_grup_capa_Id_siyle_ve_bu_satirdaki_tutarla_gorunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var onI = await GetAsync(kit, ex.I);

        onI.Balance.Balance.Should().Be(0m);
        onI.Transactions.Should().HaveCount(2);
        onI.Transactions[0].Should().BeEquivalentTo(new
        {
            Id = ex.K, Amount = -100m, Kind = Kit.KindDeduction, OriginalAmount = 120m,
        });
        onI.Transactions.Sum(t => t.Amount).Should().Be(onI.Balance.Balance);

        var onT = await GetAsync(kit, ex.T);
        onT.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = 0m });
    }

    [Fact]
    public async Task Take_satir_degil_grup_sayar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        // Birleşmeden sonra aynı çekimin iki bacağı aynı satırda (§4.12).
        var ids = await kit.SeedDrawAsync(Kit.KindDeduction, [(a, 10m), (a, 5m)], originalAmount: 40m);

        var list = await GetAsync(kit, a, take: 1);

        list.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ids[0], Amount = -15m });
    }

    [Fact]
    public async Task Ornek_3_Members_kisinin_butun_uyelerini_silinmis_dahil_bakiyeleriyle_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var p = await kit.MemberAsync(ex.Group, 40m, purged: true);

        var list = await GetAsync(kit, ex.I);

        list.Members.Should().BeEquivalentTo(new[]
        {
            new MemberDto(ex.T, 0m, false), new MemberDto(ex.I, 0m, false),
            new MemberDto(ex.Y, 10m, false), new MemberDto(p, 40m, true),
        });
        list.Balance.Balance.Should().Be(0m, "satır kipi: bakiye rota satırınınki");
    }

    [Fact]
    public async Task UpdatedAt_kilit_dokunusunu_degil_son_para_hareketini_gosterir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 25m);
        // Kilit dokunuşu (kanca ya da birleştirici): yalnız "değişti" işareti, jetonu StampVersions ilerletir.
        await kit.Sales.WithDbAsync(async db =>
        {
            var row = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a);
            db.Entry(row).Property(b => b.UpdatedAt).IsModified = true;
        });
        (await kit.BalanceRowAsync(a))!.UpdatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        var lastMovement = await kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.WpfCustomerId == a).MaxAsync(t => t.CreatedAt));

        (await GetAsync(kit, a)).Balance.UpdatedAt.Should().Be(lastMovement);
    }
}
