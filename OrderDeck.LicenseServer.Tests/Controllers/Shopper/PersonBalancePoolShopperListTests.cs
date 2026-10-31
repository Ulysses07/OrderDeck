using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// Shopper GET, havuz kipi (spec 2026-10-11 §4.10, K3, K4): bağlantının kişisi (<c>S</c>) toplamı, hareketler
/// <c>S</c>'nin bütün üyelerinden toplanmış (silinmiş üye dahil); açıklama yalnız grubun çapası bağlı satırdaysa döner
/// (S1 maskesi aynen). Bayrak boşken satır kipi (S1) aynen.
/// </summary>
public sealed class PersonBalancePoolShopperListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolShopperListTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, DateTimeOffset CreatedAt);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions);
    private sealed record AuthDto(string AccessToken);

    /// <summary>Yayıncının lisansına bir Shopper kaydı; kayıt bağlantıyı ve projeksiyonu açar. Döner: istemci ve
    /// bağlı projeksiyonun Id'si. Kaynak: <c>PersonBalanceShopperListTests.RegisterShopperAsync</c> (S1), aynen.</summary>
    private async Task<(HttpClient Client, Guid Linked)> RegisterShopperAsync(Kit kit)
    {
        var code = "pb" + Guid.NewGuid().ToString("N")[..8];
        await kit.Sales.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId);
            license.ShopperCode = code;
            license.ShopperCodeUpdatedAt = DateTimeOffset.UtcNow;
        });
        var client = _factory.CreateClient();
        var giris = $"g-{Guid.NewGuid():N}";
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", new
        {
            broadcasterCode = code, fullName = "Bakiye Alıcı", phone = TestPhone.NewE164(), password = giris,
            address = "Adres", platform = "youtube", username = $"s{Guid.NewGuid():N}"[..12],
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = await resp.Content.ReadFromJsonAsync<AuthDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        var linked = await kit.Sales.ReadDbAsync(db => db.ShopperBroadcasterLinks.AsNoTracking()
            .Where(l => l.LicenseId == kit.LicenseId && l.WpfCustomerId != null)
            .Select(l => l.WpfCustomerId!.Value).SingleAsync());
        return (client, linked);
    }

    /// <summary>Kaynak: <c>PersonBalanceShopperListTests.GetAsync</c> (S1), aynen.</summary>
    private static async Task<Response> GetAsync(HttpClient client, Guid licenseId) =>
        (await client.GetFromJsonAsync<Response>($"/api/v1/shopper/broadcasters/{licenseId}/balance"))!;

    /// <summary>Bağlantıyı kişinin bir üyesine taşır (S1 testindeki kopya bağlantısıyla aynı yol).</summary>
    private static Task LinkToAsync(Kit kit, Guid row) =>
        kit.Sales.WithDbAsync(async db =>
            (await db.ShopperBroadcasterLinks.SingleAsync(l => l.LicenseId == kit.LicenseId)).WpfCustomerId = row);

    [Fact]
    public async Task Havuz_kipinde_S_toplami_ve_hareketleri()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var ex = await kit.Example1Async();
        await LinkToAsync(kit, ex.T);
        await kit.EnablePoolAsync();

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(10m);
        body.Transactions.Should().HaveCount(3);
        body.Transactions.Single(t => t.Id == ex.K).Should().BeEquivalentTo(new
        {
            Amount = -120m, Kind = Kit.KindDeduction, OriginalAmount = 120m,
        });
        body.Transactions.Sum(t => t.Amount).Should().Be(body.Balance.Balance);
    }

    [Fact]
    public async Task Reason_maskesi_baska_uyenin_aciklamasi_null()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 30m);
        await LinkToAsync(kit, t);
        var kendiNotu = $"n-{Guid.NewGuid():N}";
        var digerNotu = $"n-{Guid.NewGuid():N}";
        var draw = await kit.SeedDrawAsync(Kit.KindManual, [(t, 0m), (i, 5m)], reason: kendiNotu);
        var fund = await kit.FundAsync(i, 50m, reason: digerNotu);
        await kit.EnablePoolAsync();

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(75m);
        // Çapası bağlı satırda (T) olan grup: açıklama görünür; tutar bu kişideki bacakların toplamı.
        body.Transactions.Single(x => x.Id == draw[0]).Should().BeEquivalentTo(new
        {
            Amount = -5m, Kind = Kit.KindManual, Reason = kendiNotu,
        });
        // Başka üyenin (I) iadesi: tutar ve tür görünür, serbest metin gizli (K4).
        body.Transactions.Single(x => x.Id == fund).Should().BeEquivalentTo(new
        {
            Amount = 50m, Kind = "refund-full", Reason = (string?)null,
        });
        body.Transactions.Should().NotContain(x => x.Reason == digerNotu);
    }

    [Fact]
    public async Task Silinmis_uye_dahil()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, purged: true);
        await LinkToAsync(kit, t);
        var fund = await kit.FundAsync(p, 40m, reason: $"n-{Guid.NewGuid():N}");
        await kit.EnablePoolAsync();

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(40m, "silinmiş üyenin parası kişinin (K3)");
        body.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = fund, Amount = 40m, Reason = (string?)null,
        });
    }

    [Fact]
    public async Task Satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, _) = await RegisterShopperAsync(kit);
        var ex = await kit.Example1Async();
        await LinkToAsync(kit, ex.T);

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(0m, "bayrak boş: bakiye bağlı satırınki");
        body.Transactions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ex.K, Amount = 0m });
    }
}
