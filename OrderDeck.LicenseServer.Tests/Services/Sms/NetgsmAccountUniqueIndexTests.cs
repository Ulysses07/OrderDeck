using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// Tekil index yalnız GERÇEK SQL Server'da kanıtlanabilir — InMemory
/// sağlayıcısı index ihlali fırlatmaz (bkz. IysConsentUniqueIndexTests).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class NetgsmAccountUniqueIndexTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public NetgsmAccountUniqueIndexTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="NetgsmAccount.UserCode"/> ve <see cref="NetgsmAccount.Header"/>
    /// satır başına ÜRETİLİR (sabit değil): sabit olsalardı marka testi, tekil
    /// index yanlışlıkla o iki kolona konmuş olsa bile aynı
    /// <see cref="DbUpdateException"/>'ı alır ve doğru sebeple geçtiğini
    /// sanardık. Ayrıca repo kuralı: testte sabit kimlik-bilgisi metni yazma.
    /// </summary>
    private static NetgsmAccount Row(
        Guid licenseId, string brandCode,
        NetgsmAccountStatus status = NetgsmAccountStatus.Verified) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        PasswordProtected = $"pw-{Guid.NewGuid():N}",
        Header = $"OD{Guid.NewGuid():N}"[..11],
        BrandCode = brandCode,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Boş bir yayıncı lisansı açar. Ortak bir <c>TestData</c> yardımcısı
    /// bu repoda YOK — mevcut testler (ör. <c>BroadcastPostCleanupJobTests</c>)
    /// aynı gövdeyi kendi içlerinde tutuyor; kalıbı bozmuyoruz.</summary>
    private async Task<Guid> NewLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Netgsm-IX",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    [Fact]
    public async Task Ayni_lisansa_ikinci_hesap_reddedilir()
    {
        var licenseId = await NewLicenseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(licenseId, "731734"));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(licenseId, "763208"));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "bir lisansın tek Netgsm hesabı olur");
        }
    }

    [Fact]
    public async Task Ayni_marka_kodu_iki_lisansa_verilemez()
    {
        var a = await NewLicenseAsync();
        var b = await NewLicenseAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(a, "731734"));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(b, "731734"));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "marka→hesap araması tek satır dönmeli; iki lisans aynı markayı " +
                "paylaşırsa push işi hangi kimlikle gideceğini bilemez");
        }
    }

    [Fact]
    public async Task Bos_marka_kodu_reddedilir()
    {
        var licenseId = await NewLicenseAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.NetgsmAccounts.Add(Row(licenseId, ""));

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "boş marka kodu global tekil index'te bir yer kapar; ikinci boş " +
            "kayıt çakışır ve marka→hesap araması \"\" ile gerçek bir kiracının " +
            "satırını döndürerek onayı yanlış markaya yazardı");
    }

    [Fact]
    public async Task Bastan_bosluklu_marka_kodu_reddedilir()
    {
        var licenseId = await NewLicenseAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.NetgsmAccounts.Add(Row(licenseId, " 731734"));

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "SQL Server karşılaştırmada SONDAKİ boşluğu yok sayar ama BAŞTAKİNİ " +
            "saymaz: \" 731734\" tekil index'te \"731734\"ten ayrı bir anahtar " +
            "olur. Böylece işletme gözüyle aynı marka iki lisansta durabilir ve " +
            "marka→hesap araması ikisinden yalnız birini görür — index'in var " +
            "olma sebebi çöker. İYS marka kodları sayısal olduğu için kısıt " +
            "rakam dışı her karakteri kapıda kesiyor");
    }

    [Fact]
    public async Task Dogrulanmamis_satir_marka_kodunu_ISGAL_ETMEZ()
    {
        // Yayıncı A marka kodunu yanlış yazdı (satırı Failed). Filtresiz
        // indekste bu kod global olarak yanardı ve gerçek sahibi B kendi
        // kurulumunu ASLA tamamlayamazdı — kendi hatası olmayan, kendi
        // düzeltemeyeceği kalıcı bir kilit.
        var a = await NewLicenseAsync();
        var b = await NewLicenseAsync();
        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(a, brandCode, NetgsmAccountStatus.Failed));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(b, brandCode, NetgsmAccountStatus.Verified));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync(
                "yalnız DOĞRULANMIŞ satırlar markayı sahiplenir");
        }
    }

    [Fact]
    public async Task Disabled_satir_marka_kodunu_SERBEST_BIRAKIR()
    {
        // Kill switch'le kapatılan yayıncının markası, aynı markayı gerçekten
        // İYS'de doğrulayabilen bir hesabı engellemeye devam etmemeli.
        var a = await NewLicenseAsync();
        var b = await NewLicenseAsync();
        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(a, brandCode, NetgsmAccountStatus.Disabled));
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(b, brandCode, NetgsmAccountStatus.Verified));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task Bayat_hesap_yazimi_kampanya_duraklatmasini_da_geri_alir()
    {
        // Görev 3'te `UpsertAsync` hesabı `Failed` yaparken kampanyaları AYNI
        // `SaveChanges` içinde duraklatıyor. Burada kanıtlanan şey o birliğin
        // gerçek: hesap yazımı sürüm jetonuna takılıp reddedilirse kampanya
        // duraklatması da geri alınmalı. Alınmazsa, admin'in kapattığı bir
        // hesabın kampanyası "paused"a düşer ama hesap `Disabled` kalır —
        // kimsenin devam ettiremeyeceği, rezerve kredisi asılı bir kampanya.
        //
        // InMemory bunu KANITLAYAMAZ: jetonu uygular ama çok-varlıklı yazımı
        // bir transaction'da geri almaz. Bu yüzden Testcontainers.
        var licenseId = await NewLicenseAsync();
        var campaignId = Guid.NewGuid();

        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmAccounts.Add(Row(
                licenseId, Random.Shared.Next(100_000, 999_999).ToString(),
                NetgsmAccountStatus.Verified));
            db.SmsCampaigns.Add(new SmsCampaign
            {
                Id = campaignId,
                LicenseId = licenseId,
                MessageBody = "Kampanya",
                Status = "sending",
                ClaimedAt = DateTimeOffset.UnixEpoch,
                SegmentsPerMessage = 1,
                RecipientCount = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Yayıncının paneli hesabı okudu (bu sürümü sahipleniyor).
        using var workerScope = _factory.Services.CreateScope();
        var workerDb = workerScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = workerScope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var stale = await workerDb.NetgsmAccounts.SingleAsync(a => a.LicenseId == licenseId);

        // Admin araya girip kapattı — sürüm ilerledi.
        using (var adminScope = _factory.Services.CreateScope())
        {
            var adminDb = adminScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var current = await adminDb.NetgsmAccounts.SingleAsync(a => a.LicenseId == licenseId);
            current.Status = NetgsmAccountStatus.Disabled;
            await adminDb.SaveChangesAsync();
        }

        // Panel bayat sürümle yazmaya çalışıyor. Servisteki `Disabled` guard'ı
        // bu yarışı GÖREMEZ (izlenen kopya hâlâ Verified); kararı jeton verir.
        Func<Task> write = async () => await accounts.UpsertAsync(
            licenseId, stale.UserCode, $"pw-{Guid.NewGuid():N}",
            stale.Header, stale.BrandCode, CancellationToken.None);

        await write.Should().ThrowAsync<DbUpdateConcurrencyException>();

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await verifyDb.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId))
            .Status.Should().Be(NetgsmAccountStatus.Disabled,
                "admin kararı bayat yazımla geri alınamaz");

        var campaign = await verifyDb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("sending",
            "hesap yazımı düştüyse duraklatma da geri alınmalı — ya ikisi ya hiçbiri");
        campaign.ClaimedAt.Should().Be(DateTimeOffset.UnixEpoch);
    }
}
