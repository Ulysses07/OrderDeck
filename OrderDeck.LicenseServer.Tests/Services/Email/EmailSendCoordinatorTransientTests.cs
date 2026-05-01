using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Email;

/// <summary>
/// Verifies the transient/permanent classification in EmailSendCoordinator.
/// Hangfire's AutomaticRetry only fires when the job throws, so transient
/// failures must propagate while permanent ones must stay logged + swallowed.
/// </summary>
public class EmailSendCoordinatorTransientTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public EmailSendCoordinatorTransientTests(ApiFactory factory) => _factory = factory;

    private async Task<Guid> SeedCustomerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"transient-{Guid.NewGuid():N}@x.com",
            Name = "T",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static (string subject, string html, string plain) Tpl(Customer c, string? unsub) =>
        ("subj", "<p>html</p>", "plain");

    [Fact]
    public async Task Transient_failure_throws_EmailTransientFailureException()
    {
        var customerId = await SeedCustomerAsync();
        _factory.Email.FailWith = _ => new TimeoutException("smtp timeout");

        try
        {
            using var scope = _factory.Services.CreateScope();
            var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();

            Func<Task> act = () => coord.TrySendAsync(customerId, "test-template", "ctx-1",
                Tpl, requiresUnsubscribeRespect: false);
            await act.Should().ThrowAsync<EmailTransientFailureException>()
                .WithMessage("*timeout*");

            // Failure row written so the next attempt sees it as a non-success retry.
            using var scope2 = _factory.Services.CreateScope();
            var db = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = await db.EmailLogs.FirstOrDefaultAsync(e => e.CustomerId == customerId && e.TemplateKey == "test-template");
            row.Should().NotBeNull();
            row!.Error.Should().Contain("timeout");
        }
        finally { _factory.Email.FailWith = null; }
    }

    [Fact]
    public async Task Permanent_failure_logs_but_does_not_throw()
    {
        var customerId = await SeedCustomerAsync();
        _factory.Email.FailWith = _ => new ArgumentException("invalid recipient");

        try
        {
            using var scope = _factory.Services.CreateScope();
            var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();

            // Permanent → returns false, no throw. Hangfire wouldn't retry.
            var ok = await coord.TrySendAsync(customerId, "perm-template", "ctx-1",
                Tpl, requiresUnsubscribeRespect: false);
            ok.Should().BeFalse();
        }
        finally { _factory.Email.FailWith = null; }
    }

    [Fact]
    public async Task Successful_send_returns_true_and_writes_log_with_no_error()
    {
        var customerId = await SeedCustomerAsync();
        _factory.Email.FailWith = null;  // baseline success

        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();

        var ok = await coord.TrySendAsync(customerId, "ok-template", "ctx-1",
            Tpl, requiresUnsubscribeRespect: false);
        ok.Should().BeTrue();

        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.EmailLogs.FirstAsync(e => e.CustomerId == customerId && e.TemplateKey == "ok-template");
        row.Error.Should().BeNull();
    }
}
