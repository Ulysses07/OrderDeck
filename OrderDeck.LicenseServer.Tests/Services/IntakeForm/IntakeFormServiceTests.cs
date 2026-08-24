using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

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

    /// <summary>
    /// <c>SaveSubmissionAsync</c> damgayı kendi koyuyor (hep "şimdi"), o yüzden
    /// imleç testleri satırları doğrudan DB'ye yazıyor: hem damga hem Id kontrol
    /// altında olmalı.
    /// </summary>
    private async Task<Guid> SeedSubmissionAsync(
        Guid configId, Guid id, DateTimeOffset submittedAt, string username)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.IntakeFormSubmissions.Add(new IntakeFormSubmission
        {
            Id = id,
            IntakeFormConfigId = configId,
            Username = username,
            FullName = username,
            Address = "a",
            SubmittedAt = submittedAt
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<IntakeFormConfig> SeedConfigAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        return await svc.UpsertConfigAsync(customerId, slug, "+905551234567", null, true, default);
    }

    /// <summary>Kararlılık ufkunun gerisinde kalan bir damga — <c>since</c> ucu
    /// son saniyelerde yazılan satırları bilerek okumuyor (bkz. ReverseSyncCursor).</summary>
    private static DateTimeOffset Settled => DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task GetSubmissionsSinceAsync_returns_only_newer_than_cursor_ordered_asc()
    {
        var customer = await SeedCustomerAsync();
        var cfg = await SeedConfigAsync(customer.Id);

        var t2 = Settled.AddMinutes(-5);
        await SeedSubmissionAsync(cfg.Id, Guid.NewGuid(), t2.AddMinutes(-1), "u1");
        await SeedSubmissionAsync(cfg.Id, Guid.NewGuid(), t2.AddMinutes(1), "u2");
        await SeedSubmissionAsync(cfg.Id, Guid.NewGuid(), t2.AddMinutes(2), "u3");

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var rows = await svc.GetSubmissionsSinceAsync(customer.Id, t2, Guid.Empty, limit: 50, default);

        rows.Should().HaveCount(2);
        rows[0].Username.Should().Be("u2");
        rows[1].Username.Should().Be("u3");
    }

    [Fact]
    public async Task A_row_sharing_the_cursor_timestamp_is_not_skipped_forever()
    {
        // Düzeltilen kusur: imleç yalnız SubmittedAt'ti. WPF sayfanın en büyük
        // damgasını imleç yapıp `> imleç` sorduğu için, aynı milisaniyedeki
        // kardeş satır bir daha HİÇ dönmüyordu. Atlanan satır bir müşteri KAYDI
        // ve gönderim bir daha güncellenmediği için eksik kendiliğinden kapanmaz.
        var customer = await SeedCustomerAsync();
        var cfg = await SeedConfigAsync(customer.Id);

        var sameInstant = Settled;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(g => g).ToArray();
        await SeedSubmissionAsync(cfg.Id, ids[0], sameInstant, "a");
        await SeedSubmissionAsync(cfg.Id, ids[1], sameInstant, "b");
        await SeedSubmissionAsync(cfg.Id, ids[2], sameInstant, "c");

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();

        // İlk sayfa kasten eşitlik kümesinin ORTASINDAN kesiliyor.
        var page1 = await svc.GetSubmissionsSinceAsync(
            customer.Id, DateTimeOffset.MinValue, Guid.Empty, limit: 2, default);
        page1.Select(r => r.Id).Should().Equal(ids[0], ids[1]);

        // İmleç son satırdan okunuyor; kalan satır hâlâ geliyor.
        var last = page1[^1];
        var page2 = await svc.GetSubmissionsSinceAsync(
            customer.Id, last.SubmittedAt, last.Id, limit: 2, default);
        page2.Select(r => r.Id).Should().Equal(ids[2]);
    }

    [Fact]
    public async Task Rows_written_in_the_last_seconds_are_held_back()
    {
        // Commit sırası damga sırasına eşit değil: damgasını okuyup geç commit
        // eden bir gönderim, imleç ilerlemişse arkada kalır ve bir daha
        // istenmez. Tavan koyup son saniyeleri hiç okumamak bunu kapatıyor.
        var customer = await SeedCustomerAsync();
        var cfg = await SeedConfigAsync(customer.Id);

        await SeedSubmissionAsync(cfg.Id, Guid.NewGuid(), Settled, "eski");
        await SeedSubmissionAsync(cfg.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, "taze");

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var rows = await svc.GetSubmissionsSinceAsync(
            customer.Id, DateTimeOffset.MinValue, Guid.Empty, limit: 50, default);

        rows.Select(r => r.Username).Should().Equal("eski");
    }
}
