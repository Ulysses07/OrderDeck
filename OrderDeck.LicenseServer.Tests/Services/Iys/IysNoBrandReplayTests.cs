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
/// <para><b>RET her zaman, ONAY yalnız penceresi açıkken oynatılır.</b> Onay
/// zamana bağlı — İYS dışında alınan onay üç iş günü içinde kaydedilmezse
/// hukuken geçersiz; haftalarca beklemiş bir onayı canlandırmak geçersiz bir
/// onayı kayda geçirmek olur. Penceresi henüz kapanmamış onay ise orijinal
/// tarihiyle kuyruğa girer: kurulumunu bitirmeden önce izleyici toplayan
/// yayıncı o onayları kaybetmez (2026-09-21: 4 onay elle İYS'ye yüklenmek
/// zorunda kalmıştı). Düşen (süresi dolmuş) onayın bedeli "o kişiye pazarlama
/// yapılamaz", düşen reddin bedeli yasa dışı gönderim. Asimetri bilinçli.</para>
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
    public async Task Suresi_gecmis_onay_satirina_gelen_no_brand_ret_yeni_push_penceresi_acar()
    {
        // Gerçek hayattaki satır: onay 10 gün önce alınıp İYS'ye gitmiş,
        // PushDeadline'ı dolmuş. Kurulum kapalıyken kişi reddetmiş (no-brand).
        // Oynatma RET'i Pending yapar ama pencere yenilenmezse push işinin
        // süpürmesi satırı Expired'a düşürür: kapı kapalı (Status≠Onay) ama
        // yasal RET bildirimi sessizce kaybolur.
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var phone = NewPhone();
        var consentedAt = DateTimeOffset.UtcNow.AddDays(-10);
        var revokedAt = consentedAt.AddDays(1);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var stale = ConsentRow(brandCode, phone, consentedAt);
            stale.PushDeadline = consentedAt.AddDays(3); // dolmuş
            db.IysConsents.Add(stale);
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, revokedAt));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        var row = await RowAsync(factory, brandCode, phone);
        row!.Status.Should().Be(IysConsentStatus.Ret);
        row.PushState.Should().Be(IysPushState.Pending);
        row.PushDeadline.Should().NotBeNull().And.BeAfter(DateTimeOffset.UtcNow,
            "RET'in penceresi işlendiği andan sayılır — dolmuş onay penceresi devralınmaz, "
            + "yoksa push süpürmesi RET'i itmeden Expired yazar");
    }

    [Fact]
    public async Task Dogrulama_penceresi_kapanmis_no_brand_onayini_uygulamaz()
    {
        // Onay zamana bağlı: İYS dışında alınan onay üç iş günü içinde
        // kaydedilmezse hukuken geçersiz (6563 Yönetmelik m.7). Haftalarca
        // `no-brand` bekleyen bir onayı canlandırıp İYS'ye push etmek geçersiz
        // bir onayı kayda geçirmek olurdu. 10 gün her takvimde pencereyi aşar.
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
    public async Task Dogrulama_penceresi_acik_no_brand_onayini_orijinal_tarihiyle_uygular()
    {
        // Kurulumunu bitirmeden izleyici toplayan yayıncı: form onayları
        // `no-brand` düşmüştü. Marka doğrulandığında penceresi hâlâ açık
        // olanlar ORİJİNAL tarihiyle kuyruğa girmeli — İYS beyan tarihini
        // onay anına göre değerlendirir, oynatma anına göre değil.
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var phone = NewPhone();
        var at = DateTimeOffset.UtcNow.AddHours(-1);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalConsent, at));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        var row = await RowAsync(factory, brandCode, phone);
        row.Should().NotBeNull("penceresi açık onay kaybolmamalı");
        row!.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending, "push işi bunu İYS'ye taşımalı");
        row.ConsentDate.Should().Be(at, "beyan tarihi onayın alındığı an — oynatma anı değil");
        row.LastLocalEventAt.Should().Be(at);
        row.PushDeadline.Should().Be(
            IysBusinessDays.Add(at, IysConsentCollector.PushDeadlineBusinessDays),
            "son tarih de orijinal onay anından sayılır");
        row.LastVerifiedStatus.Should().BeNull(
            "kapı İYS doğrulaması gelene kadar kapalı kalır — oynatma bunu açamaz");
    }

    [Fact]
    public async Task Pencere_ici_onaydan_sonra_gelen_no_brand_ret_kazanir()
    {
        // Aynı numarada iki olay: önce onay, sonra ret. Numara başına en yeni
        // olay uygulanır; kişi onayını geri çekmişse satır Ret olmalı.
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var phone = NewPhone();
        var consentedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var revokedAt = consentedAt.AddHours(1);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalConsent, consentedAt));
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, revokedAt));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        var row = await RowAsync(factory, brandCode, phone);
        row!.Status.Should().Be(IysConsentStatus.Ret, "son söz kişinin — geri çekti");
        row.LastLocalEventAt.Should().Be(revokedAt);
    }

    [Fact]
    public async Task Retten_sonra_gelen_pencere_ici_no_brand_onay_kazanir()
    {
        // Ters sıra: önce ret, sonra (penceresi açık) onay → kişi fikrini
        // değiştirdi, satır Onay ve İYS'ye beyan edilmeli.
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var phone = NewPhone();
        var revokedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var consentedAt = revokedAt.AddHours(1);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, revokedAt));
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalConsent, consentedAt));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        var row = await RowAsync(factory, brandCode, phone);
        row!.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending);
        row.ConsentDate.Should().Be(consentedAt);
    }

    [Fact]
    public async Task Suresi_dolmus_onaydan_eski_ret_yine_uygulanir()
    {
        // Numaranın en yeni olayı SÜRESİ DOLMUŞ bir onay: beyan edilemez.
        // Ondan eski RET ise yaşar — fail-closed: geçerli onay yoksa satır Ret.
        // (Mevcut davranışla aynı: eski kod da yalnız RET'lere bakıyordu.)
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var phone = NewPhone();
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-12);
        var consentedAt = revokedAt.AddDays(1); // 11 gün önce — pencere kapalı

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, revokedAt));
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalConsent, consentedAt));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        var row = await RowAsync(factory, brandCode, phone);
        row!.Status.Should().Be(IysConsentStatus.Ret,
            "süresi dolmuş onay canlandırılamaz; geriye kalan en yeni geçerli olay RET");
        row.LastLocalEventAt.Should().Be(revokedAt);
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
    public async Task Ayni_numaraya_iki_no_brand_ret_tek_satir_acar()
    {
        // Oynatma TEK SaveChanges'e yazıyor: döngü içinde kaydetmiyoruz
        // (bilinçli — hesabın Verified yazımıyla atomik olmalı). Ama o yüzden
        // döngünün 2. turu, 1. turda `Add` edilmiş ama henüz diske inmemiş
        // satırı sorguyla BULAMAZ. Bulamazsa ikinci bir satır açar ve
        // (BrandCode, ChannelType, RecipientType, Recipient) tekil indeksi
        // patlar: yayıncının PUT'u 500 döner ve kurulum ARTIK HİÇ
        // doğrulanamaz — düzeltilene dek kalıcı kilitlenme.
        //
        // Senaryo uydurma değil: kişi reddeder, sonra onaylar, sonra yine
        // reddeder; ya da tek kaynak reddi iki kez yazar. Kurulum kapalıyken
        // bunların hepsi `no-brand` RET olarak birikir.
        var factory = NewFactory();
        var (client, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var phone = NewPhone();
        var firstAt = DateTimeOffset.UtcNow.AddDays(-10);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            // Durum satırı YOK: ilk RET onu açacak, ikincisi bulmak zorunda.
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, firstAt));
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, phone, IysConsentEventType.LocalRevoke, firstAt.AddDays(1)));
            await db.SaveChangesAsync();
        }

        (await client.PutAsJsonAsync("/api/panel/netgsm/account",
            Body(NewUserCode(), brandCode))).EnsureSuccessStatusCode();

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await vdb.IysConsents.AsNoTracking()
            .Where(c => c.BrandCode == brandCode && c.Recipient == phone)
            .ToListAsync();

        rows.Should().HaveCount(1,
            "aynı numara için ikinci satır açılamaz — tekil indeks bunu SQL "
            + "Server'da patlatır, InMemory'de sessizce kopyalar");
        rows[0].Status.Should().Be(IysConsentStatus.Ret);
        rows[0].LastLocalEventAt.Should().Be(firstAt.AddDays(1),
            "son söz en yeni olayın");
    }

    [Fact]
    public async Task Ayni_numaranin_eski_retleri_tekrar_tekrar_uygulanmaz()
    {
        // Profil kaydı onay kutusunun MEVCUT değerini her kaydetmede yeniden
        // yazıyor (ShopperMeController — bilinçli), yani tek müşteri tek başına
        // bu tabloya yüzlerce `no-brand` RET bırakabilir. Oynatma tek HTTP
        // PUT'un ve tek SaveChanges'in içinde koştuğu için iş listesi
        // OLAY sayısıyla büyürse istek zaman aşımına uğrar ve hiçbir şey
        // commit edilmez — hesabın `Verified` yazımı da dahil. Olay tablosu
        // ekle-only olduğu için bir sonraki deneme aynı yükü çeker: kurulum
        // kalıcı olarak doğrulanamaz hâle gelir.
        //
        // İş listesi bu yüzden NUMARA sayısıyla sınırlı. Sonuç değişmiyor:
        // hepsi RET, satır zaten Ret'te ve en yeni damgada kapanıyor.
        var factory = NewFactory();
        var (_, licenseId) = await SeedTenantAsync(factory);
        var brandCode = NewBrandCode();
        var noisyPhone = NewPhone();
        var otherPhone = NewPhone();
        var firstAt = DateTimeOffset.UtcNow.AddDays(-10);

        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<LicenseDbContext>();
            for (var i = 0; i < 5; i++)
            {
                db.IysConsentEvents.Add(NoBrandEvent(
                    licenseId, noisyPhone, IysConsentEventType.LocalRevoke,
                    firstAt.AddDays(i)));
            }
            db.IysConsentEvents.Add(NoBrandEvent(
                licenseId, otherPhone, IysConsentEventType.LocalRevoke, firstAt));
            await db.SaveChangesAsync();
        }

        using (var replay = factory.Services.CreateScope())
        {
            var db = replay.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var collector = replay.ServiceProvider.GetRequiredService<IysConsentCollector>();

            (await collector.StageReplayNoBrandEventsAsync(licenseId, brandCode))
                .Should().Be(2,
                    "6 olay iki numaraya ait; iş listesi olay sayısıyla değil "
                    + "numara sayısıyla büyümeli");

            await db.SaveChangesAsync();
        }

        var row = await RowAsync(factory, brandCode, noisyPhone);
        row!.Status.Should().Be(IysConsentStatus.Ret);
        row.LastLocalEventAt.Should().Be(firstAt.AddDays(4),
            "uygulanan olay en yenisi olmalı — eskileri atlamak sonucu değiştirmez");

        (await RowAsync(factory, brandCode, otherPhone))!.Status
            .Should().Be(IysConsentStatus.Ret, "diğer numara atlanmamalı");
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
