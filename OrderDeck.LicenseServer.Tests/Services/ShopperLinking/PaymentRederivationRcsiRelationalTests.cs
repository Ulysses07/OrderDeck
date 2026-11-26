using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperLinking;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperLinking;

/// <summary>
/// Dekontun müşterisini yazan iki deyimin koşulu (S2b planı Karar 11) RCSI açık veritabanında. Prod veritabanı ilk kurulumda
/// <c>EnsureCreated()</c> ile oluşturuldu ve EF Core onu oluştururken <c>READ_COMMITTED_SNAPSHOT</c>'ı açar; fikstürün
/// veritabanında RCSI kapalıdır, burada göçten önce açılır (<see cref="ReadCommittedSnapshot"/>). RCSI açıkken ipucusuz koşul
/// ifade başındaki anlık görüntüyü okur: bağı kesen işlem commit olmamışken koşan yazım eski bağlantıyı görürdü. Kesen işlem
/// ayrı bağlantıda açık tutulur; yazım ayrı görevde başlar, işlem 2 sn sonra commit olur. Yazım o ana kadar bitmemeli (koşul
/// tabloları kilitli okunur, kesen işlemin commit'ini bekler) ve kesmeyi görmeli. RCSI kapalı veritabanında üç test ipucusuz
/// kodda da geçer (okumalar kilitlidir, kesen işlemin kilidinde bekler); önkoşul bu yüzden her testin başında sınanır.
/// Değerler üretilir.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PaymentRederivationRcsiRelationalTests : IAsyncLifetime
{
    // Kesen işlemin commit'siz açık kaldığı süre: yazım bu sürede bitmemeli.
    private static readonly TimeSpan HeldOpen = TimeSpan.FromSeconds(2);

    private readonly SqlServerContainerFixture _sql;
    private string _connectionString = null!;
    private RelationalApiFactory _factory = null!;

    public PaymentRederivationRcsiRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connectionString = await _sql.CreateDatabaseAsync();
        // Fabrikadan (göç) önce: veritabanında başka oturum varken ALTER bekler.
        await ReadCommittedSnapshot.EnableAsync(_connectionString);
        _factory = new RelationalApiFactory(_connectionString);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record Seed(Guid LicenseId, Guid ShopperId, Guid CustomerId, Guid LinkId, Guid PaymentId);

    /// <summary>Lisans, telefonu doğrulanmış shopper, asıl kayıt P, shopper'ın P'ye bağlı bağlantısı ve dekontu: müşterisiz
    /// (bağ beklerken gönderilmiş) ya da <paramref name="paymentCarriesCustomer"/> ise P'de (gönderim yazmış).</summary>
    private async Task<Seed> SeedAsync(bool paymentCarriesCustomer)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"rcsi-{Guid.NewGuid():N}@x.test", Name = "Yayıncı",
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
            WpfCustomerId = paymentCarriesCustomer ? p.Id : null,
        };
        db.AddRange(customer, license, shopper, p, link, payment);
        await db.SaveChangesAsync();
        return new Seed(license.Id, shopper.Id, p.Id, link.Id, payment.Id);
    }

    /// <summary>Kesen işlem bağlantıyı siler (silmenin etkisi), commit etmez. Yeniden yazımın türetmesi bağlantıyı anlık
    /// görüntüden okur, koşulu ise kesen işlemin commit'ini bekler ve silinmiş bağlantıyı görür: hiçbir satır yazılmaz.
    /// İpucusuz koşul eski bağlantıyı görür, P'yi silinen bağın dekontuna yazardı.</summary>
    [Fact]
    public async Task Yeniden_yazim_commit_olmamis_baglanti_silmesini_bekler()
    {
        await RcsiShouldBeOnAsync();
        var s = await SeedAsync(paymentCarriesCustomer: false);
        await using var cutterScope = _factory.Services.CreateAsyncScope();
        var cutter = cutterScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await using var cut = await cutter.Database.BeginTransactionAsync();
        (await cutter.ShopperBroadcasterLinks.Where(l => l.Id == s.LinkId).ExecuteDeleteAsync()).Should().Be(1);

        var rederive = Task.Run(() => RederiveAsync(s));
        var finishedWhileOpen = await Task.WhenAny(rederive, Task.Delay(HeldOpen)) == rederive;
        await cut.CommitAsync();
        var changed = await rederive.WaitAsync(TimeSpan.FromSeconds(30));

        changed.Should().Be(0);
        (await PaymentCustomerAsync(s.PaymentId)).Should().BeNull("bağlantı silindi; dekonta yazılmaz");
        finishedWhileOpen.Should().BeFalse("koşul kilitli okunur: yazım kesen işlemin commit'ini bekler");
    }

    /// <summary>Kesen işlem shopper'a <c>DeletedAt</c> yazar (hesap silme talebinin etkisi; bağlantı yerinde), commit etmez.
    /// Yalnız shopper koşulu tutmaz; yazım kesen işlemin commit'ini bekler ve hiçbir satır yazmaz. İpucusuz koşul silinmemiş
    /// hesabı görür, P'yi silinen hesabın dekontuna yazardı.</summary>
    [Fact]
    public async Task Yeniden_yazim_commit_olmamis_hesap_silmesini_bekler()
    {
        await RcsiShouldBeOnAsync();
        var s = await SeedAsync(paymentCarriesCustomer: false);
        await using var cutterScope = _factory.Services.CreateAsyncScope();
        var cutter = cutterScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await using var cut = await cutter.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        (await cutter.Shoppers.Where(x => x.Id == s.ShopperId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DeletedAt, now))).Should().Be(1);

        var rederive = Task.Run(() => RederiveAsync(s));
        var finishedWhileOpen = await Task.WhenAny(rederive, Task.Delay(HeldOpen)) == rederive;
        await cut.CommitAsync();
        var changed = await rederive.WaitAsync(TimeSpan.FromSeconds(30));

        changed.Should().Be(0);
        (await PaymentCustomerAsync(s.PaymentId)).Should().BeNull("hesap silindi; dekonta yazılmaz");
        (await LinkCustomerAsync(s.LinkId)).Should().Be(s.CustomerId, "bağ yerinde: yalnız shopper koşulu tutmadı");
        finishedWhileOpen.Should().BeFalse("koşul kilitli okunur: yazım kesen işlemin commit'ini bekler");
    }

    /// <summary>Gönderim müşteriyi P'ye çözdü ve dekontu P ile kaydetti; kayıttan sonraki denetimden önce kesen işlem
    /// bağlantıyı siler, commit etmez. Denetim kesen işlemin commit'ini bekler, silinmiş bağlantıyı görür ve dekontun
    /// müşterisini boşaltır. İpucusuz koşul eski bağlantıyı görür, dekont silinen bağın müşterisinde kalırdı.</summary>
    [Fact]
    public async Task Gonderim_denetimi_commit_olmamis_baglanti_silmesini_bekler()
    {
        await RcsiShouldBeOnAsync();
        var s = await SeedAsync(paymentCarriesCustomer: true);
        var derived = await DeriveAsync(s);
        derived.Should().Be(new DerivedCustomer(s.CustomerId, s.LinkId, s.CustomerId), "gönderimin çözümü kesmeden önce");
        await using var cutterScope = _factory.Services.CreateAsyncScope();
        var cutter = cutterScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await using var cut = await cutter.Database.BeginTransactionAsync();
        (await cutter.ShopperBroadcasterLinks.Where(l => l.Id == s.LinkId).ExecuteDeleteAsync()).Should().Be(1);

        var check = Task.Run(() => ClearIfLinkChangedAsync(s, derived!.Value));
        var finishedWhileOpen = await Task.WhenAny(check, Task.Delay(HeldOpen)) == check;
        await cut.CommitAsync();
        await check.WaitAsync(TimeSpan.FromSeconds(30));

        (await PaymentCustomerAsync(s.PaymentId)).Should().BeNull(
            "bağlantı çözümden sonra silindi; dekont kesilen bağın müşterisinde kalmaz");
        finishedWhileOpen.Should().BeFalse("koşul kilitli okunur: denetim kesen işlemin commit'ini bekler");
    }

    /// <summary>Önkoşul: testin sınadığı okuma satır sürümlüdür, RCSI kapalı veritabanında test bir şey kanıtlamaz.</summary>
    private async Task RcsiShouldBeOnAsync()
        => (await ReadCommittedSnapshot.IsOnAsync(_connectionString)).Should().BeTrue("önkoşul: veritabanında RCSI açık");

    private async Task<int> RederiveAsync(Seed s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await ShopperCustomerResolver.RederivePaymentsAsync(db, s.ShopperId, [s.LicenseId], default);
    }

    private async Task<DerivedCustomer?> DeriveAsync(Seed s)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await ShopperCustomerResolver.DeriveAsync(db, s.LicenseId, s.ShopperId, default);
    }

    private async Task ClearIfLinkChangedAsync(Seed s, DerivedCustomer derived)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await ShopperCustomerResolver.ClearIfLinkChangedAsync(db, s.PaymentId, s.ShopperId, derived, default);
    }

    private async Task<Guid?> PaymentCustomerAsync(Guid paymentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => p.WpfCustomerId).SingleAsync();
    }

    private async Task<Guid?> LinkCustomerAsync(Guid linkId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.ShopperBroadcasterLinks.AsNoTracking().Where(l => l.Id == linkId)
            .Select(l => l.WpfCustomerId).SingleAsync();
    }
}
