using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// CustomerIdentityMerger'ın bakiye dalı için gerçek SQL Server gerekir:
/// A7 birleştirme işi aynı kişinin BİRDEN FAZLA kopyasını TEK SaveChanges'te
/// taşır (grup başına işlem — bkz. sınıfın kendi dokümanı). İkinci kopya
/// taşınırken asıl kaydın bakiye satırı henüz veritabanında YOK; izlenen
/// (ama henüz kaydedilmemiş) satırlarda bulunmalı — yoksa ikinci çağrı da
/// kendi "asıl" satırını açar ve IX_CustomerBalances_LicenseId_WpfCustomerId
/// tekil indeksini patlatır. InMemory sağlayıcı bu indeksi uygulamadığından
/// senaryo yalnız gerçek SQL Server'da (Testcontainers) kanıtlanabilir (A4
/// kalite incelemesi, 2026-10-06 — reviewer SQL Server'da tekrar üretti).
/// Şema B1 öncesi (<see cref="PreB1Schema"/>): tohum aynı kişinin üç asıl
/// kaydı — A7'nin koştuğu durum.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerIdentityMergerRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public CustomerIdentityMergerRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        var cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(cs);
        await PreB1Schema.ApplyAsync(_factory, cs);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<Guid> SeedLicenseAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"musteri-{Guid.NewGuid():N}@example.test",
            Name = "Birleştirme Testi Müşteri",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "cim-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    [Fact]
    public async Task Ayni_grup_icindeki_iki_kopya_tek_SaveChanges_ile_tasinir_tekil_indeksi_patlatmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = await SeedLicenseAsync(db);

        var canonical = new WpfCustomerProjection
        { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "ayse", UpdatedAt = DateTimeOffset.UtcNow };
        var copy1 = new WpfCustomerProjection
        { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "Ayse", UpdatedAt = DateTimeOffset.UtcNow };
        var copy2 = new WpfCustomerProjection
        { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = "AYSE", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.AddRange(canonical, copy1, copy2);

        // Asıl kayıtta bakiye YOK, iki kopyada var — A7'nin gerçek senaryosu:
        // aynı kişinin birden çok kopyası tek birleştirme işleminde taşınır.
        db.CustomerBalances.Add(new CustomerBalance
        { Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = copy1.Id, Balance = 30m, UpdatedAt = DateTimeOffset.UtcNow });
        db.CustomerBalances.Add(new CustomerBalance
        { Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = copy2.Id, Balance = 20m, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        await merger.RepointReferencesAsync(licenseId, copy1.Id, canonical.Id, default);
        await merger.RepointReferencesAsync(licenseId, copy2.Id, canonical.Id, default);

        // TEK SaveChanges — asıl satır bu noktada henüz veritabanında yok;
        // ikinci çağrı onu ancak izlenen (Local) satırlarda bulabilir.
        Func<Task> act = async () => await db.SaveChangesAsync();
        await act.Should().NotThrowAsync(
            "asıl satır Local'de bulunmalı — ikinci kopya kendi 'asıl' satırını açıp tekil indeksi patlatmamalı");

        var balances = await db.CustomerBalances.Where(b => b.LicenseId == licenseId).ToListAsync();
        balances.Should().HaveCount(3, "hiçbir satır silinmedi — ikisi kopya (0'lanmış), biri asıl");
        balances.Single(b => b.WpfCustomerId == canonical.Id).Balance.Should().Be(50m);
        balances.Single(b => b.WpfCustomerId == copy1.Id).Balance.Should().Be(0m);
        balances.Single(b => b.WpfCustomerId == copy2.Id).Balance.Should().Be(0m);
    }
}
