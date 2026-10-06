using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>
/// TcknBackfillJob: var olan düz metin TCKN'leri (Shoppers.Tc,
/// IntakeFormSubmissions.Tckn) arka planda şifreler. Üretim DI'ından çözülür
/// (<c>GetRequiredService</c> kaydı da sınar — AddScoped olmasaydı fırlatırdı).
///
/// <para>Gerçek SQL Server gerekir: satır başına CAS <c>ExecuteUpdateAsync</c>
/// kullanıyor ve bu, InMemory sağlayıcıda desteklenmiyor (bkz.
/// <c>SqlServerContainerFixture</c> sınıf dokümanı). <see cref="IAsyncLifetime"/>
/// (IClassFixture DEĞİL) her test METODUNA kendi veritabanını veriyor — bu
/// yüzden ilk koşu sayıları ALT SINIR değil TAM sayı: bu sınıftaki başka bir
/// testin satırı bu testin veritabanına hiç sızamaz.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class TcknBackfillJobTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;

    public TcknBackfillJobTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        // Bağlantı dizesi ayrıca saklanıyor: aşağıdaki uçtan uca yarış testi
        // factory'nin DI'ı DIŞINDA, kendi interceptor'ını taşıyan AYRI bir
        // LicenseDbContext kurmak için buna ihtiyaç duyuyor.
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static string NewPhone() => $"+9055{Random.Shared.Next(10_000_000, 99_999_999)}";

    private static Shopper NewShopper(string? tc) => new()
    {
        Id = Guid.NewGuid(),
        FullName = "Backfill Shopper",
        Phone = NewPhone(),
        PasswordHash = $"h-{Guid.NewGuid():N}",
        Address = "Adres",
        TcProtected = tc,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Form satırları için yayıncı (Customer) + IntakeFormConfig —
    /// IntakeFormSubmission.Config navigasyonu gerektiriyor.</summary>
    private static async Task<IntakeFormConfig> SeedFormConfigAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "Backfill-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var cfg = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = $"bf-{Guid.NewGuid():N}"[..10],
            WhatsAppPhone = NewPhone(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.IntakeFormConfigs.Add(cfg);
        await db.SaveChangesAsync();
        return cfg;
    }

    [Fact]
    public async Task Duz_metin_TCKNleri_sifreler_ikinci_kosu_hicbir_sey_degistirmez()
    {
        Guid plaintextId, alreadyProtectedId, malformedId, blankId, formSubmissionId;
        string plaintextValue, alreadyProtectedCiphertext, malformedValue, formPlainValue;

        // ── Seed: 4 Shopper senaryosu (a-d) + 1 IntakeFormSubmission (e) ────
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();

            // (a) üretilmiş geçerli düz metin TC — şifrelenmeli.
            plaintextValue = TestTckn.NewValid();
            var shopperA = NewShopper(plaintextValue);
            plaintextId = shopperA.Id;

            // (b) önceden şifrelenmiş değer — dokunulmamalı (byte-eşit kalır).
            alreadyProtectedCiphertext = protector.Protect(TestTckn.NewValid())!;
            var shopperB = NewShopper(alreadyProtectedCiphertext);
            alreadyProtectedId = shopperB.Id;

            // (c) bozuk (10 haneli) eski düz metin — kayıt akışı eskiden TC'yi
            // doğrulamadan yazıyordu (bkz. TcknProtector.IsLegacyPlaintext
            // dokümanı). Biçimi bozuk olsa da legacy plaintext sayılır ve
            // olduğu gibi şifrelenmeli.
            malformedValue = TestTckn.NewValid()[..10];
            var shopperC = NewShopper(malformedValue);
            malformedId = shopperC.Id;

            // (d) yalnız boşluktan oluşan çok eski bir satır (normalize'den
            // önceki bir dönemi taklit eder) — Protect("   ") null döner.
            var shopperD = NewShopper("   ");
            blankId = shopperD.Id;

            db.Shoppers.AddRange(shopperA, shopperB, shopperC, shopperD);

            var cfg = await SeedFormConfigAsync(db);

            // (e) form gönderimi — düz metin Tckn, şifrelenmeli.
            formPlainValue = TestTckn.NewValid();
            var submission = new IntakeFormSubmission
            {
                Id = Guid.NewGuid(),
                IntakeFormConfigId = cfg.Id,
                Username = "backfilluser" + Guid.NewGuid().ToString("N")[..6],
                FullName = "Backfill Form User",
                Address = "Adres",
                TcknProtected = formPlainValue,
                SubmittedAt = DateTimeOffset.UtcNow,
            };
            db.IntakeFormSubmissions.Add(submission);
            formSubmissionId = submission.Id;

            await db.SaveChangesAsync();
        }

        // ── 1. koşu: (a),(c),(d) Shopper satırları + (e) form satırı şifrelenir.
        // Bu test metoduna ÖZEL, boş bir veritabanı (IAsyncLifetime) — tam
        // sayı güvenle sınanabilir.
        using (var scope = _factory.Services.CreateScope())
        {
            var firstRunCount = await scope.ServiceProvider.GetRequiredService<TcknBackfillJob>()
                .RunAsync(CancellationToken.None);
            firstRunCount.Should().Be(4);
        }

        // ── 2. koşu: artık hiçbir satır legacy plaintext değil → idempotent, 0.
        using (var scope = _factory.Services.CreateScope())
        {
            var secondRunCount = await scope.ServiceProvider.GetRequiredService<TcknBackfillJob>()
                .RunAsync(CancellationToken.None);
            secondRunCount.Should().Be(0);
        }

        // ── Doğrulama: satır başına beklenen son durum ──────────────────────
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var verifyProtector = verify.ServiceProvider.GetRequiredService<TcknProtector>();

        var a = await verifyDb.Shoppers.AsNoTracking().FirstAsync(s => s.Id == plaintextId);
        a.TcProtected.Should().StartWith("CfDJ8");
        verifyProtector.Unprotect(a.TcProtected).Should().Be(plaintextValue);

        var b = await verifyDb.Shoppers.AsNoTracking().FirstAsync(s => s.Id == alreadyProtectedId);
        b.TcProtected.Should().Be(alreadyProtectedCiphertext, "zaten şifreliydi — job dokunmamalı");

        var c = await verifyDb.Shoppers.AsNoTracking().FirstAsync(s => s.Id == malformedId);
        c.TcProtected.Should().StartWith("CfDJ8");
        verifyProtector.Unprotect(c.TcProtected).Should().Be(malformedValue);

        var d = await verifyDb.Shoppers.AsNoTracking().FirstAsync(s => s.Id == blankId);
        d.TcProtected.Should().BeNull("yalnız boşluk Protect'ten NULL döner");

        var form = await verifyDb.IntakeFormSubmissions.AsNoTracking().FirstAsync(s => s.Id == formSubmissionId);
        form.TcknProtected.Should().StartWith("CfDJ8");
        verifyProtector.Unprotect(form.TcknProtected).Should().Be(formPlainValue);
    }

    // ── Yarış testleri: CAS'ın "kendi okumadığı değerin üstüne yazmaz"
    // garantisini DETERMİNİSTİK biçimde kanıtlar — gerçek eşzamanlı thread
    // yerine satırı iki adım arasında bilerek değiştiriyoruz (bkz.
    // TcknBackfillJob sınıf dokümanı — purge/PATCH yarışı). ───────────────────

    [Fact]
    public async Task Shopper_CAS_arada_purge_edilen_satiri_atlar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<TcknBackfillJob>();

        var plainA = TestTckn.NewValid();
        var shopper = NewShopper(plainA);
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();

        // İş A'yı OKUDUKTAN SONRA satır purge edilir (ShopperPurgeService
        // TcProtected'ı null yazar). Shopper'ın eşzamanlılık jetonları
        // (DeletedAt, LastResetCodeIssuedAt) burada DEĞİŞMİYOR — gerçek
        // purge'de de DeletedAt zaten doluysa (??=) aynı şey olurdu — bu
        // yüzden izlenen-entity + SaveChanges yolu bunu YAKALAYAMAZDI; CAS
        // doğrudan kolonu karşılaştırdığı için yakalıyor.
        await db.Shoppers.Where(s => s.Id == shopper.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.TcProtected, (string?)null));

        var changed = await job.EncryptShopperIfUnchangedAsync(shopper.Id, plainA, CancellationToken.None);
        changed.Should().Be(0, "satır arada purge edildi — iş kendi okumadığı değerin üstüne yazmamalı");

        var after = await db.Shoppers.AsNoTracking().FirstAsync(s => s.Id == shopper.Id);
        after.TcProtected.Should().BeNull("purge'ün sildiği kişisel veri geri gelmemeli");
    }

    [Fact]
    public async Task Shopper_CAS_arada_PATCH_edilen_satiri_atlar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
        var job = scope.ServiceProvider.GetRequiredService<TcknBackfillJob>();

        var plainA = TestTckn.NewValid();
        var plainB = TestTckn.NewValid();
        var shopper = NewShopper(plainA);
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();

        // İş A'yı okuduktan sonra kullanıcı PATCH /me ile B'yi yazar (gerçek
        // uçta da aynı _tckn.Protect(req.Tc) çağrısı) — A artık bayat.
        var cipherB = protector.Protect(plainB)!;
        await db.Shoppers.Where(s => s.Id == shopper.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.TcProtected, cipherB));

        var changed = await job.EncryptShopperIfUnchangedAsync(shopper.Id, plainA, CancellationToken.None);
        changed.Should().Be(0, "satır arada PATCH edildi — iş bayat A değerinin üstüne yazmamalı");

        var after = await db.Shoppers.AsNoTracking().FirstAsync(s => s.Id == shopper.Id);
        protector.Unprotect(after.TcProtected).Should().Be(plainB, "kazanan PATCH'in yazdığı B kaybolmamalı");
    }

    [Fact]
    public async Task Form_CAS_arada_silinen_satiri_sessizce_atlar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<TcknBackfillJob>();

        var cfg = await SeedFormConfigAsync(db);
        var plainA = TestTckn.NewValid();
        var submission = new IntakeFormSubmission
        {
            Id = Guid.NewGuid(),
            IntakeFormConfigId = cfg.Id,
            Username = "silinecekuser" + Guid.NewGuid().ToString("N")[..6],
            FullName = "Silinecek Form User",
            Address = "Adres",
            TcknProtected = plainA,
            SubmittedAt = DateTimeOffset.UtcNow,
        };
        db.IntakeFormSubmissions.Add(submission);
        await db.SaveChangesAsync();

        // İş A'yı okuduktan sonra satır tamamen silinir (örn. KVKK talebi).
        await db.IntakeFormSubmissions.Where(f => f.Id == submission.Id).ExecuteDeleteAsync();

        var changed = await job.EncryptFormSubmissionIfUnchangedAsync(submission.Id, plainA, CancellationToken.None);
        changed.Should().Be(0, "satır arada silindi — CAS'ın WHERE'i hiçbir satıra uymaz, istisna fırlamaz");

        (await db.IntakeFormSubmissions.AsNoTracking().AnyAsync(f => f.Id == submission.Id))
            .Should().BeFalse("silinen satır geri gelmemeli");
    }

    // ── Uçtan uca yarış testi: yukarıdaki üç test CAS yardımcı metotlarını
    // doğrudan çağırıyor — bu da güzel ama RunAsync'in GERÇEKTEN o yardımcıları
    // kullandığını (izlenen-entity + SaveChanges'e bir daha geri DÖNMEDİĞİNİ)
    // kanıtlamaz. Biri RunAsync'i yardımcıları atlayıp eski kalıba çevirse,
    // yukarıdaki üç test yine YEŞİL kalırdı — yalnız bu test yakalar. ────────

    /// <summary>
    /// RunAsync'in Shopper aday sorgusu (<c>SELECT ... FROM [Shoppers] ...</c>)
    /// kapanırken, AYRI bir SQL bağlantısından gerçek bir purge'ü taklit eder.
    ///
    /// <para><b>Neden <c>DataReaderClosingAsync</c>, <c>ReaderExecutedAsync</c>
    /// değil.</b> Aday sorgusu <c>ToListAsync</c> ile TÜM satırları belleğe
    /// alıyor; <c>ReaderExecutedAsync</c> okuma henüz BAŞLARKEN (okuyucu hâlâ
    /// açık, satırlar henüz tüketilmemiş) tetiklenir — o an ikinci bağlantıdan
    /// aynı satıra yazmak, ilk bağlantının okuma kilitleriyle KİLİTLENMEYE
    /// (deadlock) girebilirdi. <c>DataReaderClosingAsync</c> ise TÜM satırlar
    /// zaten belleğe alınıp okuyucu kapanırKEN tetiklenir — iş artık bayat bir
    /// LİSTE tutuyor, okuma kilitleri serbest — tam da CAS'ın kapatması
    /// gereken pencere budur: aday okunduktan SONRA, ilk UPDATE'ten ÖNCE.</para>
    ///
    /// <para><b>Filtre neden güvenilir.</b> <c>_fired</c> tek seferlik bayrak:
    /// aynı komut birden çok kapanma bildirimi üretse de taklit purge yalnız
    /// BİR kez çalışır. CAS'ın kendi UPDATE'i (<c>ExecuteUpdateAsync</c>)
    /// <c>ExecuteNonQueryAsync</c> ile gider — hiç okuyucu açmaz, bu interceptor
    /// metodunu TETİKLEMEZ. İzlenen-entity + SaveChanges'e bir regresyon olursa
    /// o yol SQL Server'da <c>UPDATE [Shoppers] ... OUTPUT ...</c> üretir
    /// (eşzamanlılık kontrolü için) — bu metin <c>"FROM [Shoppers]"</c> ALT
    /// DİZESİNİ içermez, yalnız SELECT'in <c>FROM [Shoppers] AS [s]</c> kalıbı
    /// içerir. Yani filtre yanlışlıkla CAS'ın ya da bir regresyonun kendi
    /// yazmasını da tetiklemez — sadece aday okuma sorgusunu yakalar.</para>
    /// </summary>
    private sealed class PurgeAfterShopperCandidateRead(string cs, Guid id) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("FROM [Shoppers]", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await using var conn = new SqlConnection(cs);          // ayrı bağlantı
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Shoppers SET Tc = NULL WHERE Id = @id"; // purge; jetonlara dokunmaz
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }
            return result;
        }
    }

    [Fact]
    public async Task RunAsync_ucdan_uca_CAS_kullanir_arada_purge_edilen_satiri_atlar()
    {
        Guid shopperId;
        TcknProtector protector;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();

            // DeletedAt ÖNCEDEN dolu — silme talebi daha önce açılmış demek.
            // Gerçek ShopperPurgeService.PurgeAsync bu durumda "DeletedAt ??=
            // now" yazdığı için DeletedAt'e dokunmaz — izlenen-entity +
            // SaveChanges yolunun hiçbir eşzamanlılık çatışması GÖRMEYECEĞİ
            // tam o senaryo. TcknProtector tekil (singleton) olduğu için
            // scope'un dışında da kullanılabilir.
            var shopper = NewShopper(TestTckn.NewValid());
            shopper.DeletedAt = DateTimeOffset.UtcNow.AddDays(-1);
            db.Shoppers.Add(shopper);
            await db.SaveChangesAsync();
            shopperId = shopper.Id;
        }

        // RunAsync'i DI dışında, interceptor'lı ÖZEL bir LicenseDbContext'le
        // çağırıyoruz — bkz. PurgeAfterShopperCandidateRead dokümanı.
        using var interceptedDb = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>()
                .UseSqlServer(_cs)
                .AddInterceptors(new PurgeAfterShopperCandidateRead(_cs, shopperId))
                .Options);
        var job = new TcknBackfillJob(interceptedDb, protector, NullLogger<TcknBackfillJob>.Instance);

        (await job.RunAsync(default)).Should().Be(0,
            "satır aday olarak okunduktan sonra purge edildi — RunAsync kendi okumadığı değerin üstüne yazmamalı");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var after = await verifyDb.Shoppers.AsNoTracking().FirstAsync(s => s.Id == shopperId);
        after.TcProtected.Should().BeNull("purge'ün sildiği kişisel veri geri gelmemeli");
    }

    // ── WpfCustomerProjections.Tckn (A5 — A1 incelemesi): sync ucu bu kolona
    // HER ZAMAN şifreli yazıyor; bekçi burada düz metin bulursa Protect'i
    // atlayan bir yazma yolu var demektir. Aynı satır başına CAS deseni. ──────

    /// <summary>Projeksiyon satırları için lisans (LicenseId FK'si) ve onun
    /// yayıncısı (Customer).</summary>
    private static async Task<Guid> SeedLicenseAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "Backfill-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            // LicenseKey HasMaxLength(40) — gerçek SQL'de tam Guid taşar.
            LicenseKey = "bf-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private static WpfCustomerProjection NewProjection(Guid licenseId, string? tckn) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        Platform = "tiktok",
        Username = "bf-" + Guid.NewGuid().ToString("N")[..8],
        TcknProtected = tckn,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Projeksiyon_duz_metin_TCKNsini_sifreler_ikinci_kosu_dokunmaz()
    {
        Guid plaintextId, alreadyProtectedId, emptyId;
        string plaintextValue, alreadyProtectedCiphertext;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<TcknProtector>();
            var licenseId = await SeedLicenseAsync(db);

            // (a) düz metin — Protect'i atlayan bir yolun bıraktığı satır.
            plaintextValue = TestTckn.NewValid();
            var plain = NewProjection(licenseId, plaintextValue);
            // (b) sync ucunun yazdığı gibi şifreli — dokunulmamalı.
            alreadyProtectedCiphertext = protector.Protect(TestTckn.NewValid())!;
            var alreadyProtected = NewProjection(licenseId, alreadyProtectedCiphertext);
            // (c) TCKN'siz satır — aday bile değil.
            var empty = NewProjection(licenseId, null);
            db.WpfCustomerProjections.AddRange(plain, alreadyProtected, empty);
            await db.SaveChangesAsync();
            (plaintextId, alreadyProtectedId, emptyId) = (plain.Id, alreadyProtected.Id, empty.Id);
        }

        // Bu test metoduna ÖZEL veritabanı (IAsyncLifetime) — tam sayı.
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<TcknBackfillJob>().RunAsync(CancellationToken.None))
                .Should().Be(1);
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<TcknBackfillJob>().RunAsync(CancellationToken.None))
                .Should().Be(0, "idempotent: şifreli satır yeniden şifrelenmez");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var vprotector = verify.ServiceProvider.GetRequiredService<TcknProtector>();
        var a = await vdb.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == plaintextId);
        a.TcknProtected.Should().StartWith("CfDJ8");
        vprotector.Unprotect(a.TcknProtected).Should().Be(plaintextValue);
        (await vdb.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == alreadyProtectedId))
            .TcknProtected.Should().Be(alreadyProtectedCiphertext, "zaten şifreliydi — job dokunmamalı");
        (await vdb.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == emptyId))
            .TcknProtected.Should().BeNull();
    }

    [Fact]
    public async Task Projeksiyon_CAS_arada_purge_edilen_satiri_atlar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<TcknBackfillJob>();
        var licenseId = await SeedLicenseAsync(db);

        var plainA = TestTckn.NewValid();
        var projection = NewProjection(licenseId, plainA);
        db.WpfCustomerProjections.Add(projection);
        await db.SaveChangesAsync();

        // İş A'yı OKUDUKTAN SONRA KVKK silmesi satırı boşaltır (MarkPurged:
        // TcknProtected = null, PurgedAt damgası) — bayat A'nın şifreli hâli
        // satıra geri yazılmamalı.
        await db.WpfCustomerProjections.Where(p => p.Id == projection.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.TcknProtected, (string?)null)
                .SetProperty(p => p.PurgedAt, DateTimeOffset.UtcNow));

        var changed = await job.EncryptProjectionIfUnchangedAsync(projection.Id, plainA, CancellationToken.None);
        changed.Should().Be(0, "satır arada purge edildi — iş kendi okumadığı değerin üstüne yazmamalı");

        var after = await db.WpfCustomerProjections.AsNoTracking().SingleAsync(p => p.Id == projection.Id);
        after.TcknProtected.Should().BeNull("purge'ün sildiği kişisel veri geri gelmemeli");
    }
}
