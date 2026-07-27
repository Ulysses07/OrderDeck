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
/// Test gönderim ucu. Test ortamında provider "log" olduğu için Graph'a hiç
/// çıkılmaz — burada doğrulanan, isteğin doğru gönderim yoluna (metin vs
/// template) düşmesi ve <b>gönderilemedi durumunun 200 gövdesinde</b>
/// taşınması: window_closed/no_account bir istemci hatası değil, işletme
/// sonucudur ve çağıran hepsini aynı şekilde okuyabilmelidir.
/// </summary>
public class AdminWhatsAppSendTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminWhatsAppSendTests(ApiFactory factory) => _factory = factory;

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
            LicenseKey = "LDK-WAS-" + Guid.NewGuid().ToString("N"),
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

    private async Task ConnectAccountAsync(HttpClient admin, Guid licenseId) =>
        (await admin.PutAsJsonAsync($"/api/v1/admin/licenses/{licenseId}/whatsapp/account", new
        {
            wabaId = "waba-1",
            phoneNumberId = $"pnid-{Guid.NewGuid():N}",
            displayPhoneNumber = "+905550000000",
            accessToken = "token-1234",
        })).EnsureSuccessStatusCode();

    /// <summary>24s pencereyi açmak için müşteriden gelmiş bir mesaj gerekiyor.</summary>
    private async Task OpenServiceWindowAsync(Guid licenseId, string phone)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = phone,
            PhoneNumberId = "pnid-open",
            Status = "open",
            LastInboundAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    private sealed record SendResponse(bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    private static string Url(Guid licenseId) => $"/api/v1/admin/licenses/{licenseId}/whatsapp/send-test";

    [Fact]
    public async Task Text_send_succeeds_when_service_window_open()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        await ConnectAccountAsync(admin, licenseId);
        await OpenServiceWindowAsync(licenseId, "905551112233");

        var resp = await admin.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "+90 555 111 22 33", text = "merhaba" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeTrue();
        body.MessageId.Should().NotBeNull();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var msg = await db.WaMessages.SingleAsync(m => m.LicenseId == licenseId);
        msg.Direction.Should().Be("out");
        msg.Origin.Should().Be("admin-test");
        msg.Status.Should().Be("sent");
    }

    [Fact]
    public async Task Text_send_reports_window_closed_with_200()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        await ConnectAccountAsync(admin, licenseId);

        // Hiç gelen mesaj yok → pencere kapalı.
        var resp = await admin.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "905559998877", text = "merhaba" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeFalse();
        body.ErrorCode.Should().Be("window_closed");
    }

    [Fact]
    public async Task Template_send_bypasses_service_window()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();
        await ConnectAccountAsync(admin, licenseId);

        var resp = await admin.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "905559998877", templateName = "hello_world" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await resp.Content.ReadFromJsonAsync<SendResponse>())!;
        body.Ok.Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var msg = await db.WaMessages.SingleAsync(m => m.LicenseId == licenseId);
        msg.Type.Should().Be("template");
        msg.TemplateName.Should().Be("hello_world");
    }

    [Fact]
    public async Task Reports_no_account_when_license_has_no_connection()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();

        var resp = await admin.PostAsJsonAsync(
            Url(licenseId), new { toPhone = "905559998877", templateName = "hello_world" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<SendResponse>())!.ErrorCode.Should().Be("no_account");
    }

    [Fact]
    public async Task Rejects_invalid_phone_and_empty_body()
    {
        var licenseId = await SeedLicenseAsync();
        var admin = await AdminClientAsync();

        (await admin.PostAsJsonAsync(Url(licenseId), new { toPhone = "abc", text = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(Url(licenseId), new { toPhone = "905559998877" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Returns_404_for_unknown_license()
    {
        var admin = await AdminClientAsync();

        var resp = await admin.PostAsJsonAsync(
            Url(Guid.NewGuid()), new { toPhone = "905559998877", text = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requires_admin_auth()
    {
        var licenseId = await SeedLicenseAsync();
        var anon = _factory.CreateClient();

        (await anon.PostAsJsonAsync(Url(licenseId), new { toPhone = "905559998877", text = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
