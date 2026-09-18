using FluentAssertions;
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

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"iys-{Guid.NewGuid():N}").Options);

    private static IysConsentCollector Collector(LicenseDbContext db)
        => new(db, Options.Create(new NetgsmOptions { BrandCode = "731734" }),
            NullLogger<IysConsentCollector>.Instance);

    private static Task RecordAsync(
        IysConsentCollector c, bool consented, DateTimeOffset at, string phone = Phone)
        => c.RecordAsync(phone, consented, at, "Shopper", Guid.NewGuid(),
            ip: "203.0.113.7", userAgent: "test-agent");

    [Fact]
    public async Task Yeni_onay_Pending_olarak_yazilir_ve_son_tarih_hesaplanir()
    {
        using var db = NewDb();
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
        // 9 hane + baştaki 0 — Faz 1'den sonra normalize edilemez
        await RecordAsync(Collector(db), true, DateTimeOffset.UtcNow, phone: "0533466482");
        await db.SaveChangesAsync();

        db.IysConsents.Should().BeEmpty("E.164 olmayan numara tekil anahtarı kirletmez");
        var ev = await db.IysConsentEvents.SingleAsync();
        ev.ErrorCode.Should().Be("invalid-phone");
        ev.Status.Should().Be(IysConsentStatus.Unknown);
    }

    [Fact]
    public async Task Olay_kiraci_sutunlarini_saklar()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var consentId = Guid.NewGuid();

        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            Recipient = "+905551112233",
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = IysConsentEventType.LocalConsent,
            Status = IysConsentStatus.Onay,
            LicenseId = licenseId,
            BrandCode = "731734",
            IysConsentId = consentId,
        });
        await db.SaveChangesAsync();

        var ev = await db.IysConsentEvents.SingleAsync();
        ev.LicenseId.Should().Be(licenseId, "olay hangi yayıncıya ait olduğunu taşımalı");
        ev.BrandCode.Should().Be("731734",
            "aynı telefon A markasında ONAY, B'de RET olabilir — denetimde ayrışmalı");
        ev.IysConsentId.Should().Be(consentId, "olay durum satırına bağlanabilmeli");
    }

    [Fact]
    public async Task Kiraci_sutunlari_null_kabul_eder()
    {
        using var db = NewDb();
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(),
            Recipient = "+905551112233",
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = IysConsentEventType.LocalConsent,
            Status = IysConsentStatus.Onay,
        });
        await db.SaveChangesAsync();

        var ev = await db.IysConsentEvents.SingleAsync();
        ev.LicenseId.Should().BeNull(
            "prod'daki eski olaylar geriye dönük doldurulamaz; sütun NOT NULL olsaydı göç düşerdi");
        ev.BrandCode.Should().BeNull();
        ev.IysConsentId.Should().BeNull();
    }
}
