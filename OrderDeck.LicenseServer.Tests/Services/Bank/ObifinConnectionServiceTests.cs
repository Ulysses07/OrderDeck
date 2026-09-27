using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class ObifinConnectionServiceTests
{
    /// <summary>Betikli sahte istemci: hesap listesi ve banka bağlantı listesi sabit, çağrılar kaydedilir.</summary>
    private sealed class StubObifin : IObifinClient
    {
        public List<ObifinAccountDto> Accounts { get; } = new();
        public List<ObifinBankConnectionDto> Connections { get; } = new();
        public List<(string Banka, IReadOnlyDictionary<string, string> Form)> Added { get; } = new();
        /// <summary>Her ekleme denemesinin Obifin'e gönderdiği etiket (<c>BankaApiAdi</c>) — başarısız denemeler dahil.</summary>
        public List<string> AttemptedLabels { get; } = new();
        public ObifinCredentials? LastCreds { get; private set; }
        public int ListAccountsCalls { get; private set; }
        public Exception? ListAccountsError { get; set; }
        /// <summary>Ayarlıysa banka bağlantısı ekleme çağrısı Obifin'e ULAŞMADAN bu istisnayı fırlatır.</summary>
        public Exception? AddBankConnectionError { get; set; }
        /// <summary>Ayarlıysa banka bağlantı listesi çağrısı bu istisnayı fırlatır (ekleme Obifin'de kalmıştır).</summary>
        public Exception? ListBankConnectionsError { get; set; }
        /// <summary>Ayarlıysa hesap listesi çağrısı ÇAĞIRANIN jetonunu iptal edip onunla iptal istisnası fırlatır
        /// (zaman aşımı değil, kullanıcı vazgeçti senaryosu).</summary>
        public CancellationTokenSource? CancelCallerOnListAccounts { get; set; }
        /// <summary>Her eklemede Obifin'in vereceği sıradaki BankaApiId; eklemeden sonra artar.</summary>
        public long NextBankaApiId { get; set; } = 4242;
        /// <summary>Ayarlıysa ekleme başarılı döner ama kayıt banka bağlantı listesinde görünmez.</summary>
        public bool HideAddedFromList { get; set; }

        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
        {
            LastCreds = c;
            ListAccountsCalls++;
            if (CancelCallerOnListAccounts is { } cts)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
            if (ListAccountsError is not null) throw ListAccountsError;
            return Task.FromResult<IReadOnlyList<ObifinAccountDto>>(Accounts);
        }
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
        {
            if (ListBankConnectionsError is not null) throw ListBankConnectionsError;
            return Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>(Connections);
        }
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        {
            AttemptedLabels.Add(f["BankaApiAdi"]);
            if (AddBankConnectionError is not null) throw AddBankConnectionError;
            Added.Add((b, f));
            // Obifin gerçekte Id döndürmüyor (doküman sessiz): listede etiketle bulunur.
            if (!HideAddedFromList) Connections.Add(new ObifinBankConnectionDto(NextBankaApiId++, b, f["BankaApiAdi"], true));
            return Task.CompletedTask;
        }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => Task.FromResult(new ObifinPage<ObifinTransactionDto>(Array.Empty<ObifinTransactionDto>(), p, 0, 0, ps));
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    /// <summary>Ortak kök: aynı adla açılan ikinci bağlam (taze okuma) kesicili/kesicisiz seçenek farkına
    /// rağmen aynı bellek deposunu görür.</summary>
    private static readonly InMemoryDatabaseRoot DbRoot = new();

    private static LicenseDbContext NewDb() => NewDb($"obifin-conn-{Guid.NewGuid():N}");

    private static LicenseDbContext NewDb(string name, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, DbRoot);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new(builder.Options);
    }

    /// <summary>SaveChanges'i, izleyicide EKLENMİŞ bir <see cref="BankAccount"/> varken patlatır: hesap
    /// yazımı sırasındaki DB hatası (tekil index yarışı, bağlantı kopması) senaryosu.</summary>
    private sealed class FailOnBankAccountInsert : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            => Check(eventData, result);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Check(eventData, result));

        private static InterceptionResult<int> Check(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (eventData.Context!.ChangeTracker.Entries<BankAccount>().Any(e => e.State == EntityState.Added))
                throw new DbUpdateException("Hesap yazımı patladı (test kesicisi).");
            return result;
        }
    }

    private static Guid SeedLicense(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"c-{Guid.NewGuid():N}@x", Name = "C",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        db.SaveChanges();
        return licenseId;
    }

    private static BankHasher NewHasher()
        => new(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));

    private static ObifinConnectionService Svc(LicenseDbContext db, IObifinClient client, ILogger<ObifinConnectionService>? log = null)
        => new(db, client, Protection, NewHasher(),
            Options.Create(new ObifinOptions()), log ?? NullLogger<ObifinConnectionService>.Instance);

    /// <summary>Her günlük satırını biçimlenmiş metin + (varsa) istisnanın tam metniyle toplar: loga ne girdiğini sınamak için.</summary>
    private sealed class RecordingLog : ILogger<ObifinConnectionService>
    {
        public List<string> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    private static string NewPw() => $"pw-{Guid.NewGuid():N}";
    private static string NewKey() => $"k-{Guid.NewGuid():N}";

    [Fact]
    public async Task Kimlik_sifreli_saklanir_ve_cozulup_istemciye_gider()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var pw = NewPw(); var key = NewKey();

        var conn = await svc.UpsertAsync(lic, "https://example.invalid", "api@x", pw, key, CancellationToken.None);
        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        conn.PasswordProtected.Should().NotContain(pw);
        conn.ApiKeyProtected.Should().NotContain(key);
        stub.LastCreds!.Password.Should().Be(pw);
        stub.LastCreds.ApiKey.Should().Be(key);
        result.Ok.Should().BeTrue();
        (await db.ObifinConnections.SingleAsync()).Status.Should().Be(ObifinConnectionStatus.Verified);
    }

    [Fact]
    public async Task Dogrulama_Obifin_hatasinda_Failed_ve_mesaj_saklanir()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin { ListAccountsError = new ObifinApiException(new[] { "Kullanici Bilgileri Hatali!" }) };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        result.Ok.Should().BeFalse();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Contain("Kullanici Bilgileri Hatali");
    }

    [Fact]
    public async Task Dogrulama_Obifin_hatasinda_LastError_mesajlarin_birlesimidir_on_ek_yok()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin { ListAccountsError = new ObifinApiException(new[] { "Birinci", "Ikinci" }) };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        // Admin ekranında Obifin'in kendi metni görünür; istisnanın "Obifin: " ön eki değil.
        (await db.ObifinConnections.SingleAsync()).LastError.Should().Be("Birinci | Ikinci");
        result.Error.Should().Be("Birinci | Ikinci");
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(ObifinProtocolException))]
    public async Task Dogrulama_gecici_hatada_Failed_ve_kisa_Turkce_mesaj_tur_adiyla(Type exceptionType)
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        // HttpRequestException'ın İngilizce ağ metni ya da vekil HTML'i admin ekranına çıkmaz; tür adı tanı için yeter.
        Exception error = exceptionType == typeof(ObifinProtocolException)
            ? new ObifinProtocolException("Obifin HTTP 502 /webservis/hesaplar/hesaplistesi/")
            : (Exception)Activator.CreateInstance(exceptionType, "Name or service not known")!;
        var stub = new StubObifin { ListAccountsError = error };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        result.Ok.Should().BeFalse();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Be($"Obifin'e ulaşılamadı ({exceptionType.Name})");
        result.Error.Should().Be(conn.LastError);
    }

    [Fact]
    public async Task Dogrulama_cagiranin_iptali_Failed_yazmaz_istisna_yukari_gider()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        using var cts = new CancellationTokenSource();
        var stub = new StubObifin { CancelCallerOnListAccounts = cts };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var act = () => svc.VerifyAsync(lic, cts.Token);

        // Kullanıcı sayfadan ayrıldı / istek düştü: bu Obifin'in suçu değil, bağlantı Failed'a çekilmez.
        await act.Should().ThrowAsync<OperationCanceledException>();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        conn.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Dogrulama_hesap_yazimi_patlarsa_Verified_kalici_olmaz()
    {
        // Durum + hesaplar TEK SaveChanges'te yazılır: ikisi birlikte ya da hiçbiri. Aksi hâlde hesabı hiç
        // yazılmamış bir bağlantı Verified görünür ve çekim işi onu çekmeye başlardı.
        var dbName = $"obifin-conn-{Guid.NewGuid():N}";
        using var db = NewDb(dbName, new FailOnBankAccountInsert());
        var lic = SeedLicense(db); var stub = new StubObifin();
        stub.Accounts.Add(new ObifinAccountDto(9298, "qnb", 77, "123", BankHasherTests.TestIban(), "TL", 10m, null, true, ""));
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var act = () => svc.VerifyAsync(lic, CancellationToken.None);

        // DB hatası Obifin hatası değildir: sınıflandırılmaz, olduğu gibi yukarı gider.
        await act.Should().ThrowAsync<DbUpdateException>();
        // İzlenen varlıkta Verified bellekte kalmış olabilir; kalıcı durum taze bağlamdan okunur.
        using var fresh = NewDb(dbName);
        var persisted = await fresh.ObifinConnections.SingleAsync();
        persisted.Status.Should().Be(ObifinConnectionStatus.Unverified);
        persisted.LastVerifiedAt.Should().BeNull();
        (await fresh.BankAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Cozulemeyen_kimlik_Failed_ve_yeniden_giris_mesaji_istemciye_gidilmez()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        // Başka anahtar halkasıyla şifrelenmiş metin = anahtar klasörü kaybı senaryosu.
        conn.PasswordProtected = new EphemeralDataProtectionProvider().CreateProtector("x").Protect(NewPw());
        await db.SaveChangesAsync();

        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(ObifinConnectionService.UndecryptableMessage);
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Be(ObifinConnectionService.UndecryptableMessage);
        stub.ListAccountsCalls.Should().Be(0, "çözülemeyen kimlikle Obifin'e gidilmez");
    }

    [Fact]
    public async Task Parola_ve_API_anahtari_ayri_amaclarla_korunur_sutunlar_yer_degistiremez()
    {
        // Repo kuralı (NetgsmAccountService): korunan her alanın KENDİ purpose'u olur. Ortak purpose'la
        // bir sütunun şifreli metni öbür sütuna taşınıp okutulabilirdi.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        (conn.PasswordProtected, conn.ApiKeyProtected) = (conn.ApiKeyProtected, conn.PasswordProtected);
        await db.SaveChangesAsync();

        svc.TryResolveCredentials(conn).Should().BeNull();
    }

    [Fact]
    public async Task Yeniden_kayitta_bos_sifre_eskisini_korur()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var pw = NewPw();
        await svc.UpsertAsync(lic, "", "api@x", pw, NewKey(), CancellationToken.None);

        await svc.UpsertAsync(lic, "", "api2@x", password: null, apiKey: null, CancellationToken.None);
        await svc.VerifyAsync(lic, CancellationToken.None);

        stub.LastCreds!.UserCode.Should().Be("api2@x");
        stub.LastCreds.Password.Should().Be(pw, "boş şifre = değiştirme");
        (await db.ObifinConnections.CountAsync()).Should().Be(1, "lisans başına tek bağlantı");
    }

    [Fact]
    public async Task Ilk_kayitta_sifre_ve_API_anahtari_zorunlu()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());

        var act = () => svc.UpsertAsync(lic, "", "api@x", password: null, apiKey: NewKey(), CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>();
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("http://example.invalid")]
    [InlineData("example.invalid")]
    [InlineData("/webservis")]
    public async Task BaseUrl_mutlak_https_degilse_reddedilir(string baseUrl)
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());

        var act = () => svc.UpsertAsync(lic, baseUrl, "api@x", NewPw(), NewKey(), CancellationToken.None);

        (await act.Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Be(ObifinConnectionService.BaseUrlMessage);
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BaseUrl_bossa_varsayilan_adres_yazilir()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var conn = await Svc(db, new StubObifin()).UpsertAsync(lic, "  ", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.BaseUrl.Should().Be(new ObifinOptions().DefaultBaseUrl);
    }

    [Theory]
    [InlineData("kullanıcı@x", true, true)]
    [InlineData("api@x", false, true)]
    [InlineData("api@x", true, false)]
    public async Task Kimlik_alanlari_ASCII_disi_karakter_iceremez(string userCode, bool asciiPassword, bool asciiApiKey)
    {
        // Kimlik HTTP header'ında gider; SocketsHttpHandler ASCII dışı header değerini gönderim anında
        // anlaşılmaz bir ağ hatasıyla reddeder. Kayıtta yakalanır, doğrulamada değil.
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());
        var pw = asciiPassword ? NewPw() : $"pw-ş-{Guid.NewGuid():N}";
        var key = asciiApiKey ? NewKey() : $"k-ğ-{Guid.NewGuid():N}";

        var act = () => svc.UpsertAsync(lic, "", userCode, pw, key, CancellationToken.None);

        (await act.Should().ThrowAsync<ObifinValidationException>()).Which.Message
            .Should().Be(ObifinConnectionService.NonAsciiMessage);
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Kimlik_alanlari_kontrol_karakteri_iceremez()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());

        var act = () => svc.UpsertAsync(lic, "", "api@x", $"pw-\n{Guid.NewGuid():N}", NewKey(), CancellationToken.None);

        await act.Should().ThrowAsync<ObifinValidationException>();
    }

    /// <summary>Eski hesabın imleci ve hareketleri yeni hesap için anlamsız: kullanıcı kodu değişince
    /// imleç sıfırlanır, gölge satırlar silinir. Aynı kullanıcı + yeni parola ise hiçbir şeye dokunulmaz.</summary>
    [Fact]
    public async Task Kullanici_kodu_degisince_imlec_sifirlanir_ve_golge_veri_silinir()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);

        await svc.UpsertAsync(lic, "", "api2@x", password: null, apiKey: null, CancellationToken.None, allowShadowReset: true);

        conn.LastObifinTransactionId.Should().BeNull();
        conn.BackfillCompletedAt.Should().BeNull();
        conn.LastPolledAt.Should().BeNull();
        conn.LastError.Should().BeNull();
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        (await db.PaymentMatches.CountAsync()).Should().Be(0);
        (await db.BankAccounts.CountAsync()).Should().Be(0);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(0);
        (await db.PaymentMatchGaps.CountAsync()).Should().Be(0);
        // BankaApiId'ler ESKİ Obifin hesabının kimlikleridir; yeni hesapta o kayıt yoktur (ya da başkasınındır).
        (await db.BankConnections.CountAsync()).Should().Be(0, "banka bağlantıları eski hesaba aitti");
    }

    [Fact]
    public async Task BaseUrl_degisince_de_imlec_sifirlanir_ve_golge_veri_silinir()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "https://a.example.invalid", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);

        await svc.UpsertAsync(lic, "https://b.example.invalid", "api@x", password: null, apiKey: null, CancellationToken.None,
            allowShadowReset: true);

        conn.LastObifinTransactionId.Should().BeNull();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        (await db.BankAccounts.CountAsync()).Should().Be(0);
        (await db.BankConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BaseUrl_kozmetik_farki_kimlik_degisikligi_sayilmaz_golge_veri_kalir()
    {
        // Şema/ana makine büyük-küçük harf ve sondaki eğik çizgi aynı Obifin hesabıdır (DNS ve şema harf
        // duyarsız; istemci zaten TrimEnd('/') yapıyor). Geri alınamaz bir silme kozmetik farka bağlanamaz —
        // IBAN hafızası insan kararından öğrenilir, yeniden üretilemez.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "https://a.example.invalid", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);
        var cursor = conn.LastObifinTransactionId;

        await svc.UpsertAsync(lic, "HTTPS://A.example.invalid/", "api@x", password: null, apiKey: null, CancellationToken.None);

        conn.LastObifinTransactionId.Should().Be(cursor);
        conn.BaseUrl.Should().Be("https://a.example.invalid", "adres normalize edilerek saklanır");
        (await db.BankTransactions.CountAsync()).Should().Be(1);
        (await db.BankAccounts.CountAsync()).Should().Be(1);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(1);
        (await db.BankConnections.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("HTTPS://Example.Invalid/", "https://example.invalid")]
    [InlineData("https://example.invalid/api/", "https://example.invalid/api")]
    [InlineData("  https://example.invalid  ", "https://example.invalid")]
    public async Task BaseUrl_normalize_edilerek_saklanir(string given, string stored)
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var conn = await Svc(db, new StubObifin()).UpsertAsync(lic, given, "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.BaseUrl.Should().Be(stored);
    }

    [Fact]
    public async Task Kimlik_degisikligi_yalniz_o_lisansin_golge_verisini_siler()
    {
        // Bu özellikteki tek toplu silme yolu: kiracı sınırı testle korunur — ileride ExecuteDelete'e ya da
        // kaskada geçen bir yeniden yazım başka lisansın verisini sessizce götüremesin.
        using var db = NewDb(); var stub = new StubObifin();
        var licA = SeedLicense(db); var licB = SeedLicense(db);
        var svc = Svc(db, stub);
        var connA = await svc.UpsertAsync(licA, "", "api-a@x", NewPw(), NewKey(), CancellationToken.None);
        var connB = await svc.UpsertAsync(licB, "", "api-b@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, connA);
        SeedShadowData(db, connB);
        var cursorB = connB.LastObifinTransactionId;
        var backfillB = connB.BackfillCompletedAt;
        var polledB = connB.LastPolledAt;

        await svc.UpsertAsync(licA, "", "api-a2@x", password: null, apiKey: null, CancellationToken.None, allowShadowReset: true);

        connA.LastObifinTransactionId.Should().BeNull();
        (await db.BankTransactions.CountAsync(t => t.LicenseId == licA)).Should().Be(0);
        (await db.BankConnections.CountAsync(b => b.LicenseId == licA)).Should().Be(0);

        connB.LastObifinTransactionId.Should().Be(cursorB);
        connB.BackfillCompletedAt.Should().Be(backfillB);
        connB.LastPolledAt.Should().Be(polledB);
        connB.LastError.Should().Be("eski hata");
        connB.Status.Should().Be(ObifinConnectionStatus.Unverified);
        (await db.BankTransactions.CountAsync(t => t.LicenseId == licB)).Should().Be(1);
        (await db.PaymentMatches.CountAsync(m => m.LicenseId == licB)).Should().Be(1);
        (await db.BankAccounts.CountAsync(a => a.LicenseId == licB)).Should().Be(1);
        (await db.CustomerIbanMemories.CountAsync(m => m.LicenseId == licB)).Should().Be(1);
        (await db.PaymentMatchGaps.CountAsync(g => g.LicenseId == licB)).Should().Be(1);
        (await db.BankConnections.CountAsync(b => b.LicenseId == licB)).Should().Be(1);
    }

    [Fact]
    public async Task Ayni_kullanici_yeni_parola_imlece_ve_golge_veriye_dokunmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);
        var cursor = conn.LastObifinTransactionId;
        var backfill = conn.BackfillCompletedAt;
        var oldProtected = conn.PasswordProtected;

        // Boş BaseUrl "görüş yok" demektir; varsayılan adresle aynı hesap. allowShadowReset verilmedi (varsayılan):
        // kimlik değişmediği için onay gerekmez, kayıt yapılır.
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), apiKey: null, CancellationToken.None);

        conn.PasswordProtected.Should().NotBe(oldProtected, "yeni parola kaydedildi");
        conn.LastObifinTransactionId.Should().Be(cursor);
        conn.BackfillCompletedAt.Should().Be(backfill);
        (await db.BankTransactions.CountAsync()).Should().Be(1);
        (await db.PaymentMatches.CountAsync()).Should().Be(1);
        (await db.BankAccounts.CountAsync()).Should().Be(1);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(1);
        (await db.PaymentMatchGaps.CountAsync()).Should().Be(1);
        (await db.BankConnections.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("api2@x", "https://a.example.invalid")]
    [InlineData("api@x", "https://b.example.invalid")]
    public async Task Kimlik_degisikligi_golge_veri_varken_izinsiz_hicbir_seye_dokunmaz_onay_ister(string userCode, string baseUrl)
    {
        // Silme kararının TEK yeri servis. İzin (allowShadowReset) yoksa yalnız "var mı" sorulur — satırlar (ham JSON dahil)
        // yüklenmez; bağlantı da gölge satırlar da olduğu gibi kalır, istisnadan önce izleyicide bekleyen değişiklik bile yok.
        var dbName = $"obifin-conn-{Guid.NewGuid():N}";
        using var db = NewDb(dbName); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());
        var conn = await svc.UpsertAsync(lic, "https://a.example.invalid", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);
        db.ChangeTracker.Clear(); // tohumlanan satırlar izlenmesin: servisin neyi yüklediği görülsün
        ObifinConnection before;
        using (var snapshot = NewDb(dbName)) before = await snapshot.ObifinConnections.AsNoTracking().SingleAsync();

        var act = () => svc.UpsertAsync(lic, baseUrl, userCode, NewPw(), NewKey(), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ShadowResetConfirmationRequiredException>()).Which;
        thrown.Message.Should().Be(ObifinConnectionService.ResetConfirmMessage);
        thrown.Should().BeAssignableTo<ObifinValidationException>("admin'e gösterilen doğrulama mesajı");
        db.ChangeTracker.HasChanges().Should().BeFalse("istisnadan önce hiçbir şey değiştirilmedi");
        db.ChangeTracker.Entries().Select(e => e.Entity).Should().OnlyContain(e => e is ObifinConnection,
            "onaysız yolda gölge satırlar yüklenmez");
        using var fresh = NewDb(dbName);
        var after = await fresh.ObifinConnections.SingleAsync();
        after.UserCode.Should().Be(before.UserCode);
        after.BaseUrl.Should().Be(before.BaseUrl);
        after.PasswordProtected.Should().Be(before.PasswordProtected);
        after.ApiKeyProtected.Should().Be(before.ApiKeyProtected);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        after.LastObifinTransactionId.Should().Be(before.LastObifinTransactionId);
        after.LastError.Should().Be(before.LastError);
        (await fresh.BankTransactions.CountAsync()).Should().Be(1);
        (await fresh.PaymentMatches.CountAsync()).Should().Be(1);
        (await fresh.BankAccounts.CountAsync()).Should().Be(1);
        (await fresh.CustomerIbanMemories.CountAsync()).Should().Be(1);
        (await fresh.PaymentMatchGaps.CountAsync()).Should().Be(1);
        (await fresh.BankConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Kimlik_degisikligi_golge_veri_yokken_onaysiz_kaydedilir()
    {
        // Silinecek satır yoksa onay gerekmez. Eski hesabın imleci yine sıfırlanır: yeni hesapta anlamı yok.
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.LastObifinTransactionId = 326404; // satırlar saklama işiyle gitmiş, imleç kalmış
        await db.SaveChangesAsync();

        var result = await svc.UpsertWithResultAsync(lic, "", "api2@x", password: null, apiKey: null, CancellationToken.None);

        result.ShadowDataReset.Should().BeFalse("silinecek satır yoktu");
        result.Connection.UserCode.Should().Be("api2@x");
        result.Connection.LastObifinTransactionId.Should().BeNull();
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).UserCode.Should().Be("api2@x", "kaydedildi");
    }

    [Fact]
    public async Task Izinli_kimlik_degisikligi_golge_verinin_silindigini_bildirir()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);

        var result = await svc.UpsertWithResultAsync(lic, "", "api2@x", password: null, apiKey: null, CancellationToken.None,
            allowShadowReset: true);

        result.ShadowDataReset.Should().BeTrue();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        (await db.ObifinConnections.AsNoTracking().SingleAsync()).UserCode.Should().Be("api2@x");
    }

    [Fact]
    public async Task Izin_kimlik_degismeden_hicbir_seyi_silmez()
    {
        // allowShadowReset bir izin, emir değil: kimlik aynıysa silme yok.
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);

        var result = await svc.UpsertWithResultAsync(lic, "", "api@x", NewPw(), apiKey: null, CancellationToken.None,
            allowShadowReset: true);

        result.ShadowDataReset.Should().BeFalse();
        conn.LastObifinTransactionId.Should().Be(326404);
        (await db.BankTransactions.CountAsync()).Should().Be(1);
        (await db.BankConnections.CountAsync()).Should().Be(1);
    }

    private static void SeedShadowData(LicenseDbContext db, ObifinConnection conn)
    {
        var now = DateTimeOffset.UtcNow;
        conn.LastObifinTransactionId = 326404;
        conn.BackfillCompletedAt = now;
        conn.LastPolledAt = now;
        conn.LastError = "eski hata";
        var hash = NewHasher().HashIban(BankHasherTests.TestIban())!;
        db.BankConnections.Add(new BankConnection
        {
            Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinConnectionId = conn.Id, BankaKodu = "qnb",
            BankaApiId = 4242, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now,
        });
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinId = 326404, ObifinAccountId = 9298, BankaKodu = "qnb",
            Direction = BankTransactionDirection.Incoming, Amount = 10m, OccurredAt = now, FetchedAt = now,
        };
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = conn.LicenseId, BankTransactionId = tx.Id,
            Layer = PaymentMatchLayer.None, Status = PaymentMatchStatus.NoProposal, CreatedAt = now, UpdatedAt = now,
        });
        db.BankAccounts.Add(new BankAccount
        {
            Id = Guid.NewGuid(), LicenseId = conn.LicenseId, ObifinAccountId = 9298, BankaKodu = "qnb",
            IbanMasked = "TR12…345", IbanHash = hash, RefreshedAt = now,
        });
        db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = conn.LicenseId, WpfCustomerId = Guid.NewGuid(),
            IbanHash = hash, IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = now,
        });
        db.PaymentMatchGaps.Add(new PaymentMatchGap
        {
            Id = Guid.NewGuid(), LicenseId = conn.LicenseId, PaymentId = Guid.NewGuid(),
            Reason = PaymentMatchGapReason.NoCandidate, CreatedAt = now,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Banka_baglantisi_eklenince_BankaApiId_listeden_etiketle_bulunur_kimlik_saklanmaz()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var bankPw = NewPw();

        var bc = await svc.AddBankConnectionAsync(lic, "qnb", "QNB ana hesap",
            new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = bankPw, ["Url"] = "https://example.invalid/wsdl" },
            CancellationToken.None);

        bc.BankaApiId.Should().Be(4242);
        bc.BankaKodu.Should().Be("qnb");
        bc.Label.Should().Be("QNB ana hesap", "admin etiketi görüntü içindir");
        // Obifin'e giden etiket bizim ürettiğimiz tekil ad: liste dönüşünde BankaApiId onunla bulunur.
        stub.Added.Single().Form["BankaApiAdi"].Should().StartWith("OrderDeck-").And.Contain("-qnb-");
        // İzlenen varlık lisans→müşteri→lisanslar döngüsüne bağlanır; yalnız satırın sütunları serileştirilir.
        var row = db.Entry(await db.BankConnections.SingleAsync()).CurrentValues.ToObject();
        var json = System.Text.Json.JsonSerializer.Serialize(row);
        json.Should().NotContain(bankPw, "banka web servis kimliği saklanmaz")
            .And.NotContain("webservis-user");
    }

    [Fact]
    public async Task Banka_baglantisi_etiketsiz_eklenince_Obifin_etiketi_saklanir()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var bc = await svc.AddBankConnectionAsync(lic, " QNB ", "  ",
            new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() }, CancellationToken.None);

        bc.BankaKodu.Should().Be("qnb", "banka kodu küçük harfe normalize edilir");
        bc.Label.Should().Be(stub.Added.Single().Form["BankaApiAdi"], "etiket verilmezse Obifin'deki ad görünür");
        bc.Label.Length.Should().BeLessThanOrEqualTo(80, "Label sütunu 80 karakter");
    }

    [Fact]
    public async Task Ayni_dakikada_iki_banka_baglantisi_farkli_etiket_ve_BankaApiId_alir()
    {
        // Dakika çözünürlüklü etiket tek başına yetmez: aynı dakikada ikinci ekleme (iki QNB hesabı ya da
        // "görünmedi" hatasından sonra hemen tekrar) listede İLK kaydın BankaApiId'sini bulur, tekil index
        // patlar ve ikinci kayıt Obifin'de yetim kalırdı.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() };

        var first = await svc.AddBankConnectionAsync(lic, "qnb", "QNB 1", form, CancellationToken.None);
        var second = await svc.AddBankConnectionAsync(lic, "qnb", "QNB 2", form, CancellationToken.None);

        stub.Added.Should().HaveCount(2);
        stub.Added[0].Form["BankaApiAdi"].Should().NotBe(stub.Added[1].Form["BankaApiAdi"], "Obifin etiketi tekil");
        first.BankaApiId.Should().Be(4242);
        second.BankaApiId.Should().Be(4243);
        (await db.BankConnections.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Banka_baglantisi_etiketi_80_karakteri_asarsa_Obifin_cagrilmadan_reddedilir()
    {
        // Label sütunu 80 (LicenseDbContext). SQL Server'da INSERT "truncated" ile patlasaydı Obifin'deki kayıt
        // (banka web servis kimliğiyle) çoktan açılmış, yerelde bilinmeyen bir yetim kalırdı; admin'in tekrarı
        // ikincisini açardı. Kontrol ağ çağrısından ÖNCE.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() };

        var act = () => svc.AddBankConnectionAsync(lic, "qnb", new string('e', 81), form, CancellationToken.None);

        (await act.Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Contain("80 karakter");
        stub.Added.Should().BeEmpty("Obifin'e istek gitmedi");
        (await db.BankConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Banka_baglantisi_etiketi_tam_80_karakter_kabul_edilir()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() };

        var bc = await svc.AddBankConnectionAsync(lic, "qnb", " " + new string('e', 80) + " ", form, CancellationToken.None);

        bc.Label.Should().HaveLength(80, "kırpıldıktan sonra sınır dahil");
        stub.Added.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task Banka_baglantisi_banka_kodu_bos_ya_da_32_karakteri_asarsa_Obifin_cagrilmadan_reddedilir(int length)
    {
        // BankaKodu sütunu 32 (LicenseDbContext); istemci yalnız harf/rakam denetler, uzunluğu değil.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() };

        var act = () => svc.AddBankConnectionAsync(lic, new string('q', length), "QNB", form, CancellationToken.None);

        (await act.Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Be("Banka kodu 1–32 karakter olmalı.");
        stub.Added.Should().BeEmpty("Obifin'e istek gitmedi");
        (await db.BankConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Banka_baglantisi_ekleme_Obifin_hatasinda_Failed_ve_mesaj_saklanir_istisna_yukari_gider()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin { AddBankConnectionError = new ObifinApiException(new[] { "Banka bilgileri hatali" }) };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() };

        var act = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None);

        // Dönüş tipi (BankConnection) başarısızlık taşıyamaz: durum kaydedilir, istisna yine yukarı gider.
        await act.Should().ThrowAsync<ObifinApiException>();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Be("Banka bilgileri hatali");
        (await db.BankConnections.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Banka_baglantisi_hata_mesaji_gonderilen_banka_alanlarini_yankilarsa_kacisli_bicimleri_de_maskelenir(bool failOnList)
    {
        // Obifin ya da bankanın SOAP hatası gönderilen alanı (web servis kullanıcısı/şifresi) geri yankılayabilir — ham ya da
        // HTML/XML kaçışlı (&amp; &quot; &lt; &gt;, .NET &#39;, PHP &#039;, XML &apos;): LastError'a, loga ve yukarı giden
        // istisnaya yalnız maskeli metin gider. Eşleşme harf duyarsız; 3 karakterden kısa değer maskelenmez (her "ab"yi
        // gizlemek metni okunmaz yapardı). Asıl istisna iç istisna olarak da taşınmaz. Ekleme reddi Failed yazar; eklemeden
        // sonraki liste hatası sonucu belirsiz bırakır (durum korunur).
        using var db = NewDb(); var lic = SeedLicense(db);
        var bankUser = $"ws-{Guid.NewGuid():N}";
        var bankPw = $"pw&'<>\"-{Guid.NewGuid():N}";
        var html = WebUtility.HtmlEncode(bankPw);
        var php = html.Replace("&#39;", "&#039;", StringComparison.Ordinal);
        var xml = html.Replace("&#39;", "&apos;", StringComparison.Ordinal);
        var echo = new ObifinApiException(new[]
        {
            $"Kullanici {bankUser.ToUpperInvariant()} reddedildi", $"Sifre {bankPw}", $"<faultstring>Sifre {html}</faultstring>",
            $"PHP {php}", $"XML {xml}", "Firma ab yok",
        });
        var masked = new[] { "Kullanici [gizli] reddedildi", "Sifre [gizli]", "<faultstring>Sifre [gizli]</faultstring>",
            "PHP [gizli]", "XML [gizli]", "Firma ab yok" };
        var stub = failOnList ? new StubObifin { ListBankConnectionsError = echo } : new StubObifin { AddBankConnectionError = echo };
        var log = new RecordingLog();
        var svc = Svc(db, stub, log);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = bankUser, ["Sifre"] = bankPw, ["FirmaKodu"] = "ab" };

        var act = () => svc.AddBankConnectionAsync(lic, "garanti", "G", form, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().NotBeSameAs(echo);
        thrown.InnerException.Should().BeNull("asıl istisnanın metni banka kimliğini taşır");
        var conn = await db.ObifinConnections.SingleAsync();
        if (failOnList)
        {
            thrown.Should().BeOfType<ObifinBankAddUncertainException>().Which.Message.Should().EndWith(string.Join(" | ", masked));
            conn.Status.Should().Be(ObifinConnectionStatus.Unverified, "ekleme geçti; liste hatası kimlik aleyhine kanıt değil");
            conn.LastError.Should().Be(thrown.Message);
        }
        else
        {
            thrown.Should().BeOfType<ObifinApiException>().Which.Messages.Should().Equal(masked);
            conn.Status.Should().Be(ObifinConnectionStatus.Failed);
            conn.LastError.Should().Be(string.Join(" | ", masked));
        }
        var leaks = new[] { bankUser, bankPw, html, php, xml };
        log.Entries.Should().NotBeEmpty().And.OnlyContain(e => leaks.All(l => !e.Contains(l, StringComparison.OrdinalIgnoreCase)));
        leaks.Should().OnlyContain(l => !thrown.Message.Contains(l, StringComparison.OrdinalIgnoreCase)
            && !conn.LastError!.Contains(l, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ağ/vekil/zaman aşımı sınıfından bir istemci hatası; mesajı <paramref name="marker"/> (ham metin, gösterilmemeli).</summary>
    private static Exception NewTransientError(Type exceptionType, string marker)
        => exceptionType == typeof(ObifinProtocolException)
            ? new ObifinProtocolException(marker)
            : (Exception)Activator.CreateInstance(exceptionType, marker)!;

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(ObifinProtocolException))]
    public async Task Banka_baglantisi_ekleme_cagrisi_ag_hatasiyla_duserse_sonuc_belirsiz_Failed_yazilmaz_etiket_soylenir(Type exceptionType)
    {
        // Ağ/vekil/zaman aşımı: istek Obifin'e ulaşıp kaydı açmış olabilir. "Eklenemedi" demek admin'i tekrar eklemeye iter
        // (Obifin'de ikinci kayıt, farklı Id'lerle mükerrer hesap/hareket); Failed yazmak kimlik aleyhine kanıtı olmayan bir
        // hatayla çekimi "Doğrula"ya dek durdururdu. Durum korunur, son hata ve istisna Obifin etiketini söyler. İstisna banka
        // form değeri taşımaz: istisna nesnesiyle (mesaj + iz) ve etiketle (sır değil) loglanır; ham metni gösterilmez.
        using var db = NewDb(); var lic = SeedLicense(db);
        var marker = $"ag-{Guid.NewGuid():N}";
        var stub = new StubObifin { AddBankConnectionError = NewTransientError(exceptionType, marker) };
        var log = new RecordingLog();
        var svc = Svc(db, stub, log);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Verified;
        await db.SaveChangesAsync();
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = $"ws-{Guid.NewGuid():N}", ["Sifre"] = NewPw() };

        var act = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ObifinBankAddUncertainException>()).Which;
        var label = stub.AttemptedLabels.Single();
        thrown.Message.Should().Contain("belirsiz").And.Contain($"'{label}'")
            .And.Contain($"Obifin'e ulaşılamadı ({exceptionType.Name})")
            .And.NotContain(marker, "ham istisna metni gösterilmez").And.NotContain("eklenemedi");
        conn.Status.Should().Be(ObifinConnectionStatus.Verified, "geçici hata kimlik aleyhine kanıt değil");
        conn.LastError.Should().Be(thrown.Message);
        (await db.BankConnections.CountAsync()).Should().Be(0);
        log.Entries.Should().ContainSingle(e => e.Contains("banka bağlantısı ekleme")).Which
            .Should().Contain(marker, "istisna nesnesi günlükte").And.Contain(exceptionType.Name).And.Contain(label);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(ObifinProtocolException))]
    [InlineData(typeof(ObifinApiException))]
    public async Task Banka_baglantisi_eklendi_ama_liste_alinamazsa_Failed_yazilmaz_etiketle_tekrar_eklemeyin_der(Type exceptionType)
    {
        // bankaapi/ekle Obifin'de kaydı AÇTI; yalnız ardından gelen liste düştü (502, zaman aşımı, BankaApiId'siz satır,
        // Obifin reddi). "Eklenemedi" + Failed admin'i tekrar eklemeye iter ve çalışan bağlantının çekimini durdururdu.
        // Durum korunur; son hata ve istisna Obifin etiketini ve "TEKRAR EKLEMEYİN" uyarısını taşır; etiket günlüğe de
        // düşer (sır değil — sonradan elle eşleştirmek için). Yerel satır yazılmaz: BankaApiId bilinmiyor.
        using var db = NewDb(); var lic = SeedLicense(db);
        var marker = $"liste-{Guid.NewGuid():N}";
        var error = exceptionType == typeof(ObifinApiException)
            ? new ObifinApiException(new[] { marker })
            : NewTransientError(exceptionType, marker);
        var stub = new StubObifin { ListBankConnectionsError = error };
        var log = new RecordingLog();
        var svc = Svc(db, stub, log);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Verified;
        await db.SaveChangesAsync();
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() };

        var act = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ObifinBankAddUncertainException>()).Which;
        var label = stub.Added.Single().Form["BankaApiAdi"];
        thrown.Message.Should().Contain($"'{label}'").And.Contain("TEKRAR EKLEMEYİN").And.NotContain("eklenemedi");
        if (error is ObifinApiException) thrown.Message.Should().EndWith(marker, "Obifin'in kendi mesajı gösterilir");
        else thrown.Message.Should().EndWith($"Obifin'e ulaşılamadı ({exceptionType.Name})").And.NotContain(marker);
        conn.Status.Should().Be(ObifinConnectionStatus.Verified, "ekleme geçti; liste hatası kimlik aleyhine kanıt değil");
        conn.LastError.Should().Be(thrown.Message);
        (await db.BankConnections.CountAsync()).Should().Be(0);
        log.Entries.Should().ContainSingle(e => e.Contains("banka bağlantısı ekleme")).Which
            .Should().Contain(label).And.Contain(exceptionType.Name);
    }

    [Fact]
    public async Task Durum_hatalari_admin_mesaji_olarak_ObifinValidationException_firlatir()
    {
        // Admin sayfası yalnız ObifinValidationException'ı (ve sınıflandırılmış istemci hatalarını) bildirime çevirir; başka
        // her InvalidOperationException programlama hatası sayılıp yukarı gider. Servisin admin'e yönelik durum mesajları
        // bu türle atılmalı.
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin();
        var svc = Svc(db, stub);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = $"ws-{Guid.NewGuid():N}", ["Sifre"] = NewPw() };

        (await FluentActions.Awaiting(() => svc.VerifyAsync(lic, CancellationToken.None))
            .Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Be("Obifin bağlantısı yok.");
        (await FluentActions.Awaiting(() => svc.RefreshAccountsAsync(lic, CancellationToken.None))
            .Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Be("Obifin bağlantısı yok.");
        (await FluentActions.Awaiting(() => svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None))
            .Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Be("Önce Obifin bağlantısı kaydedilmeli.");

        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        stub.HideAddedFromList = true;
        (await FluentActions.Awaiting(() => svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None))
            .Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().StartWith("Banka bağlantısı Obifin'de görünmedi");

        conn.PasswordProtected = new EphemeralDataProtectionProvider().CreateProtector("x").Protect(NewPw());
        await db.SaveChangesAsync();
        (await FluentActions.Awaiting(() => svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None))
            .Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Be(ObifinConnectionService.UndecryptableMessage);
        (await FluentActions.Awaiting(() => svc.RefreshAccountsAsync(lic, CancellationToken.None))
            .Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Be(ObifinConnectionService.UndecryptableMessage);
    }

    [Fact]
    public async Task Banka_baglantisi_ekleme_basarisi_Failed_baglantiyi_Verified_yapar_ve_son_hatayi_siler()
    {
        // Başarısızlık Failed yazıyorsa başarı da Verified yazmalı: ekle + liste başarısı kimliğin çalıştığının
        // kanıtıdır; aksi hâlde tek geçici hata insan "Doğrula"ya basana kadar yapışkan kalırdı.
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin { AddBankConnectionError = new ObifinApiException(new[] { "Banka bilgileri hatali" }) };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var form = new Dictionary<string, string> { ["KullaniciAdi"] = "webservis-user", ["Sifre"] = NewPw() };
        var failing = () => svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None);
        await failing.Should().ThrowAsync<ObifinApiException>();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        stub.AddBankConnectionError = null;
        var before = DateTimeOffset.UtcNow;

        await svc.AddBankConnectionAsync(lic, "qnb", "QNB", form, CancellationToken.None);

        conn.Status.Should().Be(ObifinConnectionStatus.Verified);
        conn.LastError.Should().BeNull();
        conn.LastVerifiedAt.Should().NotBeNull().And.BeOnOrAfter(before);
        (await db.BankConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Hesap_yenileme_maskeli_iban_ve_banka_senkron_zamanini_yazar()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var iban = BankHasherTests.TestIban();
        stub.Accounts.Add(new ObifinAccountDto(9298, "qnb", 77, "123", iban, "TL", 10m,
            new DateTime(2026, 9, 25, 10, 0, 0), true, ""));
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        await svc.RefreshAccountsAsync(lic, CancellationToken.None);
        await svc.RefreshAccountsAsync(lic, CancellationToken.None);

        var acc = (await db.BankAccounts.ToListAsync()).Should().ContainSingle("ikinci yenileme upsert").Subject;
        acc.IbanMasked.Should().Be(BankHasher.MaskIban(iban));
        acc.IbanHash.Should().NotBeNullOrEmpty().And.NotContain(iban[4..10]);
        acc.LastBankSyncAt.Should().Be(new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero), "TR 10:00 = UTC 07:00");
        acc.Active.Should().BeTrue();
    }

    [Fact]
    public async Task Hesap_yenileme_Obifin_hatasinda_Failed_ve_mesaj_saklanir_istisna_yukari_gider()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin { ListAccountsError = new ObifinApiException(new[] { "Kullanici Bilgileri Hatali!" }) };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var act = () => svc.RefreshAccountsAsync(lic, CancellationToken.None);

        // Dönüş tipi (int) başarısızlık taşıyamaz: durum kaydedilir, istisna yine yukarı gider.
        await act.Should().ThrowAsync<ObifinApiException>();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastError.Should().Be("Kullanici Bilgileri Hatali!");
        (await db.BankAccounts.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(ObifinProtocolException))]
    public async Task Hesap_yenileme_gecici_hatada_durum_korunur_kisa_Turkce_mesaj_tur_adiyla(Type exceptionType)
    {
        // Ağ/vekil/zaman aşımı kimlik aleyhine kanıt değildir (çekim işiyle aynı ayrım): Failed yazılsaydı saatlik
        // yenileme işi de çekim de yalnız Verified bağlantıya baktığından tek bir 502 çekimi admin "Doğrula"ya
        // basana dek durdururdu. Yalnız son hata yazılır, istisna yine yukarı gider.
        using var db = NewDb(); var lic = SeedLicense(db);
        Exception error = exceptionType == typeof(ObifinProtocolException)
            ? new ObifinProtocolException("Obifin HTTP 502 /webservis/hesaplar/hesaplistesi/")
            : (Exception)Activator.CreateInstance(exceptionType, "Name or service not known")!;
        var stub = new StubObifin { ListAccountsError = error };
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Verified;
        await db.SaveChangesAsync();

        var act = () => svc.RefreshAccountsAsync(lic, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(exceptionType);
        var fresh = await db.ObifinConnections.AsNoTracking().SingleAsync();
        fresh.Status.Should().Be(ObifinConnectionStatus.Verified);
        fresh.LastError.Should().Be($"Obifin'e ulaşılamadı ({exceptionType.Name})");
    }

    [Fact]
    public async Task Hesap_yenileme_cagiranin_iptali_Failed_yazmaz_istisna_yukari_gider()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        using var cts = new CancellationTokenSource();
        var stub = new StubObifin { CancelCallerOnListAccounts = cts };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        var act = () => svc.RefreshAccountsAsync(lic, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        conn.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Hesap_yenileme_basarisi_Failed_baglantiyi_Verified_yapar_ve_son_hatayi_siler()
    {
        // Başarısızlık ve toparlanma kanıtı aynı uçtan (hesaplistesi) gelir: Obifin'in reddiyle Failed olmuş bağlantı,
        // aynı uçtan gelen başarıyla (ör. admin'in elle yenilemesi) "Doğrula"ya basılmadan toparlanır. Geçici hata
        // zaten Failed yazmaz (bkz. Hesap_yenileme_gecici_hatada_durum_korunur...).
        using var db = NewDb(); var lic = SeedLicense(db);
        var stub = new StubObifin { ListAccountsError = new ObifinApiException(new[] { "Kullanici Bilgileri Hatali!" }) };
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var failing = () => svc.RefreshAccountsAsync(lic, CancellationToken.None);
        await failing.Should().ThrowAsync<ObifinApiException>();
        var conn = await db.ObifinConnections.SingleAsync();
        conn.Status.Should().Be(ObifinConnectionStatus.Failed);
        conn.LastVerifiedAt.Should().BeNull();
        stub.ListAccountsError = null;
        var before = DateTimeOffset.UtcNow;

        await svc.RefreshAccountsAsync(lic, CancellationToken.None);

        conn.Status.Should().Be(ObifinConnectionStatus.Verified);
        conn.LastError.Should().BeNull();
        conn.LastVerifiedAt.Should().NotBeNull().And.BeOnOrAfter(before);
    }

    [Fact]
    public async Task Devre_disi_baglanti_basarili_yenilemeyle_acilmaz_kanit_yine_yazilir()
    {
        // Disabled admin'in anahtarıdır: Obifin kanıtı onu çevirmez (Görev 6'daki açık etkinleştirme çevirir).
        // Aksi hâlde yenileme işi kapatılmış bağlantıyı sessizce yeniden açar, çekim işi onu çekmeye başlardı.
        // Kanıt alanları (son doğrulama, son hata) yine yazılır.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Disabled; conn.LastError = "eski hata";
        await db.SaveChangesAsync();

        await svc.RefreshAccountsAsync(lic, CancellationToken.None);

        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.LastError.Should().BeNull();
        conn.LastVerifiedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Devre_disi_baglanti_basarisiz_yenilemeyle_Failed_olmaz_son_hata_yine_yazilir(bool obifinRejected)
    {
        // Hata da anahtarı çevirmez: Disabled → Failed → (başarı) → Verified zinciri kapatılmış bağlantıyı
        // arka kapıdan açardı. Obifin'in reddi (durumu çeviren tek sınıf) ve geçici hata ayrı ayrı.
        using var db = NewDb(); var lic = SeedLicense(db);
        Exception error = obifinRejected
            ? new ObifinApiException(new[] { "Kullanici Bilgileri Hatali!" })
            : new HttpRequestException("Name or service not known");
        var stub = new StubObifin { ListAccountsError = error };
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Disabled;
        await db.SaveChangesAsync();

        var act = () => svc.RefreshAccountsAsync(lic, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(error);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.LastError.Should().Be(obifinRejected ? "Kullanici Bilgileri Hatali!" : "Obifin'e ulaşılamadı (HttpRequestException)");
    }

    [Fact]
    public async Task Devre_disi_baglanti_dogrulama_basarisiyla_acilmaz_sonuc_yine_Ok()
    {
        // "Doğrula" kimliğin çalıştığını kanıtlar; admin'in kapatma kararını geri almaz.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        conn.Status = ObifinConnectionStatus.Disabled;
        await db.SaveChangesAsync();

        var result = await svc.VerifyAsync(lic, CancellationToken.None);

        result.Ok.Should().BeTrue();
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.LastVerifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Kullanici_kodu_200_karakteri_asarsa_reddedilir()
    {
        // UserCode sütunu 200 (LicenseDbContext). SQL Server'da INSERT "truncated" ile patlardı; admin formu
        // anlaşılır bir mesaj görsün, kayıt hiç açılmasın.
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());

        var act = () => svc.UpsertAsync(lic, "", new string('u', 201), NewPw(), NewKey(), CancellationToken.None);

        (await act.Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Contain("200 karakter");
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Kullanici_kodu_tam_200_karakter_kabul_edilir()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var conn = await Svc(db, new StubObifin()).UpsertAsync(lic, "", " " + new string('u', 200) + " ", NewPw(), NewKey(), CancellationToken.None);
        conn.UserCode.Should().HaveLength(200, "kırpıldıktan sonra sınır dahil");
    }

    [Fact]
    public async Task BaseUrl_200_karakteri_asarsa_reddedilir()
    {
        // BaseUrl sütunu 200 (LicenseDbContext); sınır normalize edilmiş adrese uygulanır.
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());
        var longUrl = "https://example.invalid/" + new string('a', 200);

        var act = () => svc.UpsertAsync(lic, longUrl, "api@x", NewPw(), NewKey(), CancellationToken.None);

        (await act.Should().ThrowAsync<ObifinValidationException>()).Which.Message.Should().Contain("200 karakter");
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hesap_yenileme_Obifin_kaynakli_uzun_alanlari_sutun_sinirina_kirpar()
    {
        // NotificationNote 500, BankaKodu 32, Currency 3, IbanMasked 40 (LicenseDbContext). Obifin'den gelen
        // uzun bir değer başarılı doğrulamayı SQL Server'da DbUpdateException'a çevirmesin.
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        stub.Accounts.Add(new ObifinAccountDto(9298, new string('q', 40), 77, "123", BankHasherTests.TestIban(), "TRYX",
            10m, null, true, new string('n', 600)));
        var svc = Svc(db, stub);
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);

        await svc.RefreshAccountsAsync(lic, CancellationToken.None);

        var acc = await db.BankAccounts.SingleAsync();
        acc.NotificationNote.Should().HaveLength(500);
        acc.BankaKodu.Should().HaveLength(32);
        acc.Currency.Should().Be("TRY");
        acc.IbanMasked.Length.Should().BeLessThanOrEqualTo(40);
        (await db.ObifinConnections.SingleAsync()).Status.Should().Be(ObifinConnectionStatus.Verified);
    }

    [Fact]
    public async Task Hesap_yenileme_BankaApiId_eslesen_banka_baglantisina_baglar_eslesmeyen_null_kalir()
    {
        // Hesabın BankaApiId'si yerel BankConnection'a bağlanır; eşleşme yalnız AYNI lisans içinde aranır —
        // başka lisansın aynı BankaApiId'li bağlantısı bağ kurmaz.
        using var db = NewDb(); var stub = new StubObifin();
        var lic = SeedLicense(db); var other = SeedLicense(db);
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "", "api@x", NewPw(), NewKey(), CancellationToken.None);
        var otherConn = await svc.UpsertAsync(other, "", "api-o@x", NewPw(), NewKey(), CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var bc = new BankConnection { Id = Guid.NewGuid(), LicenseId = lic, ObifinConnectionId = conn.Id, BankaKodu = "qnb",
            BankaApiId = 77, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now };
        db.BankConnections.Add(bc);
        db.BankConnections.Add(new BankConnection { Id = Guid.NewGuid(), LicenseId = other, ObifinConnectionId = otherConn.Id,
            BankaKodu = "qnb", BankaApiId = 78, Label = "Başka lisans", Status = BankConnectionStatus.Active, CreatedAt = now });
        await db.SaveChangesAsync();
        stub.Accounts.Add(new ObifinAccountDto(9298, "qnb", 77, "1", BankHasherTests.TestIban(), "TL", 0, null, true, null));
        stub.Accounts.Add(new ObifinAccountDto(9299, "qnb", 78, "2", BankHasherTests.TestIban(), "TL", 0, null, true, null));
        stub.Accounts.Add(new ObifinAccountDto(9300, "qnb", null, "3", BankHasherTests.TestIban(), "TL", 0, null, true, null));

        await svc.RefreshAccountsAsync(lic, CancellationToken.None);

        (await db.BankAccounts.SingleAsync(a => a.ObifinAccountId == 9298)).BankConnectionId.Should().Be(bc.Id);
        (await db.BankAccounts.SingleAsync(a => a.ObifinAccountId == 9299)).BankConnectionId
            .Should().BeNull("BankaApiId 78 yalnız başka lisansta var");
        (await db.BankAccounts.SingleAsync(a => a.ObifinAccountId == 9300)).BankConnectionId.Should().BeNull("BankaApiId yok");
    }
}
