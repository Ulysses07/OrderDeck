using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public class BarcodeAllocatorTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public void Format_on_haneye_sifirla_doldurur()
    {
        BarcodeAllocator.Format(1).Should().Be("0000000001");
        BarcodeAllocator.Format(9_999_999_999).Should().Be("9999999999");
    }

    [Fact]
    public async Task Ilk_ayirma_birden_baslar_ve_ardisik_verir()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        var sut = new BarcodeAllocator(db);

        var codes = await sut.AllocateAsync(licenseId, 3, default);

        codes.Should().Equal("0000000001", "0000000002", "0000000003");
    }

    [Fact]
    public async Task Ayirma_kaydetmez_sayaci_cagiran_isler()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        var sut = new BarcodeAllocator(db);

        await sut.AllocateAsync(licenseId, 1, default);

        // Ayırıcı kendi SaveChanges'ini çağırmıyor: sayaç ile varyant AYNI
        // iş biriminde işlenmeli. Çağırsaydı, sonraki doğrulama hatasında
        // sayaç ilerlemiş ama varyant yazılmamış olurdu.
        db.ChangeTracker.HasChanges().Should().BeTrue();
    }

    [Fact]
    public async Task Ikinci_ayirma_kaldigi_yerden_devam_eder()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        var sut = new BarcodeAllocator(db);

        await sut.AllocateAsync(licenseId, 2, default);
        await db.SaveChangesAsync();
        var second = await sut.AllocateAsync(licenseId, 2, default);

        second.Should().Equal("0000000003", "0000000004");
    }

    [Fact]
    public async Task Elle_alinmis_numaralar_atlanir()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        db.ProductVariants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ProductId = Guid.NewGuid(),
            Barcode = "0000000002",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var sut = new BarcodeAllocator(db);

        var codes = await sut.AllocateAsync(licenseId, 2, default);

        codes.Should().Equal("0000000001", "0000000003");
    }

    [Fact]
    public async Task Baska_lisansin_numarasi_engel_degildir()
    {
        var mine = Guid.NewGuid();
        await using var db = NewDb();
        db.ProductVariants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),   // BAŞKA lisans
            ProductId = Guid.NewGuid(),
            Barcode = "0000000001",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var sut = new BarcodeAllocator(db);

        var codes = await sut.AllocateAsync(mine, 1, default);

        codes.Should().Equal("0000000001");
    }

    [Fact]
    public async Task Ikinci_tur_zorunlu_olunca_dongu_tekrar_calisir()
    {
        // Döngünün sonlanma argümanı ("her turda Next en az 1 ilerler")
        // gerçekten işe yarıyor mu? Bu test bunu doğrular: ilk turda
        // tek aday alınmış çıkar, döngü ikinci tura geçmek ZORUNDA kalır.
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        db.ProductVariants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ProductId = Guid.NewGuid(),
            Barcode = "0000000001",   // sayacın ilk çıkaracağı numara alınmış
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var sut = new BarcodeAllocator(db);

        var codes = await sut.AllocateAsync(licenseId, 1, default);

        // 1. tur: aday "0000000001" alınmış → atlandı, Next=2, sonuç boş.
        // 2. tur: aday "0000000002" serbest → kabul edildi, Next=3.
        // Döndürülen kod:
        codes.Should().Equal("0000000002");
        // Sayaç muhasebesi: iki aday işlendi, Next 3'te olmalı.
        // Ayırıcı SaveChanges çağırmaz; sayaç henüz yalnızca change tracker'da.
        // Local koleksiyonu kaydedilmemiş izlenen varlıkları da görür.
        var counter = db.BarcodeCounters.Local.Single(c => c.LicenseId == licenseId);
        counter.Next.Should().Be(3);
    }

    [Fact]
    public async Task Sifir_veya_negatif_count_bos_liste_verir_sayaci_dokunmaz()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        var sut = new BarcodeAllocator(db);

        var codesZero = await sut.AllocateAsync(licenseId, 0, default);
        var codesNeg  = await sut.AllocateAsync(licenseId, -5, default);

        codesZero.Should().BeEmpty();
        codesNeg.Should().BeEmpty();
        // Erken dönüş sayacı hiç oluşturmamalı/güncellememelidir.
        db.ChangeTracker.HasChanges().Should().BeFalse();
    }
}
