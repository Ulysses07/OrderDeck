using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// <see cref="IysConsentEvent"/> kiracı sütunlarının (LicenseId/BrandCode/
/// IysConsentId) eşlemesini ve "hiç yabancı anahtar yok" kararını çiviler.
///
/// <para>Toplayıcıya hiç dokunmadıkları için <c>IysConsentCollectorTests</c>
/// içinde değiller: orası toplayıcının davranışını anlatan dosya, burası
/// şemayı anlatan dosya.</para>
/// </summary>
public class IysConsentEventTenantColumnsTests
{
    private const string Phone = "+905551112233";
    private const string BrandCode = "731734";

    /// <summary>
    /// Adı VERİLEN InMemory veritabanı — aynı adı iki context'e geçirip yazma ile
    /// okumayı ayırabilelim diye. Tek context'te <c>SingleAsync</c> store'a hiç
    /// gitmez, kimlik haritasından az önce <c>Add</c> edilen NESNENİN KENDİSİNİ
    /// döndürür; o hâlde sütun <c>[NotMapped]</c> olsa bile iddialar geçer ve test
    /// eşlemeyi kanıtlamaz. Ayrı context kimlik haritasını tamamen devre dışı bırakır.
    /// </summary>
    private static LicenseDbContext Db(string name)
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(name).Options);

    [Fact]
    public async Task Olay_kiraci_sutunlarini_saklar()
    {
        var dbName = $"iys-tenant-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var consentId = Guid.NewGuid();

        await using (var write = Db(dbName))
        {
            write.IysConsentEvents.Add(new IysConsentEvent
            {
                Id = id,
                Recipient = Phone,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = IysConsentEventType.LocalConsent,
                Status = IysConsentStatus.Onay,
                LicenseId = licenseId,
                BrandCode = BrandCode,
                IysConsentId = consentId,
            });
            await write.SaveChangesAsync();
        }

        // Yeni context = boş kimlik haritası: aşağıdaki değerler store'dan geliyor.
        await using var read = Db(dbName);
        var ev = await read.IysConsentEvents.SingleAsync(e => e.Id == id);

        ev.LicenseId.Should().Be(licenseId, "olay hangi yayıncıya ait olduğunu taşımalı");
        ev.BrandCode.Should().Be(BrandCode,
            "aynı telefon A markasında ONAY, B'de RET olabilir — denetimde ayrışmalı");
        ev.IysConsentId.Should().Be(consentId, "olay durum satırına bağlanabilmeli");
    }

    [Fact]
    public async Task Kiraci_sutunlari_null_kabul_eder()
    {
        var dbName = $"iys-tenant-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();

        await using (var write = Db(dbName))
        {
            write.IysConsentEvents.Add(new IysConsentEvent
            {
                Id = id,
                Recipient = Phone,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = IysConsentEventType.LocalConsent,
                Status = IysConsentStatus.Onay,
            });
            await write.SaveChangesAsync();
        }

        await using var read = Db(dbName);
        var ev = await read.IysConsentEvents.SingleAsync(e => e.Id == id);

        ev.LicenseId.Should().BeNull(
            "prod'daki eski olaylar geriye dönük doldurulamaz; sütun NOT NULL olsaydı göç düşerdi");
        ev.BrandCode.Should().BeNull();
        ev.IysConsentId.Should().BeNull();
    }

    [Fact]
    public void Olay_tablosunun_hic_yabanci_anahtari_olmamali()
    {
        using var db = Db($"iys-tenant-{Guid.NewGuid():N}");

        db.Model.FindEntityType(typeof(IysConsentEvent))!.GetForeignKeys()
            .Should().BeEmpty(
                "LicenseId ve IysConsentId bilinçli olarak FK DEĞİL: FK konvansiyonla " +
                "birlikte OnDelete(Cascade) getirir ve lisans/durum satırı silindiğinde " +
                "6563 ispat olayları da silinir — tablo ekle-only olduğu için bu, kaydın " +
                "kaybolabileceği tek yoldur. Navigasyon özelliği eklemek de yeter: EF " +
                "adı konvansiyona uyan sütun üzerinden FK'yı sessizce kurar");
    }
}
