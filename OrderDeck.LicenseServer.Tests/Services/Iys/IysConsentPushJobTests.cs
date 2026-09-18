using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

public class IysConsentPushJobTests
{
    private sealed class FakeIysClient : IIysClient
    {
        public List<IReadOnlyList<IysConsentRecord>> AddCalls { get; } = new();
        public List<IysAccountContext> AddAccounts { get; } = new();
        public Func<IReadOnlyList<IysConsentRecord>, IysAddResult>? AddBehavior { get; set; }
        public Exception? ThrowOnAdd { get; set; }

        /// <summary>Marka kodu → o markada fırlatılacak hata. Marka yalıtımı testleri için.</summary>
        public Dictionary<string, Exception> ThrowByBrand { get; } = new();

        public Task<IysAddResult> AddAsync(
            IysAccountContext account, IReadOnlyList<IysConsentRecord> items,
            CancellationToken ct = default)
        {
            AddAccounts.Add(account);
            AddCalls.Add(items);
            if (ThrowByBrand.TryGetValue(account.BrandCode, out var brandEx)) throw brandEx;
            if (ThrowOnAdd is not null) throw ThrowOnAdd;
            return Task.FromResult(AddBehavior?.Invoke(items)
                ?? new IysAddResult("0", "{\"code\":\"0\"}", Queued: true));
        }

        public Task<IysSearchResult> SearchAsync(
            IysAccountContext account, IReadOnlyList<string> recipients,
            CancellationToken ct = default)
            => Task.FromResult(new IysSearchResult("0", "{}", new Dictionary<string, IysConsentStatus>()));
    }

    private static readonly Guid LicenseA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LicenseB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string BrandA = "731734";
    private const string BrandB = "763208";

    // Tek sağlayıcı: ProtectPassword ile korunan metni aynı anahtarla çözebilmek
    // için testler boyunca paylaşılıyor. Her yeni Ephemeral örneği yeni anahtar üretir.
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static NetgsmAccountService Accounts(LicenseDbContext db) => new(db, Protection);

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-push-{Guid.NewGuid():N}").Options);

    private static IysConsentPushJob Job(LicenseDbContext db, IIysClient client)
        => new(db, client, Accounts(db), Options.Create(new NetgsmOptions()),
            NullLogger<IysConsentPushJob>.Instance);

    private static void SeedAccount(LicenseDbContext db, Guid licenseId, string brandCode)
    {
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static IysConsent Pending(string phone, string brandCode = BrandA) => new()
    {
        Id = Guid.NewGuid(),
        BrandCode = brandCode,
        ChannelType = "MESAJ",
        RecipientType = "BIREYSEL",
        Recipient = phone,
        Status = IysConsentStatus.Onay,
        ConsentDate = DateTimeOffset.UtcNow,
        SourceCode = "HS_WEB",
        PushState = IysPushState.Pending,
        PushDeadline = DateTimeOffset.UtcNow.AddDays(3),
        LastLocalEventAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<LicenseDbContext> SeedAsync(int count)
    {
        var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        for (var i = 0; i < count; i++)
            db.IysConsents.Add(Pending($"+90555{i:D7}"));
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Code_sifir_Confirmed_YAPMAZ_yalnizca_Pushed()
    {
        // 2026-09-17'de 284 kaydı kaybettiren hata tam olarak buydu:
        // "code 0" kuyruğa alındı demek, kabul edildi değil. Kabul kararını
        // yalnız IysConsentVerifyJob (/iys/search) verebilir.
        using var db = await SeedAsync(1);
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Pushed);
        row.PushState.Should().NotBe(IysPushState.Confirmed);
        row.LastVerifiedStatus.Should().BeNull("İYS'ye henüz sorulmadı");
        row.NextVerifyAt.Should().NotBeNull("doğrulama randevusu alınmalı");
    }

    [Fact]
    public async Task Ham_yanit_olay_tablosuna_yazilir()
    {
        using var db = await SeedAsync(1);

        await Job(db, new FakeIysClient()).RunAsync();

        var ev = await db.IysConsentEvents
            .SingleAsync(e => e.EventType == IysConsentEventType.PushAttempt);
        ev.ApiResponseCode.Should().Be("0");
        ev.ApiResponseBody.Should().Contain("code");
    }

    [Fact]
    public async Task Bekleyenler_yirmiserli_partilenir()
    {
        using var db = await SeedAsync(45);
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Select(c => c.Count).Should().Equal(20, 20, 5);
    }

    [Fact]
    public async Task Gecici_hata_Failed_birakir_deadline_icinde_yeniden_denenir()
    {
        using var db = await SeedAsync(1);
        var client = new FakeIysClient { ThrowOnAdd = new HttpRequestException("ağ") };

        await Job(db, client).RunAsync();

        var row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Failed);
        row.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Son_tarihi_gecmis_kayit_itilmez_Expired_olur()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var row = Pending("+905551112233");
        row.PushDeadline = DateTimeOffset.UtcNow.AddHours(-1);
        db.IysConsents.Add(row);
        await db.SaveChangesAsync();
        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Should().BeEmpty("süresi dolmuş kayıt İYS'ye gönderilmez");
        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Expired);
    }

    [Fact]
    public async Task Yapilandirma_hatasi_yalniz_o_markanin_kalan_partilerini_durdurur()
    {
        // Tek markanın 45 kaydı: ilk parti yapılandırma hatasıyla düşünce o
        // markanın kalan partileri denenmez (hepsi aynı hatayla düşecek),
        // ama iş ARTIK FIRLATMIYOR — döngü diğer markalara devam etmeli.
        using var db = await SeedAsync(45);
        var client = new FakeIysClient();
        client.ThrowByBrand[BrandA] = new IysConfigurationException("60", "marka kodu");

        await Job(db, client).RunAsync();

        client.AddCalls.Should().HaveCount(1, "ilk partiden sonra bu marka için durmalı");
        (await db.IysConsents.CountAsync(c => c.PushState == IysPushState.Pending))
            .Should().Be(45, "ayar hatası kayıt başına kalıcı yara olarak yazılmaz");
    }

    [Fact]
    public async Task Bir_yayincinin_bozuk_ayari_digerinin_pushunu_durdurmaz()
    {
        // Spec sözleşme #3. Bu test bugünkü davranışın TERSİNİ istiyor.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pending("+905551110001", BrandA));
        db.IysConsents.Add(Pending("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();
        client.ThrowByBrand[BrandA] = new IysConfigurationException("60", "marka kodu");

        await Job(db, client).RunAsync();

        var a = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandA);
        var b = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandB);
        a.PushState.Should().Be(IysPushState.Pending, "A'nın ayarı bozuk, kaydı bekliyor");
        b.PushState.Should().Be(IysPushState.Pushed, "B, A'nın hatasından etkilenmemeli");
    }

    [Fact]
    public async Task Her_parti_kendi_markasinin_kimligiyle_gider()
    {
        // Spec sözleşme #11'in push ucu: istek markası = itilen satırın markası.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pending("+905551110001", BrandA));
        db.IysConsents.Add(Pending("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddAccounts.Should().HaveCount(2);
        for (var i = 0; i < client.AddCalls.Count; i++)
        {
            var brand = client.AddAccounts[i].BrandCode;
            var expectedPhone = brand == BrandA ? "+905551110001" : "+905551110002";
            client.AddCalls[i].Select(r => r.Recipient).Should().Equal(expectedPhone);
        }
    }

    [Fact]
    public async Task Dogrulanmamis_hesabin_kayitlari_itilmez()
    {
        // Fail-closed: hesap Verified değilse o markanın adına konuşamayız.
        using var db = NewDb();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseA,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = Accounts(db).ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = BrandA,
            Status = NetgsmAccountStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.IysConsents.Add(Pending("+905551110001", BrandA));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        client.AddCalls.Should().BeEmpty();
        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Pending);
    }

    [Fact]
    public async Task Sifresi_cozulemeyen_hesap_Failed_isaretlenir_digeri_itilmeye_devam_eder()
    {
        // Anahtar döndüyse şifre çözülemez. Fırlatmak diğer yayıncıları
        // susturur, sessizce atlamak hesabı Verified göstermeye devam ederdi:
        // hesap görünür biçimde bozulmalı ki yayıncı yeniden bağlansın.
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        SeedAccount(db, LicenseB, BrandB);

        // Başka bir anahtarla korunan metin: bu sağlayıcı onu çözemez.
        var foreign = new EphemeralDataProtectionProvider()
            .CreateProtector("OrderDeck.Netgsm.Password.v1")
            .Protect($"pw-{Guid.NewGuid():N}");
        var broken = await db.NetgsmAccounts.SingleAsync(a => a.LicenseId == LicenseA);
        broken.PasswordProtected = foreign;

        db.IysConsents.Add(Pending("+905551110001", BrandA));
        db.IysConsents.Add(Pending("+905551110002", BrandB));
        await db.SaveChangesAsync();

        var client = new FakeIysClient();

        await Job(db, client).RunAsync();

        var acct = await db.NetgsmAccounts.SingleAsync(a => a.LicenseId == LicenseA);
        acct.Status.Should().Be(NetgsmAccountStatus.Failed, "çözülemeyen şifre hesabı kapatmalı");
        acct.LastError.Should().NotBeNullOrEmpty();

        var a = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandA);
        var b = await db.IysConsents.SingleAsync(c => c.BrandCode == BrandB);
        a.PushState.Should().Be(IysPushState.Pending, "A itilmedi");
        b.PushState.Should().Be(IysPushState.Pushed, "B, A'nın anahtar sorunundan etkilenmemeli");
    }

    [Fact]
    public async Task Olaya_kiracinin_lisansi_ve_markasi_yazilir()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseB, BrandB);
        db.IysConsents.Add(Pending("+905551110002", BrandB));
        await db.SaveChangesAsync();

        await Job(db, new FakeIysClient()).RunAsync();

        var ev = await db.IysConsentEvents
            .SingleAsync(e => e.EventType == IysConsentEventType.PushAttempt);
        ev.LicenseId.Should().Be(LicenseB);
        ev.BrandCode.Should().Be(BrandB);
    }
}
