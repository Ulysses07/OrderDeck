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

    private static NetgsmAccountService Service(LicenseDbContext db, IDataProtectionProvider protection)
        => new(db, protection);

    /// <summary>Netgsm abone numarası ÜRETİLİR: depo public ve sabit bir değer
    /// gerçek bir aboneye ait olabilir (bkz. NetgsmAccountUniqueIndexTests).</summary>
    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private static NetgsmAccount Seed(
        LicenseDbContext db, Guid licenseId, string brandCode, NetgsmAccountStatus status)
    {
        var row = new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
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
        // Girdi ÜRETİLİYOR: sabit bir metin kasten geçersiz olsa bile sır
        // tarayıcısı fixture'ı gerçek parolandan ayırt edemiyor (depo public).
        Service(db).TryUnprotectPassword($"gecersiz-{Guid.NewGuid():N}")
            .Should().BeNull("anahtar döndüyse çağıran hesabı disabled yapmalı, patlamamalı");
    }

    [Fact]
    public void Baska_anahtarla_sifrelenmis_metin_null_doner()
    {
        using var db = NewDb();
        // Prod'da korktuğumuz senaryo bozuk girdi değil: anahtar klasörü kaybolur
        // veya döner, gerçek şifreli metin çözülemez hâle gelir.
        var eskiAnahtar = new EphemeralDataProtectionProvider();
        var yeniAnahtar = new EphemeralDataProtectionProvider();

        var protectedPw = Service(db, eskiAnahtar).ProtectPassword($"pw-{Guid.NewGuid():N}");

        Service(db, yeniAnahtar).TryUnprotectPassword(protectedPw)
            .Should().BeNull("anahtar halkası dönmüşse servis patlamadan null dönmeli");
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
    public async Task Baska_lisansin_dogrulanmis_markasi_sizmaz()
    {
        using var db = NewDb();
        Seed(db, Guid.NewGuid(), "731734", NetgsmAccountStatus.Verified);

        (await Service(db).GetBrandCodeAsync(Guid.NewGuid(), default))
            .Should().BeNull(
                "İYS onayı markaya bağlıdır: hesabı olmayan bir lisans, başka bir "
                + "yayıncının doğrulanmış markasıyla onay toplarsa onay o markanın "
                + "sahibi olmayan tarafa yazılır");
    }

    [Fact]
    public async Task ListVerifiedAsync_yalniz_dogrulanmis_hesaplari_doner()
    {
        using var db = NewDb();
        Seed(db, Guid.NewGuid(), "731734", NetgsmAccountStatus.Verified);
        Seed(db, Guid.NewGuid(), "763208", NetgsmAccountStatus.Disabled);
        // Failed = varsayılan durum: henüz doğrulanmamış her hesap burada.
        // Bu satır olmadan "Status != Disabled" filtresi de testi yeşil geçerdi.
        Seed(db, Guid.NewGuid(), "999999", NetgsmAccountStatus.Failed);

        var rows = await Service(db).ListVerifiedAsync(default);

        rows.Should()
            .ContainSingle("fail-closed: yalnız doğrulanmış hesap SMS gönderebilir")
            .Which.BrandCode.Should().Be("731734");
    }

    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();

    [Fact]
    public async Task Upsert_yeni_hesabi_DOGRULANMAMIS_acar()
    {
        using var db = NewDb();
        var svc = Service(db);

        var acc = await svc.UpsertAsync(
            Guid.NewGuid(), NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "fail-closed: doğrulama henüz koşmadı, satır kapalı doğar");
        acc.LastVerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_sifreyi_sifreli_saklar()
    {
        using var db = NewDb();
        var svc = Service(db);
        var raw = $"pw-{Guid.NewGuid():N}";

        var acc = await svc.UpsertAsync(
            Guid.NewGuid(), NewUserCode(), raw, "ORDERDECK", NewBrandCode(),
            CancellationToken.None);

        acc.PasswordProtected.Should().NotBe(raw, "düz metin şifre DB'ye yazılmaz");
        svc.TryUnprotectPassword(acc.PasswordProtected).Should().Be(raw);
    }

    [Fact]
    public async Task Upsert_bos_sifreyle_saklanani_korur()
    {
        // Panel şifreyi geri GÖSTERMİYOR (yalnız "girildi/girilmedi").
        // Yayıncı başlığını düzeltmek için formu kaydettiğinde şifre alanı boş
        // gelir; boşu kaydedersek çalışan kurulumu kendi elimizle bozarız.
        using var db = NewDb();
        var svc = Service(db);
        var licenseId = Guid.NewGuid();
        var raw = $"pw-{Guid.NewGuid():N}";
        var userCode = NewUserCode();
        var brandCode = NewBrandCode();

        await svc.UpsertAsync(licenseId, userCode, raw, "ORDERDECK", brandCode, CancellationToken.None);
        var acc = await svc.UpsertAsync(
            licenseId, userCode, null, "YENIBASLIK", brandCode, CancellationToken.None);

        svc.TryUnprotectPassword(acc.PasswordProtected).Should().Be(raw);
        acc.Header.Should().Be("YENIBASLIK");
    }

    [Fact]
    public async Task Upsert_ilk_kayitta_sifre_zorunlu()
    {
        using var db = NewDb();
        var svc = Service(db);

        var act = async () => await svc.UpsertAsync(
            Guid.NewGuid(), NewUserCode(), null, "ORDERDECK", NewBrandCode(),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Upsert_lisans_basina_tek_satir_tutar()
    {
        using var db = NewDb();
        var svc = Service(db);
        var licenseId = Guid.NewGuid();

        var first = await svc.UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);
        var second = await svc.UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        second.Id.Should().Be(first.Id, "ikinci kayıt YENİ satır açmamalı");
        (await db.NetgsmAccounts.CountAsync(a => a.LicenseId == licenseId)).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_alanlarin_bosluklarini_kirpar()
    {
        // Baştaki boşluk marka kodunu tekil indekste AYRI bir anahtar yapıyor
        // (bkz. NetgsmAccountUniqueIndexTests): " 731734" ile "731734" iki ayrı
        // satır olarak durabilir ve marka→hesap araması yalnız birini görür.
        using var db = NewDb();
        var svc = Service(db);
        var brandCode = NewBrandCode();

        var acc = await svc.UpsertAsync(
            Guid.NewGuid(), $"  {NewUserCode()} ", $"pw-{Guid.NewGuid():N}",
            " ORDERDECK ", $" {brandCode} ", CancellationToken.None);

        acc.BrandCode.Should().Be(brandCode);
        acc.Header.Should().Be("ORDERDECK");
        acc.UserCode.Should().NotStartWith(" ");
    }

    [Fact]
    public async Task Upsert_disabled_hesabi_acmaz()
    {
        // Admin kill switch'i servis katmanında tutuluyor: controller ön kontrolü
        // yalnız erken ve anlaşılır bir 409 üretmek için var. Kapı burada olmazsa
        // panelin ön kontrolü ile yazım arasındaki pencerede kapatılan hesap,
        // yayıncının kaydıyla yeniden açılır.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();

        var account = Seed(
            db,
            licenseId,
            Random.Shared.Next(100_000, 999_999).ToString(),
            NetgsmAccountStatus.Disabled);

        var originalPassword = account.PasswordProtected;

        Func<Task> write = async () =>
        {
            await Service(db).UpsertAsync(
                licenseId,
                NewUserCode(),
                $"pw-{Guid.NewGuid():N}",
                "ORDERDECK",
                account.BrandCode,
                CancellationToken.None);
        };

        await write.Should().ThrowAsync<NetgsmAccountDisabledException>();
        account.Status.Should().Be(NetgsmAccountStatus.Disabled);
        account.PasswordProtected.Should().Be(originalPassword);
    }
}
