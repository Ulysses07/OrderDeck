using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>KVKK K-3: yayıncı veri sorumlusu bilgilerini panelden girer; yalnız hesap sahibi değiştirir.</summary>
public sealed class PanelDataControllerControllerTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/panel/data-controller";
    private readonly ApiFactory _factory;
    public PanelDataControllerControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record ProfileDto(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress);
    private sealed record Resp(ProfileDto? Profile);

    private async Task<(HttpClient Client, Guid CustomerId)> OwnerAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License { Id = Guid.NewGuid(), LicenseKey = $"LDK-DC-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
        return (client, customerId);
    }

    private static object Body(string email = "kvkk@example.test") => new
    {
        legalName = "Örnek Ticaret Ltd. Şti.", postalAddress = "Örnek Mah. 1. Sok. No:2 İstanbul", kvkkEmail = email,
        kepAddress = "ornek@hs01.kep.tr",
    };

    [Fact]
    public async Task Sahip_kaydeder_ve_okur()
    {
        var (client, _) = await OwnerAsync();
        (await (await client.GetAsync(Path)).Content.ReadFromJsonAsync<Resp>())!.Profile.Should().BeNull();

        var put = await client.PutAsJsonAsync(Path, Body());

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var read = (await (await client.GetAsync(Path)).Content.ReadFromJsonAsync<Resp>())!.Profile!;
        read.Should().Be(new ProfileDto("Örnek Ticaret Ltd. Şti.", "Örnek Mah. 1. Sok. No:2 İstanbul", "kvkk@example.test",
            "ornek@hs01.kep.tr"));
    }

    [Fact]
    public async Task Gecersiz_alan_400_doner()
    {
        var (client, _) = await OwnerAsync();
        var put = await client.PutAsJsonAsync(Path, Body(email: "gecersiz"));
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync()).Should().Contain("invalid-field");
    }

    [Fact]
    public async Task Personel_degistiremez()
    {
        var (owner, _) = await OwnerAsync();
        var staff = await PanelOperatorHelper.StaffClientAsync(_factory, owner);
        var put = await staff.PutAsJsonAsync(Path, Body());
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await put.Content.ReadAsStringAsync()).Should().Contain("owner-only");
    }
}
