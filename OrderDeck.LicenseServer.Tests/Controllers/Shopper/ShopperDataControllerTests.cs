using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>KVKK K-3: bağlı müşteri, ödeme yaptığı yayıncının veri sorumlusu bilgisini görür (dekont ekranı); bağlı olmadığı
/// yayıncınınkini değil. Biçim Shopper'ın sözleşmesi (<c>{ profile }</c>; OrderDeck-Shopper
/// <c>apps/shopper/src/privacy/dataController.ts</c>).</summary>
public sealed class ShopperDataControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperDataControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record ControllerDto(string LegalName, string PostalAddress, string KvkkEmail, string? KepAddress);
    private sealed record Resp(ControllerDto? Profile);
    private sealed record AuthResponse(string AccessToken);

    /// <summary>Kayıt isteğinin kimlik alanı için üretilmiş değer (public repo: literal yok).</summary>
    private static string Generated(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<(Guid LicenseId, string Code)> SeedLicenseAsync(bool withProfile)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"sdc-{Guid.NewGuid():N}@x.test", Name = "Sdc",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow };
        db.Customers.Add(customer);
        var code = "sdc-" + Guid.NewGuid().ToString("N")[..8];
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, CustomerId = customer.Id, SkuCode = "STD", IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1), LicenseKey = "key-" + Guid.NewGuid().ToString("N"), ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow });
        if (withProfile)
            db.DataControllerProfiles.Add(new DataControllerProfile { CustomerId = customer.Id, LegalName = "Örnek Ticaret",
                PostalAddress = "Adres", KvkkEmail = "kvkk@example.test", UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel" });
        await db.SaveChangesAsync();
        return (licenseId, code);
    }

    /// <summary>Koddaki yayıncıya bağlı yeni bir müşteri kaydeder; istemci onun erişim jetonunu taşır.</summary>
    private async Task<HttpClient> RegisteredClientAsync(string code)
    {
        var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", new
        {
            broadcasterCode = code, fullName = "Dc Kullanici", phone = TestPhone.NewE164(), password = Generated("dc"),
            address = "Ankara", platform = "youtube", username = "dckullanici",
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await register.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Bagli_musteri_gorur_bagli_olmadigi_yayincida_403()
    {
        var (linked, code) = await SeedLicenseAsync(withProfile: true);
        var (unlinked, _) = await SeedLicenseAsync(withProfile: true);
        var client = await RegisteredClientAsync(code);

        var mine = await client.GetFromJsonAsync<Resp>($"/api/v1/shopper/broadcasters/{linked}/data-controller");
        var other = await client.GetAsync($"/api/v1/shopper/broadcasters/{unlinked}/data-controller");

        mine!.Profile.Should().Be(new ControllerDto("Örnek Ticaret", "Adres", "kvkk@example.test", null));
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.Content.ReadAsStringAsync()).Should().Contain("not-linked", "Shopper 403'ü 'bilgi yok' okur");
    }

    [Fact]
    public async Task Bagli_yayici_bilgi_girmediyse_profile_null()
    {
        var (linked, code) = await SeedLicenseAsync(withProfile: false);
        var client = await RegisteredClientAsync(code);

        var raw = await client.GetStringAsync($"/api/v1/shopper/broadcasters/{linked}/data-controller");

        raw.Should().Contain("\"profile\":null", "alan her zaman var; Shopper null'ı 'bilgi girilmedi' okur");
    }

    [Fact]
    public async Task Oturumsuz_istek_401()
    {
        var (linked, _) = await SeedLicenseAsync(withProfile: true);

        var resp = await _factory.CreateClient().GetAsync($"/api/v1/shopper/broadcasters/{linked}/data-controller");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
