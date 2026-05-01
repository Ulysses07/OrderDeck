using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Licensing;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public class ActivationManagerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ActivationManagerTests(ApiFactory factory) => _factory = factory;

    private async Task<(Customer customer, License license)> SeedAsync(int slots = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"a-{Guid.NewGuid():N}@x.com",
            Name = "AM", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = slots,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(365)
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (customer, license);
    }

    [Fact]
    public async Task Activate_first_device_succeeds()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        var act = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", "PC-1");
        act.Should().NotBeNull();
        act.HardwareFingerprint.Should().Be("fp-1");
    }

    [Fact]
    public async Task Activate_second_device_when_slots_1_throws_slot_full()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);

        var act = async () => await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-2", null);
        var ex = await act.Should().ThrowAsync<ActivationManager.ActivationException>();
        ex.Which.Code.Should().Be("slot-full");
    }

    [Fact]
    public async Task Activate_after_deactivate_succeeds()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);
        var ok = await mgr.DeactivateAsync(license.LicenseKey, customer.Id, "fp-1");
        ok.Should().BeTrue();

        var act = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-2", null);
        act.HardwareFingerprint.Should().Be("fp-2");
    }

    [Fact]
    public async Task Activate_same_device_twice_returns_same_activation_and_updates_LastSeen()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        var first = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);
        await Task.Delay(20);
        var second = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);

        second.Id.Should().Be(first.Id);
        second.LastSeenAt.Should().BeAfter(first.ActivatedAt);
    }

    [Fact]
    public async Task Activate_revoked_license_throws()
    {
        var (customer, license) = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        var fresh = db.Licenses.Find(license.Id)!;
        fresh.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var act = async () => await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp", null);
        var ex = await act.Should().ThrowAsync<ActivationManager.ActivationException>();
        ex.Which.Code.Should().Be("license-revoked");
    }

    /// <summary>
    /// Two devices race to claim the last slot. With the optimistic-concurrency retry
    /// loop in ActivationManager exactly one must win and exactly one must see
    /// "slot-full". Without retry logic both could succeed and exceed the slot budget.
    /// </summary>
    [Fact]
    public async Task Activate_two_devices_racing_for_last_slot_yields_exactly_one_winner()
    {
        var (customer, license) = await SeedAsync(slots: 1);

        // Each task gets its own DI scope + DbContext so they truly contend at the
        // change tracker level, not within a single shared context.
        async Task<Result> Try(string fp)
        {
            using var scope = _factory.Services.CreateScope();
            var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();
            try
            {
                var act = await mgr.ActivateAsync(license.LicenseKey, customer.Id, fp, null);
                return new Result(true, null, act.Id);
            }
            catch (ActivationManager.ActivationException ex)
            {
                return new Result(false, ex.Code, null);
            }
        }

        // Fire both in parallel.
        var results = await Task.WhenAll(Try("fp-A"), Try("fp-B"));

        var winners = results.Where(r => r.Ok).ToList();
        var losers = results.Where(r => !r.Ok).ToList();

        winners.Should().HaveCount(1, "the single slot must be granted to exactly one device");
        losers.Should().HaveCount(1, "the other device must observe slot-full, not crash");
        losers[0].Code.Should().Be("slot-full");

        // DB should reflect exactly 1 active activation, never 2.
        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var active = db.Activations.Count(a => a.LicenseId == license.Id && a.DeactivatedAt == null);
        active.Should().Be(1);
    }

    private sealed record Result(bool Ok, string? Code, Guid? ActivationId);
}
