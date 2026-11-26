using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
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
/// <para>Çift IBAN bağı (banka havalesi S2a) da burada: geniş tekil
/// (LicenseId, IbanHash, WpfCustomerId) ve EF'in DELETE/UPDATE sırası InMemory'de
/// yok. O testler dar indeksi düşürür (<see cref="SharedIbanSchema"/>): çift bağ
/// yalnız orada var olabilir.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerIdentityMergerRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public CustomerIdentityMergerRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        await PreB1Schema.ApplyAsync(_factory, _cs);
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

    private static WpfCustomerProjection Person(Guid licenseId, string username) => new()
    { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = username, UpdatedAt = DateTimeOffset.UtcNow };

    private static CustomerIbanMemory Bond(
        WpfCustomerProjection customer, string ibanHash, IbanMemorySource learnedFrom, DateTimeOffset createdAt,
        DateTimeOffset lastConfirmedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = customer.LicenseId, WpfCustomerId = customer.Id, IbanHash = ibanHash,
        IbanMasked = "TR..", LearnedFrom = learnedFrom, CreatedAt = createdAt, LastConfirmedAt = lastConfirmedAt,
    };

    [Fact]
    public async Task Ayni_ozetli_uc_bag_tek_SaveChangeste_tekillesir()
    {
        await SharedIbanSchema.ApplyAsync(_cs);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = await SeedLicenseAsync(db);
        var (canonical, copy1, copy2) = (Person(licenseId, "ayse"), Person(licenseId, "Ayse"), Person(licenseId, "AYSE"));
        db.WpfCustomerProjections.AddRange(canonical, copy1, copy2);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var newestConfirmation = DateTimeOffset.UtcNow.AddDays(-2); // ilk adımda düşen satırda: ikinci adımda kaybolmamalı
        var kept = Bond(copy1, hash, IbanMemorySource.HumanApproval, t0, t0);
        db.CustomerIbanMemories.AddRange(
            Bond(canonical, hash, IbanMemorySource.HumanApproval, t0.AddDays(1), newestConfirmation), kept,
            Bond(copy2, hash, IbanMemorySource.BankConfirm, t0.AddDays(2), t0.AddDays(2)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); // birleştirici bağları kendi okur

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        await merger.RepointReferencesAsync(licenseId, copy1.Id, canonical.Id, default);
        await merger.RepointReferencesAsync(licenseId, copy2.Id, canonical.Id, default);

        // TEK SaveChanges: ikinci çağrıda copy1'den taşınan (en eski, kalan) satır veritabanında hâlâ copy1'de, asıl kayıttaki
        // ise silinmeyi bekliyor. Karar izlenen satırlardan verilmezse kopya2'ninki de asıl kayda çıkar ya da "Evet"i silinen
        // satıra yazılır.
        Func<Task> act = async () => await db.SaveChangesAsync();
        await act.Should().NotThrowAsync("geniş IBAN tekili (2601) birleştirmeyi ve onu koşan müşteri senkronunu düşürmemeli");

        db.ChangeTracker.Clear();
        var row = await db.CustomerIbanMemories.SingleAsync(m => m.LicenseId == licenseId);
        row.Id.Should().Be(kept.Id, "en eski kalır");
        row.WpfCustomerId.Should().Be(canonical.Id);
        row.LearnedFrom.Should().Be(IbanMemorySource.BankConfirm, "kopya2'nin \"Evet\"i kalan satıra geçer");
        row.LastConfirmedAt.Should().BeCloseTo(newestConfirmation, TimeSpan.FromMilliseconds(1), "üçünün en yenisi");
    }

    [Fact]
    public async Task Kopyanin_satiri_kalirken_asil_satir_once_silinir()
    {
        // Kalan kopyanın (daha eski) satırı: UPDATE asıl kaydın satırı silinmeden giderse geniş tekile çarpar (2601).
        await SharedIbanSchema.ApplyAsync(_cs);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = await SeedLicenseAsync(db);
        var (canonical, copy) = (Person(licenseId, "ayse"), Person(licenseId, "Ayse"));
        db.WpfCustomerProjections.AddRange(canonical, copy);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var kept = Bond(copy, hash, IbanMemorySource.HumanApproval, t0, t0);
        db.CustomerIbanMemories.AddRange(Bond(canonical, hash, IbanMemorySource.BankConfirm, t0.AddDays(1), t0.AddDays(1)), kept);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>()
            .RepointReferencesAsync(licenseId, copy.Id, canonical.Id, default);
        Func<Task> act = async () => await db.SaveChangesAsync();

        await act.Should().NotThrowAsync("DELETE, UPDATE'ten önce gitmeli");
        db.ChangeTracker.Clear();
        var row = await db.CustomerIbanMemories.SingleAsync(m => m.LicenseId == licenseId);
        (row.Id, row.WpfCustomerId, row.LearnedFrom).Should().Be((kept.Id, canonical.Id, IbanMemorySource.BankConfirm));
    }

    [Fact]
    public async Task Once_birlestirme_sonra_onay_iki_yazim_da_kalir()
    {
        // Payment.Status jetondur, WpfCustomerId değil: dekontu birleştirmeden önce okuyan onay çakışmaz, kendi alanlarını
        // yazar, birleştirmenin yazdığı müşteriyi ezmez.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = await SeedLicenseAsync(db);
        var (canonical, copy) = (Person(licenseId, "ayse"), Person(licenseId, "Ayse"));
        db.WpfCustomerProjections.AddRange(canonical, copy);
        var paymentId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        db.Payments.Add(new Payment
        {
            Id = paymentId, LicenseId = licenseId, WpfCustomerId = copy.Id, ReferansNo = $"r-{Guid.NewGuid():N}", Amount = 10m,
            PaidAt = at, CreatedAt = at, UpdatedAt = at,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var approval = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).Options);
        var staleRead = await approval.Payments.SingleAsync(p => p.Id == paymentId);

        await scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>()
            .RepointReferencesAsync(licenseId, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();
        staleRead.Status = PaymentStatus.Approved;

        (await approval.SaveChangesAsync()).Should().Be(1);
        db.ChangeTracker.Clear();
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        payment.Status.Should().Be(PaymentStatus.Approved);
        payment.WpfCustomerId.Should().Be(canonical.Id);
    }
}
