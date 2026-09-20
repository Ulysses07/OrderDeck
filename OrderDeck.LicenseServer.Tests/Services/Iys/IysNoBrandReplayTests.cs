using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// Görev 17 — marka çözülemezken gelen RET kaybolmasın.
///
/// <para>Kurulum <c>Failed</c>/<c>Disabled</c> iken gelen onay/ret olayları
/// <c>ErrorCode="no-brand"</c> ile yazılıyor ama durum satırına DOKUNMUYOR.
/// Kişi onayını geri çekmişse satır <c>Onay</c> kalıyor ve kurulum geri
/// doğrulandığı an gönderim kapısı o bayat <c>Onay</c>'ı kabul ediyor:
/// onayını geri çekmiş kişiye ticari SMS. 6563 ihlali.</para>
///
/// <para><b>Yalnız RET oynatılır.</b> Onay zamana bağlı — İYS dışında alınan
/// onay üç iş günü içinde kaydedilmezse hukuken geçersiz; haftalarca beklemiş
/// bir onayı canlandırmak geçersiz bir onayı kayda geçirmek olur. Düşen onayın
/// bedeli "o kişiye pazarlama yapılamaz", düşen reddin bedeli yasa dışı
/// gönderim. Asimetri bilinçli.</para>
/// </summary>
public sealed class IysNoBrandReplayTests : IDisposable
{
    private readonly List<ReplayApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    /// <summary>Panel PUT'unun senkron doğrulaması İYS'ye çıkıyor; testler
    /// "kabul" cevabını sabitliyor — ölçtüğümüz şey doğrulamanın ARDINDAN
    /// koşan oynatma.</summary>
    private sealed class StubIysClient : IIysClient
    {
        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(new IysSearchResult(
                "0", "{}", new Dictionary<string, IysConsentStatus>()));
    }

    private sealed class ReplayApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IIysClient>();
                s.AddSingleton<IIysClient>(new StubIysClient());
            });
        }
    }

    private ReplayApiFactory NewFactory()
    {
        var f = new ReplayApiFactory();
        _factories.Add(f);
        return f;
    }

    /// <summary>Netgsm abone numarası ve parola ÜRETİLİR: depo public, sabit bir
    /// değer gerçek bir aboneye ait olabilir.</summary>
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    private static string NewPhone()
        => $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}";

    private static object Body(string userCode, string brandCode) => new
    {
        userCode,
        password = $"pw-{Guid.NewGuid():N}",
        header = "ORDERDECK",
        brandCode,
    };

    /// <summary>Kendi müşterisi, kendi lisansı, kendi oturumu olan bir kiracı.
    /// Kiracı sızıntısı ancak aynı veritabanında iki kiracı varken
    /// gözlemlenebilir.</summary>
    private static async Task<(HttpClient Client, Guid LicenseId)> SeedTenantAsync(
        ReplayApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-NBR-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary>Marka çözülemediği için düşmüş olay: durum satırına hiç
    /// bağlanmamış, <c>BrandCode</c>'u boş, <c>ErrorCode="no-brand"</c>.</summary>
    private static IysConsentEvent NoBrandEvent(
        Guid licenseId, string phone, IysConsentEventType type, DateTimeOffset at)
        => new()
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            BrandCode = null,
            IysConsentId = null,
            Recipient = phone,
            OccurredAt = at,
            EventType = type,
            Status = type == IysConsentEventType.LocalRevoke
                ? IysConsentStatus.Ret
                : IysConsentStatus.Onay,
            SourceTable = "Shopper",
            SourceId = Guid.NewGuid(),
            ErrorCode = "no-brand",
        };

    /// <summary>Kurulum HÂLÂ açıkken açılmış, sonra bayatlamış onay satırı.</summary>
    private static IysConsent ConsentRow(string brandCode, string phone, DateTimeOffset at)
        => new()
        {
            Id = Guid.NewGuid(),
            BrandCode = brandCode,
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = phone,
            Status = IysConsentStatus.Onay,
            ConsentDate = at,
            LastLocalEventAt = at,
            LastVerifiedStatus = IysConsentStatus.Onay,
            LastVerifiedAt = at,
            PushState = IysPushState.Confirmed,
            CreatedAt = at,
            UpdatedAt = at,
        };

    private static async Task<IysConsent?> RowAsync(
        ReplayApiFactory factory, string brandCode, string phone)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.IysConsents.AsNoTracking()
            .SingleOrDefaultAsync(c => c.BrandCode == brandCode && c.Recipient == phone);
    }

    [Fact]
    public async Task Dogrulama_no_brand_retlerini_uygular()
    {
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var phone = NewPhone();
        var consentedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var revokedAt = consentedAt.AddDays(1);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsents.Add(ConsentRow(brandCode, phone, consentedAt));
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, revokedAt));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        var row = await RowAsync(factory, brandCode, phone);
        row.Should().NotBeNull();
        row!.Status.Should().Be(IysConsentStatus.Ret,
            "kurulum kapalıyken gelen RET yalnız olay tablosuna düşmüştü; "
            + "marka geri doğrulandığında satır da düşmezse gönderim kapısı "
            + "bayat ONAY'ı kabul eder ve onayını geri çekmiş kişiye SMS gider");
        row.LastLocalEventAt.Should().Be(revokedAt,
            "sıra damgası ilerlemezse aynı RET her doğrulamada yeniden uygulanır");
        row.PushState.Should().Be(IysPushState.Pending,
            "RET de İYS'ye iletilmeli — yasal kayıt; gönderim bunu beklemez");
    }

    [Fact]
    public async Task Dogrulama_no_brand_onaylarini_uygulamaz()
    {
        // Onay zamana bağlı: İYS dışında alınan onay üç iş günü içinde
        // kaydedilmezse hukuken geçersiz (6563 Yönetmelik m.7). Haftalarca
        // `no-brand` bekleyen bir onayı canlandırıp İYS'ye push etmek geçersiz
        // bir onayı kayda geçirmek olurdu.
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var consentPhone = NewPhone();
        var revokePhone = NewPhone();
        var at = DateTimeOffset.UtcNow.AddDays(-10);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, consentPhone, IysConsentEventType.LocalConsent, at));
            // Aynı PUT'ta oynatmanın GERÇEKTEN koştuğunu kanıtlayan tanık:
            // olmasaydı test, oynatma hiç çağrılmadığında da yeşile dönerdi.
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, revokePhone, IysConsentEventType.LocalRevoke, at));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        (await RowAsync(factory, brandCode, revokePhone))
            .Should().NotBeNull("tanık RET uygulanmalı — yoksa test boş yere yeşil");

        (await RowAsync(factory, brandCode, consentPhone)).Should().BeNull(
            "geçersizleşmiş bir onayı canlandırmak İYS'ye yanlış beyan olurdu; "
            + "düşen onayın bedeli pazarlama erişimi, düşen reddin bedeli yasa dışı gönderim");
    }

    [Fact]
    public async Task Ikinci_dogrulama_daha_yeni_bir_onayi_ezmez()
    {
        // Olay tablosu ekle-only ("hiç silinmez, hiç güncellenmez"), yani olayı
        // "oynatıldı" diye İŞARETLEYEMEYİZ. Gerek de yok: toplayıcının sıra
        // damgası kuralı (`OccurredAt <= LastLocalEventAt` → atla) ikinci
        // oynatmayı kendiliğinden etkisiz kılıyor. Bu test o bedava-idempotentlik
        // iddiasını çiviliyor; DÜŞERSE tasarım yanlıştır, test değil.
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var userCode = NewUserCode();
        var phone = NewPhone();
        var consentedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var revokedAt = consentedAt.AddDays(1);
        var reconsentedAt = revokedAt.AddDays(1);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsents.Add(ConsentRow(brandCode, phone, consentedAt));
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, revokedAt));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(userCode, brandCode))).EnsureSuccessStatusCode();

        (await RowAsync(factory, brandCode, phone))!.Status
            .Should().Be(IysConsentStatus.Ret, "önce RET uygulanmış olmalı");

        // Kişi fikrini değiştirdi: kurulum artık AÇIK olduğu için bu onay
        // normal yoldan, markası çözülmüş hâlde iniyor.
        using (var reconsent = factory.Services.CreateScope())
        {
            var db = reconsent.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var collector = reconsent.ServiceProvider.GetRequiredService<IysConsentCollector>();
            await collector.RecordAsync(
                licenseId, phone, consented: true, reconsentedAt,
                "Shopper", Guid.NewGuid(), ip: null, userAgent: null);
            await db.SaveChangesAsync();
        }

        // Yayıncı formu bir kez daha kaydediyor — oynatma İKİNCİ kez koşuyor.
        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(userCode, brandCode))).EnsureSuccessStatusCode();

        var row = await RowAsync(factory, brandCode, phone);
        row!.Status.Should().Be(IysConsentStatus.Onay,
            "eski RET olayı ikinci kez uygulanmamalı; sıra damgası onu atlar, "
            + "yoksa oynatma DAHA YENİ gerçek bir olayı ezerdi");
        row.LastLocalEventAt.Should().Be(reconsentedAt);
    }

    [Fact]
    public async Task Baska_lisansin_no_brand_reti_bu_markaya_dokunmaz()
    {
        // İzin MARKA BAZINDA ayrıdır — aynı numara A'da ONAY, B'de RET olabilir.
        // Lisans filtresi düşerse B'nin doğrulaması A'nın müşterisinin reddini
        // B'nin markasına uygular: B, kendisinden onayını hiç çekmemiş bir
        // müşterisine bir daha ulaşamaz. Kiracı sızıntısı.
        var factory = NewFactory();
        var a = await SeedTenantAsync(factory);
        var b = await SeedTenantAsync(factory);

        var brandB = NewBrandCode();
        var sharedPhone = NewPhone();  // hem A'nın hem B'nin müşterisi
        var ownPhone = NewPhone();     // yalnız B'nin müşterisi
        var consentedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var revokedAt = consentedAt.AddDays(1);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsents.Add(ConsentRow(brandB, sharedPhone, consentedAt));
            db.IysConsents.Add(ConsentRow(brandB, ownPhone, consentedAt));

            // A'nın müşterisi A'dan onayını çekti — B'yi ilgilendirmez.
            db.IysConsentEvents.Add(NoBrandEvent(
                a.LicenseId, sharedPhone, IysConsentEventType.LocalRevoke, revokedAt));
            // B'nin kendi müşterisinin reddi — uygulanmalı.
            db.IysConsentEvents.Add(NoBrandEvent(
                b.LicenseId, ownPhone, IysConsentEventType.LocalRevoke, revokedAt));
            await db.SaveChangesAsync();
        }

        (await b.Client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandB))).EnsureSuccessStatusCode();

        (await RowAsync(factory, brandB, ownPhone))!.Status
            .Should().Be(IysConsentStatus.Ret, "B kendi müşterisinin reddini uygulamalı");

        (await RowAsync(factory, brandB, sharedPhone))!.Status
            .Should().Be(IysConsentStatus.Onay,
                "A'nın müşterisinin reddi B'nin markasına İNMEMELİ — izin marka "
                + "bazında ayrı; lisans filtresi düşerse B, kendisinden onayını "
                + "hiç çekmemiş müşterisine bir daha ulaşamaz");
    }
}
