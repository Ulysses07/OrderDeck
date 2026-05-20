using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesPaymentAccountControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicensesPaymentAccountControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-PA-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, customerId, licenseId);
    }

    [Fact]
    public async Task Happy_path_sets_iban_and_holder()
    {
        var (client, _, licenseId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payment-account",
            new { iban = "TR330006100519786457841326", accountHolder = "Ali Veli" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = await db.Licenses.FirstAsync(l => l.Id == licenseId);
        license.PaymentIban.Should().Be("TR330006100519786457841326");
        license.PaymentAccountHolder.Should().Be("Ali Veli");
    }

    [Fact]
    public async Task Normalizes_iban_strips_spaces_and_uppercases()
    {
        var (client, _, licenseId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payment-account",
            new { iban = "tr33 0006 1005 1978 6457 8413 26", accountHolder = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = await db.Licenses.FirstAsync(l => l.Id == licenseId);
        license.PaymentIban.Should().Be("TR330006100519786457841326");
    }

    [Fact]
    public async Task Empty_iban_stored_as_null()
    {
        var (client, _, licenseId) = await SetupAsync();

        // First set a non-null IBAN so we can verify it gets cleared
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payment-account",
            new { iban = "TR330006100519786457841326", accountHolder = "Holder" });

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payment-account",
            new { iban = "", accountHolder = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = await db.Licenses.FirstAsync(l => l.Id == licenseId);
        license.PaymentIban.Should().BeNull();
    }

    [Fact]
    public async Task Iban_too_long_returns_400()
    {
        var (client, _, licenseId) = await SetupAsync();

        // 50-character alphanumeric string after stripping would still be 50 chars > 34
        var longIban = new string('A', 50);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payment-account",
            new { iban = longIban, accountHolder = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Different_customer_returns_404()
    {
        var (clientA, _, _) = await SetupAsync();
        var (_, _, licenseB) = await SetupAsync();

        var resp = await clientA.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseB}/payment-account",
            new { iban = "TR330006100519786457841326", accountHolder = "Test" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task License_does_not_exist_returns_404()
    {
        var (client, _, _) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{Guid.NewGuid()}/payment-account",
            new { iban = "TR330006100519786457841326", accountHolder = "Test" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task No_auth_returns_401()
    {
        var (_, _, licenseId) = await SetupAsync();
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/payment-account",
            new { iban = "TR330006100519786457841326", accountHolder = "Test" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
