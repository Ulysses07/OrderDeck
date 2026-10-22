using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>KVKK K-3: yayıncı veri sorumlusu bilgilerini panelden girer; yalnız hesap sahibi değiştirir, her panel kullanıcısı
/// okur. Silinmiş yayıncının hâlâ geçerli erişim jetonu yazamaz.</summary>
public sealed class PanelDataControllerControllerTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/panel/data-controller";
    /// <summary>KEP örneği: gerçek bir KEP sağlayıcısının alan adı değil (public repo, gerçek veri yok).</summary>
    private const string Kep = "ornek@kep.example.test";
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
        kepAddress = Kep,
    };

    [Fact]
    public async Task Sahip_kaydeder_ve_okur()
    {
        var (client, _) = await OwnerAsync();
        (await (await client.GetAsync(Path)).Content.ReadFromJsonAsync<Resp>())!.Profile.Should().BeNull();

        var put = await client.PutAsJsonAsync(Path, Body());

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var read = (await (await client.GetAsync(Path)).Content.ReadFromJsonAsync<Resp>())!.Profile!;
        read.Should().Be(new ProfileDto("Örnek Ticaret Ltd. Şti.", "Örnek Mah. 1. Sok. No:2 İstanbul", "kvkk@example.test", Kep));
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
    public async Task Personel_okur_ama_degistiremez()
    {
        var (owner, _) = await OwnerAsync();
        (await owner.PutAsJsonAsync(Path, Body())).StatusCode.Should().Be(HttpStatusCode.OK);
        var staff = await PanelOperatorHelper.StaffClientAsync(_factory, owner);

        var get = await staff.GetAsync(Path);
        get.StatusCode.Should().Be(HttpStatusCode.OK, "okuma her panel kullanıcısına açık");
        (await get.Content.ReadFromJsonAsync<Resp>())!.Profile!.LegalName.Should().Be("Örnek Ticaret Ltd. Şti.");

        var put = await staff.PutAsJsonAsync(Path, Body(email: "baska@example.test"));
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await put.Content.ReadAsStringAsync()).Should().Contain("owner-only");
    }

    [Fact]
    public async Task Silinmis_yayicinin_hala_gecerli_jetonu_bilgi_yazamaz()
    {
        // KVKK silmesi müşteri satırını anonimleştirip bırakır; Bearer-Customer kimlik neslini her istekte denetlemez, erişim
        // jetonu süresi dolana kadar (15 dk) geçerli kalır. O pencerede açık panelden gelen yazım anonim müşteriye yeni kişisel
        // veri (ad, adres) ve sürüm satırı bırakırdı: artık görünmez ama veritabanında ve yedeklerde durur.
        var (client, customerId) = await OwnerAsync();
        string email;
        using (var scope = _factory.Services.CreateScope())
            email = (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().Customers.AsNoTracking()
                .SingleAsync(c => c.Id == customerId)).Email;
        using var admin = await AdminApiClientAsync();
        (await admin.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var put = await client.PutAsJsonAsync(Path, Body());

        put.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await put.Content.ReadAsStringAsync()).Should().Contain("customer-purged");
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.DataControllerProfiles.AnyAsync(p => p.CustomerId == customerId)).Should().BeFalse();
        (await db.DataControllerProfileVersions.AnyAsync(v => v.CustomerId == customerId)).Should().BeFalse();
    }

    /// <summary>Yönetim API'si istemcisi (KVKK silmesi için); kullanıcı adı ve parola üretilir.</summary>
    private async Task<HttpClient> AdminApiClientAsync()
    {
        string adminToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var admin = new AdminUser
            {
                Id = Guid.NewGuid(), Username = $"a-{Guid.NewGuid():N}", PasswordHash = hasher.Hash($"p-{Guid.NewGuid():N}"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.AdminUsers.Add(admin);
            await db.SaveChangesAsync();
            adminToken = jwt.IssueAdminToken(admin.Id, admin.Username).Token;
        }
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        return client;
    }
}
