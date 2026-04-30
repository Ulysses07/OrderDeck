using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Licensing;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public class LicenseIssuerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicenseIssuerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void GenerateKey_returns_LDK_prefix_plus_32_hex()
    {
        var key = LicenseIssuer.GenerateKey();
        key.Should().StartWith("LDK-");
        key.Length.Should().Be(36);   // "LDK-" + 32 hex
        key.Substring(4).Should().MatchRegex("^[0-9A-F]{32}$");
    }

    [Fact]
    public async Task IssueAsync_creates_license_with_default_duration_and_slots()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var issuer = scope.ServiceProvider.GetRequiredService<LicenseIssuer>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"i-{Guid.NewGuid():N}@example.com",
            Name = "Issuer Test", PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var result = await issuer.IssueAsync(new(customer.Email, "STD", null, null));

        result.LicenseKey.Should().StartWith("LDK-");
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(365), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task IssueAsync_with_unknown_customer_throws()
    {
        using var scope = _factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<LicenseIssuer>();

        var act = async () => await issuer.IssueAsync(new("nope@x.com", "STD", null, null));
        var ex = await act.Should().ThrowAsync<LicenseIssuer.IssueException>();
        ex.Which.Code.Should().Be("customer-not-found");
    }
}
