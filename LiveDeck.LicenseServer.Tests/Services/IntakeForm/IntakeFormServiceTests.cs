using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.IntakeForm;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class IntakeFormServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormServiceTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedCustomerAsync(bool withActiveLicense = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"if-{Guid.NewGuid():N}@x",
            Name = "If",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        if (withActiveLicense)
        {
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
                CustomerId = c.Id,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
        }
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task UpsertConfigAsync_creates_new_config_when_none_exists()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();

        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        var cfg = await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", "Title", true, default);

        cfg.Slug.Should().Be(slug);
        cfg.WhatsAppPhone.Should().Be("+905551234567");
        cfg.IsActive.Should().BeTrue();
        cfg.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpsertConfigAsync_updates_existing_config()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();

        var slug1 = $"slug-{Guid.NewGuid():N}"[..15];
        var slug2 = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug1, "+905551111111", null, true, default);
        var updated = await svc.UpsertConfigAsync(customer.Id, slug2, "+905552222222", "New", false, default);

        updated.Slug.Should().Be(slug2);
        updated.WhatsAppPhone.Should().Be("+905552222222");
        updated.CustomTitle.Should().Be("New");
        updated.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertConfigAsync_throws_SlugAlreadyTaken_when_used_by_another_customer()
    {
        var c1 = await SeedCustomerAsync();
        var c2 = await SeedCustomerAsync();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();

        await svc.UpsertConfigAsync(c1.Id, slug, "+905551111111", null, true, default);

        var act = async () => await svc.UpsertConfigAsync(c2.Id, slug, "+905552222222", null, true, default);
        var ex = await act.Should().ThrowAsync<IntakeFormService.SlugAlreadyTakenException>();
        ex.Which.Slug.Should().Be(slug);
    }

    [Fact]
    public async Task GetActiveBySlugAsync_returns_config_when_license_active_and_form_active()
    {
        var customer = await SeedCustomerAsync(withActiveLicense: true);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        var loaded = await svc.GetActiveBySlugAsync(slug, default);

        loaded.Should().NotBeNull();
        loaded!.Slug.Should().Be(slug);
    }

    [Fact]
    public async Task GetActiveBySlugAsync_returns_null_when_form_isactive_false()
    {
        var customer = await SeedCustomerAsync(withActiveLicense: true);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, isActive: false, default);

        var loaded = await svc.GetActiveBySlugAsync(slug, default);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveBySlugAsync_returns_null_when_customer_has_no_active_license()
    {
        var customer = await SeedCustomerAsync(withActiveLicense: false);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        var loaded = await svc.GetActiveBySlugAsync(slug, default);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveSubmissionAsync_persists_submission_with_audit_fields()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        var cfg = await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        var submission = await svc.SaveSubmissionAsync(
            cfg.Id, "uname", "Full Name", "Address",
            "10.0.0.5", "TestAgent/1.0", default);

        submission.Username.Should().Be("uname");
        submission.IpAddress.Should().Be("10.0.0.5");
        submission.UserAgent.Should().Be("TestAgent/1.0");

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.IntakeFormSubmissions.FirstOrDefaultAsync(s => s.Id == submission.Id);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSubmissionsSinceAsync_returns_only_newer_than_cursor_ordered_asc()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        var cfg = await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        // 3 submissions: now-2h, now-1h, now (in DB, scope shared with InMemory)
        await svc.SaveSubmissionAsync(cfg.Id, "u1", "n1", "a1", null, null, default);
        await Task.Delay(20);
        var t2 = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        await svc.SaveSubmissionAsync(cfg.Id, "u2", "n2", "a2", null, null, default);
        await Task.Delay(20);
        await svc.SaveSubmissionAsync(cfg.Id, "u3", "n3", "a3", null, null, default);

        var rows = await svc.GetSubmissionsSinceAsync(customer.Id, t2, limit: 50, default);

        rows.Should().HaveCount(2);
        rows[0].Username.Should().Be("u2");
        rows[1].Username.Should().Be("u3");
    }
}
