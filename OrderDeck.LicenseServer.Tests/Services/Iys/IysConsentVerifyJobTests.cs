using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentVerifyJobTests
{
    private sealed class FakeIysClient : IIysClient
    {
        public Dictionary<string, IysConsentStatus> Answer { get; set; } = new();
        public List<IReadOnlyList<string>> SearchCalls { get; } = new();
        public List<IysAccountContext> SearchAccounts { get; } = new();

        /// <summary>Marka kodu → o markada fırlatılacak hata.</summary>
        public Dictionary<string, Exception> ThrowByBrand { get; } = new();

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
            => Task.FromResult(new IysAddResult("0", "{}", true));

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
        {
            SearchAccounts.Add(account);
            SearchCalls.Add(recipients);
            if (ThrowByBrand.TryGetValue(account.BrandCode, out var ex)) throw ex;
            return Task.FromResult(new IysSearchResult("0", "{\"code\":\"0\"}", Answer));
        }
    }

    /// <summary>
    /// Bir markanın <c>SaveChangesAsync</c>'ini BİR KEZ patlatır: gerçek
    /// hayatta kirli change tracker'ı bırakan yol budur. Tek kez olması şart —
    /// sonraki markanın kaydını da patlatsaydı sızıntı zaten oluşamaz, test
    /// yanlış sebeple yeşil kalırdı.
    /// </summary>
    private sealed class FailOnceOnSaveInterceptor : SaveChangesInterceptor
    {
        private bool _fired;

        public string? FailBrand { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken ct = default)
        {
            if (!_fired && FailBrand is not null
                && eventData.Context!.ChangeTracker.Entries<IysConsentEvent>()
                    .Any(e => e.State == EntityState.Added && e.Entity.BrandCode == FailBrand))
            {
                _fired = true;
                throw new InvalidOperationException("veritabanı yazımı düştü");
            }

            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private const string Phone = "+905551112233";
    private static readonly Guid LicenseA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LicenseB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static NetgsmAccountService Accounts(LicenseDbContext db) => new(db, Protection);

    private static LicenseDbContext NewDb(params IInterceptor[] interceptors)
        => NewDb($"iys-verify-{Guid.NewGuid():N}", interceptors);

    /// <summary>Adı verilen InMemory veritabanına ikinci bir context açar —
    /// yazımın gerçekten kalıcı olduğunu temiz bir context'ten okuyarak
    /// doğrulamak için (aynı context identity-map yüzünden totoloji olur).</summary>
    private static LicenseDbContext NewDb(string name, params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(interceptors).Options);

    private static IysConsentVerifyJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Accounts(db), NullLogger<IysConsentVerifyJob>.Instance);

    private static void SeedAccount(
        LicenseDbContext db, Guid licenseId, string brandCode,
        DateTimeOffset? createdAt = null)
    {
        var stamp = createdAt ?? DateTimeOffset.UtcNow;
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = stamp,
            UpdatedAt = stamp,
        });
        db.SaveChanges();
    }

    private static IysConsent Pushed(
        string phone, string brandCode, DateTimeOffset? nextVerifyAt = null, int attempts = 0)
    {
        var now = DateTimeOffset.UtcNow;
        return new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = brandCode,
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = phone,
            Status = IysConsentStatus.Onay,
            ConsentDate = now.AddMinutes(-30),
            PushState = IysPushState.Pushed,
            PushDeadline = now.AddDays(3),
            LastPushedAt = now.AddMinutes(-20),
            VerifyAttempts = attempts,
            NextVerifyAt = nextVerifyAt ?? now.AddMinutes(-1),
            LastLocalEventAt = now.AddMinutes(-30),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static async Task<LicenseDbContext> SeedPushedAsync(
        DateTimeOffset? nextVerifyAt = null, int attempts = 0)
    {
        var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        db.IysConsents.Add(Pushed(Phone, BrandA, nextVerifyAt, attempts));
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task ONAY_donerse_Confirmed_olur()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Onay);
        row.LastVerifiedAt.Should().NotBeNull();
        row.PushState.Should().Be(IysPushState.Confirmed);
        IysConsentGate.CanSend(row).Should().BeTrue();
    }

    [Fact]
    public async Task RET_donerse_yerel_ONAY_ezilmez_ama_gonderim_kesilir()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Onay, "kişinin bize verdiği onay ispat olarak durur");
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Ret);
        IysConsentGate.CanSend(row).Should().BeFalse();
    }

    [Fact]
    public async Task Randevusu_gelmemis_kayit_sorgulanmaz()
    {
        using var db = await SeedPushedAsync(nextVerifyAt: DateTimeOffset.UtcNow.AddHours(1));
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.SearchCalls.Should().BeEmpty(
            "İYS işleme anlık değil; erken sorgu işlenmemiş kaydı RET sanar");
    }

    [Fact]
    public async Task ONAY_gelmezse_bir_sonraki_randevu_alinir()
    {
        using var db = await SeedPushedAsync(attempts: 0);
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.VerifyAttempts.Should().Be(1);
        row.NextVerifyAt.Should().NotBeNull();
        row.PushState.Should().Be(IysPushState.Pushed, "takvim bitmeden karar kesinleşmez");
    }

    [Fact]
    public async Task Takvim_tukenince_Failed_olur_ve_admin_listesine_duser()
    {
        using var db = await SeedPushedAsync(attempts: 3);
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Ret } };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Failed);
        row.NextVerifyAt.Should().BeNull();
        row.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sonuc_olay_tablosuna_yazilir()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient { Answer = { [Phone] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var ev = await db.IysConsentEvents
            .SingleAsync(e => e.EventType == IysConsentEventType.SearchResult);
        ev.Status.Should().Be(IysConsentStatus.Onay);
        ev.ApiResponseCode.Should().Be("0");
        ev.LicenseId.Should().Be(LicenseA, "kanıt kiracıya bağlanabilmeli");
        ev.BrandCode.Should().Be(BrandA);
    }

    [Fact]
    public async Task Marka_turu_yalniz_kendi_numaralarini_sorar_ve_kendi_kotasini_alir()
    {
        // Spec sözleşme #12. İki markanın kuyruğu da EŞİT öncelikli (aynı eski
        // randevu) ve tam kotalık; kayıtlar dönüşümlü ekleniyor ki global sıranın
        // ilk MaxPerBrandPerRun'ı iki markayı da içersin.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);

        var old = DateTimeOffset.UtcNow.AddHours(-2);
        var aPhones = new List<string>();
        var bPhones = new List<string>();
        for (var i = 0; i < IysConsentVerifyJob.MaxPerBrandPerRun; i++)
        {
            aPhones.Add($"+90555{i:D7}");
            bPhones.Add($"+90666{i:D7}");
            db.IysConsents.Add(Pushed(aPhones[i], BrandA, nextVerifyAt: old));
            db.IysConsents.Add(Pushed(bPhones[i], BrandB, nextVerifyAt: old));
        }
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        var askedForA = new List<string>();
        var askedForB = new List<string>();
        for (var i = 0; i < client.SearchCalls.Count; i++)
            (client.SearchAccounts[i].BrandCode == BrandA ? askedForA : askedForB)
                .AddRange(client.SearchCalls[i]);

        askedForA.Should().OnlyContain(p => aPhones.Contains(p),
            "onay MARKA bazlıdır: aynı numara A'da ONAY, B'de RET olabilir. "
            + "B'nin numarası A'nın kimliğiyle sorulursa A'nın cevabı B'nin "
            + "satırına yazılır — hiçbir hata fırlamaz, sessiz veri bozulması olur");
        askedForB.Should().OnlyContain(p => bPhones.Contains(p), "aynısı ters yönde");

        askedForA.Should().HaveCount(IysConsentVerifyJob.MaxPerBrandPerRun);
        askedForB.Should().HaveCount(IysConsentVerifyJob.MaxPerBrandPerRun,
            "koşu sınırı marka BAŞINA uygulanır; ortak havuz olsaydı B, A'nın "
            + "kuyruğunun arkasında yarım kotayla kalırdı (hat başı tıkanması)");
    }

    [Fact]
    public async Task Dusen_marka_turunun_kirli_kayitlari_sonraki_markayla_yazilmaz()
    {
        // Bütün marka turları aynı scoped DbContext'i paylaşıyor. A'nın
        // SaveChangesAsync'i patlarsa A'nın eklediği olaylar change tracker'da
        // askıda kalır; temizlenmezse B'nin turu SaveChanges dediğinde onlar da
        // yazılır — A'nın BAŞARISIZ turu B'nin turunda commit edilmiş olur.
        var fail = new FailOnceOnSaveInterceptor { FailBrand = BrandA };
        using var db = NewDb(fail);
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pushed("+905551110001", BrandA));
        db.IysConsents.Add(Pushed("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient { Answer = { ["+905551110002"] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var leaked = await db.IysConsentEvents.CountAsync(e => e.BrandCode == BrandA);
        leaked.Should().Be(0,
            "A'nın kaydedilemeyen doğrulama olayı B'nin SaveChanges'ine binerek "
            + "veritabanına girmemeli (çapraz kiracı yazma)");

        var b = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandB);
        b.PushState.Should().Be(IysPushState.Confirmed,
            "B'nin kendi turu A'nın arızasından bağımsız tamamlanmalı");
    }

    [Fact]
    public async Task Onceki_marka_patlasa_bile_cozulemeyen_sifrenin_hatasi_kalici_yazilir()
    {
        // Yukarıdaki Clear() düzeltmesinin doğurduğu SIRALAMAYA BAĞLI sessiz
        // kayıp. A'nın SaveChanges'i düşünce catch bloğu change tracker'ı
        // boşaltıyor; hesap listesi tracked gelseydi B'nin hesap nesnesi de
        // detach olur, "şifre çözülemedi" yazımı hiçbir hata vermeden
        // kaybolurdu — LastError'ın var olma sebebi olan görünürlük tam da
        // arıza anında yok olurdu.
        var name = $"iys-verify-{Guid.NewGuid():N}";
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        var fail = new FailOnceOnSaveInterceptor { FailBrand = BrandA };

        using (var db = NewDb(name, fail))
        {
            // A önce dönmeli: sıra CreatedAt'e göre, eşit damgaya güvenmiyoruz.
            SeedAccount(db, LicenseA, BrandA, createdAt: t0);
            SeedAccount(db, LicenseB, BrandB, createdAt: t0.AddMinutes(1));

            // B'nin şifresi başka bir anahtarla korunmuş: bu sağlayıcı çözemez.
            var foreign = new EphemeralDataProtectionProvider()
                .CreateProtector("OrderDeck.Netgsm.Password.v1")
                .Protect($"pw-{Guid.NewGuid():N}");
            var brokenB = await db.NetgsmAccounts.SingleAsync(a => a.LicenseId == LicenseB);
            brokenB.PasswordProtected = foreign;

            db.IysConsents.Add(Pushed("+905551110001", BrandA));
            db.IysConsents.Add(Pushed("+905551110002", BrandB));
            await db.SaveChangesAsync();

            await Job(db, new FakeIysClient()).RunAsync();
        }

        // TEMİZ context'ten oku: aynı context'ten okumak EF identity-map
        // totolojisi olur, bellekteki nesneyi doğrular, satırı değil.
        using var fresh = NewDb(name);
        var acct = await fresh.NetgsmAccounts.SingleAsync(a => a.LicenseId == LicenseB);
        acct.LastError.Should().NotBeNullOrEmpty(
            "LastError'ın tek amacı arızayı operatöre göstermek; önceki markanın "
            + "düşmesi bu görünürlüğü sessizce yok edemez");
        acct.Status.Should().Be(NetgsmAccountStatus.Verified,
            "anahtar arızası hesabı kapatmaz (yerleşik karar)");
    }

    [Fact]
    public async Task Gecici_hata_randevuyu_ILERI_alir_ama_takvimi_tuketmez()
    {
        using var db = await SeedPushedAsync();
        var client = new FakeIysClient();
        client.ThrowByBrand[BrandA] = new HttpRequestException("ağ");

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.NextVerifyAt.Should().BeAfter(DateTimeOffset.UtcNow,
            "randevu olduğu yerde kalırsa aynı kayıt her koşuda ilk sırayı kapar");
        row.VerifyAttempts.Should().Be(0,
            "VerifyAttempts İYS cevabını bekleme takvimidir; ağ hatası onu tüketmemeli");
        row.PushState.Should().Be(IysPushState.Pushed);
        row.UpdatedAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow,
            "UpdatedAt randevu değil dokunulma damgasıdır; geleceğe yazılırsa "
            + "satırı zaman aralığına göre süzen her sorgu yanılır");
    }

    [Fact]
    public async Task Sorgu_kendi_markasinin_kimligiyle_gider()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pushed("+905551110001", BrandA));
        db.IysConsents.Add(Pushed("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.SearchAccounts.Should().HaveCount(2);
        for (var i = 0; i < client.SearchCalls.Count; i++)
        {
            var expectedPhone = client.SearchAccounts[i].BrandCode == BrandA
                ? "+905551110001" : "+905551110002";
            client.SearchCalls[i].Should().Equal(expectedPhone);
        }
    }

    [Fact]
    public async Task Bir_yayincinin_yapilandirma_hatasi_digerini_durdurmaz()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pushed("+905551110001", BrandA));
        db.IysConsents.Add(Pushed("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient { Answer = { ["+905551110002"] = IysConsentStatus.Onay } };
        client.ThrowByBrand[BrandA] = new IysConfigurationException("60", "marka kodu");

        await Job(db, client).RunAsync();

        var b = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandB);
        b.PushState.Should().Be(IysPushState.Confirmed, "B, A'nın bozuk ayarından etkilenmemeli");
    }

    [Fact]
    public async Task Sifresi_cozulemeyen_hesap_Verified_kalir_turu_atlanir_digeri_sorulmaya_devam_eder()
    {
        // Push işiyle aynı karar: anahtar arızasında hesabı Failed işaretlemek
        // geri alınamaz (Failed → Verified dönen kod yolu yok, collector markayı
        // yalnız Verified hesaptan çözer). Turu atla, durumu koru, arızayı yaz.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);

        // Başka bir anahtarla korunan metin: bu sağlayıcı onu çözemez.
        var foreign = new EphemeralDataProtectionProvider()
            .CreateProtector("OrderDeck.Netgsm.Password.v1")
            .Protect($"pw-{Guid.NewGuid():N}");
        var broken = await db.NetgsmAccounts.SingleAsync(a => a.LicenseId == LicenseA);
        broken.PasswordProtected = foreign;

        db.IysConsents.Add(Pushed("+905551110001", BrandA));
        db.IysConsents.Add(Pushed("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient { Answer = { ["+905551110002"] = IysConsentStatus.Onay } };

        await Job(db, client).RunAsync();

        var acct = await db.NetgsmAccounts.SingleAsync(a => a.LicenseId == LicenseA);
        acct.Status.Should().Be(NetgsmAccountStatus.Verified,
            "geçici anahtar arızası o yayıncının yeni onay toplamasını da durdurmamalı");
        acct.LastError.Should().NotBeNullOrEmpty("arıza panelde görünür olmalı");

        client.SearchAccounts.Select(a => a.BrandCode).Should().NotContain(BrandA);
        var b = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandB);
        b.PushState.Should().Be(IysPushState.Confirmed,
            "B, A'nın anahtar sorunundan etkilenmemeli");
    }
}
