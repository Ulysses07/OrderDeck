using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public sealed class AdminActionEmailServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminActionEmailServiceTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedConfirmedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"a-{Guid.NewGuid():N}@x",
            Name = "A",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task NotifyLicenseIssued_sends_email_and_writes_log()
    {
        var customer = await SeedConfirmedAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AdminActionEmailService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.NotifyLicenseIssuedAsync(customer.Id, "LDK-AAA", "STD", DateTimeOffset.UtcNow.AddDays(365));

        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);
        _factory.Email.Sent.Should().Contain(e => e.Subject.Contains("Yeni lisansınız"));

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var log = await db.EmailLogs.Where(e => e.CustomerId == customer.Id && e.TemplateKey == "license-issued").FirstOrDefaultAsync();
        log.Should().NotBeNull();
    }

    [Fact]
    public async Task NotifyLicenseRevoked_sends_email_and_writes_log()
    {
        var customer = await SeedConfirmedAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AdminActionEmailService>();

        await svc.NotifyLicenseRevokedAsync(customer.Id, "LDK-BBB", "Test sebep");

        _factory.Email.Sent.Should().Contain(e => e.Subject.Contains("iptal"));

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var log = await db.EmailLogs.Where(e => e.CustomerId == customer.Id && e.TemplateKey == "license-revoked").FirstOrDefaultAsync();
        log.Should().NotBeNull();
    }

    [Fact]
    public async Task NotifyLicenseExtended_sends_email_for_each_call_with_unique_contextKey()
    {
        var customer = await SeedConfirmedAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AdminActionEmailService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.NotifyLicenseExtendedAsync(customer.Id, "LDK-CCC", DateTimeOffset.UtcNow.AddDays(395), 30);
        await svc.NotifyLicenseExtendedAsync(customer.Id, "LDK-CCC", DateTimeOffset.UtcNow.AddDays(425), 30);

        // Her extend için ayrı email (contextKey unique-per-extend)
        _factory.Email.Sent.Count.Should().Be(sentBefore + 2);
    }
}
