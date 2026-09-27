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
using Microsoft.Extensions.Logging;
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
    private const string ResetConfirmMessage = ObifinConnectionService.ResetConfirmMessage;
    private const string ReenterSecretsHint = OrderDeck.LicenseServer.Pages.Admin.Obifin.IndexModel.ReenterSecretsHint;

    private readonly ApiFactory _factory;
    public AdminObifinPageTests(ApiFactory factory) => _factory = factory;

    /// <summary>Banka anahtarı boş: modül kapalı açılır (BankHasher kurucuda düşer).</summary>
    private sealed class DisabledBankApiFactory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig
            => new Dictionary<string, string?> { ["OrderDeck:Bank:HashKey"] = "" };
    }

    /// <summary>Betikli istemci: başarılı ekleme kaydı banka bağlantı listesine koyar (BankaApiId rastgele), hesap listesi
    /// boş döner; ayarlanan çağrı istenen istisnayı fırlatır. Kaldırma/hareket çağrısı beklenmez.</summary>
    private sealed class PageObifin : IObifinClient
    {
        /// <summary>Null = ekleme başarılı.</summary>
        public Func<Exception>? AddFails { get; set; }
        /// <summary>Null = hesap listesi boş döner.</summary>
        public Func<Exception>? ListAccountsFails { get; set; }
        private readonly List<ObifinBankConnectionDto> _connections = new();

        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default)
        {
            if (AddFails is { } fail) return Task.FromException(fail());
            lock (_connections) _connections.Add(new ObifinBankConnectionDto(Random.Shared.NextInt64(1, 1_000_000), b, f["BankaApiAdi"], true));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default)
            => ListAccountsFails is { } fail
                ? Task.FromException<IReadOnlyList<ObifinAccountDto>>(fail())
                : Task.FromResult<IReadOnlyList<ObifinAccountDto>>(Array.Empty<ObifinAccountDto>());
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default)
        {
            lock (_connections) return Task.FromResult<IReadOnlyList<ObifinBankConnectionDto>>(_connections.ToList());
        }
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte beklenmiyor.");
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default)
            => throw new NotSupportedException("Bu testte beklenmiyor.");
    }

    /// <summary>Uyarı düzeyindeki günlük satırlarını biçimlenmiş metin + (varsa) istisnanın tam metniyle toplar.</summary>
    private sealed class WarningRecorder : ILoggerProvider
    {
        private readonly List<string> _warnings = new();
        public List<string> Warnings { get { lock (_warnings) return _warnings.ToList(); } }
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void Dispose() { }

        private sealed class Recorder(WarningRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;
                lock (owner._warnings) owner._warnings.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
            }
        }
    }

    private sealed class StubObifinApiFactory : ApiFactory
    {
        public PageObifin Obifin { get; } = new();
        public WarningRecorder Log { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(l => l.AddProvider(Log));
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

    private int PollEnqueueCount(Guid connectionId) => PollEnqueueCount(_factory, connectionId);

    /// <summary><paramref name="connectionId"/> null = tüm çekim işleri.</summary>
    private static int PollEnqueueCount(ApiFactory factory, Guid? connectionId)
        => factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi().EnqueuedJobs("default", 0, 1000).Count(j =>
            j.Value.Job.Type == typeof(ObifinPollJob) && (connectionId is null || j.Value.Job.Args.Contains((object)connectionId.Value)));

    /// <summary>Bağlantıyı servissiz, doğrudan DB'ye yazar (modül kapalıyken servis kurulamaz). Verified: koruma eksik
    /// olsaydı Doğrula / Şimdi çek ona dokunurdu. Korunan alanlar sahte, üretilmiş metin.</summary>
    private static async Task<ObifinConnection> SeedRawConnectionAsync(ApiFactory factory, Guid licenseId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var conn = new ObifinConnection { Id = Guid.NewGuid(), LicenseId = licenseId, BaseUrl = "https://example.invalid",
            UserCode = "api@x", PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
            Status = ObifinConnectionStatus.Verified, CreatedAt = now, UpdatedAt = now };
        db.ObifinConnections.Add(conn);
        await db.SaveChangesAsync();
        return conn;
    }

    private static Task<HttpResponseMessage> AddBankAsync(HttpClient client, Guid licenseId, string bankUser, string bankPw)
        => PostAsync(client, "AddBank", new Dictionary<string, string>
        {
            ["LicenseId"] = licenseId.ToString(), ["BankaKodu"] = "isbank", ["Label"] = "Is",
            ["Field_KullaniciAdi"] = bankUser, ["Field_Sifre"] = bankPw,
        });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields)
        => await client.PostAsync($"/admin/obifin?handler={handler}", await FormAsync(client, "/admin/obifin", fields));

    /// <summary>Sayfanın bildirim şeridi (TempData, _ToastPartial) — kind: "success" | "danger".</summary>
    private static async Task<string?> ToastAsync(HttpClient client, string kind)
        => (await ParseAsync(await client.GetStringAsync("/admin/obifin"))).QuerySelector($".alert-{kind}.alert-dismissible")?.TextContent.Trim();

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
        using var factory = new StubObifinApiFactory();
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

    [Theory]
    [InlineData("Save")]
    [InlineData("Verify")]
    [InlineData("AddBank")]
    [InlineData("PollNow")]
    public async Task Banka_modulu_kapaliyken_sayfa_acilir_POST_hicbir_sey_yazmaz(string handler)
    {
        // Anahtar yok: BankHasher (ve ona bağlı bağlantı servisi) kurulamaz. Sayfa 500 değil, açık bir uyarı gösterir;
        // her POST (her işleyicinin kendi koruması var) aynı mesajla döner, Obifin'e, DB'ye ve kuyruğa dokunmaz.
        using var factory = new DisabledBankApiFactory();
        var licenseId = await SeedLicenseAsync(factory);
        var conn = await SeedRawConnectionAsync(factory, licenseId);
        var client = await factory.CreateLoggedInAdminClientAsync();

        var get = await client.GetAsync("/admin/obifin");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await ParseAsync(await get.Content.ReadAsStringAsync());
        doc.QuerySelector("[data-banner='bank-disabled']")!.TextContent.Trim().Should().Be(BankHasher.DisabledMessage);

        var fields = new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString() };
        if (handler == "Save")
        {
            fields["UserCode"] = $"yeni-{Guid.NewGuid():N}@x";
            fields["Password"] = $"pw-{Guid.NewGuid():N}";
            fields["ApiKey"] = $"k-{Guid.NewGuid():N}";
            fields["ConfirmReset"] = "true";
        }
        if (handler == "AddBank")
        {
            fields["BankaKodu"] = "isbank"; fields["Label"] = "Is";
            fields["Field_KullaniciAdi"] = $"ws-{Guid.NewGuid():N}"; fields["Field_Sifre"] = $"pw-{Guid.NewGuid():N}";
        }
        var post = await PostAsync(client, handler, fields);

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, "danger")).Should().Be(BankHasher.DisabledMessage, $"{handler} aynı mesajla döner");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var after = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == licenseId);
        after.UserCode.Should().Be(conn.UserCode);
        after.Status.Should().Be(conn.Status);
        after.UpdatedAt.Should().Be(conn.UpdatedAt);
        after.LastError.Should().BeNull();
        (await db.BankConnections.CountAsync(b => b.LicenseId == licenseId)).Should().Be(0);
        PollEnqueueCount(factory, conn.Id).Should().Be(0, "kuyruğa hiçbir şey atılmadı");
    }

    [Fact]
    public async Task Banka_eklemede_Obifin_mesaji_banka_kimligini_yankilarsa_hicbir_yerde_gorunmez()
    {
        // Obifin ya da bankanın SOAP hatası gönderilen banka alanlarını geri yankılayabilir: son hata, bildirim (TempData),
        // sayfa ve sunucu günlüğü yalnız maskeli metni görür. Eşleşme harf duyarsız.
        using var factory = new StubObifinApiFactory();
        var bankUser = $"ws-{Guid.NewGuid():N}"; var bankPw = $"pw-{Guid.NewGuid():N}";
        factory.Obifin.AddFails = () => new ObifinApiException(new[] { $"Kullanici {bankUser.ToUpperInvariant()} sifre {bankPw} hatali" });
        var licenseId = await SeedLicenseAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");

        var resp = await AddBankAsync(client, licenseId, bankUser, bankPw);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var html = await client.GetStringAsync("/admin/obifin"); // bildirim burada tüketilir
        html.Should().NotContainEquivalentOf(bankUser).And.NotContainEquivalentOf(bankPw);
        var doc = await ParseAsync(html);
        doc.QuerySelector(".alert-danger.alert-dismissible")!.TextContent.Trim()
            .Should().Be("Banka bağlantısı eklenemedi: Kullanici [gizli] sifre [gizli] hatali");
        doc.QuerySelector($"tr[data-license='{licenseId}'] [data-cell='last-error']")!.TextContent
            .Should().Be("Kullanici [gizli] sifre [gizli] hatali");
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().ObifinConnections.AsNoTracking()
            .SingleAsync(c => c.LicenseId == licenseId)).LastError.Should().Be("Kullanici [gizli] sifre [gizli] hatali");
        var warnings = factory.Log.Warnings;
        warnings.Should().OnlyContain(w => !w.Contains(bankUser, StringComparison.OrdinalIgnoreCase)
            && !w.Contains(bankPw, StringComparison.OrdinalIgnoreCase), "günlüğe banka kimliği girmez");
        warnings.Should().Contain(w => w.Contains("AddBank") && w.Contains(nameof(ObifinApiException)),
            "dostça mesaja çevrilen hata sunucu izine tür + işleyici adıyla düşer");
    }

    [Fact]
    public async Task Banka_eklendi_ama_hesap_yenileme_duserse_satir_kalir_ve_bunu_soyler()
    {
        // Bağlantı Obifin'de ve yerelde AÇILDI: hesap yenilemesi düştü diye "eklenemedi" demek admin'i tekrar eklemeye
        // iter, Obifin'de ikinci bir kayıt açılırdı.
        using var factory = new StubObifinApiFactory();
        factory.Obifin.ListAccountsFails = () => new HttpRequestException($"ham-{Guid.NewGuid():N}");
        var licenseId = await SeedLicenseAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");

        var resp = await AddBankAsync(client, licenseId, $"ws-{Guid.NewGuid():N}", $"pw-{Guid.NewGuid():N}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var scope = factory.Services.CreateScope();
        var bc = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankConnections.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId);
        (await ToastAsync(client, "danger")).Should().Be(
            $"Banka bağlantısı eklendi (Obifin #{bc.BankaApiId}) ama hesap listesi yenilenemedi: Obifin'e ulaşılamadı (HttpRequestException). Saatlik yenileme tekrar dener.");
    }

    [Fact]
    public async Task Banka_eklemede_programlama_hatasi_dostca_mesaja_cevrilmez_yukari_gider()
    {
        // ObjectDisposedException bir InvalidOperationException'dır ama doğrulama/durum hatası değil: admin'e
        // "eklenemedi: …" diye gösterilmez, istisna olarak yukarı gider (sunucu izi bırakır).
        using var factory = new StubObifinApiFactory();
        factory.Obifin.AddFails = () => new ObjectDisposedException($"ham-{Guid.NewGuid():N}");
        var licenseId = await SeedLicenseAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");

        var act = () => AddBankAsync(client, licenseId, $"ws-{Guid.NewGuid():N}", $"pw-{Guid.NewGuid():N}");

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Kimlik_degisikligi_onaysiz_kaydedilmez_golge_veri_kalir()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");
        await SeedShadowDataAsync(licenseId);
        var before = await ConnectionAsync(licenseId);
        var newUser = $"yeni-{Guid.NewGuid():N}@x";

        var resp = await SaveAsync(client, licenseId, newUser);

        // Yönlendirme YOK: form admin'in YAZDIĞI kimlikle yeniden basılır. Saklı kimlikle dolsaydı kutuyu işaretleyip
        // tekrar kaydeden admin eski kimliği gönderir, değişiklik sessizce düşer ve "kaydedildi" bildirimi alırdı.
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "onay istenince sayfa yeniden basılır");
        var doc = await ParseAsync(await resp.Content.ReadAsStringAsync());
        doc.QuerySelector(".alert-danger.alert-dismissible")!.TextContent.Should().Contain(ResetConfirmMessage)
            .And.Contain(ReenterSecretsHint);
        var form = doc.QuerySelector("form[action*='handler=Save']")!;
        form.QuerySelector("input[name='UserCode']")!.GetAttribute("value").Should().Be(newUser, "yazılan kullanıcı kodu korunur");
        form.QuerySelector("select[name='LicenseId'] option[selected]")!.GetAttribute("value").Should().Be(licenseId.ToString());
        form.QuerySelector("input[name='ConfirmReset'][type='checkbox']")!.HasAttribute("checked")
            .Should().BeFalse("onay her seferinde açıkça verilir");
        form.QuerySelectorAll("input[type='password']").Should().HaveCount(2)
            .And.OnlyContain(i => string.IsNullOrEmpty(i.GetAttribute("value")), "şifre ve API anahtarı asla geri basılmaz");
        (await ToastAsync(client, "danger")).Should().BeNull("bildirim aynı istekte tüketildi, sonraki sayfada yinelenmez");
        var after = await ConnectionAsync(licenseId);
        after.UserCode.Should().Be("api@x");
        after.PasswordProtected.Should().Be(before.PasswordProtected, "hiçbir şey kaydedilmedi");
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        (await ShadowCountsAsync(licenseId)).Should().Be((1, 1, 1), "onaysız silme yok");
    }

    [Fact]
    public async Task Onay_adimi_yeniden_basilan_formla_tamamlaninca_yeni_kimlik_kaydedilir()
    {
        // Ekrandaki adımı birebir izle: onaysız kaydet → yeniden basılan formu (lisans, kullanıcı, adres; şifre/API
        // anahtarı boş = değiştirme) kutu işaretli gönder. Yeni kimlik yazılır, eski hesabın gölge verisi silinir.
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x", baseUrl: "https://example.invalid");
        await SeedShadowDataAsync(licenseId);
        var newUser = $"yeni-{Guid.NewGuid():N}@x";
        var newBaseUrl = $"https://{Guid.NewGuid():N}.example.invalid";
        var first = await SaveAsync(client, licenseId, newUser, baseUrl: newBaseUrl);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var form = (await ParseAsync(await first.Content.ReadAsStringAsync())).QuerySelector("form[action*='handler=Save']")!;

        var resp = await SaveAsync(client, new Dictionary<string, string>
        {
            ["LicenseId"] = form.QuerySelector("select[name='LicenseId'] option[selected]")!.GetAttribute("value")!,
            ["BaseUrl"] = form.QuerySelector("input[name='BaseUrl']")!.GetAttribute("value") ?? "",
            ["UserCode"] = form.QuerySelector("input[name='UserCode']")!.GetAttribute("value") ?? "",
            ["Password"] = "", ["ApiKey"] = "",
        }, confirmReset: true);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, "success")).Should().Be("Obifin kimliği kaydedildi, eski hesabın banka verisi silindi. Şimdi doğrulayın.");
        var conn = await ConnectionAsync(licenseId);
        conn.UserCode.Should().Be(newUser);
        conn.BaseUrl.Should().Be(newBaseUrl);
        (await ShadowCountsAsync(licenseId)).Should().Be((0, 0, 0));
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
        (await ToastAsync(client, "success")).Should().Be("Obifin kimliği kaydedildi, eski hesabın banka verisi silindi. Şimdi doğrulayın.");
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

        var resp = await SaveAsync(client, licenseId, "api@x", baseUrl: postedBaseUrl);

        (await ShadowCountsAsync(licenseId)).Should().Be((1, 1, 1));
        // Onay istenirse sayfa yazılan adresle yeniden basılır (200); istenmezse kaydedip yönlendirir.
        resp.StatusCode.Should().Be(needsConfirm ? HttpStatusCode.OK : HttpStatusCode.Redirect);
        var doc = await ParseAsync(needsConfirm ? await resp.Content.ReadAsStringAsync() : await client.GetStringAsync("/admin/obifin"));
        doc.Body!.TextContent.Contains(ResetConfirmMessage).Should().Be(needsConfirm);
        if (needsConfirm)
            doc.QuerySelector("form[action*='handler=Save'] input[name='BaseUrl']")!.GetAttribute("value").Should().Be(postedBaseUrl);
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
    public async Task Duz_GET_de_lisans_secili_gelmez_banka_formu_yalniz_bagli_lisanslari_listeler()
    {
        // Düz GET'te (?license= yok) hiçbir lisans seçili gelmemeli: yoksa tarayıcı e-postaya göre İLK lisansı gönderir;
        // admin lisans seçmeyi unutursa kimlik ya da bir yayıncının banka kimliği sessizce o lisansa yazılır. Boş yer
        // tutucu + required seçimi zorunlu kılar. Banka ekleme yalnız Obifin bağlantısı olan lisansta çalışır; liste de
        // yalnız onları sunar.
        using var factory = new ApiFactory();
        var connected = await SeedLicenseAsync(factory);
        var unconnected = await SeedLicenseAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, connected, "api@x");

        var doc = await ParseAsync(await client.GetStringAsync("/admin/obifin"));

        foreach (var handler in new[] { "Save", "AddBank" })
        {
            var select = doc.QuerySelector($"form[action*='handler={handler}'] select[name='LicenseId']")!;
            select.HasAttribute("required").Should().BeTrue($"{handler} formunda lisans seçimi zorunlu");
            select.QuerySelector("option")!.GetAttribute("value").Should().BeEmpty($"{handler} formunun ilk seçeneği boş yer tutucu");
            select.QuerySelectorAll("option[selected]").Select(o => o.GetAttribute("value")).Where(v => !string.IsNullOrEmpty(v))
                .Should().BeEmpty($"{handler} formunda düz GET'te hiçbir lisans seçili gelmez");
        }
        doc.QuerySelectorAll("form[action*='handler=Save'] select[name='LicenseId'] option").Select(o => o.GetAttribute("value"))
            .Should().Contain(new[] { connected.ToString(), unconnected.ToString() }, "kimlik yeni bir lisansa da kaydedilebilir");
        doc.QuerySelectorAll("form[action*='handler=AddBank'] select[name='LicenseId'] option").Select(o => o.GetAttribute("value"))
            .Where(v => !string.IsNullOrEmpty(v))
            .Should().Equal(new[] { connected.ToString() }, "banka ekleme yalnız Obifin bağlantısı olan lisansta çalışır");
    }

    [Fact]
    public async Task Bos_lisans_secimi_hicbir_lisansa_yazmaz()
    {
        // required'ı yok sayan istemci boş değer gönderir: Guid.Empty'ye bağlanır, mevcut korumalar yakalar.
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        await SaveAsync(client, licenseId, "api@x");

        var save = await SaveAsync(client, new Dictionary<string, string>
        {
            ["LicenseId"] = "", ["UserCode"] = "api@x", ["Password"] = $"pw-{Guid.NewGuid():N}", ["ApiKey"] = $"k-{Guid.NewGuid():N}",
        });
        save.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageTextAsync(client)).Should().Contain("Lisans bulunamadı.");

        var add = await client.PostAsync("/admin/obifin?handler=AddBank", await FormAsync(client, "/admin/obifin", new Dictionary<string, string>
        {
            ["LicenseId"] = "", ["BankaKodu"] = "isbank", ["Label"] = "Is",
            ["Field_KullaniciAdi"] = $"ws-{Guid.NewGuid():N}", ["Field_Sifre"] = $"pw-{Guid.NewGuid():N}",
        }));
        add.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageTextAsync(client)).Should().Contain("Banka bağlantısı eklenemedi: Önce Obifin bağlantısı kaydedilmeli.");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.ObifinConnections.CountAsync(c => c.LicenseId == Guid.Empty)).Should().Be(0);
        (await db.BankConnections.CountAsync(b => b.LicenseId == Guid.Empty || b.LicenseId == licenseId)).Should().Be(0);
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
        (await ToastAsync(client, "success")).Should()
            .Be("Çekim kuyruğa alındı; başka bir çekim sürüyorsa birkaç dakika içinde başlar.");
    }

    [Fact]
    public async Task Simdi_cek_baglantisiz_lisansta_hata_bildirir_kuyruga_atmaz()
    {
        var licenseId = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var before = PollEnqueueCount(_factory, null);

        var resp = await PostAsync(client, "PollNow", new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString() });

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, "danger")).Should().Be("Bu lisansın Obifin bağlantısı yok.");
        PollEnqueueCount(_factory, null).Should().Be(before);
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
    public async Task Gizli_banka_alanlari_parola_kutusunda_bos_birakma_ipucu_hep_gorunur()
    {
        // Ekran paylaşımında/omuz üstünden okunmasın: şifre, secret, token, key, anahtar taşıyan her banka alanı parola
        // kutusu. Obifin şifre/API anahtarı kutularında "(boş = değiştirme)" düzenleme dışında da görünür.
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var doc = await ParseAsync(await client.GetStringAsync("/admin/obifin"));

        var masked = new[] { "Sifre", "FirmaAnahtar", "ClientSecret", "AccessToken", "RefreshToken", "APIKey", "APISecret" };
        var plain = new[] { "KullaniciAdi", "Url", "FirmaKodu", "TanimNumarasi", "ClientId" };
        foreach (var f in masked)
            doc.QuerySelector($"form[action*='handler=AddBank'] input[name='Field_{f}']")!.GetAttribute("type")
                .Should().Be("password", $"{f} gizli");
        foreach (var f in plain)
            doc.QuerySelector($"form[action*='handler=AddBank'] input[name='Field_{f}']")!.GetAttribute("type")
                .Should().Be("text", $"{f} gizli değil");
        foreach (var name in new[] { "Password", "ApiKey" })
        {
            var label = doc.QuerySelector($"input[name='{name}']")!.ParentElement!.QuerySelector("label")!.TextContent;
            label.Should().Contain("boş = değiştirme").And.NotContain("kayıtlı", "düz GET'te saklı değer yok");
        }
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
        foreach (var name in new[] { "Password", "ApiKey" })
            doc.QuerySelector($"input[name='{name}']")!.ParentElement!.QuerySelector("label")!.TextContent
                .Should().Contain("kayıtlı").And.Contain("boş = değiştirme");
        // Banka ekleme formu işleyicinin okuduğu adları (Field_<Ad>) taşımalı; testler POST'u elle kurduğu için ayrıca bakılır.
        foreach (var field in OrderDeck.LicenseServer.Pages.Admin.Obifin.IndexModel.BankFields.Values.SelectMany(f => f).Distinct())
            doc.QuerySelector($"form[action*='handler=AddBank'] input[name='Field_{field}']").Should().NotBeNull($"{field} formda olmalı");
    }
}
