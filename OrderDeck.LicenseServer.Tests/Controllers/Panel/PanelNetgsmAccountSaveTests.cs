using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Kaydetme anı: satır önce KAPALI yazılır, sonra tek bir /iys/search
/// çağrısıyla doğrulanır. Doğrulanana kadar (ve doğrulanamazsa) SMS ve onay
/// toplama kapalıdır — spec §2.1/§2.2.
/// </summary>
public sealed class PanelNetgsmAccountSaveTests : IDisposable
{
    private readonly List<NetgsmApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    /// <summary>Doğrulama çağrısını yönlendirilebilir kılar: her test kendi
    /// İYS cevabını kurar.</summary>
    private sealed class StubIysClient : IIysClient
    {
        public Func<IysSearchResult> OnSearch { get; set; } =
            () => new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());

        public int SearchCalls { get; private set; }

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(OnSearch());
        }
    }

    private sealed class NetgsmApiFactory : ApiFactory
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

    private NetgsmApiFactory NewFactory()
    {
        var f = new NetgsmApiFactory();
        _factories.Add(f);
        return f;
    }

    private static object NewBody(string? password = null) => new
    {
        userCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
        password = password ?? $"pw-{Guid.NewGuid():N}",
        header = "ORDERDECK",
        brandCode = Random.Shared.Next(100_000, 999_999).ToString(),
    };

    private async Task<(NetgsmApiFactory Factory, HttpClient Client, Guid LicenseId)> SeedAsync()
    {
        var factory = NewFactory();
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-PNS-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (factory, client, license.Id);
    }

    [Fact]
    public async Task Iys_kabul_ederse_verified_olur()
    {
        var (factory, client, licenseId) = await SeedAsync();

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Iys.SearchCalls.Should().Be(1, "doğrulama SENKRON koşar, kuyruğa alınmaz");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("verified");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeTrue();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Verified);
        acc.LastVerifiedAt.Should().NotBeNull();
        acc.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Iys_reddederse_failed_kalir_ve_sebep_yazilir()
    {
        var (factory, client, licenseId) = await SeedAsync();
        factory.Iys.OnSearch = () => throw new IysConfigurationException("60", "marka yok");

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "kaydetme başarılı — kimlik saklandı, yalnız DOĞRULANMADI; 4xx "
            + "dönersek yayıncı girdiği değerleri kaybeder ve düzeltemez");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("failed");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("lastError").GetString().Should().Contain("60");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed);
    }

    [Fact]
    public async Task Iys_ulasilamazsa_failed_kalir()
    {
        // Yeni kayıt zaten kapalı doğuyor; ulaşılamayan doğrulama onu AÇMAZ.
        // (Çalışan bir hesabın geçici arızada düşmemesi Görev 8'de.)
        var (factory, client, _) = await SeedAsync();
        factory.Iys.OnSearch = () => throw new HttpRequestException("bağlantı yok");

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("failed");
        doc.RootElement.GetProperty("lastError").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Dogrulama_sirasinda_admin_kapatirsa_sonuc_UYGULANMAZ()
    {
        // `VerifyAsync` bir ağ çağrısı — saniyeler sürer. O pencerede admin
        // kapatma anahtarına basarsa, dönen "kabul" cevabını yazmak kapatmayı
        // sessizce geri alırdı. Yarışı zamanlamaya bırakmıyoruz: stub'ın
        // İÇİNDE, tam o pencerede kapatıyoruz — deterministik.
        var (factory, client, licenseId) = await SeedAsync();
        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = db.NetgsmAccounts.Single(a => a.LicenseId == licenseId);
            row.Status = NetgsmAccountStatus.Disabled;
            db.SaveChanges();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "doğruladığımız durum artık satırda durmuyor; sonuç atılmalı");

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db2.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Disabled,
            "İYS 'kabul' dese bile admin kapatması geri alınamaz (Görev 6 sözleşmesi)");
    }

    [Fact]
    public async Task Dogrulama_sirasinda_kimlikler_degisirse_sonuc_UYGULANMAZ()
    {
        // İkinci bir PUT, biz İYS'yi beklerken BAŞKA kimlikleri yazdı. Bizim
        // "kabul" cevabımız o kimliklere ait DEĞİL; yazarsak doğrulanmamış
        // kimlikleri Verified yapmış oluruz — fail-closed tam burada delinir.
        var (factory, client, licenseId) = await SeedAsync();
        var otherUserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();
        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = db.NetgsmAccounts.Single(a => a.LicenseId == licenseId);
            row.UserCode = otherUserCode;
            db.SaveChanges();
            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var acc = await db2.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);
        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "doğrulanmamış kimlikler ASLA Verified yazılmaz");
        acc.UserCode.Should().Be(otherUserCode, "ikinci PUT'un yazdığı kimlik korunur");
    }

    [Fact]
    public async Task Dogrulama_sirasinda_yalniz_parola_degisirse_sonuc_uygulanmaz()
    {
        // Yukarıdaki testin kardeşi ama ONDAN DAHA SERT: burada `UserCode` ve
        // `BrandCode` AYNI kalıyor, yalnız parola değişiyor. Alan karşılaştırmasına
        // dayanan bir CAS bu yarışı GÖREMEZ — "doğruladığım kimlikler duruyor"
        // der ve satırı açar. Oysa doğrulanan parola artık satırda yok: hiç
        // sınanmamış bir parola `Verified` damgası alırdı. Sürüm jetonu bunu
        // alan alan karşılaştırmadan yakalar.
        var (factory, client, licenseId) = await SeedAsync();
        var replacementPassword = $"pw-{Guid.NewGuid():N}";

        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();

            var row = db.NetgsmAccounts.Single(a => a.LicenseId == licenseId);
            row.PasswordProtected = accounts.ProtectPassword(replacementPassword);
            db.SaveChanges();

            return new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>());
        };

        var response = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString()
            .Should().Be("verification-superseded");

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var verifyAccounts = verify.ServiceProvider.GetRequiredService<NetgsmAccountService>();

        var persisted = await verifyDb.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.LicenseId == licenseId);

        persisted.Status.Should().Be(NetgsmAccountStatus.Failed);
        verifyAccounts.TryUnprotectPassword(persisted.PasswordProtected)
            .Should().Be(replacementPassword, "araya giren PUT'un parolası korunur");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Panel_kaydi_dogrulama_baslamadan_kampanyayi_duraklatir(bool unavailable)
    {
        // Kurulum yeniden kaydedildiği AN kapı kapanır. Duraklatmayı doğrulama
        // sonucuna bağlasaydık, ağ çağrısını beklediğimiz saniyelerde (ya da
        // süreç tam orada ölürse sonsuza dek) koşan işçi gönderime devam
        // ederdi — işçi markayı ve onayları çoktan okumuştur, hesabın Failed
        // olması onu TEK BAŞINA durdurmaz. `unavailable` iki ret biçimini de
        // sınıyor: İYS'nin "hayır"ı ve İYS'ye hiç ulaşamama.
        var (factory, client, licenseId) = await SeedAsync();

        (await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody()))
            .EnsureSuccessStatusCode();

        var campaignId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            db.SmsCampaigns.Add(new SmsCampaign
            {
                Id = campaignId,
                LicenseId = licenseId,
                MessageBody = "Kampanya",
                Status = "sending",
                ClaimedAt = DateTimeOffset.UtcNow,
                SegmentsPerMessage = 1,
                RecipientCount = 2,
                ReservedCredits = 2,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            for (var i = 0; i < 2; i++)
            {
                db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
                {
                    Id = Guid.NewGuid(),
                    CampaignId = campaignId,
                    Phone = $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}",
                    Status = "pending",
                });
            }

            db.LicenseSmsBalances.Add(new LicenseSmsBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                CreditsRemaining = 98,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        var observedPausedBeforeVerification = false;

        factory.Iys.OnSearch = () =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

            var account = db.NetgsmAccounts.AsNoTracking()
                .Single(a => a.LicenseId == licenseId);
            var campaign = db.SmsCampaigns.AsNoTracking()
                .Single(c => c.Id == campaignId);

            observedPausedBeforeVerification =
                account.Status == NetgsmAccountStatus.Failed
                && campaign.Status == "paused";

            if (unavailable) throw new HttpRequestException("İYS erişilemiyor");
            throw new IysConfigurationException("30", "kimlik reddedildi");
        };

        var response = await client.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        observedPausedBeforeVerification.Should().BeTrue(
            "duraklatma İYS çağrısından ÖNCE, Upsert'in kendi SaveChanges'inde inmeli");

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var persisted = await verifyDb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        persisted.Status.Should().Be("paused");
        persisted.CompletedAt.Should().BeNull();
        persisted.RefundedCredits.Should().Be(0);

        // Duraklatma KREDİ İADE ETMEZ: kalan alıcılar "pending" kalıyor,
        // rezervasyon tam da onların karşılığı. Devam ettirildiğinde aynı
        // krediyle gönderilecekler.
        (await verifyDb.SmsCampaignRecipients.CountAsync(
            r => r.CampaignId == campaignId && r.Status == "pending"))
            .Should().Be(2);

        (await verifyDb.LicenseSmsBalances
            .Where(b => b.LicenseId == licenseId)
            .Select(b => b.CreditsRemaining)
            .SingleAsync()).Should().Be(98);

        (await verifyDb.LicenseSmsTransactions.CountAsync(
            t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Marka_kodu_rakam_disi_ise_400()
    {
        var (_, client, _) = await SeedAsync();

        var resp = await client.PutAsJsonAsync("/api/panel/netgsm/account", new
        {
            userCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            password = $"pw-{Guid.NewGuid():N}",
            header = "ORDERDECK",
            brandCode = "73A734",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "DB check constraint'i (CK_NetgsmAccounts_BrandCode) zaten kesiyor "
            + "ama oraya varmak 500 üretirdi; kapıda anlaşılır hata dönmeli");
    }

    [Fact]
    public async Task Staff_operator_kaydedemez()
    {
        var (factory, client, _) = await SeedAsync();
        var staff = await PanelOperatorHelper.StaffClientAsync(factory, client);

        var resp = await staff.PutAsJsonAsync("/api/panel/netgsm/account", NewBody());

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        factory.Iys.SearchCalls.Should().Be(0, "yetkisiz istek dış çağrı TETİKLEMEMELİ");
    }
}
