using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// Kiracı izolasyonunun tekil-index tarafı GERÇEK SQL Server ister: InMemory
/// unique index uygulamıyor, iki markanın aynı numarası orada zaten çakışmaz
/// ve test yanlış yere yeşil yanar.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IysConsentTenantIsolationTests : IAsyncLifetime
{
    private const string Phone = "+905551112233";
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public IysConsentTenantIsolationTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> SeedBroadcasterAsync(string brandCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Kiracı-" + brandCode,
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = "8503021111",
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return licenseId;
    }

    private async Task RecordAsync(Guid licenseId, bool consented, DateTimeOffset at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var collector = new IysConsentCollector(
            db, new NetgsmAccountService(db, new EphemeralDataProtectionProvider()),
            Options.Create(new NetgsmOptions()),
            NullLogger<IysConsentCollector>.Instance);

        await collector.RecordAsync(
            licenseId, Phone, consented, at, "Shopper", Guid.NewGuid(),
            ip: "203.0.113.7", userAgent: "test-agent");
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Iki_yayinci_ayni_telefon_iki_ayri_satir()
    {
        var a = await SeedBroadcasterAsync(BrandA);
        var b = await SeedBroadcasterAsync(BrandB);
        var at = DateTimeOffset.UtcNow;

        await RecordAsync(a, consented: true, at);
        await RecordAsync(b, consented: false, at);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.IysConsents.Where(c => c.Recipient == Phone).ToListAsync();

        rows.Should().HaveCount(2, "onay MARKA başına tutulur; tek satır B'nin RET'ini " +
            "A'nın ONAY'ının üzerine yazardı");
        rows.Single(r => r.BrandCode == BrandA).Status.Should().Be(IysConsentStatus.Onay);
        rows.Single(r => r.BrandCode == BrandB).Status.Should().Be(IysConsentStatus.Ret);
    }

    [Fact]
    public async Task Olaylar_yayinciya_gore_ayristirilabilir()
    {
        var a = await SeedBroadcasterAsync(BrandA);
        var b = await SeedBroadcasterAsync(BrandB);
        var at = DateTimeOffset.UtcNow;

        await RecordAsync(a, consented: true, at);
        await RecordAsync(b, consented: false, at);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var events = await db.IysConsentEvents
            .Where(e => e.Recipient == Phone).ToListAsync();

        events.Should().HaveCount(2);
        events.Single(e => e.LicenseId == a).Status.Should().Be(IysConsentStatus.Onay);
        events.Single(e => e.LicenseId == b).Status.Should().Be(IysConsentStatus.Ret);
        events.Single(e => e.LicenseId == a).BrandCode.Should().Be(BrandA);
    }
}
