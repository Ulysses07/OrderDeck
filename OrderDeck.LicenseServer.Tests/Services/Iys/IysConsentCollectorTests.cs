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

public class IysConsentCollectorTests
{
    private const string Phone = "+905551112233";
    private const string BrandA = "731734";
    private static readonly Guid LicenseA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-{Guid.NewGuid():N}").Options);

    /// <summary>Netgsm abone numarası ÜRETİLİR: depo public ve sabit bir değer
    /// gerçek bir aboneye ait olabilir (bkz. NetgsmAccountUniqueIndexTests).</summary>
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    /// <summary>Doğrulanmış bir Netgsm hesabı tohumlar — markanın kaynağı artık bu.</summary>
    private static void SeedAccount(LicenseDbContext db, Guid licenseId, string brandCode)
    {
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private static IysConsentCollector Collector(LicenseDbContext db)
        => new(db,
            new NetgsmAccountService(db, new EphemeralDataProtectionProvider()),
            Options.Create(new NetgsmOptions()),
            NullLogger<IysConsentCollector>.Instance);

    private static Task RecordAsync(
        IysConsentCollector c, bool consented, DateTimeOffset at,
        string phone = Phone, Guid? licenseId = null)
        => c.RecordAsync(licenseId ?? LicenseA, phone, consented, at, "Shopper", Guid.NewGuid(),
            ip: "203.0.113.7", userAgent: "test-agent");

    [Fact]
    public async Task Yeni_onay_Pending_olarak_yazilir_ve_son_tarih_hesaplanir()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var at = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero); // Pazartesi

        await RecordAsync(Collector(db), consented: true, at);
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending);
        row.ConsentDate.Should().Be(at);
        row.PushDeadline.Should().Be(new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero));
        row.LastVerifiedStatus.Should().BeNull("İYS henüz sorulmadı");
    }

    [Fact]
    public async Task Ispat_alanlari_olay_tablosunda_yasar()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        await RecordAsync(Collector(db), true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var ev = await db.IysConsentEvents.SingleAsync();
        ev.EventType.Should().Be(IysConsentEventType.LocalConsent);
        ev.ProofIp.Should().Be("203.0.113.7");
        ev.ProofUserAgent.Should().Be("test-agent");
        ev.SourceTable.Should().Be("Shopper");
    }

    [Fact]
    public async Task Kural1_eski_onay_olayi_RET_i_diriltmez()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var c = Collector(db);
        var yeni = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
        var eski = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

        await RecordAsync(c, consented: false, yeni);   // önce ret (daha yeni)
        await db.SaveChangesAsync();
        await RecordAsync(c, consented: true, eski);    // sonra ESKİ onay işlenir
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Ret, "geç işlenen eski onay reddi ezmez");
        row.LastLocalEventAt.Should().Be(yeni);
        db.IysConsentEvents.Count().Should().Be(2, "olay yine de kaydedilir");
    }

    [Fact]
    public async Task Kural1_daha_yeni_onay_RET_i_yukseltir()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var c = Collector(db);
        var eski = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);
        var yeni = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

        await RecordAsync(c, consented: false, eski);
        await db.SaveChangesAsync();
        await RecordAsync(c, consented: true, yeni);
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Onay);
        row.PushState.Should().Be(IysPushState.Pending, "yeni onay yeni push penceresi açar");
    }

    [Fact]
    public async Task Kural4_yerel_ret_dogrulamayi_beklemeden_gonderimi_keser()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var c = Collector(db);
        await RecordAsync(c, true, new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        // İYS onayı gelmiş gibi işaretle
        var row = await db.IysConsents.SingleAsync();
        row.LastVerifiedStatus = IysConsentStatus.Onay;
        row.PushState = IysPushState.Confirmed;
        await db.SaveChangesAsync();

        await RecordAsync(c, false, new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Ret);
        row.LastVerifiedStatus.Should().Be(IysConsentStatus.Onay,
            "İYS'nin cevabı uydurulmaz; kapı iki alanı BİRLİKTE okur");
        IysConsentGate.CanSend(row).Should().BeFalse();
    }

    [Fact]
    public async Task Expired_kayit_yeni_onayla_yeniden_acilir()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var c = Collector(db);
        await RecordAsync(c, true, new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        var row = await db.IysConsents.SingleAsync();
        row.PushState = IysPushState.Expired;
        await db.SaveChangesAsync();

        await RecordAsync(c, true, new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        (await db.IysConsents.SingleAsync()).PushState.Should().Be(IysPushState.Pending);
    }

    [Fact]
    public async Task Bozuk_numara_sessizce_atlanmaz_ama_kayit_satiri_acmaz()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        // 9 hane + baştaki 0 — Faz 1'den sonra normalize edilemez
        await RecordAsync(Collector(db), true, DateTimeOffset.UtcNow, phone: "0533466482");
        await db.SaveChangesAsync();

        db.IysConsents.Should().BeEmpty("E.164 olmayan numara tekil anahtarı kirletmez");
        var ev = await db.IysConsentEvents.SingleAsync();
        ev.ErrorCode.Should().Be("invalid-phone");
        ev.Status.Should().Be(IysConsentStatus.Unknown);
    }

    [Fact]
    public async Task Ayni_beyanin_tekrari_push_penceresini_yeniden_ACMAZ()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var c = Collector(db);

        await RecordAsync(c, consented: false, new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        // Beyan İYS'ye iletilmiş ve kalıcı bir hata kaydedilmiş gibi işaretle.
        var row = await db.IysConsents.SingleAsync();
        row.PushState = IysPushState.Pushed;
        row.VerifyAttempts = 3;
        row.LastError = "kalici-hata";
        await db.SaveChangesAsync();

        // Profil kaydı kutunun MEVCUT değerini gönderiyor: aynı RET tekrar geliyor.
        await RecordAsync(c, consented: false, new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Pushed,
            "aynı beyanın tekrarı İYS'ye yeni bir şey söylemez; her profil kaydında " +
            "yeniden itmek gereksiz trafik üretir");
        row.VerifyAttempts.Should().Be(3);
        row.LastError.Should().Be("kalici-hata",
            "sıfırlanırsa kalıcı bir gönderim hatası her profil kaydında görünmez olur");

        db.IysConsentEvents.Count().Should().Be(2,
            "ispat günlüğü ekle-only: beyan tekrar edilse de olay yazılır");
    }

    [Fact]
    public async Task Ayni_beyanin_tekrari_Failed_satirda_push_penceresini_ACAR()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var c = Collector(db);

        await RecordAsync(c, consented: false, new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        // Gönderim denendi ve DÜŞTÜ: beyan İYS'ye hâlâ ULAŞMADI.
        var row = await db.IysConsents.SingleAsync();
        row.PushState = IysPushState.Failed;
        row.VerifyAttempts = 3;
        row.LastError = "gecici-hata";
        await db.SaveChangesAsync();

        await RecordAsync(c, consented: false, new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        row = await db.IysConsents.SingleAsync();
        row.PushState.Should().Be(IysPushState.Pending,
            "atlama yalnız Pushed/Confirmed için geçerli; düşmüş bir gönderim " +
            "yeniden denenebilmeli, yoksa beyan İYS'ye hiç ulaşmaz");
        row.VerifyAttempts.Should().Be(0);
        row.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Degisen_beyan_Pushed_satirda_push_penceresini_ACAR()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        var c = Collector(db);

        await RecordAsync(c, consented: true, new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        // Onay İYS'ye iletildi ve orada kayıtlı.
        var row = await db.IysConsents.SingleAsync();
        row.PushState = IysPushState.Pushed;
        await db.SaveChangesAsync();

        // Kişi onayı GERİ ÇEKTİ: İYS'nin bildiği artık yanlış.
        await RecordAsync(c, consented: false, new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Ret);
        row.PushState.Should().Be(IysPushState.Pending,
            "atlama yalnız beyan DEĞİŞMEDİĞİNDE geçerli; geri çekme itilmezse " +
            "İYS bu kişiyi ONAY'lı görmeye devam eder ve 6563 ihlali doğar");
    }

    [Fact]
    public async Task Toplayici_olaya_kiraci_sutunlarini_DOLDURUR()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);

        await RecordAsync(Collector(db), consented: true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        var ev = await db.IysConsentEvents.SingleAsync();

        ev.LicenseId.Should().Be(LicenseA, "olay hangi yayıncıya ait olduğunu taşımalı");
        ev.BrandCode.Should().Be(BrandA,
            "marka artık ayardan değil hesaptan geliyor; denetimde A'nın ONAY'ı " +
            "B'nin RET'inden ayrışabilmeli");
        ev.IysConsentId.Should().Be(row.Id, "olay durum satırına bağlanabilmeli");
    }

    [Fact]
    public async Task Hesabi_olmayan_yayincida_satir_ACILMAZ()
    {
        using var db = NewDb();   // SeedAccount YOK — kurulumu bitmemiş yayıncı

        await RecordAsync(Collector(db), consented: true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        db.IysConsents.Should().BeEmpty(
            "markasız satır, kurulumu bitmemiş TÜM yayıncıların onayını tek satırda " +
            "çakıştırır — B'nin RET'i A'nın ONAY'ını sessizce ezerdi");

        var ev = await db.IysConsentEvents.SingleAsync();
        ev.ErrorCode.Should().Be("no-brand");
        ev.BrandCode.Should().BeNull();
        ev.LicenseId.Should().Be(LicenseA, "olay yine de kime ait olduğunu taşımalı");
        ev.ProofIp.Should().Be("203.0.113.7", "ispat kaybolmamalı");
    }

    [Fact]
    public async Task Ayna_satiri_yerel_ret_ile_SourceCode_yerellesir()
    {
        using var db = NewDb();
        SeedAccount(db, LicenseA, BrandA);
        // IysMirrorImportJob'un yazdığı ayna satırının aynısı: Onay/Confirmed,
        // SourceCode="IYS_MIRROR", LastLocalEventAt=default (henüz yerel olay yok).
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(), BrandCode = BrandA, ChannelType = "MESAJ",
            RecipientType = "BIREYSEL", Recipient = Phone,
            Status = IysConsentStatus.Onay,
            SourceCode = IysMirrorImportJob.SourceCodeMirror,
            PushState = IysPushState.Confirmed,
            LastLocalEventAt = default, NextVerifyAt = null,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // Kişi ayna satırındaki onayı geri çekiyor (yerel RET).
        await RecordAsync(Collector(db), consented: false,
            new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var row = await db.IysConsents.SingleAsync();
        row.Status.Should().Be(IysConsentStatus.Ret,
            "ayna satırı da geri çekilebilir — yerel RET her zaman kazanır");
        row.PushState.Should().Be(IysPushState.Pending,
            "beyan DEĞİŞTİ (Onay→Ret) — İYS'ye bildirilmesi gerekir");
        row.SourceCode.Should().Be(new NetgsmOptions().IysSourceCode,
            "İYS'nin `source` alanı tanımlı bir izin kaynağı olmalı — IYS_MIRROR " +
            "gitmemeli (İYS'de enumerated bir değer değil)");
    }

    [Fact]
    public async Task Dogrulanmamis_hesap_da_satir_ACMAZ()
    {
        using var db = NewDb();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = LicenseA,
            UserCode = NewUserCode(),
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = BrandA,
            Status = NetgsmAccountStatus.Failed,   // doğrulama düşmüş
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await RecordAsync(Collector(db), consented: true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        db.IysConsents.Should().BeEmpty("fail-closed: onay toplama verified'a bağlı");
        (await db.IysConsentEvents.SingleAsync()).ErrorCode.Should().Be("no-brand");
    }
}
