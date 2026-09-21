using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>GET /sms/balance</c> uyumluluk stub'ının testleri (kredi sistemi emekli —
/// Plan 3, §1.4b). Stub sahiplik/auth kontrollerini korur, gövde sabit 0 döner.
/// Admin topup/balance uçları silindi; eski kredi-seed'li testler de bu dosyadan
/// kaldırıldı.
/// </summary>
public class SmsBalanceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SmsBalanceTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> CustomerWithLicenseAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-SMS-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        licenseId = license.Id;
        return (client, customerId, licenseId);
    }

    private sealed record CustomerBalanceResponse(int CreditsRemaining, DateTimeOffset UpdatedAt);

    [Fact]
    public async Task Balance_stub_returns_zero_for_owned_license()
    {
        var (client, _, licenseId) = await CustomerWithLicenseAsync();

        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/sms/balance");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<CustomerBalanceResponse>();
        // Kredi sistemi emekli: stub her zaman 0 döner (eski WPF'te salt kozmetik rozet).
        body!.CreditsRemaining.Should().Be(0);
        body.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Customer_cannot_read_other_license_balance()
    {
        var (clientA, _, _) = await CustomerWithLicenseAsync();
        var (_, _, licenseB) = await CustomerWithLicenseAsync();

        // Sahiplik kontrolü stub'da da yaşar: başkasının lisansı → 404.
        var resp = await clientA.GetAsync($"/api/v1/licenses/{licenseB}/sms/balance");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unauthenticated_balance_request_is_rejected()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/v1/licenses/{Guid.NewGuid()}/sms/balance");
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_sms_topup_endpoint_is_gone()
    {
        var (client, _, licenseId) = await CustomerWithLicenseAsync();
        // AdminSmsController silindi (kredi emekliliği) — uç artık route edilmez.
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/licenses/{licenseId}/sms/topup", new { credits = 100, reason = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
