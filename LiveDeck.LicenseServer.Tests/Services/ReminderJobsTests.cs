using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public sealed class ReminderJobsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ReminderJobsTests(ApiFactory factory) => _factory = factory;

    private async Task<License> SeedLicenseAsync(double daysUntilExpiry, bool emailConfirmed = true, bool revoked = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"rem-{Guid.NewGuid():N}@x",
            Name = "Rem",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = emailConfirmed ? DateTimeOffset.UtcNow : null
        };
        var lic = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-REM-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-365),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(daysUntilExpiry),
            RevokedAt = revoked ? DateTimeOffset.UtcNow : null
        };
        db.Customers.Add(customer);
        db.Licenses.Add(lic);
        await db.SaveChangesAsync();
        return lic;
    }

    [Fact]
    public async Task SendRenewal14d_emails_license_expiring_in_14d()
    {
        var lic = await SeedLicenseAsync(14.0);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();
        var sentBefore = _factory.Email.Sent.Count;

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Count.Should().BeGreaterThan(sentBefore);
        _factory.Email.Sent.Should().Contain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_skips_license_outside_window()
    {
        var lic = await SeedLicenseAsync(20.0);   // 20d, 14d window dışında
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Should().NotContain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_skips_revoked_license()
    {
        var lic = await SeedLicenseAsync(14.0, revoked: true);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Should().NotContain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_skips_unconfirmed_email_customer()
    {
        var lic = await SeedLicenseAsync(14.0, emailConfirmed: false);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Should().NotContain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_dedup_does_not_resend()
    {
        var lic = await SeedLicenseAsync(14.0);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);
        var afterFirst = _factory.Email.Sent.Count(e => e.PlainBody.Contains(lic.LicenseKey));
        afterFirst.Should().Be(1);

        await jobs.SendRenewal14dAsync(default);   // 2nd call — dedup
        var afterSecond = _factory.Email.Sent.Count(e => e.PlainBody.Contains(lic.LicenseKey));
        afterSecond.Should().Be(1);
    }

    [Fact]
    public async Task SendExpired1d_emails_license_expired_yesterday()
    {
        var lic = await SeedLicenseAsync(-1.0);   // bir gün önce expired
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendExpired1dAsync(default);

        _factory.Email.Sent.Should().Contain(e => e.PlainBody.Contains(lic.LicenseKey));
    }
}
