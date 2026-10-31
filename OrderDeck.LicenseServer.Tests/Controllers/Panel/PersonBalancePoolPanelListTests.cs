using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Controllers.Panel;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panel GET, havuz kipi (spec 2026-10-11 §4.6, §6.1-f, M9, K3): <c>Balance = Σ_S</c> (eksi olabilir), hareketler
/// <c>S</c>'nin bütün üyelerinden gruplara toplanmış (grup tutarı bu kişideki bacakların toplamı, liste toplamı =
/// <c>Balance</c>), <c>take</c> grup sayar, <c>UpdatedAt</c> <c>S</c>'nin son para hareketi, <c>Members[]</c> <c>S</c>.
/// Kişinin hangi üyesinden açılırsa açılsın aynı toplam. Bayrak boşken satır kipi (S1) aynen.
/// </summary>
public sealed class PersonBalancePoolPanelListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolPanelListTests(ApiFactory factory) => _factory = factory;

    private static async Task<PanelCustomerBalanceController.BalanceDetailsResponse> GetAsync(
        Kit kit, Guid row, int take = 50)
    {
        var response = await kit.PanelGetAsync(row, take);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PanelCustomerBalanceController.BalanceDetailsResponse>())!;
    }

    [Fact]
    public async Task Havuz_kipinde_bakiye_S_toplami_eksi_olabilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var onA = await GetAsync(kit, a);
        var onB = await GetAsync(kit, b);

        onA.Balance.Balance.Should().Be(-50m, "borç operatöre görünmeli (D3)");
        onB.Balance.Balance.Should().Be(-50m);
        onA.Transactions.Sum(t => t.Amount).Should().Be(onA.Balance.Balance);
    }

    [Fact]
    public async Task Havuz_kipinde_hareketler_S_nin_butun_uyelerinden_toplanmis()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var onT = await GetAsync(kit, ex.T);

        onT.Balance.Balance.Should().Be(10m);
        onT.Transactions.Should().HaveCount(3);
        // K bu kişideki bütün bacaklarıyla tek satır: 0 (T) − 100 (I) − 20 (Y); en yeni grup.
        onT.Transactions[0].Should().BeEquivalentTo(new
        {
            Id = ex.K, Amount = -120m, Kind = Kit.KindDeduction, OriginalAmount = 120m,
        });
        // I'nin ve Y'nin iadeleri (Örnek 1'in başlangıç bakiyeleri) rota T olsa da listede.
        onT.Transactions.Where(t => t.Kind == "refund-full").Select(t => t.Amount)
            .Should().BeEquivalentTo(new[] { 100m, 30m });
        onT.Transactions.Sum(t => t.Amount).Should().Be(onT.Balance.Balance);
    }

    [Fact]
    public async Task Kisinin_her_uyesinden_ayni_toplam()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var onT = await GetAsync(kit, ex.T);
        var onI = await GetAsync(kit, ex.I);
        var onY = await GetAsync(kit, ex.Y);

        foreach (var (route, body) in new[] { (ex.T, onT), (ex.I, onI), (ex.Y, onY) })
        {
            body.Balance.Balance.Should().Be(10m);
            body.Balance.WpfCustomerId.Should().Be(route, "yanıttaki Id rotadaki Id");
            body.Transactions.Select(t => t.Id).Should().Equal(onT.Transactions.Select(t => t.Id));
            body.Transactions.Select(t => t.Amount).Should().Equal(onT.Transactions.Select(t => t.Amount));
        }
    }

    [Fact]
    public async Task take_grup_sayar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        var t0 = DateTimeOffset.UtcNow;
        await kit.FundAsync(a, 10m, at: t0.AddMinutes(-3));
        var second = await kit.FundAsync(b, 20m, at: t0.AddMinutes(-2));
        var third = await kit.FundAsync(a, 30m, at: t0.AddMinutes(-1));
        await kit.EnablePoolAsync();

        var list = await GetAsync(kit, a, take: 2);

        // İki üyeden en yeni iki grup; satır kipinde A'nın kendi iki hareketi dönerdi.
        list.Transactions.Select(t => t.Id).Should().Equal(third, second);
        list.Balance.Balance.Should().Be(60m, "take yalnız listeyi keser, toplamı değil");
    }

    [Fact]
    public async Task take_cok_bacakli_grubu_tek_sayar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var list = await GetAsync(kit, ex.T, take: 1);

        // En yeni grup K'nın üç üyeye yayılmış üç bacağı tek satır; satır sayan bir kesim tek bacak (0 ya da −100) dönerdi.
        list.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = -120m });
    }

    [Fact]
    public async Task UpdatedAt_S_nin_son_para_hareketi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        var an = DateTimeOffset.UtcNow.AddMinutes(-1);
        an = an.AddTicks(-(an.UtcTicks % TimeSpan.TicksPerSecond));
        // Y'ye en yeni hareket; bakiye satırının jetonu kayıtta "şimdi"ye damgalanır — M9 onu değil hareketi gösterir.
        await kit.FundAsync(ex.Y, 5m, at: an);
        await kit.EnablePoolAsync();

        var onT = await GetAsync(kit, ex.T);

        onT.Balance.UpdatedAt.Should().Be(an);
        onT.Balance.Balance.Should().Be(15m);
    }

    [Fact]
    public async Task UpdatedAt_hareket_yoksa_S_nin_en_yeni_bakiye_satiri()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        await kit.EnablePoolAsync();
        // İki üye de 0'lık satırla, hareketsiz; B sonra açıldığı için satırı daha yeni (spec §4.1 `:145`).
        var expected = new[] { (await kit.BalanceRowAsync(a))!.UpdatedAt, (await kit.BalanceRowAsync(b))!.UpdatedAt }.Max();

        var onA = await GetAsync(kit, a);

        onA.Balance.UpdatedAt.Should().Be(expected, "yalnız rota satırına bakan yedek A'nınkini dönerdi");
        onA.Transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Members_havuz_kipinde_de_S()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 20m);
        var p = await kit.MemberAsync(g, 5m, purged: true);
        await kit.EnablePoolAsync();

        var list = await GetAsync(kit, a);

        var expected = new[] { a, b, p }.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToArray();
        list.Members.Select(m => m.WpfCustomerId).Should().Equal(expected);
        list.Members.Single(m => m.WpfCustomerId == p).Should().BeEquivalentTo(new { Balance = 5m, Purged = true });
        list.Members.Single(m => m.WpfCustomerId == b).Should().BeEquivalentTo(new { Balance = 20m, Purged = false });
        list.Balance.Balance.Should().Be(list.Members.Sum(m => m.Balance));
    }

    [Fact]
    public async Task Silinmis_uyenin_bakiyesi_toplama_girer()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m, purged: true);
        await kit.EnablePoolAsync();

        var onA = await GetAsync(kit, a);

        onA.Balance.Balance.Should().Be(40m, "silinmiş üyenin parası kişinin (K3)");
        onA.Transactions.Should().ContainSingle().Which.Amount.Should().Be(40m);
    }

    [Fact]
    public async Task Satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var onT = await GetAsync(kit, ex.T);

        onT.Balance.Balance.Should().Be(0m, "bayrak boş: bakiye rota satırınınki");
        onT.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = 0m });
        onT.Members.Should().HaveCount(3, "Members iki kipte de S");
    }
}
