using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

/// <summary>
/// §2.5 — yönetici kapatma anahtarı. Kapatmanın SMS'i gerçekten kesmesi
/// gerekir: hesabı Disabled yapıp devam eden kampanyayı bırakmak, kararı
/// iki dakika sonra kurtarma işinin geri almasına izin verir.
/// </summary>
public sealed class AdminNetgsmPageTests : IClassFixture<HookedApiFactory>
{
    private readonly HookedApiFactory _factory;

    // Kanca boşken HookedApiFactory, ApiFactory'nin aynısı. Yalnız
    // "retry tükeniyor" testi dolduruyor; o test de kendi içinde
    // temizliyor. Burada ek olarak ctor'da sıfırlıyoruz ki sınıf
    // fixture'ı paylaşan testler birbirinin kancasını miras almasın.
    public AdminNetgsmPageTests(HookedApiFactory factory)
    {
        _factory = factory;
        _factory.Hook.Reset();
    }

    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private async Task<(Guid AccountId, Guid LicenseId)> SeedAccountAsync(
        NetgsmAccountStatus status = NetgsmAccountStatus.Verified)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"an-{Guid.NewGuid():N}@x",
            Name = "An",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-AN-{Guid.NewGuid():N}",
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        var accountId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = accountId,
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = Random.Shared.Next(100_000, 999_999).ToString(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (accountId, licenseId);
    }

    private static async Task<Guid> SeedCampaignAsync(
        LicenseDbContext db, Guid licenseId, string status,
        DateTimeOffset? claimedAt = null)
    {
        var id = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = id,
            LicenseId = licenseId,
            MessageBody = "Kampanya",
            Status = status,
            SegmentsPerMessage = 1,
            RecipientCount = 1,
            ReservedCredits = 1,
            ClaimedAt = claimedAt,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string handler, Guid accountId)
    {
        var getResp = await client.GetAsync("/admin/netgsm");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(
            await getResp.Content.ReadAsStringAsync());
        return await client.PostAsync(
            $"/admin/netgsm?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["AccountId"] = accountId.ToString(),
            }));
    }

    [Fact]
    public async Task Giris_yapmadan_sayfa_gorulemez()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/admin/netgsm");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Kapatma_hesabi_disabled_yapar_ve_kampanyalari_duraklatir()
    {
        var (accountId, licenseId) = await SeedAccountAsync();
        Guid pendingId, sendingId, completedId;
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            pendingId = await SeedCampaignAsync(db, licenseId, "pending");
            // TAZE ClaimedAt: canlı bir işçi elinde tutuyor. ClaimedAt bir
            // eşzamanlılık jetonu olduğu için bu, kapatmanın "meşgul" satırı
            // da yazabildiğini gösterir — jeton yüzünden sessizce atlanırsa
            // yayıncıya SMS gitmeye devam ederdi.
            sendingId = await SeedCampaignAsync(
                db, licenseId, "sending", DateTimeOffset.UtcNow);
            completedId = await SeedCampaignAsync(db, licenseId, "completed");
        }

        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await PostAsync(client, "Disable", accountId);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var acc = await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        acc.Status.Should().Be(NetgsmAccountStatus.Disabled);
        acc.LastError.Should().NotBeNullOrEmpty();

        string StatusOf(Guid id) => vdb.SmsCampaigns.AsNoTracking().Single(c => c.Id == id).Status;
        StatusOf(pendingId).Should().Be("paused",
            "pending bırakılırsa kurtarma işi iki dakika sonra diriltir");
        StatusOf(sendingId).Should().Be("paused",
            "taze claim'li (canlı işçinin elindeki) kampanya da durdurulmalı");
        StatusOf(completedId).Should().Be("completed", "biten kampanya geçmiştir");

        var audit = await vdb.AuditLogs.AsNoTracking()
            .Where(e => e.EventType == AuditEvents.NetgsmAccountDisable
                        && e.TargetId == accountId.ToString())
            .ToListAsync();
        audit.Should().ContainSingle();
    }

    [Fact]
    public async Task Kapatma_baska_lisansin_kampanyasina_dokunmaz()
    {
        var (accountId, _) = await SeedAccountAsync();
        var (_, otherLicenseId) = await SeedAccountAsync();
        Guid otherCampaignId;
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            otherCampaignId = await SeedCampaignAsync(db, otherLicenseId, "pending");
        }

        var client = await _factory.CreateLoggedInAdminClientAsync();
        (await PostAsync(client, "Disable", accountId))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == otherCampaignId))
            .Status.Should().Be("pending");
    }

    [Fact]
    public async Task Acma_Verified_degil_Failed_yazar()
    {
        var (accountId, _) = await SeedAccountAsync(NetgsmAccountStatus.Disabled);

        var client = await _factory.CreateLoggedInAdminClientAsync();
        (await PostAsync(client, "Enable", accountId))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "yönetici markanın İYS'de hâlâ geçerli olduğunu bilemez; "
            + "doğrulama normal akıştan geçmeli");
        acc.LastError.Should().BeNull();

        (await vdb.AuditLogs.AsNoTracking().CountAsync(
            e => e.EventType == AuditEvents.NetgsmAccountEnable
                 && e.TargetId == accountId.ToString()))
            .Should().Be(1);
    }

    [Fact]
    public async Task Acma_Disabled_olmayan_hesaba_dokunmaz()
    {
        // İki yönetici listeyi hesap Disabled'ken açtı. Biri açtı, yayıncı
        // panelden kaydedip doğrulattı (Verified). İkincisinin ekranı hâlâ
        // "Aç" gösteriyor. Eşzamanlılık jetonu bunu YAKALAMAZ: aradaki
        // yazımlar bittiği için tek yazan biziz, jeton eşleşir, CAS geçer —
        // ve canlı bir Verified kurulum Failed'a düşer. Failed marka
        // çözemediği için o yayıncının SMS'i sessizce durur, üstelik "aç"
        // yolu kampanya duraklatmadığı için rezerve krediler asılı kalır.
        var (accountId, _) = await SeedAccountAsync(NetgsmAccountStatus.Verified);

        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await PostAsync(client, "Enable", accountId);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "burası bir Razor Page: JSON gövde yöneticiyi çıplak bir ekrana "
            + "düşürür ve hesabın güncel durumunu göremez hâle getirir");

        // Yönlendirmeyi TAKİP ET — "302 döndü" tek başına bir şey kanıtlamaz,
        // başarı yolu da 302 dönüyor. Şerit yoksa yönetici reddi başarı sanar
        // ve kurulumu Failed'a düşürdüğünü zanneder.
        var page = await (await client.GetAsync("/admin/netgsm"))
            .Content.ReadAsStringAsync();
        page.Should().Contain("alert-danger");
        page.Should().Contain("Kurulum zaten açık",
            "reddin sebebi ekranda yazmazsa yönetici neyi yenileyeceğini bilmez");

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId))
            .Status.Should().Be(NetgsmAccountStatus.Verified,
                "\"aç\" yalnız \"kapat\"ın geri alınmasıdır; canlı bir kurulumu "
                + "Failed'a düşürmek gönderimi sessizce keserdi");

        (await vdb.AuditLogs.AsNoTracking().CountAsync(
            e => e.EventType == AuditEvents.NetgsmAccountEnable
                 && e.TargetId == accountId.ToString()))
            .Should().Be(0, "gerçekleşmemiş bir karar denetime yazılmamalı");
    }

    [Fact]
    public async Task Kapatma_retry_tukenirse_500_degil_hata_mesaji_doner()
    {
        var (accountId, licenseId) = await SeedAccountAsync();
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            await SeedCampaignAsync(db, licenseId, "pending");
        }

        // Kancayı istemci KURULDUKTAN sonra kur: CreateLoggedInAdminClientAsync
        // kendi SaveChanges'ini atıyor (admin tohumu) ve onu da çakıştırırsak
        // test kurulumu düşer.
        var client = await _factory.CreateLoggedInAdminClientAsync();

        // Her kaydetme denemesinden HEMEN ÖNCE hesabı dışarıdan yaz: servis
        // taze okuduğu satırı kaydetmeye çalıştığında jeton artık eskimiş
        // olur. Dört tur da böyle düşünce servis DbUpdateConcurrencyException
        // fırlatır. Yakalanmazsa yönetici 500 görür ve kapatmanın olup
        // olmadığını bilemez — anahtarın en çok gerektiği an tam da budur.
        _factory.Hook.BeforeSave = async () =>
        {
            using var rival = _factory.Services.CreateScope();
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await rdb.NetgsmAccounts.SingleAsync(a => a.Id == accountId);
            acc.LastError = $"rakip-{Guid.NewGuid():N}";
            await rdb.SaveChangesAsync();
        };

        try
        {
            var resp = await PostAsync(client, "Disable", accountId);

            resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "tükenme yöneticiye 500 değil, yeniden denenebilir bir mesaj olarak dönmeli");
        }
        finally
        {
            _factory.Hook.Reset();
        }

        // Yönlendirmeyi TAKİP ET. "302 döndü" tek başına bir şey kanıtlamaz:
        // başarı yolu da 302 dönüyor. Yöneticinin ekranında kapatmanın
        // OLMADIĞI yazmazsa, yönlendirmeyi başarı sanıp hesabın hâlâ açık
        // olduğunu fark etmez — ki bu tam olarak 500'den kaçınarak
        // engellemeye çalıştığımız şey. TempData çerezi istemcide
        // taşındığı için bu GET şeridi görür.
        var page = await (await client.GetAsync("/admin/netgsm"))
            .Content.ReadAsStringAsync();
        page.Should().Contain("alert-danger");
        page.Should().Contain("Tekrar deneyin",
            "hata şeridi kaldırılırsa tükenme sessiz bir başarı gibi görünür");

        using var scope = _factory.Services.CreateScope();
        var vdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Çakışan yazım hesap satırıydı, dolayısıyla UYGULANMADI.
        (await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId))
            .Status.Should().Be(NetgsmAccountStatus.Verified);

        // Gerçekleşmemiş bir karar denetime yazılmamalı.
        (await vdb.AuditLogs.AsNoTracking().CountAsync(
            e => e.EventType == AuditEvents.NetgsmAccountDisable
                 && e.TargetId == accountId.ToString()))
            .Should().Be(0);
    }

    [Fact]
    public async Task Kapatma_retry_tukenirse_izlenen_nesne_birakmaz()
    {
        // Yukarıdaki test tükenmenin HTTP yüzeyini ölçüyor; bu test Görev 8'in
        // `when (attempt >= maxAttempts)` dalındaki ChangeTracker.Clear()'ı
        // ölçüyor. İkisi ayrı olmak zorunda: temizlik servisin KENDİ
        // context'inde olup bitiyor, sayfa testi ise sonucu her zaman AYRI bir
        // scope'ta okuyor ve oradaki tracker zaten boş — yani Clear() silinse
        // bile sayfa testi yeşil kalır.
        //
        // Neden önemli: scoped LicenseDbContext istek boyunca yaşıyor. Yarım
        // yazılmış (Disabled + paused) izlenen kopyalar orada kalırsa, aynı
        // istekte sonradan atılacak HERHANGİ bir SaveChanges — denetim kaydı,
        // TempData'yı yazan bir filtre, sonraki bir handler — onları da
        // diske indirir. O zaman "kapatma başarısız" mesajını gösterirken
        // kapatmayı sessizce UYGULAMIŞ oluruz: yönetici tekrar dener, hiçbir
        // şey değişmemiş görünür, hesap ise çoktan kapanmıştır.
        var (accountId, licenseId) = await SeedAccountAsync();
        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            await SeedCampaignAsync(db, licenseId, "pending");
        }

        using var scope = _factory.Services.CreateScope();
        var sdb = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        _factory.Hook.BeforeSave = async () =>
        {
            using var rival = _factory.Services.CreateScope();
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var acc = await rdb.NetgsmAccounts.SingleAsync(a => a.Id == accountId);
            acc.LastError = $"rakip-{Guid.NewGuid():N}";
            await rdb.SaveChangesAsync();
        };

        try
        {
            var act = async () => await accounts.CloseAccountAndPauseCampaignsAsync(
                accountId, NetgsmAccountStatus.Disabled,
                "Yönetici tarafından kapatıldı.", CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
                "dört tur da çakıştı; servis sessizce başarı raporlamamalı");
        }
        finally
        {
            _factory.Hook.Reset();
        }

        sdb.ChangeTracker.Entries().Should().BeEmpty(
            "fırlatmadan önce temizlenmezse, bu context'te sonradan atılacak "
            + "herhangi bir SaveChanges yarım kapatmayı diske indirir");
    }
}
