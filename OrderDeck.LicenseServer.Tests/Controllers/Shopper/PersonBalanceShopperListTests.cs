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
/// Shopper GET, satır kipi (spec 2026-10-11 §4.10, K4): bağlantının satırı asıl kayda çözülür, hareketler gruplara
/// toplanır; açıklama yalnız grubun çapası bağlı satırdaysa döner (başka üyeden gelen grupta tutar ve tür görünür,
/// <c>Reason</c> null).
/// </summary>
public sealed class PersonBalanceShopperListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceShopperListTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, DateTimeOffset CreatedAt);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions);
    private sealed record AuthDto(string AccessToken);

    /// <summary>Yayıncının lisansına bir Shopper kaydı; kayıt bağlantıyı ve projeksiyonu açar. Döner: istemci ve
    /// bağlı projeksiyonun Id'si.</summary>
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

    private static async Task<Response> GetAsync(HttpClient client, Guid licenseId) =>
        (await client.GetFromJsonAsync<Response>($"/api/v1/shopper/broadcasters/{licenseId}/balance"))!;

    [Fact]
    public async Task Baska_uyede_capalanan_grubun_aciklamasi_gizlenir_kendi_aciklamasi_gorunur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, a) = await RegisterShopperAsync(kit);
        var g = Kit.NewGroup();
        await kit.Sales.WithDbAsync(async db =>
            (await db.WpfCustomerProjections.SingleAsync(p => p.Id == a)).GroupId = g);
        var b = await kit.MemberAsync(g, 30m);
        var own = await kit.FundAsync(a, 50m, reason: "kendi iadesi");
        var ids = await kit.SeedDrawAsync(Kit.KindManual, [(b, 10m), (a, 20m)], reason: "yayıncının iç notu");

        var body = await GetAsync(client, kit.LicenseId);

        body.Balance.Balance.Should().Be(30m);
        body.Transactions.Should().HaveCount(2);
        body.Transactions.Single(t => t.Id == ids[0]).Should().BeEquivalentTo(
            new { Amount = -20m, Kind = Kit.KindManual, Reason = (string?)null });
        body.Transactions.Single(t => t.Id == own).Reason.Should().Be("kendi iadesi");
    }

    [Fact]
    public async Task Baglanti_kopyaya_isaret_ediyorsa_asil_kaydin_bakiyesi_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (client, a) = await RegisterShopperAsync(kit);
        await kit.FundAsync(a, 45m);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: a);
        await kit.Sales.WithDbAsync(async db =>
            (await db.ShopperBroadcasterLinks.SingleAsync(l => l.LicenseId == kit.LicenseId)).WpfCustomerId = copy);

        (await GetAsync(client, kit.LicenseId)).Balance.Balance.Should().Be(45m);
    }
}
