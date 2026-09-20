using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Spec §2.3: tek seferlik doğrulama yetmez — abonelik biter, şifre döner.
/// Günlük iş her doğrulanmış hesabı tekrar sınar. Asıl tehlike aşırı tepki:
/// İYS'nin geçici arızasında bütün yayıncıları kapatmak.
/// </summary>
public sealed class NetgsmAccountVerifyJobTests : IDisposable
{
    private readonly List<JobFactory> _factories = new();
    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private sealed class StubIysClient : IIysClient
    {
        /// <summary>Marka kodu → o marka için verilecek cevap.</summary>
        public Dictionary<string, Func<IysSearchResult>> ByBrand { get; } = new();

        public Func<IysSearchResult> Default { get; set; } =
            () => new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());

        public List<string> Asked { get; } = new();

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            Asked.Add(account.BrandCode);
            var fn = ByBrand.TryGetValue(account.BrandCode, out var f) ? f : Default;
            return Task.FromResult(fn());
        }
    }

    private sealed class JobFactory : ApiFactory
    {
        public StubIysClient Iys { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IIysClient>();
                s.AddSingleton<IIysClient>(Iys);
            });
        }
    }

    private JobFactory NewFactory()
    {
        var f = new JobFactory();
        _factories.Add(f);
        return f;
    }

    /// <summary>Doğrulanmış bir hesap tohumlar, marka kodunu döner.
    ///
    /// <para><paramref name="createdAt"/> yalnız işin hesapları hangi SIRAYLA
    /// gezdiğini sabitlemek için var (<c>ListVerifiedIdsAsync</c>
    /// <c>CreatedAt</c>'e göre sıralıyor). "Önceki turun artığı sıradakine
    /// biner mi" sorusunu ancak kimin önce geldiğini bilerek sorabiliriz;
    /// <c>UtcNow</c>'a bırakılırsa iki tohum aynı damgaya düşebilir ve test
    /// sıralamaya göre bazen yanlış şeyi ölçer.</para></summary>
    private static async Task<string> SeedVerifiedAsync(
        JobFactory factory, DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"vj-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = $"LDK-VJ-{Guid.NewGuid():N}",
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            LastVerifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return brandCode;
    }

    private static async Task<NetgsmAccount> ReadAsync(JobFactory factory, string brandCode)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.BrandCode == brandCode);
    }

    private static async Task RunAsync(JobFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<NetgsmAccountVerifyJob>();
        await job.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Gecerli_hesabin_damgasi_tazelenir()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        var before = (await ReadAsync(factory, brandCode)).LastVerifiedAt;

        await RunAsync(factory);

        var after = await ReadAsync(factory, brandCode);
        after.Status.Should().Be(NetgsmAccountStatus.Verified);
        after.LastVerifiedAt.Should().BeAfter(before!.Value);
        after.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Reddedilen_hesap_Failed_olur()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        factory.Iys.ByBrand[brandCode] =
            () => throw new IysConfigurationException("30", "kimlik reddedildi");

        await RunAsync(factory);

        var acc = await ReadAsync(factory, brandCode);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "geçersiz markayla toplanan onay zaten geçersiz olurdu (spec §2.3)");
        acc.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Gecici_ariza_hesabi_DUSURMEZ()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        factory.Iys.ByBrand[brandCode] = () => throw new HttpRequestException("İYS kapalı");

        await RunAsync(factory);

        var acc = await ReadAsync(factory, brandCode);
        acc.Status.Should().Be(NetgsmAccountStatus.Verified,
            "İYS'nin yarım saatlik kesintisi bütün yayıncıları kapatmamalı; "
            + "kapanan hesap kendiliğinden geri GELMİYOR");
        acc.LastError.Should().NotBeNullOrWhiteSpace("sorun görünür olmalı");
    }

    [Fact]
    public async Task Bir_hesabin_patlamasi_digerini_ETKILEMEZ()
    {
        // Paylaşılan scoped DbContext'te A'nın kirli kayıtları B'nin
        // SaveChanges'ine binerse, B'nin satırına A'nın verisi yazılır.
        var factory = NewFactory();
        var bad = await SeedVerifiedAsync(factory);
        var good = await SeedVerifiedAsync(factory);
        factory.Iys.ByBrand[bad] = () => throw new InvalidOperationException("beklenmeyen");

        await RunAsync(factory);

        factory.Iys.Asked.Should().Contain(good, "bir tur düşünce döngü DURMAMALI");
        (await ReadAsync(factory, good)).Status.Should().Be(NetgsmAccountStatus.Verified);
    }

    [Fact]
    public async Task Dusen_turun_KIRLI_KAYDI_siradakine_binmez()
    {
        // `Bir_hesabin_patlamasi_digerini_ETKILEMEZ` yalnız "döngü durmasın"
        // diyor ve onu ağ çağrısında patlayarak kanıtlıyor — o anda hesap
        // nesnesi HENÜZ TEMİZ, yani catch'teki `ChangeTracker.Clear()` hiç
        // gerekmiyor. Asıl senaryo bu: tur `SaveChanges`'te düşüyor, yani
        // hesap `Modified` olarak izleniyor KALIYOR. Temizlenmezse o bayat
        // yazım sıradaki kiracının `SaveChanges`'ine biner ve onu da
        // düşürür — bir kiracının yarışı bütün günlük turu zehirler.
        var factory = NewFactory();
        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var poisoner = await SeedVerifiedAsync(factory, older);
        var next = await SeedVerifiedAsync(factory, older.AddMinutes(1));
        var before = (await ReadAsync(factory, next)).LastVerifiedAt;

        factory.Iys.ByBrand[poisoner] = () =>
        {
            // Ağ çağrısı SÜRERKEN satır başkası tarafından yazılıyor: işin
            // elindeki `UpdatedAt` bayatlıyor. Dönüşte `Ok` yazımı CAS'e
            // takılacak ve `SaveChanges` patlayacak.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var current = db.NetgsmAccounts.Single(a => a.BrandCode == poisoner);
            current.LastError = "Panelden araya giren kayıt.";
            db.Entry(current).Property(a => a.UpdatedAt).IsModified = true;
            db.SaveChanges();

            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        await RunAsync(factory);

        var after = await ReadAsync(factory, next);
        after.LastVerifiedAt.Should().BeAfter(before!.Value,
            "önceki kiracının kaydedilememiş yazımı bu satırın turunu "
            + "düşürmemeli");
        after.Status.Should().Be(NetgsmAccountStatus.Verified);
    }

    [Fact]
    public async Task Failed_hesap_ise_alinmaz()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
            acc.Status = NetgsmAccountStatus.Failed;
            await db.SaveChangesAsync();
        }

        await RunAsync(factory);

        factory.Iys.Asked.Should().NotContain(brandCode,
            "kapalı hesabı her gün yoklamak yayıncı adına bedelsiz de olsa "
            + "gereksiz; yeniden açılma yolu panelden kaydetmektir");
    }

    [Fact]
    public async Task Tur_SURERKEN_kapanan_hesap_atlanir()
    {
        // `Failed_hesap_ise_alinmaz` hesabı tur BAŞLAMADAN kapatıyor; onu
        // zaten `ListVerifiedIdsAsync`'in süzgeci eliyor, yani
        // `VerifyOneAsync`'teki durum kontrolü o testte hiç koşmuyor.
        // Korunması gereken pencere bu: liste ALINDIKTAN sonra admin kill
        // switch'i çalışıyor. Kontrol olmasaydı iş, emekliye ayrılmış bir
        // kimlikle yine de İYS'yi arar ve dönüşte `LastError`'ı ezerdi —
        // `Ok`'ta temizleyerek (yöneticinin kapatma notu kaybolur), başka
        // sonuçlarda da üstüne yazarak. Panelde kapalı bir kurulumun yanında
        // "Kurulumunuz kapatılmadı" yazardı: gerçeğin tam tersi.
        var factory = NewFactory();
        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = await SeedVerifiedAsync(factory, older);
        var closed = await SeedVerifiedAsync(factory, older.AddMinutes(1));
        const string adminNote = "Yayıncı ayrıldı — yönetici kapattı.";

        factory.Iys.ByBrand[first] = () =>
        {
            // İlk hesabın turu sürerken admin ikinciyi kapatıyor.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = db.NetgsmAccounts.Single(a => a.BrandCode == closed);
            acc.Status = NetgsmAccountStatus.Disabled;
            acc.LastError = adminNote;
            db.SaveChanges();

            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        await RunAsync(factory);

        factory.Iys.Asked.Should().NotContain(closed,
            "emekliye ayrılmış kimlikle İYS'ye gidilmez");
        var acc = await ReadAsync(factory, closed);
        acc.Status.Should().Be(NetgsmAccountStatus.Disabled);
        acc.LastError.Should().Be(adminNote, "yöneticinin kapatma notu silinmemeli");
    }

    [Theory]
    [InlineData(NetgsmAccountStatus.Disabled)]
    [InlineData(NetgsmAccountStatus.Verified)]
    public async Task Bayat_gunluk_ret_yeni_hesap_surumunu_degistirmez(
        NetgsmAccountStatus replacementStatus)
    {
        // `Failed_hesap_ise_alinmaz` ağ çağrısından ÖNCEKİ durumu koruyor.
        // Bu test ağ çağrısı SÜRERKEN açılan pencereyi kapatıyor — asıl
        // tehlike orada:
        //
        //  * `Disabled`: admin tam o saniyede kapattı. Ret sonucunu körlemesine
        //    yazmak hesabı `Failed`'a çeker ve ADMIN KİLİDİNİ KALDIRIR —
        //    yayıncı panelden kaydete basıp kurulumu geri açabilir hâle gelir.
        //  * `Verified` + yeni parola: yayıncı doğru kimliği girdi ve panel onu
        //    doğruladı. Bizim elimizdeki "kod 30" ESKİ parolaya ait; yazarsak
        //    çalışan bir kurulumu kapatırız.
        //
        // İkisini de ayıran şey durum kontrolü DEĞİL, sürümdür: `Verified`
        // vakasında durum hiç değişmedi. Bu yüzden ret, doğruladığı
        // `UpdatedAt` sürümünü şart koşar.
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        var replacementPassword = $"pw-{Guid.NewGuid():N}";
        const string currentMessage = "Güncel yönetici notu.";

        factory.Iys.ByBrand[brandCode] = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
            var current = db.NetgsmAccounts.Single(a => a.BrandCode == brandCode);

            current.Status = replacementStatus;
            current.PasswordProtected = accounts.ProtectPassword(replacementPassword);
            current.LastError = currentMessage;
            db.SaveChanges();

            throw new IysConfigurationException("30", "kimlik reddedildi");
        };

        await RunAsync(factory);

        var persisted = await ReadAsync(factory, brandCode);
        persisted.Status.Should().Be(replacementStatus,
            "bayat ret araya giren kararı EZEMEZ");
        persisted.LastError.Should().Be(currentMessage);

        using var verify = factory.Services.CreateScope();
        var verifyAccounts = verify.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        verifyAccounts.TryUnprotectPassword(persisted.PasswordProtected)
            .Should().Be(replacementPassword);
    }

    [Fact]
    public async Task Cozulemeyen_sifre_hesabi_KAPATMAZ()
    {
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);

        // Anahtar halkası kaybını taklit et: şifreli metni bozuk bir değerle
        // değiştir. Unprotect CryptographicException atar, TryUnprotectPassword
        // null döner.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
            acc.PasswordProtected = $"bozuk-{Guid.NewGuid():N}";
            await db.SaveChangesAsync();
        }

        await RunAsync(factory);

        var after = await ReadAsync(factory, brandCode);
        after.Status.Should().Be(NetgsmAccountStatus.Verified,
            "spec §2.4'ten bilinçli sapma: Disabled→Verified dönen kod yolu YOK, "
            + "yani bağlanmamış tek bir anahtar dizini tek koşuda bütün "
            + "yayıncıları kalıcı olarak kilitlerdi");
        after.LastError.Should().Be(NetgsmAccountService.UndecryptableMessage,
            "§2.4'ün asıl talebi sessiz bozulmanın olmaması — panel bunu gösterir");
        factory.Iys.Asked.Should().NotContain(brandCode,
            "şifre çözülemeden İYS'ye çağrı yapılmamalı");
    }

    [Fact]
    public async Task Cozulemeyen_sifre_panelde_gorunur()
    {
        // LastError panele dönmüyorsa "sessiz bozulma yok" kuralı kâğıt üstünde
        // kalır: yayıncı SMS'lerinin neden gitmediğini hiçbir yerden öğrenemez.
        var factory = NewFactory();
        var brandCode = await SeedVerifiedAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
            acc.PasswordProtected = $"bozuk-{Guid.NewGuid():N}";
            await db.SaveChangesAsync();
        }
        await RunAsync(factory);

        var licenseId = (await ReadAsync(factory, brandCode)).LicenseId;
        using var readScope = factory.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await readDb.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);

        var view = OrderDeck.LicenseServer.Controllers.Panel
            .PanelNetgsmAccountController.ToView(row);
        view.LastError.Should().Be(NetgsmAccountService.UndecryptableMessage);
        view.SmsEnabled.Should().BeTrue(
            "hesap hâlâ Verified — gönderim ilk denemede düşer ve gerçek "
            + "sebebi LastError'da yazar; kapatmanın bedeli daha ağır");
    }
}
