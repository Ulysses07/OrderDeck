using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

public class NetgsmAccountServiceTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"netgsm-{Guid.NewGuid():N}").Options);

    private static NetgsmAccountService Service(LicenseDbContext db)
        => new(db, new EphemeralDataProtectionProvider());

    private static NetgsmAccount Seed(
        LicenseDbContext db, Guid licenseId, string brandCode, NetgsmAccountStatus status)
    {
        var row = new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = "8503021111",
            PasswordProtected = "not-a-real-ciphertext",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.NetgsmAccounts.Add(row);
        db.SaveChanges();
        return row;
    }

    [Fact]
    public void Sifre_gidis_donus_ayni_metni_verir()
    {
        using var db = NewDb();
        var svc = Service(db);
        var raw = $"pw-{Guid.NewGuid():N}";

        var protectedPw = svc.ProtectPassword(raw);

        protectedPw.Should().NotBe(raw, "düz metin saklanmamalı");
        svc.TryUnprotectPassword(protectedPw).Should().Be(raw);
    }

    [Fact]
    public void Bozuk_sifreli_metin_null_doner()
    {
        using var db = NewDb();
        Service(db).TryUnprotectPassword("bu-gecerli-bir-payload-degil")
            .Should().BeNull("anahtar döndüyse çağıran hesabı disabled yapmalı, patlamamalı");
    }

    [Fact]
    public async Task Dogrulanmamis_hesabin_markasi_cozulmez()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        Seed(db, licenseId, "731734", status: NetgsmAccountStatus.Failed);

        var brand = await Service(db).GetBrandCodeAsync(licenseId, default);

        brand.Should().BeNull("fail-closed: doğrulanmamış hesap onay toplayamaz");
    }

    [Fact]
    public async Task Dogrulanmis_hesabin_markasi_cozulur()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        Seed(db, licenseId, "731734", status: NetgsmAccountStatus.Verified);

        (await Service(db).GetBrandCodeAsync(licenseId, default)).Should().Be("731734");
    }

    [Fact]
    public async Task Hesabi_olmayan_lisansin_markasi_null()
    {
        using var db = NewDb();
        (await Service(db).GetBrandCodeAsync(Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task ListVerifiedAsync_yalniz_dogrulanmis_hesaplari_doner()
    {
        using var db = NewDb();
        Seed(db, Guid.NewGuid(), "731734", NetgsmAccountStatus.Verified);
        Seed(db, Guid.NewGuid(), "763208", NetgsmAccountStatus.Disabled);

        var rows = await Service(db).ListVerifiedAsync(default);

        rows.Select(r => r.BrandCode).Should().Equal("731734");
    }
}
