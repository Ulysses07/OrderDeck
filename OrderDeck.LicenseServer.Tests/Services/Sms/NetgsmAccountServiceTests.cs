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
    /// <summary><paramref name="databaseName"/> verilirse AYNI InMemory
    /// veritabanına ikinci bir bağlam açılabilir — eşzamanlılık yarışını
    /// kurmak için şart: jetonu "arkadan" kaydıran yazım, test edilen
    /// bağlamın izlemediği bir yerden gelmeli.</summary>
    private static LicenseDbContext NewDb(string? databaseName = null)
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"netgsm-{Guid.NewGuid():N}").Options);

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
        var userCode = NewUserCode();

        var acc = await svc.UpsertAsync(
            Guid.NewGuid(), $"  {userCode} ", $"pw-{Guid.NewGuid():N}",
            " ORDERDECK ", $" {brandCode} ", CancellationToken.None);

        acc.BrandCode.Should().Be(brandCode);
        acc.Header.Should().Be("ORDERDECK");
        acc.UserCode.Should().Be(userCode);
    }

    [Fact]
    public async Task Upsert_dogrulanmis_hesabi_kapatir()
    {
        // Kimlikler değiştiyse eski doğrulama geçersizdir: Verified korunursa
        // yanlış kimlikle "açık" duran bir kurulum kalır ve marka çözülmeye
        // devam eder. Yeni satırda Failed zaten enum varsayılanı (0) — asıl
        // güvenlik özelliği MEVCUT Verified satırın düşürülmesi, burada ölçülen o.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();

        var account = Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Verified);
        account.LastVerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        account.LastError = null;
        await db.SaveChangesAsync();

        var acc = await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        acc.Status.Should().Be(NetgsmAccountStatus.Failed,
            "kimlikler değişti: eski doğrulama artık geçerli değil");
        acc.LastVerifiedAt.Should().BeNull("bayat doğrulama damgası taşınmamalı");
        acc.LastError.Should().BeNull();

        // İzlenen nesne üstündeki iddialar yalnız "alan atandı mı"yı ölçer:
        // servis aynı bağlamı kullandığı için nesneyi bellekte değiştirmesi
        // yeter. Kalıcı hâli ayrıca oku — `AsNoTracking` InMemory'de saklanan
        // değerlerden YENİ örnek materyalize ediyor, yani gerçekten diske
        // ineni görüyoruz.
        var persisted = await db.NetgsmAccounts.AsNoTracking()
            .FirstAsync(a => a.LicenseId == licenseId);
        persisted.Status.Should().Be(NetgsmAccountStatus.Failed,
            "kapatma kaydedilmeliydi, yalnız bellekte kalmamalı");
        persisted.LastVerifiedAt.Should().BeNull();
        persisted.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_kosan_kampanyalari_duraklatir()
    {
        // Hesap Failed olduğu anda marka çözülemez; kampanya açık kalırsa işçi
        // izinsiz gönderime devam eder. Duraklatma hesap yazımıyla AYNI
        // SaveChanges içinde olmalı, yoksa arada açık bir pencere kalır.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var otherLicenseId = Guid.NewGuid();

        var pending = SeedCampaign(db, licenseId, "pending");
        var sending = SeedCampaign(db, licenseId, "sending");
        var foreignPending = SeedCampaign(db, otherLicenseId, "pending");
        await db.SaveChangesAsync();

        var pendingClaim = pending.ClaimedAt;
        var sendingClaim = sending.ClaimedAt;

        await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        pending.Status.Should().Be("paused");
        sending.Status.Should().Be("paused");
        foreignPending.Status.Should().Be(
            "pending", "başka yayıncının kampanyası bu kurulumdan etkilenmemeli");

        pending.ClaimedAt.Should().BeAfter(pendingClaim!.Value,
            "jeton ilerlemezse kampanyayı zaten okumuş işçi üstlenmeyi kazanır");
        sending.ClaimedAt.Should().BeAfter(sendingClaim!.Value);

        // Yukarıdaki iddialar İZLENEN örnekler üstünde: servis aynı bağlamı
        // kullandığı için onları bellekte değiştirmesi yeter ve duraklatma hiç
        // kaydedilmese bile yeşil kalırlar. Asıl değişmez "aynı SaveChanges'te
        // indi mi" — onu kalıcı hâlden oku (`AsNoTracking` InMemory'de saklanan
        // değerlerden yeni örnek materyalize ediyor).
        var persisted = await db.SmsCampaigns.AsNoTracking()
            .Where(c => c.LicenseId == licenseId).ToListAsync();
        persisted.Should().HaveCount(2);
        persisted.Should().OnlyContain(c => c.Status == "paused",
            "duraklatma hesap yazımıyla AYNI SaveChanges'te inmeli");

        var persistedForeign = await db.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.LicenseId == otherLicenseId);
        persistedForeign.Status.Should().Be("pending",
            "başka yayıncının kampanyası bu kurulumdan etkilenmemeli");
    }

    [Fact]
    public async Task Upsert_ileri_tarihli_jetonu_geri_almaz()
    {
        // `NextClaimedAt`'in monoton muhafızı: `UtcNow` monoton DEĞİL. Jeton
        // ileri tarihliyken ham `UtcNow` ataması onu GERİ alır ve kapatmadan
        // ÖNCE kampanyayı okumuş işçi üstlenme yazımını kazanır — yani
        // duraklatma sessizce delinir.
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var originalClaim = DateTimeOffset.UtcNow.AddMinutes(5);

        var campaign = SeedCampaign(db, licenseId, "sending", originalClaim);
        await db.SaveChangesAsync();

        await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        campaign.ClaimedAt.Should().Be(originalClaim.AddTicks(1),
            "saat jetonun gerisindeyken tek güvenli sonraki değer özgün + 1 tik");
    }

    /// <summary><paramref name="claimedAt"/> verilmezse jeton GEÇMİŞTE kalır
    /// ve <c>NextClaimedAt</c> hep "saat ilerledi" kolunu seçer; ileri tarihli
    /// jeton veren test monoton muhafızın öbür kolunu koşturur.</summary>
    private static SmsCampaign SeedCampaign(
        LicenseDbContext db, Guid licenseId, string status,
        DateTimeOffset? claimedAt = null)
    {
        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            MessageBody = "Kurulum testi",
            SegmentsPerMessage = 1,
            RecipientCount = 1,
            Status = status,
            ClaimedAt = claimedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedByCustomerId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        db.SmsCampaigns.Add(campaign);
        return campaign;
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

        var account = Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Disabled);

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

    [Fact]
    public async Task Upsert_cakisma_firlatirken_izlenen_nesne_birakmaz()
    {
        // `UpsertAsync` paylaşılan scoped bağlamda çalışıyor. Çakışmayla
        // fırlarken yarı-yazılmış hesabı izleniyor bırakırsa, o scope'ta
        // atılacak SONRAKİ herhangi bir `SaveChanges` onu kimsenin karar
        // vermediği bir anda diske basar. Kardeş metot
        // `CloseAccountAndPauseCampaignsAsync` için aynı iddia kuruluyor;
        // simetri burada da ölçülmeli.
        var databaseName = $"netgsm-{Guid.NewGuid():N}";
        using var db = NewDb(databaseName);
        var licenseId = Guid.NewGuid();

        Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Failed);

        // Jetonu ARKADAN kaydır: ikinci bağlam aynı satırı yazıyor, `db`'nin
        // izlediği kopyanın özgün sürümü bayatlıyor. Yeni jeton değerini
        // `LicenseDbContext.StampNetgsmAccountVersions()` üretiyor — burada
        // önemli olan yazımın BAŞKA bir bağlamdan gelmesi.
        using (var other = NewDb(databaseName))
        {
            var row = await other.NetgsmAccounts.SingleAsync(a => a.LicenseId == licenseId);
            row.UpdatedAt = row.UpdatedAt.AddMinutes(1);
            await other.SaveChangesAsync();
        }

        Func<Task> write = async () => await Service(db).UpsertAsync(
            licenseId, NewUserCode(), $"pw-{Guid.NewGuid():N}", "ORDERDECK",
            NewBrandCode(), CancellationToken.None);

        await write.Should().ThrowAsync<DbUpdateConcurrencyException>();

        db.ChangeTracker.Entries().Should().BeEmpty(
            "fırlatmadan önce temizlenmeli: kirli nesne çağıranın sonraki "
            + "SaveChanges'ine biner");
    }

    [Fact]
    public async Task Ayrilis_kapatmasi_DisabledAt_damgalar()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var account = Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Verified);

        var before = DateTimeOffset.UtcNow;

        await Service(db).CloseAccountAndPauseCampaignsAsync(
            account.Id, NetgsmAccountStatus.Disabled,
            "Yönetici tarafından kapatıldı.", CancellationToken.None, departure: true);

        var persisted = await db.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.Id == account.Id);
        persisted.DisabledAt.Should().NotBeNull();
        persisted.DisabledAt!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task Zaten_Disabled_hesabi_tekrar_kapatmak_DisabledAt_i_ilerletmez()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var account = Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Disabled);
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        account.DisabledAt = t0;
        await db.SaveChangesAsync();

        await Service(db).CloseAccountAndPauseCampaignsAsync(
            account.Id, NetgsmAccountStatus.Disabled,
            "Yönetici tarafından kapatıldı.", CancellationToken.None, departure: true);

        var persisted = await db.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.Id == account.Id);
        persisted.DisabledAt.Should().Be(t0, "saklama saati İLK geçişten sayılır (§6)");
    }

    [Fact]
    public async Task Sistem_kaynakli_kapatma_DisabledAt_damgalamaz()
    {
        using var db = NewDb();
        var licenseId = Guid.NewGuid();
        var account = Seed(db, licenseId, NewBrandCode(), NetgsmAccountStatus.Verified);

        await Service(db).CloseAccountAndPauseCampaignsAsync(
            account.Id, NetgsmAccountStatus.Disabled,
            NetgsmAccountService.UndecryptableMessage, CancellationToken.None);

        var persisted = await db.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.Id == account.Id);
        persisted.Status.Should().Be(NetgsmAccountStatus.Disabled);
        persisted.DisabledAt.Should().BeNull(
            "anahtar halkası kaybı ayrılış değildir — §6 saklama saati başlamaz");
    }
}
