using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// A7: aynı kişinin birden çok asıl kaydını tek kayıtta toplayan bir kerelik
/// iş. Gerçek SQL Server gerekir: iş grup başına işlem açar, onarım işi ve
/// testlerin anahtar bozması <c>ExecuteUpdate</c> kullanır (InMemory ikisini de
/// desteklemiyor). <see cref="IAsyncLifetime"/> her test METODUNA kendi
/// veritabanını verir — <c>CountMismatchedKeysAsync</c> tüm lisansları saysa da
/// başka bir testin bozduğu satırı görmez. Şema B1 öncesi
/// (<see cref="PreB1Schema"/>): iş prod'da orada koşar; B1'den sonraki kullanımı
/// (onarımın çakışma politikası) IdentityKeyRepairJobTests'te gerçek indeksle.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerIdentityMergeJobTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;
    public CustomerIdentityMergeJobTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
        await PreB1Schema.ApplyAsync(_factory, _cs);
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    /// <summary>Lisans tohumu: IdentityKeyRepairJobTests'teki doğrudan
    /// Customer+License deseni (HTTP kaydı gerekmez). Değerler üretilmiş.</summary>
    private static async Task<Guid> NewLicenseAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"musteri-{Guid.NewGuid():N}@example.test",
            Name = "Birleştirme İşi Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "cmj-" + Guid.NewGuid().ToString("N")[..12],
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
        Guid license, string username, DateTimeOffset updatedAt, string platform = "tiktok") => new()
    {
        Id = Guid.NewGuid(), LicenseId = license, Platform = platform, Username = username, UpdatedAt = updatedAt,
    };

    private static Order OrderFor(Guid license, WpfCustomerProjection p, DateTimeOffset addedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = license, CustomerId = p.Id.ToString("N"), Platform = p.Platform,
        Username = p.Username, MessageText = "A1", Price = 10, AddedAt = addedAt, UpdatedAt = addedAt,
    };

    private CustomerIdentityMergeJob Job(LicenseDbContext db) => new(db, new CustomerIdentityMerger(db));

    [Fact]
    public async Task Kuru_calistirma_hicbir_sey_yazmaz_ve_sayilari_raporlar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var oldest = Row(lic, "ayse", t0.AddDays(5));
        var newer = Row(lic, "AYSE", t0.AddDays(9));
        db.WpfCustomerProjections.AddRange(oldest, newer, Row(lic, "mehmet", t0));
        db.Orders.AddRange(OrderFor(lic, oldest, t0), OrderFor(lic, newer, t0.AddDays(1)));
        db.CustomerBalances.Add(new CustomerBalance { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = newer.Id, Balance = 30m, UpdatedAt = t0 });
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: false, default);

        report.Groups.Should().Be(1);
        report.CopyRows.Should().Be(1);
        report.OrdersToMove.Should().Be(1);
        report.BalancesToSum.Should().Be(1);
        report.FailedGroups.Should().Be(0);
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.IgnoreQueryFilters().CountAsync(p => p.MergedIntoId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Gercek_calistirma_en_eski_siparisli_kaydi_asil_yapar_doldurur_ve_idempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var oldest = Row(lic, "ayse", t0.AddDays(5));
        oldest.FullName = "Örnek Müşteri";
        var newer = Row(lic, "AYSE", t0.AddDays(9));
        newer.Address = "İzmir adresi";
        db.WpfCustomerProjections.AddRange(oldest, newer);
        // En eski SİPARİŞ "oldest"ta → asıl kayıt o (UpdatedAt'e bakılmaz).
        db.Orders.AddRange(OrderFor(lic, oldest, t0), OrderFor(lic, newer, t0.AddDays(1)));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(lic, apply: true, default);
        db.ChangeTracker.Clear();

        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == oldest.Id);
        canonical.FullName.Should().Be("Örnek Müşteri");
        canonical.Address.Should().Be("İzmir adresi");
        var copy = await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == newer.Id);
        copy.MergedIntoId.Should().Be(oldest.Id);
        copy.Address.Should().BeNull();
        (await db.Orders.CountAsync(o => o.CustomerId == oldest.Id.ToString("N"))).Should().Be(2);

        (await Job(db).RunAsync(lic, apply: true, default)).Groups.Should().Be(0);
    }

    [Fact]
    public async Task Takma_ad_yedegi_olan_asil_kayit_kopyanin_gercek_adini_alir()
    {
        // E2'de sunucuda DisplayName yok; eski sürüm takma ad (= kullanıcı adı) göndermişti.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var canonical = Row(lic, "ayse_tt", t0);
        canonical.FullName = "ayse_tt";
        var copy = Row(lic, "AYSE_TT", t0.AddDays(1));
        copy.FullName = "Örnek Müşteri";
        db.WpfCustomerProjections.AddRange(canonical, copy);
        db.Orders.Add(OrderFor(lic, canonical, t0));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(lic, apply: true, default);
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.SingleAsync(p => p.Id == canonical.Id)).FullName.Should().Be("Örnek Müşteri");
    }

    [Fact]
    public async Task Kopyaya_yonlenmis_eski_kopya_asil_kayda_yonlenir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var a = Row(lic, "zeynep", t0);
        var b = Row(lic, "Zeynep", t0.AddDays(1));
        var x = Row(lic, "ZEYNEP", t0.AddDays(2));
        x.MergedIntoId = b.Id; // A5'in canlıda açtığı kopya, şimdi kopyaya dönecek satıra bağlı
        db.WpfCustomerProjections.AddRange(a, b, x);
        db.Orders.Add(OrderFor(lic, a, t0));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(lic, apply: true, default);
        db.ChangeTracker.Clear();
        var all = await db.WpfCustomerProjections.IgnoreQueryFilters().Where(p => p.LicenseId == lic).ToListAsync();
        all.Single(p => p.Id == b.Id).MergedIntoId.Should().Be(a.Id);
        all.Single(p => p.Id == x.Id).MergedIntoId.Should().Be(a.Id); // zincir yok
        // Kopyaya dönen satırların UpdatedAt'i korunur: PR-1 tarafında okuyan yok;
        // elle eski bir imaja dönülürse eski `since` onları yeniden dağıtmasın.
        all.Single(p => p.Id == b.Id).UpdatedAt.Should().BeCloseTo(t0.AddDays(1), TimeSpan.FromMilliseconds(1));
        all.Single(p => p.Id == x.Id).UpdatedAt.Should().BeCloseTo(t0.AddDays(2), TimeSpan.FromMilliseconds(1));
        (await Job(db).CountChainsAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Zincir_sayaci_kopyanin_kopyasini_sayar()
    {
        // B1 kapısı gibi BİREBİR SQL: kopyası da kopya olan satır (bir yarışın
        // bıraktığı zincir) — iş yalnız birleştirdiği grubu düzleştirir.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var root = Row(lic, "irmak", t0);
        var middle = Row(lic, "Irmak", t0);
        middle.MergedIntoId = root.Id;
        var tail = Row(lic, "IRMAK", t0);
        tail.MergedIntoId = middle.Id;
        db.WpfCustomerProjections.AddRange(root, middle, tail);
        await db.SaveChangesAsync();

        (await Job(db).CountChainsAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task Saklanan_anahtari_bozuk_satir_da_hesaplanan_anahtarla_gruplanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var a = Row(lic, "mehmet", t0);
        var b = Row(lic, "Mehmet", t0.AddDays(1));
        db.WpfCustomerProjections.AddRange(a, b);
        db.Orders.Add(OrderFor(lic, a, t0));
        await db.SaveChangesAsync();
        // Geri alınmış deploy'un NEWID varsayılanı gibi: saklı anahtar bozuk.
        await db.WpfCustomerProjections.Where(p => p.Id == b.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.IdentityKey, Guid.NewGuid().ToString()));

        var report = await Job(db).RunAsync(lic, apply: true, default);
        report.Groups.Should().Be(1);
    }

    [Fact]
    public async Task Bos_hesaplanan_anahtarli_satirlar_birlestirilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        db.WpfCustomerProjections.AddRange(Row(lic, "   ", t0), Row(lic, " ", t0.AddDays(1)));
        await db.SaveChangesAsync();

        (await Job(db).RunAsync(lic, apply: true, default)).Groups.Should().Be(0);
    }

    [Fact]
    public async Task Silinmis_kopya_kisinin_tamamini_siler_en_erken_tarihle()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var a = Row(lic, "fatma", t0);
        a.FullName = "Örnek Müşteri";
        var b = Row(lic, "Fatma", t0.AddDays(1));
        b.MarkPurged(t0.AddDays(2));
        db.WpfCustomerProjections.AddRange(a, b);
        db.Orders.Add(OrderFor(lic, a, t0));
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);
        report.PurgedGroups.Should().Be(1);
        db.ChangeTracker.Clear();
        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == a.Id);
        canonical.PurgedAt.Should().BeCloseTo(t0.AddDays(2), TimeSpan.FromMilliseconds(1));
        canonical.FullName.Should().BeNull();
    }

    [Fact]
    public async Task Lisanslar_birbirine_karismaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic1 = await NewLicenseAsync(db);
        var lic2 = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        db.WpfCustomerProjections.AddRange(Row(lic1, "ali", t0), Row(lic2, "ALI", t0));
        await db.SaveChangesAsync();

        (await Job(db).RunAsync(lic1, apply: true, default)).Groups.Should().Be(0);
        (await Job(db).RunAsync(lic2, apply: true, default)).Groups.Should().Be(0);
    }

    [Fact]
    public async Task Onarimdan_sonra_uyusmaz_anahtar_kalmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var p = Row(lic, "veli", DateTimeOffset.UtcNow);
        db.WpfCustomerProjections.Add(p);
        await db.SaveChangesAsync();
        await db.WpfCustomerProjections.Where(x => x.Id == p.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IdentityKey, Guid.NewGuid().ToString()));
        var job = Job(db);
        (await job.CountMismatchedKeysAsync(default)).Should().BeGreaterThan(0);

        await new IdentityKeyRepairJob(db, job, NullLogger<IdentityKeyRepairJob>.Instance).RunAsync(default);
        (await job.CountMismatchedKeysAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Cakisan_grup_geri_alinir_sayilir_is_devam_eder_yeniden_kosu_tamamlar()
    {
        Guid lic;
        WpfCustomerProjection a1, a2, b1, b2;
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            lic = await NewLicenseAsync(db);
            a1 = Row(lic, "hakan", t0);
            a2 = Row(lic, "Hakan", t0.AddDays(1));
            b1 = Row(lic, "irmak", t0);
            b2 = Row(lic, "Irmak", t0.AddDays(1));
            db.WpfCustomerProjections.AddRange(a1, a2, b1, b2);
            db.Orders.AddRange(
                OrderFor(lic, a1, t0), OrderFor(lic, a2, t0.AddDays(1)),
                OrderFor(lic, b1, t0), OrderFor(lic, b2, t0.AddDays(1)));
            await db.SaveChangesAsync();
        }

        // Eşzamanlı KVKK silmesi: iş a2'yi okuduktan SONRA, grubunu kaydetmeden
        // HEMEN ÖNCE ayrı bir bağlantıdan (sıraya dayalı, zamanlamaya değil).
        // a2'nin UPDATE'i PurgedAt jetonuna takılır.
        var purgedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var hook = new SaveHookInterceptor();
        await using var hooked = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(_cs).AddInterceptors(hook).Options);
        var fired = false;
        hook.BeforeSave = async () =>
        {
            if (fired || !hooked.ChangeTracker.Entries<WpfCustomerProjection>().Any(e => e.Entity.Id == a2.Id)) return;
            fired = true;
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE WpfCustomerProjections SET PurgedAt = @at WHERE Id = @id";
            cmd.Parameters.AddWithValue("@at", purgedAt);
            cmd.Parameters.AddWithValue("@id", a2.Id);
            await cmd.ExecuteNonQueryAsync();
        };

        var first = await new CustomerIdentityMergeJob(hooked, new CustomerIdentityMerger(hooked))
            .RunAsync(lic, apply: true, default);

        fired.Should().BeTrue();
        first.Groups.Should().Be(2);
        first.FailedGroups.Should().Be(1, "çakışan grup sayılır, iş durmaz");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var rows = await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.LicenseId == lic).ToDictionaryAsync(p => p.Id);
            // Çakışan grup BÜTÜNÜYLE geri alındı: yönlendirme yok, sipariş yerinde.
            rows[a2.Id].MergedIntoId.Should().BeNull();
            rows[a1.Id].PurgedAt.Should().BeNull();
            (await db.Orders.CountAsync(o => o.CustomerId == a2.Id.ToString("N"))).Should().Be(1);
            // Öteki grup birleşti.
            rows[b2.Id].MergedIntoId.Should().Be(b1.Id);
            (await db.Orders.CountAsync(o => o.CustomerId == b1.Id.ToString("N"))).Should().Be(2);

            // Yeniden koşu yalnız kalanı bulur ve tamamlar; arada silinen kopya
            // kişinin tamamını siler.
            var second = await Job(db).RunAsync(lic, apply: true, default);
            second.Groups.Should().Be(1);
            second.FailedGroups.Should().Be(0);
            second.PurgedGroups.Should().Be(1);
            db.ChangeTracker.Clear();
            (await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == a2.Id))
                .MergedIntoId.Should().Be(a1.Id);
            (await db.WpfCustomerProjections.SingleAsync(p => p.Id == a1.Id))
                .PurgedAt.Should().BeCloseTo(purgedAt, TimeSpan.FromMilliseconds(1));
            (await db.Orders.CountAsync(o => o.CustomerId == a1.Id.ToString("N"))).Should().Be(2);
            (await Job(db).RunAsync(lic, apply: true, default)).Groups.Should().Be(0);
        }
    }

    // ── A5c: Shopper'ın açtığı GEÇİCİ kayıt ─────────────────────────────────
    // Geçici satırın ad/telefon/adresi kaydolanın KENDİ beyanı, bağlantısı
    // kanıtsız. Yayıncının satırı varken asıl kayıt olamaz; ondan yalnız
    // DAMGALI birimler (yayıncının kararları) alınır, beyanı alınmaz;
    // silinmişliği kişiye yayılmaz; ondan taşınan bağlantı — ayrılmışlar dahil —
    // asıl kaydın (yayıncı kaynaklı) telefonuyla yeniden kanıt ister.

    private static string NewPhone() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    /// <summary>Shopper kaydının açtığı geçici satır (ShopperAuthController
    /// adım 8a ile aynı alanlar).</summary>
    private static WpfCustomerProjection ProvisionalRow(
        Guid license, string username, DateTimeOffset updatedAt, string phone) => new()
    {
        Id = Guid.NewGuid(), LicenseId = license, Platform = "tiktok", Username = username, UpdatedAt = updatedAt,
        FullName = "Shopper Beyanı", Phone = phone, Address = "Shopper adresi", CreatedByShopper = true,
    };

    /// <summary>Telefonu OTP ile doğrulanmış bir shopper ve
    /// <paramref name="boundTo"/>'ya bağlı bağlantısı (<paramref name="left"/>:
    /// shopper yayıncıdan ayrılmış, bağlantı yine de bağlı); bağlantı Id'sini
    /// döner. Kaydetmez — çağıran kendi tohumuyla birlikte kaydeder.</summary>
    private static Guid AddVerifiedShopperLink(
        LicenseDbContext db, Guid license, WpfCustomerProjection boundTo, string shopperPhone, bool left = false)
    {
        var shopper = new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Shopper Beyanı",
            Phone = shopperPhone,
            PhoneVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = $"hash-{Guid.NewGuid():N}",
            Address = "Shopper adresi",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var link = new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = license, Platform = boundTo.Platform,
            Username = boundTo.Username, WpfCustomerId = boundTo.Id, JoinedAt = DateTimeOffset.UtcNow,
            LeftAt = left ? DateTimeOffset.UtcNow : null,
        };
        db.Shoppers.Add(shopper);
        db.ShopperBroadcasterLinks.Add(link);
        return link.Id;
    }

    private static async Task<Guid?> LinkTargetAsync(LicenseDbContext db, Guid linkId)
        => (await db.ShopperBroadcasterLinks.AsNoTracking().SingleAsync(l => l.Id == linkId)).WpfCustomerId;

    [Fact]
    public async Task Gecici_satir_asil_kayit_olmaz_beyani_alan_kaynagi_olmaz_baglantisi_kanitsizsa_beklemeye_duser()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        // Yayıncının satırı: siparişli, telefonsuz (yayıncı kişinin telefonunu bilmiyor).
        var wpf = Row(lic, "gizem", t0.AddDays(3));
        wpf.FullName = "Yayıncının Kaydı";
        // Geçici satır her başka ölçütte kazanırdı: en eski UpdatedAt'i ve en
        // erken siparişi (eski ingest'le o Id'ye düşmüş) onda. Kaydolanın
        // telefonu doğrulanmış ama yayıncının bildiği telefon değil.
        var provisional = ProvisionalRow(lic, "Gizem", t0, NewPhone());
        db.WpfCustomerProjections.AddRange(wpf, provisional);
        db.Orders.AddRange(OrderFor(lic, provisional, t0), OrderFor(lic, wpf, t0.AddDays(1)));
        var linkId = AddVerifiedShopperLink(db, lic, provisional, provisional.Phone!);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.Groups.Should().Be(1);
        report.CopyRows.Should().Be(1);
        report.LinksToMove.Should().Be(1);
        report.LinksUnbound.Should().Be(1, "bağlantı asıl kaydın telefonuyla kanıtlanamıyor");
        report.FailedGroups.Should().Be(0);
        db.ChangeTracker.Clear();

        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id);
        canonical.CreatedByShopper.Should().BeFalse();
        canonical.Phone.Should().BeNull("geçici satırın beyanı alan kaynağı değil — dolsaydı kanıt kendiliğinden geçerdi");
        canonical.Address.Should().BeNull();
        canonical.FullName.Should().Be("Yayıncının Kaydı");

        var copy = await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == provisional.Id);
        copy.MergedIntoId.Should().Be(wpf.Id, "yayıncının satırı varken geçici satır asıl kayıt olamaz");
        copy.Phone.Should().BeNull();
        copy.FullName.Should().BeNull();
        copy.CreatedByShopper.Should().BeTrue("bayrak köken olarak kalır");

        (await LinkTargetAsync(db, linkId)).Should().BeNull("kanıtsız bağlantı beklemeye düşer");
        (await db.Orders.CountAsync(o => o.CustomerId == wpf.Id.ToString("N"))).Should().Be(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Geciciden_tasinan_baglanti_yayincinin_telefonuyla_yeniden_kanitlanir(bool samePhone)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var broadcasterPhone = NewPhone();
        var wpf = Row(lic, "kemal", t0);
        wpf.Phone = broadcasterPhone;
        var shopperPhone = samePhone ? broadcasterPhone : NewPhone();
        var provisional = ProvisionalRow(lic, "KEMAL", t0.AddDays(1), shopperPhone);
        db.WpfCustomerProjections.AddRange(wpf, provisional);
        db.Orders.Add(OrderFor(lic, wpf, t0));
        var linkId = AddVerifiedShopperLink(db, lic, provisional, shopperPhone);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.LinksUnbound.Should().Be(samePhone ? 0 : 1);
        db.ChangeTracker.Clear();
        (await LinkTargetAsync(db, linkId)).Should().Be(samePhone ? wpf.Id : null,
            "bağlantı yalnız yayıncının telefonu doğrulanmış shopper telefonuyla eşleşirse asıl kayda bağlı kalır");
        (await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id)).Phone.Should().Be(broadcasterPhone);
    }

    [Fact]
    public async Task Silinmis_gecici_kopya_kisiyi_silmez()
    {
        // Sahte bir hesabın KVKK silmesi gerçek müşterinin asıl kaydını silmesin.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var wpf = Row(lic, "derya", t0);
        wpf.FullName = "Örnek Müşteri";
        var provisional = ProvisionalRow(lic, "DERYA", t0.AddDays(1), NewPhone());
        provisional.MarkPurged(t0.AddDays(2));
        db.WpfCustomerProjections.AddRange(wpf, provisional);
        db.Orders.Add(OrderFor(lic, wpf, t0));
        await db.SaveChangesAsync();

        var dryRun = await Job(db).RunAsync(lic, apply: false, default);
        dryRun.PurgedGroups.Should().Be(0);
        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.Groups.Should().Be(1);
        report.PurgedGroups.Should().Be(0, "geçici satırın silinmişliği kişiye yayılmaz");
        db.ChangeTracker.Clear();
        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id);
        canonical.PurgedAt.Should().BeNull();
        canonical.FullName.Should().Be("Örnek Müşteri");
        var copy = await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == provisional.Id);
        copy.MergedIntoId.Should().Be(wpf.Id);
        copy.PurgedAt.Should().BeCloseTo(t0.AddDays(2), TimeSpan.FromMilliseconds(1), "kopyanın kendi silme damgasına dokunulmaz");
    }

    [Fact]
    public async Task Yeniden_kanit_kopyalarin_islenme_sirasina_bagli_degil()
    {
        // Geçici kopya en yeni UpdatedAt'li: kopyalar yeniden eskiye gezildiğinden
        // ilk o işlenir. Asıl kaydın telefonu ondan SONRA işlenen yayıncı
        // kopyasından dolar. Kanıt asıl kaydın SON telefonuna karşı verilir.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var phone = NewPhone();
        var wpf = Row(lic, "selin", t0);
        var broadcasterCopy = Row(lic, "Selin", t0.AddDays(1));
        broadcasterCopy.Phone = phone;
        var provisional = ProvisionalRow(lic, "SELIN", t0.AddDays(5), phone);
        db.WpfCustomerProjections.AddRange(wpf, broadcasterCopy, provisional);
        db.Orders.Add(OrderFor(lic, wpf, t0));
        var linkId = AddVerifiedShopperLink(db, lic, provisional, phone);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.CopyRows.Should().Be(2);
        report.LinksUnbound.Should().Be(0);
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id)).Phone.Should().Be(phone);
        (await LinkTargetAsync(db, linkId)).Should().Be(wpf.Id);
    }

    [Fact]
    public async Task Kanittan_sonra_islenen_damgali_telefon_baglantiyi_bagli_birakmaz()
    {
        // Güvensiz yön: geçici kopya önce işlenir ve o anki telefon (A) kanıtı
        // geçirir; sonra işlenen yayıncı kopyasının DAMGALI telefonu (B) asıl
        // kaydınkini ezer. Kanıt ara durumdan verilseydi bağlantı, telefonu artık
        // B olan kayda A'nın sahibi olarak bağlı kalırdı.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var oldPhone = NewPhone();
        var newPhone = NewPhone();
        var wpf = Row(lic, "cem", t0);
        wpf.Phone = oldPhone;
        var stampedCopy = Row(lic, "Cem", t0.AddDays(1));
        stampedCopy.Phone = newPhone;
        stampedCopy.PhoneChangedAt = t0.AddDays(1);
        var provisional = ProvisionalRow(lic, "CEM", t0.AddDays(5), oldPhone);
        db.WpfCustomerProjections.AddRange(wpf, stampedCopy, provisional);
        db.Orders.Add(OrderFor(lic, wpf, t0));
        var linkId = AddVerifiedShopperLink(db, lic, provisional, oldPhone);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.LinksUnbound.Should().Be(1);
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id)).Phone.Should().Be(newPhone);
        (await LinkTargetAsync(db, linkId)).Should().BeNull(
            "kanıt asıl kaydın kaydedilen telefonuna karşı verilir, ara durumuna değil");
    }

    [Fact]
    public async Task Kisi_silinirse_geciciden_tasinan_baglanti_beklemeye_duser()
    {
        // Kanıt kaydedilen asıl kayda karşı: kişi silinince telefonu boşalır,
        // geçici kökenli bağlantı silinmiş kayda bağlı kalmaz.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var phone = NewPhone();
        var wpf = Row(lic, "burcu", t0);
        wpf.Phone = phone;
        var purgedCopy = Row(lic, "Burcu", t0.AddDays(1));
        purgedCopy.MarkPurged(t0.AddDays(2));
        var provisional = ProvisionalRow(lic, "BURCU", t0.AddDays(5), phone);
        db.WpfCustomerProjections.AddRange(wpf, purgedCopy, provisional);
        db.Orders.Add(OrderFor(lic, wpf, t0));
        var linkId = AddVerifiedShopperLink(db, lic, provisional, phone);
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.PurgedGroups.Should().Be(1);
        report.LinksUnbound.Should().Be(1);
        db.ChangeTracker.Clear();
        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id);
        canonical.PurgedAt.Should().NotBeNull();
        canonical.Phone.Should().BeNull();
        (await LinkTargetAsync(db, linkId)).Should().BeNull();
    }

    // ── A5c-düzeltme 2: damgalı kararlar her kopyadan; ayrılmış bağlantı ─────

    [Fact]
    public async Task Gecici_kopyanin_yalniz_damgali_kararlari_asil_kayda_gecer()
    {
        // Yayıncı geçici satırı (devralmadan önce) kendi Id'siyle kara listeye
        // almış, not düşmüş: damgalı kararlar. Telefon/adres kaydolanın damgasız
        // beyanı — geçmez.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var wpf = Row(lic, "nalan", t0);
        wpf.FullName = "Yayıncının Kaydı";
        var provisional = ProvisionalRow(lic, "NALAN", t0.AddDays(1), NewPhone());
        provisional.IsBlacklisted = true;
        provisional.BlacklistReason = "ödeme yapmadı";
        provisional.BlacklistedAt = t0.AddDays(2);
        provisional.BlacklistChangedAt = t0.AddDays(2);
        provisional.Notes = "kapıda teslim";
        provisional.NotesChangedAt = t0.AddDays(2);
        db.WpfCustomerProjections.AddRange(wpf, provisional);
        db.Orders.Add(OrderFor(lic, wpf, t0));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(lic, apply: true, default);

        db.ChangeTracker.Clear();
        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id);
        canonical.IsBlacklisted.Should().BeTrue();
        canonical.BlacklistReason.Should().Be("ödeme yapmadı");
        canonical.Notes.Should().Be("kapıda teslim");
        canonical.Phone.Should().BeNull("damgasız beyan alan kaynağı değil");
        canonical.Address.Should().BeNull();
        canonical.FullName.Should().Be("Yayıncının Kaydı");
    }

    [Fact]
    public async Task Silinmis_kopyanin_kara_listesi_ve_notu_asil_kayda_gecer()
    {
        // Kişi silinir (silinmiş, geçici olmayan kopya); kara liste ve iş notu
        // KVKK silmesinde de kalan alanlar — birleştirmede kaybolmamalı.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var a = Row(lic, "oya", t0);
        a.FullName = "Örnek Müşteri";
        var b = Row(lic, "Oya", t0.AddDays(1));
        b.IsBlacklisted = true;
        b.BlacklistReason = "iade suistimali";
        b.BlacklistedAt = t0.AddDays(1);
        b.BlacklistChangedAt = t0.AddDays(1);
        b.Notes = "dikkat";
        b.NotesChangedAt = t0.AddDays(1);
        b.MarkPurged(t0.AddDays(2));
        db.WpfCustomerProjections.AddRange(a, b);
        db.Orders.Add(OrderFor(lic, a, t0));
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.PurgedGroups.Should().Be(1);
        db.ChangeTracker.Clear();
        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == a.Id);
        canonical.PurgedAt.Should().NotBeNull();
        canonical.FullName.Should().BeNull();
        canonical.IsBlacklisted.Should().BeTrue();
        canonical.BlacklistReason.Should().Be("iade suistimali");
        canonical.Notes.Should().Be("dikkat");
    }

    [Fact]
    public async Task Silinmis_gecici_kopyanin_damgali_bos_kisisel_birimi_asil_kaydi_silmez()
    {
        // Silinmiş kopyanın kişisel birimlerinde değer boş ama damga kaldı: bu
        // "damgalı boş" bilinçli silme değil. Asıl kaydın daha ESKİ damgalı
        // telefonunu silmemeli; damgalı kara listesi ise geçer. Geçici satırın
        // silinmişliği kişiye yayılmaz.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var phone = NewPhone();
        var wpf = Row(lic, "pelin", t0);
        wpf.Phone = phone;
        wpf.PhoneChangedAt = t0;
        var provisional = ProvisionalRow(lic, "PELIN", t0.AddDays(1), NewPhone());
        provisional.PhoneChangedAt = t0.AddDays(3);
        provisional.IsBlacklisted = true;
        provisional.BlacklistReason = "sahte sipariş";
        provisional.BlacklistedAt = t0.AddDays(2);
        provisional.BlacklistChangedAt = t0.AddDays(2);
        provisional.MarkPurged(t0.AddDays(4));
        db.WpfCustomerProjections.AddRange(wpf, provisional);
        db.Orders.Add(OrderFor(lic, wpf, t0));
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: true, default);

        report.PurgedGroups.Should().Be(0);
        db.ChangeTracker.Clear();
        var canonical = await db.WpfCustomerProjections.SingleAsync(p => p.Id == wpf.Id);
        canonical.PurgedAt.Should().BeNull();
        canonical.Phone.Should().Be(phone);
        canonical.IsBlacklisted.Should().BeTrue();
        canonical.BlacklistReason.Should().Be("sahte sipariş");
    }

    /// <summary>
    /// KRİTİK (A5c kalite incelemesi): geçici kopyadan taşınan AYRILMIŞ bağlantı
    /// da yeniden kanıt ister. ShopperPurgeService shopper'ın ayrılmış
    /// bağlantılarından da ulaştığı projeksiyonu siler: önce kaydolup ayrılan
    /// saldırganın kanıtsız bağlantısı asıl kayda bağlı kalsaydı KVKK silme
    /// talebi gerçek müşterinin kaydını silerdi.
    /// </summary>
    [Fact]
    public async Task Geciciden_tasinan_ayrilmis_baglanti_da_yeniden_kanitlanir_o_shopperin_silinmesi_asil_kayda_dokunmaz()
    {
        Guid lic, canonicalId, linkId;
        string broadcasterPhone;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            lic = await NewLicenseAsync(db);
            var t0 = DateTimeOffset.UtcNow.AddDays(-10);
            broadcasterPhone = NewPhone();
            var wpf = Row(lic, "sena", t0);
            wpf.FullName = "Gerçek Müşteri";
            wpf.Phone = broadcasterPhone;
            var provisional = ProvisionalRow(lic, "SENA", t0.AddDays(1), NewPhone());
            db.WpfCustomerProjections.AddRange(wpf, provisional);
            db.Orders.Add(OrderFor(lic, wpf, t0));
            linkId = AddVerifiedShopperLink(db, lic, provisional, provisional.Phone!, left: true);
            await db.SaveChangesAsync();
            canonicalId = wpf.Id;

            var report = await Job(db).RunAsync(lic, apply: true, default);

            report.LinksUnbound.Should().Be(1, "ayrılmış bağlantı da kanıt ister");
            db.ChangeTracker.Clear();
            (await LinkTargetAsync(db, linkId)).Should().BeNull();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopperId = await db.ShopperBroadcasterLinks.Where(l => l.Id == linkId).Select(l => l.ShopperId).SingleAsync();
            (await scope.ServiceProvider.GetRequiredService<ShopperPurgeService>().PurgeAsync(shopperId, default))
                .Should().NotBeNull();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var canonical = await db.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == canonicalId);
            canonical.PurgedAt.Should().BeNull("saldırganın KVKK silmesi gerçek müşterinin kaydını silmez");
            canonical.FullName.Should().Be("Gerçek Müşteri");
            canonical.Phone.Should().Be(broadcasterPhone);
        }
    }

    // ── A7-düzeltme: kuru çalıştırma raporu, B1 kapısı, hata yalıtımı, UpdatedAt ──

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.test";

    [Fact]
    public async Task Kuru_calistirma_platform_harf_farki_ve_alan_celiskilerini_sayar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var email = NewEmail();

        // 1) instagram, harf farkı: telefon ve not farklı; e-posta, ad, adres
        //    SameText'e göre aynı (adres bloğu uyumlu, ortak parça var).
        var a1 = Row(lic, "ayse", t0, "instagram");
        a1.Phone = NewPhone(); a1.Email = email; a1.FullName = "Örnek Müşteri"; a1.Notes = "not bir";
        a1.Address = "Atatürk Cd. 5"; a1.City = "İzmir";
        var a2 = Row(lic, "AYSE", t0.AddDays(1), "instagram");
        a2.Phone = NewPhone(); a2.Email = email.ToUpperInvariant() + " "; a2.FullName = "ÖRNEK MÜŞTERİ"; a2.Notes = "not iki";
        a2.Address = "atatürk cd. 5"; a2.District = "Bornova";
        // 2) instagram, aynı yazım (iki bilgisayar): ad, adres, e-posta farklı.
        var b1 = Row(lic, "mehmet", t0, "instagram");
        b1.FullName = "Birinci Müşteri"; b1.Address = "Cumhuriyet Cd. 1"; b1.Email = NewEmail();
        var b2 = Row(lic, "mehmet", t0.AddDays(1), "instagram");
        b2.FullName = "İkinci Müşteri"; b2.Address = "Gazi Cd. 2"; b2.Email = NewEmail();
        // 3) tiktok, boşluk farkı; asıl kayıtta telefon boş — çelişki değil.
        var c1 = Row(lic, "zeynep", t0);
        var c2 = Row(lic, " zeynep ", t0.AddDays(1));
        c2.Phone = NewPhone();
        db.WpfCustomerProjections.AddRange(a1, a2, b1, b2, c1, c2, Row(lic, "tekil", t0));
        db.Orders.AddRange(OrderFor(lic, a1, t0), OrderFor(lic, b1, t0), OrderFor(lic, c1, t0));
        await db.SaveChangesAsync();

        var report = await Job(db).RunAsync(lic, apply: false, default);

        report.Groups.Should().Be(3);
        report.GroupsByPlatform.Should().BeEquivalentTo(new Dictionary<string, int> { ["instagram"] = 2, ["tiktok"] = 1 });
        report.VariantGroups.Should().Be(2, "'ayse'/'AYSE' ve 'zeynep'/' zeynep '; 'mehmet' iki kez aynı yazım");
        report.PhoneConflicts.Should().Be(1);
        report.NotesConflicts.Should().Be(1);
        report.EmailConflicts.Should().Be(1, "harf ve kenar boşluğu farkı çelişki değil");
        report.NameConflicts.Should().Be(1, "'ÖRNEK MÜŞTERİ' ile 'Örnek Müşteri' aynı");
        report.AddressConflicts.Should().Be(1, "aynı satırı paylaşan uyumlu bloklar çelişmez");
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.IgnoreQueryFilters().CountAsync(p => p.MergedIntoId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Kuru_calistirma_tasinacak_iban_eslesme_hareket_ve_sohbetleri_sayar_uygulama_tasir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var canonical = Row(lic, "kerem", t0);
        var copy = Row(lic, "Kerem", t0.AddDays(1));
        db.WpfCustomerProjections.AddRange(canonical, copy);
        db.Orders.Add(OrderFor(lic, canonical, t0));
        BankTransaction BankTx() => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL",
            OccurredAt = t0, FetchedAt = t0,
        };
        PaymentMatch Match(BankTransaction tx, Guid? proposed, Guid? actual) => new()
        {
            Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, ProposedWpfCustomerId = proposed,
            ActualWpfCustomerId = actual, Layer = PaymentMatchLayer.UsernameInDescription, Confidence = 0.9m,
            Status = PaymentMatchStatus.Proposed, CreatedAt = t0, UpdatedAt = t0,
        };
        var tx1 = BankTx();
        var tx2 = BankTx();
        db.BankTransactions.AddRange(tx1, tx2);
        db.PaymentMatches.AddRange(Match(tx1, copy.Id, null), Match(tx2, null, copy.Id));
        db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = copy.Id,
            IbanHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            IbanMasked = "TR** **** **** 0001", LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = t0,
        });
        db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = copy.Id, Amount = 10m, Kind = "manual-adjustment",
            CreatedByCustomerId = Guid.NewGuid(), CreatedAt = t0,
        });
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = lic, CustomerPhone = NewPhone(), PhoneNumberId = $"pn-{Guid.NewGuid():N}",
            Status = "open", WpfCustomerId = copy.Id, CreatedAt = t0,
        });
        await db.SaveChangesAsync();

        var dryRun = await Job(db).RunAsync(lic, apply: false, default);

        dryRun.PaymentMatchesToMove.Should().Be(2, "önerilen de bağlanan da sayılır");
        dryRun.IbanMemoriesToMove.Should().Be(1);
        dryRun.BalanceTransactionsToMove.Should().Be(1);
        dryRun.WaConversationsToMove.Should().Be(1);

        (await Job(db).RunAsync(lic, apply: true, default)).FailedGroups.Should().Be(0);
        db.ChangeTracker.Clear();
        (await db.PaymentMatches.CountAsync(m => m.LicenseId == lic
            && (m.ProposedWpfCustomerId == canonical.Id || m.ActualWpfCustomerId == canonical.Id))).Should().Be(2);
        (await db.CustomerIbanMemories.CountAsync(m => m.WpfCustomerId == canonical.Id)).Should().Be(1);
        (await db.CustomerBalanceTransactions.CountAsync(t => t.WpfCustomerId == canonical.Id)).Should().Be(1);
        (await db.WaConversations.CountAsync(c => c.WpfCustomerId == canonical.Id)).Should().Be(1);
    }

    [Fact]
    public async Task B1_kapisi_SQL_kurallariyla_sayar_bos_anahtarli_ikizleri_saymaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var upper = Row(lic, "ali", t0, "TikTok");
        db.WpfCustomerProjections.AddRange(
            Row(lic, "   ", t0), Row(lic, " ", t0.AddDays(1)),           // anahtar boş: kimlik değil — iş birleştirmez, indeks kapsamaz
            upper, Row(lic, "ali", t0.AddDays(1), "tiktok"),              // platform harf farkı: ikisi için de aynı kişi
            Row(lic, "veli", t0, "tiktok"), Row(lic, "veli", t0, "tiktok ")); // sondaki boşluk: SQL'de aynı, işte ayrı platform
        db.Orders.Add(OrderFor(lic, upper, t0));
        await db.SaveChangesAsync();
        var job = Job(db);

        (await job.CountDuplicateHeadsAsync(default)).Should().Be(2, "ali ve veli; boş anahtarlı ikizler indeksin dışında");
        (await job.RunAsync(lic, apply: true, default)).Groups.Should().Be(1);
        (await job.CountDuplicateHeadsAsync(default)).Should().Be(1,
            "kapı SQL'in kuralıyla sayar: işin bellekteki gruplamasının kaçırdığını (veli) yakalar");
    }

    [Fact]
    public async Task Asil_kaydin_UpdatedAti_yalniz_bu_kosuda_silinirse_ilerler()
    {
        // Eski istemcinin `since` ingest'i kullanıcı adını harf duyarlı eşler:
        // ilerleyen UpdatedAt asıl kaydı, yalnız harf farklı kopyayı tutan
        // bilgisayara YENİ müşteri olarak indirirdi. Yalnız mezar taşı ulaşmalı.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var kept = Row(lic, "nur", t0);
        var keptCopy = Row(lic, "Nur", t0.AddDays(1));
        keptCopy.Address = "Kopyadaki adres";
        var nowPurged = Row(lic, "ece", t0);
        var purgingCopy = Row(lic, "Ece", t0.AddDays(1));
        purgingCopy.MarkPurged(t0.AddDays(2));
        var alreadyPurged = Row(lic, "eda", t0);
        alreadyPurged.MarkPurged(t0.AddDays(3));
        var earlierPurgedCopy = Row(lic, "Eda", t0.AddDays(1));
        earlierPurgedCopy.MarkPurged(t0.AddDays(2));
        db.WpfCustomerProjections.AddRange(kept, keptCopy, nowPurged, purgingCopy, alreadyPurged, earlierPurgedCopy);
        db.Orders.AddRange(OrderFor(lic, kept, t0), OrderFor(lic, nowPurged, t0), OrderFor(lic, alreadyPurged, t0));
        await db.SaveChangesAsync();
        var before = DateTimeOffset.UtcNow;

        (await Job(db).RunAsync(lic, apply: true, default)).FailedGroups.Should().Be(0);

        db.ChangeTracker.Clear();
        var heads = await db.WpfCustomerProjections.Where(p => p.LicenseId == lic).ToDictionaryAsync(p => p.Id);
        heads.Should().HaveCount(3);
        heads[kept.Id].Address.Should().Be("Kopyadaki adres");
        heads[kept.Id].UpdatedAt.Should().Be(t0, "alan doldurmak UpdatedAt'i ilerletmez");
        heads[nowPurged.Id].PurgedAt.Should().BeCloseTo(t0.AddDays(2), TimeSpan.FromMilliseconds(1));
        heads[nowPurged.Id].UpdatedAt.Should().BeOnOrAfter(before, "bu koşuda silinen kişinin mezar taşı eski istemcilere ulaşmalı");
        heads[alreadyPurged.Id].PurgedAt.Should().BeCloseTo(t0.AddDays(2), TimeSpan.FromMilliseconds(1), "ilk silme tarihi en erkeni");
        heads[alreadyPurged.Id].UpdatedAt.Should().Be(t0.AddDays(3), "zaten silinmişti — mezar taşı çoktan indi");
    }

    [Fact]
    public async Task Asil_kaydin_eszamanli_silinmesi_UpdatedAt_ilerlemese_de_yakalanir()
    {
        // Asıl kayda yazılacak alan yok ve UpdatedAt ilerlemiyor: jeton denetimi
        // (PurgedAt) yine yapılmalı, yoksa eşzamanlı KVKK silmesi fark edilmezdi.
        Guid lic;
        WpfCustomerProjection canonical, copy;
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            lic = await NewLicenseAsync(db);
            canonical = Row(lic, "baris", t0);
            copy = Row(lic, "Baris", t0.AddDays(1));
            db.WpfCustomerProjections.AddRange(canonical, copy);
            db.Orders.Add(OrderFor(lic, canonical, t0));
            await db.SaveChangesAsync();
        }
        var hook = new SaveHookInterceptor();
        await using var hooked = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(_cs).AddInterceptors(hook).Options);
        var fired = false;
        hook.BeforeSave = async () =>
        {
            if (fired || !hooked.ChangeTracker.Entries<WpfCustomerProjection>().Any(e => e.Entity.Id == canonical.Id)) return;
            fired = true;
            await ExecuteSqlAsync("UPDATE WpfCustomerProjections SET PurgedAt = SYSDATETIMEOFFSET() WHERE Id = @id",
                ("@id", canonical.Id));
        };

        var report = await new CustomerIdentityMergeJob(hooked, new CustomerIdentityMerger(hooked))
            .RunAsync(lic, apply: true, default);

        fired.Should().BeTrue();
        report.FailedGroups.Should().Be(1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == copy.Id))
                .MergedIntoId.Should().BeNull("grup geri alındı");
        }
    }

    [Fact]
    public async Task Eszamanlilik_disi_veritabani_hatasi_grubu_geri_alir_sayar_is_surer_gunluge_kisisel_veri_yazmaz()
    {
        // Eşzamanlı bir bakiye uygulaması asıl kaydın bakiye satırını tam o anda
        // açar: işin açtığı satır tekil indekse çarpar (2601) — DbUpdateException,
        // eşzamanlılık değil. Grup geri alınır, sayılır, iş sürer.
        Guid lic;
        WpfCustomerProjection a1, a2, b1, b2;
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            lic = await NewLicenseAsync(db);
            a1 = Row(lic, "tolga", t0);
            a2 = Row(lic, "Tolga", t0.AddDays(1));
            b1 = Row(lic, "ugur", t0);
            b2 = Row(lic, "Ugur", t0.AddDays(1));
            db.WpfCustomerProjections.AddRange(a1, a2, b1, b2);
            db.Orders.AddRange(OrderFor(lic, a1, t0), OrderFor(lic, b1, t0));
            db.CustomerBalances.Add(new CustomerBalance
                { Id = Guid.NewGuid(), LicenseId = lic, WpfCustomerId = a2.Id, Balance = 30m, UpdatedAt = t0 });
            await db.SaveChangesAsync();
        }
        var hook = new SaveHookInterceptor();
        await using var hooked = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(_cs).AddInterceptors(hook).Options);
        var fired = false;
        hook.BeforeSave = async () =>
        {
            if (fired || !hooked.ChangeTracker.Entries<CustomerBalance>()
                    .Any(e => e.State == EntityState.Added && e.Entity.WpfCustomerId == a1.Id)) return;
            fired = true;
            await ExecuteSqlAsync(
                "INSERT INTO CustomerBalances (Id, LicenseId, WpfCustomerId, Balance, UpdatedAt) VALUES (@id, @lic, @wpf, 5, SYSDATETIMEOFFSET())",
                ("@id", Guid.NewGuid()), ("@lic", lic), ("@wpf", a1.Id));
        };
        var log = new LogRecorder<CustomerIdentityMergeJob>();

        var first = await new CustomerIdentityMergeJob(hooked, new CustomerIdentityMerger(hooked), log)
            .RunAsync(lic, apply: true, default);

        fired.Should().BeTrue();
        first.Groups.Should().Be(2);
        first.FailedGroups.Should().Be(1);
        var entry = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain(nameof(DbUpdateException)).And.Contain("2601").And.Contain(lic.ToString());
        entry.Message.Should().NotContainEquivalentOf("tolga").And.NotContainEquivalentOf("ugur");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var rows = await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.LicenseId == lic).ToDictionaryAsync(p => p.Id);
            rows[a2.Id].MergedIntoId.Should().BeNull("hatalı grup bütünüyle geri alındı");
            (await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a2.Id)).Balance.Should().Be(30m);
            rows[b2.Id].MergedIntoId.Should().Be(b1.Id, "öteki grup birleşti");

            var second = await Job(db).RunAsync(lic, apply: true, default);
            second.Groups.Should().Be(1);
            second.FailedGroups.Should().Be(0);
            db.ChangeTracker.Clear();
            (await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a1.Id)).Balance.Should().Be(35m);
            (await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == a2.Id)).Balance.Should().Be(0m);
        }
    }

    [Fact]
    public async Task Esit_UpdatedAtli_kopyalar_Id_sirasiyla_gezilir()
    {
        // Kopyalar UpdatedAt'i en yeniden eskiye gezilir; eşitlikte Id. Bu iki
        // Id'de .NET sırası ile SQL Server'ın uniqueidentifier sırası TERS: sıra
        // sorgunun dönüş sırasına kalsaydı sonuç ötekisi olurdu.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = await NewLicenseAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var canonical = Row(lic, "cansu", t0);
        var firstEmail = NewEmail();
        var first = Row(lic, "Cansu", t0.AddDays(1));
        first.Id = Guid.Parse("00000001-0000-0000-0000-000000000002");
        first.Email = firstEmail;
        var second = Row(lic, "CANSU", t0.AddDays(1));
        second.Id = Guid.Parse("00000002-0000-0000-0000-000000000001");
        second.Email = NewEmail();
        db.WpfCustomerProjections.AddRange(canonical, second, first);
        db.Orders.Add(OrderFor(lic, canonical, t0));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(lic, apply: true, default);

        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.SingleAsync(p => p.Id == canonical.Id)).Email.Should().Be(firstEmail);
    }

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
