using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// "@" ikizi kipi (<c>merge-customer-identities --at-twins</c>): 2026-08-05'ten
/// v0.9.9'a dek Instagram resmi API yolu adı "@ad" diye yazdı, form "ad" — aynı
/// kişinin anahtarı farklı iki asıl kaydı oldu. Kip yalnız IG/TT/FB'de baştaki
/// '@'leri atılmış anahtarla gruplar ve "@"sız yazımı asıl kayıt yapar.
///
/// <para>Şema BUGÜNKÜ (B1 tekil indeksi yerinde — <see cref="PreB1Schema"/>
/// UYGULANMAZ): ikizlerin kimlik anahtarı farklı olduğundan indeks ikisine de
/// izin verir; kip prod'da bu şemada koşar. Her test METODU kendi
/// veritabanını alır.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerIdentityMergeJobAtTwinsTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public CustomerIdentityMergeJobAtTwinsTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        var cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(cs);
        _ = _factory.Services; // göçleri (B1 indeksi dahil) şimdi kur
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private static async Task<Guid> NewLicenseAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"musteri-{Guid.NewGuid():N}@example.test",
            Name = "Ikiz Birleştirme Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "atw-" + Guid.NewGuid().ToString("N")[..12],
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

    private static WpfCustomerProjection Row(
        Guid license, string username, DateTimeOffset updatedAt, string platform = "instagram") => new()
    {
        Id = Guid.NewGuid(), LicenseId = license, Platform = platform, Username = username, UpdatedAt = updatedAt,
    };

    private static Order OrderFor(Guid license, WpfCustomerProjection p, DateTimeOffset addedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = license, CustomerId = p.Id.ToString("N"), Platform = p.Platform,
        Username = p.Username, MessageText = "A1", Price = 10, AddedAt = addedAt, UpdatedAt = addedAt,
    };

    private static Shipment ShipmentFor(Guid license, WpfCustomerProjection p, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), LicenseId = license, CustomerId = p.Id.ToString("N"), CumulativeAmount = 10,
        CreatedAt = at, UpdatedAt = at,
    };

    private static CustomerIdentityMergeJob Job(LicenseDbContext db) => new(db, new CustomerIdentityMerger(db));

    [Theory]
    [InlineData("ayse", "ayse")]
    [InlineData("@ayse", "ayse")]
    [InlineData(" @@AYSE ", "ayse")]
    [InlineData("@İrem", "irem")]
    [InlineData("@", "@")]   // yalnız '@': kırpılmamış anahtar — "@@" ile çakışmaz
    [InlineData("@@", "@@")]
    [InlineData("   ", "")]  // boş anahtar boş kalır: gruplanmaz
    public void Ikiz_anahtari_bastaki_at_isaretlerini_atar_yalniz_at_kalirsa_anahtari_korur(string username, string expected)
        => CustomerIdentityMergeJob.AtTwinKeyOf(username).Should().Be(expected);

    [Fact]
    public async Task Kuru_calistirma_yalniz_IG_TT_FB_ikizlerini_sayar_hicbir_sey_yazmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        db.WpfCustomerProjections.AddRange(
            Row(lic, "ayse", t0), Row(lic, "@ayse", t0.AddDays(1)),
            // Platform harf farkı: ikisi de tiktok; kullanıcı adı harf farkı da aynı kişi.
            Row(lic, "@Mehmet", t0, "TikTok"), Row(lic, "mehmet", t0.AddDays(1), "tiktok"),
            Row(lic, "@ali", t0, "facebook"), Row(lic, "ali", t0.AddDays(1), "facebook"),
            // YouTube hiç gruplanmaz: oradaki ad kanal kimliği.
            Row(lic, "@h", t0, "youtube"), Row(lic, "h", t0.AddDays(1), "youtube"),
            Row(lic, "tekil", t0));
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: false, default, atTwins: true);

        report.Groups.Should().Be(3);
        report.GroupsByPlatform.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["facebook"] = 1, ["instagram"] = 1, ["tiktok"] = 1 });
        report.CopyRows.Should().Be(3);
        report.VariantGroups.Should().Be(3);
        report.FailedGroups.Should().Be(0);
        (await Job(db).RunAsync(lic, apply: false, default)).Groups.Should().Be(0,
            "olağan kip ikizleri görmez: kimlik anahtarları farklı");
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.IgnoreQueryFilters().CountAsync(p => p.MergedIntoId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Uygulama_at_isaretsiz_yazimi_asil_yapar_siparisi_daha_eski_olsa_da_siparis_kargo_tasinir_alan_dolar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        // "@"lı satır olağan kuralların hepsinde kazanırdı: en erken sipariş ve en eski UpdatedAt onda.
        var at = Row(lic, "@zeynep", t0);
        at.Notes = "kapıda ödeme";
        var bare = Row(lic, "zeynep", t0.AddDays(5));
        bare.FullName = "Örnek Müşteri";
        db.WpfCustomerProjections.AddRange(at, bare);
        db.Orders.AddRange(OrderFor(lic, at, t0), OrderFor(lic, bare, t0.AddDays(3)));
        db.Shipments.Add(ShipmentFor(lic, at, t0.AddDays(1)));
        await db.SaveChangesAsync();
        var job = Job(db);

        var report = await job.RunAsync(lic, apply: true, default, atTwins: true);

        report.Groups.Should().Be(1);
        report.CopyRows.Should().Be(1);
        report.OrdersToMove.Should().Be(1);
        report.ShipmentsToMove.Should().Be(1);
        report.FailedGroups.Should().Be(0);
        db.ChangeTracker.Clear();
        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == bare.Id);
        canonical.Username.Should().Be("zeynep");
        canonical.FullName.Should().Be("Örnek Müşteri");
        canonical.Notes.Should().Be("kapıda ödeme", "yalnız kopyada olan alan asıl kayda dolar");
        var copy = await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == at.Id);
        copy.MergedIntoId.Should().Be(bare.Id);
        var bareHex = bare.Id.ToString("N");
        var atHex = at.Id.ToString("N");
        (await db.Orders.CountAsync(o => o.CustomerId == bareHex)).Should().Be(2);
        (await db.Orders.CountAsync(o => o.CustomerId == atHex)).Should().Be(0);
        (await db.Shipments.CountAsync(s => s.CustomerId == bareHex)).Should().Be(1);
        (await db.Shipments.CountAsync(s => s.CustomerId == atHex)).Should().Be(0);

        // CLI'nin son koşulları bu kipte de tutar.
        (await job.CountMismatchedKeysAsync(default)).Should().Be(0);
        (await job.CountDuplicateHeadsAsync(default)).Should().Be(0);
        (await job.CountChainsAsync(default)).Should().Be(0);
        (await Job(db).RunAsync(lic, apply: true, default, atTwins: true)).Groups.Should().Be(0, "idempotent");
    }

    [Fact]
    public async Task Yalniz_at_isaretinden_olusan_ad_hicbir_seyle_gruplanmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var onlyAt = new[] { Row(lic, "@", t0), Row(lic, "@@", t0), Row(lic, " @@@ ", t0), Row(lic, "   ", t0), Row(lic, " ", t0) };
        db.WpfCustomerProjections.AddRange(onlyAt);
        db.WpfCustomerProjections.AddRange(Row(lic, "@x", t0), Row(lic, "x", t0));
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default, atTwins: true);

        report.Groups.Should().Be(1, "yalnız x/@x; '@', '@@', '@@@' ve boş adlar birbirine katılmaz");
        db.ChangeTracker.Clear();
        var ids = onlyAt.Select(r => r.Id).ToList();
        (await db.WpfCustomerProjections.IgnoreQueryFilters().CountAsync(p => ids.Contains(p.Id) && p.MergedIntoId == null))
            .Should().Be(onlyAt.Length);
    }

    [Fact]
    public async Task Grup_numarasi_celiskisi_sayilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        static string NewGroup() => Guid.NewGuid().ToString("N");
        var shared = NewGroup();
        var a1 = Row(lic, "g1", t0); a1.GroupId = NewGroup();
        var a2 = Row(lic, "@g1", t0); a2.GroupId = NewGroup();           // iki numara, farklı → çelişki
        var b1 = Row(lic, "g2", t0); b1.GroupId = shared;
        var b2 = Row(lic, "@g2", t0); b2.GroupId = " " + shared + " ";   // kenar boşluğu farkı çelişki değil
        var c1 = Row(lic, "g3", t0); c1.GroupId = NewGroup();
        var c2 = Row(lic, "@g3", t0);                                     // bir taraf boş → çelişki değil
        db.WpfCustomerProjections.AddRange(a1, a2, b1, b2, c1, c2);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: false, default, atTwins: true);

        report.Groups.Should().Be(3);
        report.GroupConflicts.Should().Be(1);
    }

    [Fact]
    public async Task CLI_govdesi_kipi_gosterir_0_doner_ciktida_kisisel_veri_yok()
    {
        Guid lic, bareId, atId;
        var username = "u" + Guid.NewGuid().ToString("N")[..10];
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            lic = await NewLicenseAsync(db);
            var t0 = DateTimeOffset.UtcNow.AddDays(-10);
            var at = Row(lic, "@" + username, t0);
            var bare = Row(lic, username, t0.AddDays(1));
            db.WpfCustomerProjections.AddRange(at, bare);
            await db.SaveChangesAsync();
            (bareId, atId) = (bare.Id, at.Id);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await MergeCustomerIdentities.RunAsync(
                db, lic, apply: true, output, error, NullLoggerFactory.Instance, default, atTwins: true);
            var text = output + Environment.NewLine + error;

            exit.Should().Be(0, text);
            text.Should().Contain("@ ikizi kipi").And.Contain("kopyalı kişi=1 (instagram=1)")
                .And.Contain("grup numarası=0").And.Contain(lic.ToString());
            text.Should().NotContainEquivalentOf(username, "çıktıda yalnız sayılar ve lisans Id'leri olur");
            db.ChangeTracker.Clear();
            (await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == atId))
                .MergedIntoId.Should().Be(bareId);
        }
    }
}
