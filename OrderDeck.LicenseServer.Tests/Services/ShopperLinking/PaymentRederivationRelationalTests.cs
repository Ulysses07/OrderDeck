using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperLinking;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperLinking;

/// <summary>
/// Yeniden bağlamada dekontun yeniden yazımı gerçek SQL Server'da (S2b planı Karar 10, 11): yazım lisans başına tek koşullu
/// UPDATE'tir. Seçilen bağlantı hâlâ okunan müşterideyse ve shopper silinmemişse yazar; <c>Status</c>'a bakmaz, onu ve
/// <c>UpdatedAt</c>'i yazmaz. Yarış okuma kancasıyla sıraya dizilir (<see cref="ReadQueryHook"/>): türetme bağlantıyı okuduktan
/// sonra, zinciri okumadan önce ayrı bağlamda bir yazım commit olur. InMemory'de komut kancası ve <c>ExecuteUpdate</c> yok.
/// Değerler üretilir.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PaymentRederivationRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private ReadHookRelationalApiFactory _factory = null!;

    public PaymentRederivationRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _factory = new ReadHookRelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Queries.Disarm();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record Seed(Guid LicenseId, Guid ShopperId, Guid CustomerId, Guid PaymentId, DateTimeOffset At);

    /// <summary>Lisans, telefonu doğrulanmış shopper, asıl kayıt P, shopper'ın P'ye bağlı bağlantısı ve müşterisiz bekleyen
    /// dekontu (bağ beklerken gönderilmiş).</summary>
    private async Task<Seed> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"rederive-{Guid.NewGuid():N}@x.test", Name = "Yayıncı",
            PasswordHash = $"hash-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = $"key-{Guid.NewGuid():N}", CustomerId = customer.Id, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1),
        };
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(), FullName = "Örnek Müşteri", Phone = TestPhone.NewE164(), PhoneVerifiedAt = now,
            PasswordHash = $"hash-{Guid.NewGuid():N}", Address = "Adres", CreatedAt = now, UpdatedAt = now,
        };
        var p = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "tiktok", Username = $"u-{Guid.NewGuid():N}",
            Phone = shopper.Phone, UpdatedAt = now,
        };
        var link = new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = license.Id, Platform = "tiktok", Username = p.Username,
            WpfCustomerId = p.Id, JoinedAt = now,
        };
        var at = now.AddDays(-2);
        var payment = new Payment
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ShopperId = shopper.Id, ReferansNo = $"r-{Guid.NewGuid():N}",
            Amount = 10m, PaidAt = at, CreatedAt = at, UpdatedAt = at,
        };
        db.AddRange(customer, license, shopper, p, link, payment);
        await db.SaveChangesAsync();
        return new Seed(license.Id, shopper.Id, p.Id, payment.Id, at);
    }

    /// <summary>Türetme bağlantıyı okuduktan sonra, zincirin ilk okumasından hemen önce <paramref name="interleave"/>
    /// koşar.</summary>
    private void BetweenLinkAndChainRead(Func<Task> interleave)
        => _factory.Queries.Arm(sql => sql.Contains("[WpfCustomerProjections]", StringComparison.Ordinal), interleave);

    private async Task<int> RederiveAsync(Seed s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await ShopperCustomerResolver.RederivePaymentsAsync(db, s.ShopperId, [s.LicenseId], default);
    }

    private async Task<Payment> PaymentAsync(Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    /// <summary>Gerçek silme yazımın okumasından sonra commit olur: bağlantıyı siler, shopper'ı siler, dekontun müşterisini
    /// boşaltır, <c>Status</c>'a dokunmaz. Koşullu UPDATE hiçbir satır yazmaz. <c>WHERE Status = özgün</c> taşıyan izlenen
    /// yazım P'yi silinmiş shopper'ın dekontuna geri yazardı; okuyucu bunu ayırt edemezdi.</summary>
    [Fact]
    public async Task Yeniden_yazim_bagi_okuduktan_sonra_silme_commit_olursa_yazmaz()
    {
        var s = await SeedAsync();
        var purged = false;
        BetweenLinkAndChainRead(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<ShopperPurgeService>().PurgeAsync(s.ShopperId, default))
                .Should().NotBeNull();
            purged = true;
        });

        var changed = await RederiveAsync(s);

        purged.Should().BeTrue("kanca silmeyi bağlantı okumasıyla zincir okuması arasında commit ettirmeli");
        changed.Should().Be(0);
        (await PaymentAsync(s.PaymentId)).WpfCustomerId.Should().BeNull("silme dekontun müşterisini boşalttı; geri yazılmaz");
    }

    /// <summary>Kanıt kaybı (<c>ReproveLinksAsync</c> ve <c>CustomerIdentityMergeJob</c>'un etkisi) yazımın okumasından sonra
    /// commit olur: bağlantı boşalır, shopper canlı kalır, <c>Status</c>'a dokunulmaz. Yalnız bağlantı koşulu tutmaz; koşullu
    /// UPDATE hiçbir satır yazmaz. Silme iki koşulu birden bozduğu için yukarıdaki test bu koşulu tek başına sınamaz. Koşul
    /// olmasa P dekonta yazılırdı; okuyucu geride kalan asıl kaydı ayırt edemezdi (S2b planı Karar 11).</summary>
    [Fact]
    public async Task Yeniden_yazim_bagi_okuduktan_sonra_bag_bosalirsa_yazmaz()
    {
        var s = await SeedAsync();
        var cut = false;
        BetweenLinkAndChainRead(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.ShopperBroadcasterLinks.Where(l => l.ShopperId == s.ShopperId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.WpfCustomerId, (Guid?)null))).Should().Be(1);
            cut = true;
        });

        var changed = await RederiveAsync(s);

        cut.Should().BeTrue("kanca bağı bağlantı okumasıyla zincir okuması arasında boşaltmalı");
        changed.Should().Be(0);
        (await PaymentAsync(s.PaymentId)).WpfCustomerId.Should().BeNull("bağ okumadan sonra kesildi; dekonta yazılmaz");
    }

    /// <summary>Hesap silme talebi (<c>DeleteMe</c>: shopper'a <c>DeletedAt</c>, bağlantıya <c>LeftAt</c> yazar, bağlantının
    /// müşterisine dokunmaz) yazımın okumasından sonra commit olur. Bağlantı koşulu tutar (türetme gibi <c>LeftAt</c>'e bakmaz),
    /// yalnız shopper koşulu tutmaz; koşullu UPDATE hiçbir satır yazmaz. Koşul olmasa P silinmiş hesabın dekontunda purge'e kadar
    /// kalırdı (S2b planı Karar 11).</summary>
    [Fact]
    public async Task Yeniden_yazim_bagi_okuduktan_sonra_hesap_silinirse_yazmaz()
    {
        var s = await SeedAsync();
        var deleted = false;
        BetweenLinkAndChainRead(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var now = DateTimeOffset.UtcNow;
            (await db.Shoppers.Where(x => x.Id == s.ShopperId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DeletedAt, now))).Should().Be(1);
            (await db.ShopperBroadcasterLinks.Where(l => l.ShopperId == s.ShopperId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.LeftAt, now))).Should().Be(1);
            deleted = true;
        });

        var changed = await RederiveAsync(s);

        deleted.Should().BeTrue("kanca silme talebini bağlantı okumasıyla zincir okuması arasında commit ettirmeli");
        changed.Should().Be(0);
        (await PaymentAsync(s.PaymentId)).WpfCustomerId.Should().BeNull("silinmiş hesabın dekontuna yazılmaz");
        (await LinkCustomerAsync(s.ShopperId)).Should().Be(s.CustomerId, "bağ yerinde: yalnız shopper koşulu tutmadı");
    }

    private async Task<Guid?> LinkCustomerAsync(Guid shopperId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return (await db.ShopperBroadcasterLinks.AsNoTracking().SingleAsync(l => l.ShopperId == shopperId)).WpfCustomerId;
    }

    /// <summary>Araya giren onay (ayrı bağlamda <c>Status = Approved</c>) yazımı düşürmez: koşul <c>Status</c>'a bakmaz,
    /// yazım jetonu atlar. <c>Status</c> ve <c>UpdatedAt</c> (payments/since imleci) yazılmaz.</summary>
    [Fact]
    public async Task Yeniden_yazim_araya_giren_onayla_dusmez()
    {
        var s = await SeedAsync();
        var approved = false;
        BetweenLinkAndChainRead(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.Payments.Where(p => p.Id == s.PaymentId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentStatus.Approved))).Should().Be(1);
            approved = true;
        });

        var changed = await RederiveAsync(s);

        approved.Should().BeTrue("kanca onayı bağlantı okumasıyla zincir okuması arasında commit ettirmeli");
        changed.Should().Be(1);
        var payment = await PaymentAsync(s.PaymentId);
        payment.WpfCustomerId.Should().Be(s.CustomerId);
        payment.Status.Should().Be(PaymentStatus.Approved, "yazım Status'u yazmaz");
        payment.UpdatedAt.Should().Be(s.At, "payments/since imleci UpdatedAt'tir");
    }
}
