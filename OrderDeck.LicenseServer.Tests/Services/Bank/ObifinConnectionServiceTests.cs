using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
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
        public ObifinCredentials? LastCreds { get; private set; }
        public int ListAccountsCalls { get; private set; }
        public Exception? ListAccountsError { get; set; }
        /// <summary>Ayarlıysa hesap listesi çağrısı ÇAĞIRANIN jetonunu iptal edip onunla iptal istisnası fırlatır
        /// (zaman aşımı değil, kullanıcı vazgeçti senaryosu).</summary>
        public CancellationTokenSource? CancelCallerOnListAccounts { get; set; }

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
            => Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>(Connections);
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        {
            Added.Add((b, f));
            // Obifin gerçekte Id döndürmüyor (doküman sessiz): listede etiketle bulunur.
            Connections.Add(new ObifinBankConnectionDto(4242, b, f["BankaApiAdi"], true));
            return Task.CompletedTask;
        }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => Task.FromResult(new ObifinPage<ObifinTransactionDto>(Array.Empty<ObifinTransactionDto>(), p, 0, 0, ps));
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"obifin-conn-{Guid.NewGuid():N}").Options);

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

    private static ObifinConnectionService Svc(LicenseDbContext db, IObifinClient client)
        => new(db, client, Protection, NewHasher(),
            Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);

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

        await act.Should().ThrowAsync<ArgumentException>();
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

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("mutlak bir https adresi");
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

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("Obifin kimlik bilgileri yalnız ASCII karakter içerebilir.");
        (await db.ObifinConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Kimlik_alanlari_kontrol_karakteri_iceremez()
    {
        using var db = NewDb(); var lic = SeedLicense(db);
        var svc = Svc(db, new StubObifin());

        var act = () => svc.UpsertAsync(lic, "", "api@x", $"pw-\n{Guid.NewGuid():N}", NewKey(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
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

        await svc.UpsertAsync(lic, "", "api2@x", password: null, apiKey: null, CancellationToken.None);

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
    }

    [Fact]
    public async Task BaseUrl_degisince_de_imlec_sifirlanir_ve_golge_veri_silinir()
    {
        using var db = NewDb(); var lic = SeedLicense(db); var stub = new StubObifin();
        var svc = Svc(db, stub);
        var conn = await svc.UpsertAsync(lic, "https://a.example.invalid", "api@x", NewPw(), NewKey(), CancellationToken.None);
        SeedShadowData(db, conn);

        await svc.UpsertAsync(lic, "https://b.example.invalid", "api@x", password: null, apiKey: null, CancellationToken.None);

        conn.LastObifinTransactionId.Should().BeNull();
        (await db.BankTransactions.CountAsync()).Should().Be(0);
        (await db.BankAccounts.CountAsync()).Should().Be(0);
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

        // Boş BaseUrl "görüş yok" demektir; varsayılan adresle aynı hesap.
        await svc.UpsertAsync(lic, "", "api@x", NewPw(), apiKey: null, CancellationToken.None);

        conn.LastObifinTransactionId.Should().Be(cursor);
        conn.BackfillCompletedAt.Should().Be(backfill);
        (await db.BankTransactions.CountAsync()).Should().Be(1);
        (await db.PaymentMatches.CountAsync()).Should().Be(1);
        (await db.BankAccounts.CountAsync()).Should().Be(1);
        (await db.CustomerIbanMemories.CountAsync()).Should().Be(1);
        (await db.PaymentMatchGaps.CountAsync()).Should().Be(1);
    }

    private static void SeedShadowData(LicenseDbContext db, ObifinConnection conn)
    {
        var now = DateTimeOffset.UtcNow;
        conn.LastObifinTransactionId = 326404;
        conn.BackfillCompletedAt = now;
        conn.LastPolledAt = now;
        conn.LastError = "eski hata";
        var hash = NewHasher().HashIban(BankHasherTests.TestIban())!;
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
}
