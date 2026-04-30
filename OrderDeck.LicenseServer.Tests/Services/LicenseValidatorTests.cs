using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Licensing;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public class LicenseValidatorTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicenseValidatorTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Validate_returns_null_for_unknown_key()
    {
        using var scope = _factory.Services.CreateScope();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();
        var result = await v.ValidateAsync("LDK-NONE", "fp", Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task Validate_returns_NotActivated_when_no_activation()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"v-{Guid.NewGuid():N}@x.com",
            Name = "V", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id, SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.AddRange(customer, license);
        await db.SaveChangesAsync();

        var result = await v.ValidateAsync(license.LicenseKey, "fp-x", customer.Id);
        result.Should().NotBeNull();
        result!.Status.Should().Be(LicenseStatus.NotActivated);
        result.RemainingDays.Should().Be(30);
    }

    [Fact]
    public async Task Validate_returns_Expired_for_past_expiry()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"x-{Guid.NewGuid():N}@x.com",
            Name = "X", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id, SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-400),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.AddRange(customer, license);
        await db.SaveChangesAsync();

        var result = await v.ValidateAsync(license.LicenseKey, "fp", customer.Id);
        result!.Status.Should().Be(LicenseStatus.Expired);
    }

    [Fact]
    public async Task Validate_returns_Active_when_activation_exists()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"a-{Guid.NewGuid():N}@x.com",
            Name = "A", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id, SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(60)
        };
        db.AddRange(customer, license);
        db.Activations.Add(new Activation
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, HardwareFingerprint = "fp-1",
            ActivatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await v.ValidateAsync(license.LicenseKey, "fp-1", customer.Id);
        result!.Status.Should().Be(LicenseStatus.Active);
        result.SlotInfo!.ThisDeviceActive.Should().BeTrue();
        result.SlotInfo.Used.Should().Be(1);
        result.SlotInfo.Total.Should().Be(1);
    }
}
