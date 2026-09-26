using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public sealed class AdminObifinPageTests : IClassFixture<ApiFactory>
{
    private const string ResetConfirmMessage =
        "Kimlik değişikliği bu lisansın banka verisini siler (hareketler, eşleşmeler, IBAN hafızası). Onaylamak için kutuyu işaretleyip tekrar kaydedin.";

    private readonly ApiFactory _factory;
    public AdminObifinPageTests(ApiFactory factory) => _factory = factory;

    /// <summary>Banka anahtarı boş: modül kapalı açılır (BankHasher kurucuda düşer).</summary>
    private sealed class DisabledBankApiFactory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig
            => new Dictionary<string, string?> { ["OrderDeck:Bank:HashKey"] = "" };
    }

    /// <summary>Banka eklemede istenen istisnayı fırlatan istemci; başka çağrı beklenmez.</summary>
    private sealed class FailingAddObifin : IObifinClient
    {
        public Func<Exception> AddFails { get; set; } = () => new HttpRequestException("ağ");
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
            => Task.FromException(AddFails());
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte beklenmiyor.");
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte beklenmiyor.");
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte beklenmiyor.");
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte beklenmiyor.");
    }

    private sealed class FailingAddApiFactory : ApiFactory
    {
        public FailingAddObifin Obifin { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IObifinClient>();
                s.AddSingleton<IObifinClient>(Obifin);
            });
        }
    }

    private Task<Guid> SeedLicenseAsync() => SeedLicenseAsync(_factory);

    private static async Task<Guid> SeedLicenseAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = AddLicense(db);
        await db.SaveChangesAsync();
        return licenseId;
    }

    /// <summary>Müşteri + lisans ekler (kaydetmez); e-posta <paramref name="emailPrefix"/> ile başlar — seçim listesi
    /// e-postaya göre sıralı.</summary>
    private static Guid AddLicense(LicenseDbContext db, string emailPrefix = "ob")
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"{emailPrefix}-{Guid.NewGuid():N}@x", Name = "Ob",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License { Id = licenseId, LicenseKey = $"LDK-OB-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        return licenseId;
    }

    /// <summary>Sayfayı GET'leyip anti-forgery jetonunu forma ekler (AdminNetgsmPageTests kalıbı).</summary>
    private static async Task<FormUrlEncodedContent> FormAsync(HttpClient client, string getPath, Dictionary<string, string> fields)
    {
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(getPath));
        fields["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>Razor ASCII dışını sayısal varlığa çevirir; operatörün gördüğü metni sınamak için belge ayrıştırılır.</summary>
    private static async Task<IDocument> ParseAsync(string html)
        => await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

    private static async Task<string> PageTextAsync(HttpClient client, string path = "/admin/obifin")
        => (await ParseAsync(await client.GetStringAsync(path))).Body!.TextContent;

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid licenseId, string userCode,
        string? baseUrl = null, bool confirmReset = false)
        => SaveAsync(client, new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["BaseUrl"] = baseUrl ?? "", ["UserCode"] = userCode,
            ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        }, confirmReset);

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, Dictionary<string, string> fields, bool confirmReset = false)
    {
        if (confirmReset) fields["ConfirmReset"] = "true";
        return await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", fields));
    }

    private async Task<ObifinConnection> ConnectionAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().ObifinConnections.AsNoTracking()
            .SingleAsync(c => c.LicenseId == licenseId);
    }

    /// <summary>Kimlik değişiminde silinecek gölge veri: banka bağlantısı + hesap + hareket.</summary>
    private async Task SeedShadowDataAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == licenseId);
        var now = DateTimeOffset.UtcNow;
        var bc = new BankConnection { Id = Guid.NewGuid(), LicenseId = licenseId, ObifinConnectionId = conn.Id, BankaKodu = "qnb",
            BankaApiId = Random.Shared.NextInt64(1, 1_000_000), Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now };
        var account = new BankAccount { Id = Guid.NewGuid(), LicenseId = licenseId, BankConnectionId = bc.Id,
            ObifinAccountId = Random.Shared.NextInt64(1, 1_000_000), BankaKodu = "qnb",
            IbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban()), Currency = "TL", Active = true, RefreshedAt = now };
        db.BankConnections.Add(bc);
        db.BankAccounts.Add(account);
        db.BankTransactions.Add(new BankTransaction { Id = Guid.NewGuid(), LicenseId = licenseId,
            ObifinId = Random.Shared.NextInt64(1, 1_000_000), BankAccountId = account.Id, ObifinAccountId = account.ObifinAccountId,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 100m, Currency = "TL",
            OccurredAt = now.AddHours(-1), FetchedAt = now });
        await db.SaveChangesAsync();
    }

    private async Task<(int BankConnections, int Accounts, int Transactions)> ShadowCountsAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return (await db.BankConnections.CountAsync(b => b.LicenseId == licenseId),
            await db.BankAccounts.CountAsync(a => a.LicenseId == licenseId),
            await db.BankTransactions.CountAsync(t => t.LicenseId == licenseId));
    }

    private async Task SetStatusAsync(Guid licenseId, ObifinConnectionStatus status, Action<ObifinConnection>? more = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var conn = await db.ObifinConnections.SingleAsync(c => c.LicenseId == licenseId);
        conn.Status = status;
        more?.Invoke(conn);
        await db.SaveChangesAsync();
    }

    private int PollEnqueueCount(Guid connectionId)
        => _factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi().EnqueuedJobs("default", 0, 1000).Count(j =>
            j.Value.Job.Type == typeof(ObifinPollJob) && j.Value.Job.Args.Contains((object)connectionId));

    [Fact]
    public async Task Girissiz_istek_login_e_yonlenir()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/admin/obifin");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Kimlik_kaydi_sifreli_saklanir_ve_sayfada_gorunmez()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var pw = $"pw-{Guid.NewGuid():N}"; var key = $"k-{Guid.NewGuid():N}";

        var post = await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["BaseUrl"] = "https://example.invalid",
            ["UserCode"] = "api@x", ["Password"] = pw, ["ApiKey"] = key,
        }));
        post.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.OK);

        var html = await client.GetStringAsync("/admin/obifin");
        html.Should().Contain("api@x").And.NotContain(pw).And.NotContain(key);
        (await client.GetStringAsync($"/admin/obifin?license={licenseId}")).Should().NotContain(pw).And.NotContain(key,
            "düzenleme formu kullanıcı ve adresi doldurur, şifre ve API anahtarını asla");
        var conn = await ConnectionAsync(licenseId);
        conn.PasswordProtected.Should().NotContain(pw);
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
                .Where(a => a.TargetId == licenseId.ToString()).Select(a => a.Details ?? "").ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(d => !d.Contains(pw) && !d.Contains(key), "audit'e şifre/API anahtarı girmez");
    }

    [Fact]
    public async Task Dogrula_dugmesi_Null_istemcide_Failed_ve_hata_metni_gosterir()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["UserCode"] = "api@x", ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        }));

        await client.PostAsync("/admin/obifin?handler=Verify", await FormAsync(client, "/admin/obifin",
            new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString() }));
        await client.GetStringAsync("/admin/obifin"); // TempData bildirimi ("Doğrulanamadı: …") burada tüketilir

        // Sayfanın tamamına bakmak hiçbir şey kanıtlamaz: yardım satırı her zaman "Failed" der, bildirim de hata metnini
        // taşır. Kalıcı durum ve son hata bağlantının SATIRINDA görünmeli.
        var row = (await ParseAsync(await client.GetStringAsync("/admin/obifin"))).QuerySelector($"tr[data-license='{licenseId}']")!;
        row.QuerySelector("[data-cell='status']")!.TextContent.Should().Be("Failed");
        row.QuerySelector("[data-cell='last-error']")!.TextContent.Should().Contain("obifin-not-configured");
    }

    [Fact]
    public async Task Banka_baglantisi_formu_kimligi_saklamaz()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["UserCode"] = "api@x", ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        }));
        var bankUser = $"ws-{Guid.NewGuid():N}";
        var bankPw = $"pw-{Guid.NewGuid():N}";
        var bankUrl = $"https://{Guid.NewGuid():N}.example.invalid/wsdl";

        var resp = await client.PostAsync("/admin/obifin?handler=AddBank", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["BankaKodu"] = "qnb", ["Label"] = "QNB",
            ["Field_KullaniciAdi"] = bankUser, ["Field_Sifre"] = bankPw, ["Field_Url"] = bankUrl,
        }));

        // NullObifinClient fırlatır → TempData["Error"], kayıt yok; ama kimlik hiçbir yere yazılmamış olmalı.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.OK);
        (resp.Headers.Location?.ToString() ?? "").Should().NotContain(bankUser).And.NotContain(bankPw);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankConnections.CountAsync(b => b.LicenseId == licenseId)).Should().Be(0);
        var html = await client.GetStringAsync("/admin/obifin");
        html.Should().NotContain(bankPw).And.NotContain(bankUser).And.NotContain(bankUrl);
        (await ParseAsync(html)).Body!.TextContent.Should().Contain("Banka bağlantısı eklenemedi: obifin-not-configured");
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == licenseId);
        (conn.LastError ?? "").Should().NotContain(bankPw).And.NotContain(bankUser);
        (await db.AuditLogs.AsNoTracking().Select(a => a.Details ?? "").ToListAsync())
            .Should().OnlyContain(d => !d.Contains(bankPw) && !d.Contains(bankUser) && !d.Contains(bankUrl));
    }

    [Theory]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(ObifinProtocolException))]
    public async Task Banka_eklemede_gecici_hata_500_vermez_siniflandirilmis_metni_gosterir(Type exceptionType)
    {
        // Zaman aşımı (HttpClient.Timeout → TaskCanceledException, isteğin kendi jetonu iptal DEĞİL), ağ ve vekil hataları:
        // sayfa düşmez; ham istisna metni (İngilizce ağ metni, vekil HTML'i) yerine sınıflandırılmış kısa metin görünür.
        using var factory = new FailingAddApiFactory();
        var marker = $"ham-{Guid.NewGuid():N}";
        factory.Obifin.AddFails = () => exceptionType == typeof(ObifinProtocolException)
            ? new ObifinProtocolException(marker)
            : (Exception)Activator.CreateInstance(exceptionType, marker)!;
        var licenseId = await SeedLicenseAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["UserCode"] = "api@x", ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        }));
        var bankPw = $"pw-{Guid.NewGuid():N}";

        var resp = await client.PostAsync("/admin/obifin?handler=AddBank", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["BankaKodu"] = "isbank", ["Label"] = "Is",
            ["Field_KullaniciAdi"] = $"ws-{Guid.NewGuid():N}", ["Field_Sifre"] = bankPw,
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var html = await client.GetStringAsync("/admin/obifin");
        html.Should().NotContain(marker, "ham istisna metni gösterilmez").And.NotContain(bankPw);
        (await ParseAsync(html)).Body!.TextContent.Should()
            .Contain($"Banka bağlantısı eklenemedi: Obifin'e ulaşılamadı ({exceptionType.Name})");
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankConnections.CountAsync(b => b.LicenseId == licenseId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Banka_modulu_kapaliyken_sayfa_acilir_POST_hicbir_sey_yazmaz()
    {
        // Anahtar yok: BankHasher (ve ona bağlı bağlantı servisi) kurulamaz. Sayfa 500 değil, açık bir uyarı gösterir;
        // POST'lar aynı mesajla döner, Obifin'e ve DB'ye dokunmaz.
        using var factory = new DisabledBankApiFactory();
        var licenseId = await SeedLicenseAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();

        var get = await client.GetAsync("/admin/obifin");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await ParseAsync(await get.Content.ReadAsStringAsync());
        doc.QuerySelector("[data-banner='bank-disabled']")!.TextContent.Trim().Should().Be(BankHasher.DisabledMessage);

        var post = await client.PostAsync("/admin/obifin?handler=Save", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["UserCode"] = "api@x", ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        }));

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ParseAsync(await client.GetStringAsync("/admin/obifin"))).QuerySelector(".alert-danger.alert-dismissible")!
            .TextContent.Trim().Should().Be(BankHasher.DisabledMessage, "POST aynı mesajla döner");
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().ObifinConnections.CountAsync(c => c.LicenseId == licenseId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Kimlik_degisikligi_onaysiz_kaydedilmez_golge_veri_kalir()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");
        await SeedShadowDataAsync(licenseId);
        var before = await ConnectionAsync(licenseId);

        var resp = await SaveAsync(client, licenseId, $"yeni-{Guid.NewGuid():N}@x");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageTextAsync(client)).Should().Contain(ResetConfirmMessage);
        var after = await ConnectionAsync(licenseId);
        after.UserCode.Should().Be("api@x");
        after.PasswordProtected.Should().Be(before.PasswordProtected, "hiçbir şey kaydedilmedi");
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        (await ShadowCountsAsync(licenseId)).Should().Be((1, 1, 1), "onaysız silme yok");
    }

    [Fact]
    public async Task Kimlik_degisikligi_onayla_kaydedilir_golge_veri_silinir()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");
        await SeedShadowDataAsync(licenseId);
        var newUser = $"yeni-{Guid.NewGuid():N}@x";

        var resp = await SaveAsync(client, licenseId, newUser, confirmReset: true);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ConnectionAsync(licenseId)).UserCode.Should().Be(newUser);
        (await ShadowCountsAsync(licenseId)).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Ayni_kullaniciya_yeni_sifre_onaysiz_kaydedilir_golge_veri_kalir()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");
        await SeedShadowDataAsync(licenseId);
        var before = await ConnectionAsync(licenseId);

        var resp = await SaveAsync(client, licenseId, "api@x");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageTextAsync(client)).Should().NotContain(ResetConfirmMessage);
        (await ConnectionAsync(licenseId)).PasswordProtected.Should().NotBe(before.PasswordProtected, "yeni şifre kaydedildi");
        (await ShadowCountsAsync(licenseId)).Should().Be((1, 1, 1));
    }

    [Theory]
    [InlineData("HTTPS://EXAMPLE.invalid/", false)]
    [InlineData("https://baska.example.invalid", true)]
    public async Task Adres_degisikligi_normalize_karsilastirilir(string postedBaseUrl, bool needsConfirm)
    {
        // Sayfanın "değişti mi" kararı servisin silme kararıyla aynı olmalı: kozmetik fark (büyük harf, sondaki '/')
        // onay istemez ve veri silmez; başka bir adres onay ister.
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x", baseUrl: "https://example.invalid");
        await SeedShadowDataAsync(licenseId);

        await SaveAsync(client, licenseId, "api@x", baseUrl: postedBaseUrl);

        (await ShadowCountsAsync(licenseId)).Should().Be((1, 1, 1));
        (await PageTextAsync(client)).Contains(ResetConfirmMessage).Should().Be(needsConfirm);
        (await ConnectionAsync(licenseId)).BaseUrl.Should().Be("https://example.invalid");
    }

    [Fact]
    public async Task Duzenlenen_lisans_ilk_200_lisansin_disinda_olsa_da_iki_formda_secili_gelir()
    {
        // Seçim listesi e-postaya göre ilk 200 lisansla sınırlı. Düzenlenen lisans o pencerenin dışında kalıp seçili
        // gelemezse tarayıcı İLK seçeneği gönderir: form X'in kimliğini gösterirken başka bir lisansa yazar (onay kutusu
        // o lisansın verisini siler), banka ekleme formu X'in banka kimliğini başka lisansın Obifin hesabına iletir.
        using var factory = new ApiFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            for (var i = 0; i < 200; i++) AddLicense(db, emailPrefix: "0"); // "0-…" e-postaları "ob-…"dan önce sıralanır
            await db.SaveChangesAsync();
        }
        var licenseId = await SeedLicenseAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");

        var doc = await ParseAsync(await client.GetStringAsync($"/admin/obifin?license={licenseId}"));

        foreach (var handler in new[] { "Save", "AddBank" })
            (doc.QuerySelector($"form[action*='handler={handler}'] select[name='LicenseId'] option[selected]")?.GetAttribute("value"))
                .Should().Be(licenseId.ToString(), $"{handler} formu düzenlenen lisansa yazmalı");
        (await ParseAsync(await client.GetStringAsync("/admin/obifin")))
            .QuerySelector($"form[action*='handler=AddBank'] select[name='LicenseId'] option[value='{licenseId}']")
            .Should().NotBeNull("Obifin bağlantısı olan lisans banka ekleme listesinde hep bulunur");
    }

    [Fact]
    public async Task Simdi_cek_Verified_baglantida_cekim_isini_kuyruga_atar()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");
        await SetStatusAsync(licenseId, ObifinConnectionStatus.Verified);
        var connId = (await ConnectionAsync(licenseId)).Id;

        var resp = await client.PostAsync("/admin/obifin?handler=PollNow", await FormAsync(client, "/admin/obifin",
            new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString() }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        PollEnqueueCount(connId).Should().Be(1);
    }

    [Fact]
    public async Task Simdi_cek_dogrulanmamis_baglantida_kuyruga_atmaz_hata_gosterir()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");
        var connId = (await ConnectionAsync(licenseId)).Id;

        var resp = await client.PostAsync("/admin/obifin?handler=PollNow", await FormAsync(client, "/admin/obifin",
            new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString() }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageTextAsync(client)).Should().Contain("Bağlantı doğrulanmamış; önce Doğrula.");
        PollEnqueueCount(connId).Should().Be(0);
    }

    [Fact]
    public async Task Durum_paneli_son_cekim_yasini_gosterir_sifre_alanlari_hep_bos()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x", baseUrl: "https://example.invalid");
        await SetStatusAsync(licenseId, ObifinConnectionStatus.Verified, c =>
        {
            c.LastPolledAt = DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(-10);
            c.BackfillCompletedAt = DateTimeOffset.UtcNow.AddDays(-1);
            c.LastObifinTransactionId = 4242;
        });

        var doc = await ParseAsync(await client.GetStringAsync($"/admin/obifin?license={licenseId}"));

        var row = doc.QuerySelector($"tr[data-license='{licenseId}']")!;
        row.QuerySelector("[data-cell='status']")!.TextContent.Should().Be("Verified");
        row.QuerySelector("[data-cell='last-polled']")!.TextContent.Should().Contain("5 dk önce");
        row.TextContent.Should().Contain("4242");
        doc.Body!.TextContent.Should().Contain("Geçici hatalar durumu değiştirmez");
        foreach (var name in new[] { "Password", "ApiKey" })
        {
            var input = doc.QuerySelector($"input[name='{name}']")!;
            input.GetAttribute("type").Should().Be("password");
            (input.GetAttribute("value") ?? "").Should().BeEmpty($"{name} asla geri basılmaz");
        }
        doc.QuerySelector("input[name='UserCode']")!.GetAttribute("value").Should().Be("api@x", "düzenleme formu doldurulur");
        doc.QuerySelector("input[name='BaseUrl']")!.GetAttribute("value").Should().Be("https://example.invalid");
        doc.QuerySelector("form[action*='handler=Save'] select[name='LicenseId'] option[selected]")!
            .GetAttribute("value").Should().Be(licenseId.ToString());
        doc.QuerySelector("form[action*='handler=Save']")!.TextContent.Should().Contain("kayıtlı");
        // Banka ekleme formu işleyicinin okuduğu adları (Field_<Ad>) taşımalı; testler POST'u elle kurduğu için ayrıca bakılır.
        foreach (var field in OrderDeck.LicenseServer.Pages.Admin.Obifin.IndexModel.BankFields.Values.SelectMany(f => f).Distinct())
            doc.QuerySelector($"form[action*='handler=AddBank'] input[name='Field_{field}']").Should().NotBeNull($"{field} formda olmalı");
    }
}
