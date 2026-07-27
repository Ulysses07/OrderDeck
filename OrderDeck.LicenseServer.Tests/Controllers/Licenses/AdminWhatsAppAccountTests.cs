using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Hesap bağlama ucunun HTTP yüzeyi. Kritik davranışlar: token düz metin
/// dönmemeli, aynı Phone Number ID iki lisansa bağlanmamalı (webhook
/// yönlendirmesi tekil olmak zorunda) ve tekrar bağlama token'ı yenilemeli.
/// </summary>
public class AdminWhatsAppAccountTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminWhatsAppAccountTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"wa-{Guid.NewGuid():N}@example.com",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-WA-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private sealed record AccountResponse(
        Guid Id, string WabaId, string PhoneNumberId, string DisplayPhoneNumber,
        string? VerifiedName, string Status, string TokenHint, string? LastError,
        DateTimeOffset ConnectedAt);

    private static object Body(string phoneNumberId, string token = "EAAG-super-secret-1234") => new
    {
        wabaId = "waba-1",
        phoneNumberId,
        displayPhoneNumber = "+90 555 000 00 00",
        accessToken = token,
        verifiedName = "OrderDeck",
    };

    [Fact]
    public async Task Connect_creates_account_and_never_returns_raw_token()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var pnid = $"pnid-{Guid.NewGuid():N}";

        var resp = await admin.PutAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/whatsapp/account", Body(pnid));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("EAAG-super-secret-1234");

        var body = System.Text.Json.JsonSerializer.Deserialize<AccountResponse>(
            raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        body.PhoneNumberId.Should().Be(pnid);
        body.Status.Should().Be("active");
        body.TokenHint.Should().Be("****1234");
        // Numara kanonikleştirilmeli — sohbet eşleştirmesi bu forma dayanıyor.
        body.DisplayPhoneNumber.Should().Be("905550000000");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.WhatsAppAccounts.SingleAsync(a => a.LicenseId == licenseId);
        stored.AccessTokenProtected.Should().NotContain("EAAG-super-secret-1234");
    }

    [Fact]
    public async Task Connect_twice_updates_existing_row_instead_of_duplicating()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var pnid = $"pnid-{Guid.NewGuid():N}";
        var url = $"/api/v1/admin/licenses/{licenseId}/whatsapp/account";

        await admin.PutAsJsonAsync(url, Body(pnid, "token-AAAA"));
        var second = await admin.PutAsJsonAsync(url, Body(pnid, "token-BBBB"));

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<AccountResponse>())!.TokenHint.Should().Be("****BBBB");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.WhatsAppAccounts.CountAsync(a => a.LicenseId == licenseId)).Should().Be(1);
    }

    [Fact]
    public async Task Connect_rejects_phone_number_id_owned_by_another_license()
    {
        var first = await SeedLicenseAsync();
        var second = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var pnid = $"pnid-{Guid.NewGuid():N}";

        await admin.PutAsJsonAsync($"/api/v1/admin/licenses/{first}/whatsapp/account", Body(pnid));
        var clash = await admin.PutAsJsonAsync(
            $"/api/v1/admin/licenses/{second}/whatsapp/account", Body(pnid));

        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Connect_returns_404_for_unknown_license()
    {
        var admin = await AdminClientAsync();

        var resp = await admin.PutAsJsonAsync(
            $"/api/v1/admin/licenses/{Guid.NewGuid()}/whatsapp/account", Body("pnid-x"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Connect_rejects_missing_access_token()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();

        var resp = await admin.PutAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/whatsapp/account",
            new { wabaId = "w", phoneNumberId = "p", displayPhoneNumber = "+905550000000", accessToken = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_returns_404_before_connect_and_account_after()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        var url = $"/api/v1/admin/licenses/{licenseId}/whatsapp/account";

        (await admin.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await admin.PutAsJsonAsync(url, Body($"pnid-{Guid.NewGuid():N}"));

        var after = await admin.GetAsync(url);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        (await after.Content.ReadFromJsonAsync<AccountResponse>())!.TokenHint.Should().Be("****1234");
    }

    [Fact]
    public async Task Endpoints_require_admin_auth()
    {
        var licenseId = await SeedLicenseAsync();
        var anon = _factory.CreateClient();

        (await anon.GetAsync($"/api/v1/admin/licenses/{licenseId}/whatsapp/account"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PutAsJsonAsync($"/api/v1/admin/licenses/{licenseId}/whatsapp/account", Body("pnid-y")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
