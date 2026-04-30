using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public sealed class EmailSendCoordinatorTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public EmailSendCoordinatorTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedCustomerAsync(bool unsubscribed = false, bool emailConfirmed = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"coord-{Guid.NewGuid():N}@x",
            Name = "Coord Test",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = emailConfirmed ? DateTimeOffset.UtcNow : null,
            Unsubscribed = unsubscribed
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task TrySendAsync_returns_true_and_writes_EmailLog_on_success()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        var sent = await coord.TrySendAsync(
            customer.Id,
            templateKey: "test-template",
            contextKey: "ctx-1",
            templateBuilder: (c, unsubUrl) => ("Test", "html", "plain"),
            requiresUnsubscribeRespect: false);

        sent.Should().BeTrue();
        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var log = await db.EmailLogs.Where(e => e.CustomerId == customer.Id && e.TemplateKey == "test-template").FirstOrDefaultAsync();
        log.Should().NotBeNull();
        log!.Error.Should().BeNull();
    }

    [Fact]
    public async Task TrySendAsync_returns_false_and_skips_when_dedup_log_exists()
    {
        var customer = await SeedCustomerAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.EmailLogs.Add(new EmailLog
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TemplateKey = "dedup-tpl",
                ContextKey = "ctx-x",
                SentAt = DateTimeOffset.UtcNow,
                Error = null
            });
            await db.SaveChangesAsync();
        }

        using var s2 = _factory.Services.CreateScope();
        var coord = s2.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        var sent = await coord.TrySendAsync(
            customer.Id, "dedup-tpl", "ctx-x",
            (c, u) => ("S", "h", "p"), requiresUnsubscribeRespect: false);

        sent.Should().BeFalse();
        _factory.Email.Sent.Count.Should().Be(sentBefore);   // no new send
    }

    [Fact]
    public async Task TrySendAsync_returns_false_when_customer_unsubscribed_and_respect_required()
    {
        var customer = await SeedCustomerAsync(unsubscribed: true);
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        var sent = await coord.TrySendAsync(
            customer.Id, "renewal-14d", "LDK-X",
            (c, u) => ("S", "h", "p"), requiresUnsubscribeRespect: true);

        sent.Should().BeFalse();
        _factory.Email.Sent.Count.Should().Be(sentBefore);
    }

    [Fact]
    public async Task TrySendAsync_sends_when_customer_unsubscribed_but_respect_NOT_required()
    {
        var customer = await SeedCustomerAsync(unsubscribed: true);
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        // Transactional email (password-reset, confirm-email) bypass-respect
        var sent = await coord.TrySendAsync(
            customer.Id, "password-reset", "tok-1",
            (c, u) => ("Reset", "h", "p"), requiresUnsubscribeRespect: false);

        sent.Should().BeTrue();
        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);
    }

    [Fact]
    public async Task TrySendAsync_passes_unsubscribe_url_to_template_builder_when_respect_required()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();

        string? capturedUrl = null;
        var sent = await coord.TrySendAsync(
            customer.Id, "renewal-7d", "LDK-Y",
            (c, unsubUrl) => { capturedUrl = unsubUrl; return ("S", "h", "p"); },
            requiresUnsubscribeRespect: true);

        sent.Should().BeTrue();
        capturedUrl.Should().NotBeNull();
        capturedUrl!.Should().Contain("/unsubscribe?token=");
    }

    [Fact]
    public async Task TrySendAsync_returns_false_when_customer_not_found()
    {
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();

        var sent = await coord.TrySendAsync(
            customerId: Guid.NewGuid(),
            templateKey: "any", contextKey: null,
            templateBuilder: (c, u) => ("S", "h", "p"),
            requiresUnsubscribeRespect: false);

        sent.Should().BeFalse();
    }
}
