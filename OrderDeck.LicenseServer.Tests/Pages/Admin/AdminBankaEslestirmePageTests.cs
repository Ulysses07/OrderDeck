using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Pages.Admin.BankaEslestirme;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public sealed class AdminBankaEslestirmePageTests : IClassFixture<ApiFactory>
{
    private const string PagePath = "/admin/banka-eslestirme";

    /// <summary>Sayfa tr-TR'ye sabit (Program.cs): tutar ve oran beklentileri aynı kültürle biçimlenir.</summary>
    private static readonly CultureInfo Tr = new("tr-TR");

    private readonly ApiFactory _factory;
    public AdminBankaEslestirmePageTests(ApiFactory factory) => _factory = factory;

    /// <summary>Banka anahtarı boş: modül kapalı açılır (BankHasher kurucuda düşer).</summary>
    private sealed class DisabledBankApiFactory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig
            => new Dictionary<string, string?> { ["OrderDeck:Bank:HashKey"] = "" };
    }

    /// <summary>Kaldırmanın ardından koşan yeniden hesabın kaydını düşürür (DB kesintisi benzetimi); yalnız kurulduktan
    /// (<see cref="RecomputeFailure.Armed"/>) sonra ve bir kez.</summary>
    private sealed class RecomputeFailingApiFactory : ApiFactory
    {
        public RecomputeFailure Failure { get; } = new();
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Failure);
    }

    /// <summary>Karara bağlı olmayan satırın yeniden hesabını tanır: satır kayıttan önce de sonra da kararsız. Kaldırmanın
    /// kendi kaydı kararlı satırı kararsıza çevirir, elle eşleme kararsız satıra karar yazar; ikisi de buna uymaz.</summary>
    private sealed class RecomputeFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && Fired == 0 && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified
                    && e.Entity.DecidedAt is null && e.OriginalValues.GetValue<DateTimeOffset?>(nameof(PaymentMatch.DecidedAt)) is null))
            {
                Fired++;
                throw new DbUpdateException("DB kesintisi benzetimi");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static string PathFor(Guid licenseId, int? pageNo = null)
        => $"{PagePath}?licenseId={licenseId}" + (pageNo is { } p ? $"&pageNo={p}" : "");

    private static async Task<FormUrlEncodedContent> FormAsync(HttpClient client, string getPath, Dictionary<string, string> fields)
    {
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(getPath));
        fields["__RequestVerificationToken"] = token;
        return new FormUrlEncodedContent(fields);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Guid licenseId, Guid transactionId,
        string? username = null, int? pageNo = null, Guid? customerId = null)
    {
        var fields = new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString(), ["TransactionId"] = transactionId.ToString() };
        if (username is not null) fields["Username"] = username;
        if (pageNo is { } p) fields["PageNo"] = p.ToString();
        if (customerId is { } c) fields["CustomerId"] = c.ToString();
        return await client.PostAsync($"{PagePath}?handler={handler}", await FormAsync(client, PathFor(licenseId), fields));
    }

    /// <summary>Müşteri araması yanıtının bir satırı (istemci betiğinin okuduğu alanlar).</summary>
    private sealed record Hit(Guid Id, string Username, string? Name, string Platform);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>Sayfanın antiforgery belirteci: arama POST'u onu satırın formundan okur.</summary>
    private static async Task<string> TokenAsync(HttpClient client, Guid licenseId)
        => AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(PathFor(licenseId)));

    /// <summary>Arama ucu: POST, sorgu gövdede (URL'de değil). <paramref name="formLicenseId"/> verilirse forma lisans da
    /// eklenir — uç onu okumamalı, lisansı hareketten türetir.</summary>
    private static Task<HttpResponseMessage> SearchRawAsync(HttpClient client, string? token, Guid transactionId, string q,
        Guid? formLicenseId = null)
    {
        var fields = new Dictionary<string, string> { ["TransactionId"] = transactionId.ToString(), ["q"] = q };
        if (token is not null) fields["__RequestVerificationToken"] = token;
        if (formLicenseId is { } l) fields["LicenseId"] = l.ToString();
        return client.PostAsync($"{PagePath}?handler=CustomerSearch", new FormUrlEncodedContent(fields));
    }

    private static async Task<List<Hit>> SearchAsync(HttpClient client, string token, Guid transactionId, string q,
        Guid? formLicenseId = null)
    {
        var resp = await SearchRawAsync(client, token, transactionId, q, formLicenseId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, q);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        return (await resp.Content.ReadFromJsonAsync<List<Hit>>(WebJson))!;
    }

    private async Task<Guid> AddCustomerAsync(Guid licenseId, string platform, string username, string? fullName = null,
        string? displayName = null, bool purged = false, Guid? mergedInto = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = platform, Username = username,
            FullName = fullName, DisplayName = displayName, MergedIntoId = mergedInto, UpdatedAt = DateTimeOffset.UtcNow,
            PurgedAt = purged ? DateTimeOffset.UtcNow : null };
        db.WpfCustomerProjections.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static async Task<IDocument> ParseAsync(string html)
        => await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

    private static async Task<IDocument> PageAsync(HttpClient client, string path)
        => await ParseAsync(await client.GetStringAsync(path));

    /// <summary>Sayfanın bildirim şeridi (TempData, _ToastPartial) — kind: "success" | "danger".</summary>
    private static async Task<string?> ToastAsync(HttpClient client, string path, string kind)
        => (await PageAsync(client, path)).QuerySelector($".alert-{kind}.alert-dismissible")?.TextContent.Trim();

    private Task<(Guid LicenseId, Guid TxId, Guid WpfId)> SeedAsync() => SeedAsync(_factory);

    /// <summary>Obifin bağlantılı lisans + YouTube müşterisi + açıklamasında kullanıcı adı geçmeyen gelen hareket.</summary>
    private static async Task<(Guid LicenseId, Guid TxId, Guid WpfId)> SeedAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var lic = AddLicense(db, connected: true);
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.Add(wpf);
        var tx = Tx(lic, 120m, "EFT GELEN aciklamasiz");
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return (lic, tx.Id, wpf.Id);
    }

    /// <summary>Müşteri + lisans (kaydetmez); <paramref name="connected"/> ise Obifin bağlantısı da (korunan alanlar boş).</summary>
    private static Guid AddLicense(LicenseDbContext db, bool connected)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"be-{Guid.NewGuid():N}@x", Name = "Be", PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-BE-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        if (connected)
        {
            db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://example.invalid",
                UserCode = $"api-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        }
        return lic;
    }

    private static BankTransaction Tx(Guid lic, decimal amount, string description, DateTimeOffset? at = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb",
        Direction = BankTransactionDirection.Incoming, Amount = amount, Currency = "TL", OccurredAt = at ?? DateTimeOffset.UtcNow,
        Description = description, FetchedAt = DateTimeOffset.UtcNow,
    };

    private async Task<PaymentMatch?> MatchAsync(Guid txId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BankTransactionId == txId);
    }

    [Fact]
    public async Task Girissiz_login_e_yonlenir()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync(PagePath);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Liste_gosterir_elle_esler_ve_kaldirir()
    {
        var (lic, txId, wpfId) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var path = PathFor(lic);

        var list = await PageAsync(client, path);
        var tr = list.QuerySelector($"tr[data-tx='{txId}']")!;
        tr.QuerySelector("[data-cell='amount']")!.TextContent.Trim().Should().Be($"{120m.ToString("N2", Tr)} TL");
        tr.QuerySelector("[data-cell='description']")!.TextContent.Trim().Should().Be("EFT GELEN aciklamasiz");
        list.QuerySelector("[data-box='metrics'] [data-phase2='receipt']")!.TextContent.Trim().Should().Be("0/200, çelişki 0/0 (—)");

        var post = await PostAsync(client, "ManualMatch", lic, txId, "ornek_musteri34");
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var m = (await MatchAsync(txId))!;
        m.Status.Should().Be(PaymentMatchStatus.ManualOnly); m.ActualWpfCustomerId.Should().Be(wpfId);
        var doc = await PageAsync(client, path);
        doc.QuerySelector($"tr[data-tx='{txId}'] [data-cell='actual']")!.TextContent.Should().Contain("ornek_musteri34");
        doc.QuerySelector("[data-box='metrics'] [data-platform='youtube']")!.TextContent.Trim()
            .Should().Be("youtube ✓0 ✗0 elle 1 · çelişki —", "önerisiz karar gerçek müşterinin platformunda elle sayılır");
        doc.QuerySelector($"tr[data-tx='{txId}'] button[formaction*='Unmatch']")!.GetAttribute("onclick")
            .Should().Be($"return confirm('{IndexModel.UnmatchConfirmMessage}')", "kaldırma arayüzden geri alınamaz");

        // Karara bağlı harekete ikinci elle eşleme: servisin mesajı gösterilir, karar değişmez.
        await PostAsync(client, "ManualMatch", lic, txId, "ornek_musteri34");
        (await ToastAsync(client, path, "danger")).Should().Be(PaymentMatchReconciler.AlreadyDecidedMessage);
        (await MatchAsync(txId))!.ActualWpfCustomerId.Should().Be(wpfId);

        await PostAsync(client, "Unmatch", lic, txId);
        m = (await MatchAsync(txId))!;
        m.Status.Should().Be(PaymentMatchStatus.NoProposal); m.ActualWpfCustomerId.Should().BeNull();
        (await ToastAsync(client, path, "success")).Should().Be(IndexModel.UnmatchedMessage);

        // Audit ayrıntısı yalnız müşteri kayıt numarası (KVKK Ö-14): kullanıcı adı, açıklama, tutar, IBAN girmez.
        using var scope = _factory.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
            .Where(a => a.TargetId == txId.ToString()).ToListAsync();
        audits.Should().OnlyContain(a => a.TargetType == AuditTargets.BankTransaction);
        audits.Should().ContainSingle(a => a.EventType == AuditEvents.BankMatchManual, "reddedilen ikinci eşleme audit yazmaz")
            .Which.Details.Should().Be($"{{\"wpfCustomerId\":\"{wpfId}\"}}");
        audits.Should().ContainSingle(a => a.EventType == AuditEvents.BankMatchUnmatch).Which.Details.Should().BeNull();
    }

    [Fact]
    public async Task Bilinmeyen_kullanici_adi_hata_seridi_verir_eslemez()
    {
        var (lic, txId, _) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var path = PathFor(lic);

        await PostAsync(client, "ManualMatch", lic, txId, "yok_boyle_biri");

        (await ToastAsync(client, path, "danger")).Should().Be(IndexModel.UnknownUsernameMessage);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.CountAsync(x => x.BankTransactionId == txId && x.ActualWpfCustomerId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Kullanici_adi_yalniz_lisansin_silinmemis_musterilerinde_tam_esitlikle_aranir()
    {
        // Başka lisansın müşterisi, KVKK'yla silinmiş müşteri ve önek eşleşmesi bulunmaz; aynı ad iki müşterideyse (iki platform)
        // hangisi olduğu tahmin edilmez.
        var (lic, txId, _) = await SeedAsync();
        var (otherLic, _, _) = await SeedAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            WpfCustomerProjection C(Guid license, string platform, string username, bool purged = false) => new()
            { Id = Guid.NewGuid(), LicenseId = license, Platform = platform, Username = username, UpdatedAt = DateTimeOffset.UtcNow,
              PurgedAt = purged ? DateTimeOffset.UtcNow : null };
            db.WpfCustomerProjections.AddRange(
                C(otherLic, "instagram", "baska_lisansta"),
                C(lic, "instagram", "silinmis_biri", purged: true),
                C(lic, "instagram", "iki_platformda"), C(lic, "tiktok", "iki_platformda"));
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        foreach (var (username, message) in new[]
                 {
                     ("baska_lisansta", IndexModel.UnknownUsernameMessage),
                     ("silinmis_biri", IndexModel.UnknownUsernameMessage),
                     ("ornek_musteri3", IndexModel.UnknownUsernameMessage),
                     ("iki_platformda", IndexModel.AmbiguousUsernameMessage),
                 })
        {
            var post = await PostAsync(client, "ManualMatch", lic, txId, username);
            post.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ToastAsync(client, PathFor(lic), "danger")).Should().Be(message, username);
        }
        (await MatchAsync(txId))?.ActualWpfCustomerId.Should().BeNull();
    }

    /// <summary>
    /// A5b: kopya (MergedIntoId dolu) asıl kaydın kullanıcı adını taşır ama müşteri değil, yönlendirmedir. Aranan adla
    /// eşleşen ikinci satır sayılsaydı birleşmiş her kişi için "belirsiz" denir, elle eşleme hiç yapılamazdı.
    /// </summary>
    [Fact]
    public async Task Elle_eslemede_ayni_kullanici_adli_kopya_belirsizlik_yaratmaz_asil_kayit_bulunur()
    {
        var (lic, txId, wpfId) = await SeedAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ornek_musteri34",
                MergedIntoId = wpfId, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var post = await PostAsync(client, "ManualMatch", lic, txId, "ornek_musteri34");

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var m = await MatchAsync(txId);
        m.Should().NotBeNull("kullanıcı adı tek bir müşteriye (asıl kayıt) çözülmeli");
        m!.ActualWpfCustomerId.Should().Be(wpfId);
    }

    /// <summary>
    /// Kullanıcı adı kimlik anahtarıyla aranır (Shopper aday aramalarıyla aynı gerekçe): SQL Server'ın CI_AS'si "İrem" ile
    /// "irem"i farklı sayar; yönetici büyük harfle, noktalı İ ile ya da kenar boşluğuyla yazsa da aynı müşteri bulunmalı.
    /// </summary>
    [Fact]
    public async Task Elle_esleme_kullanici_adini_kimlik_anahtariyla_bulur()
    {
        var (lic, txId, _) = await SeedAsync();
        Guid customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = new WpfCustomerProjection
                { Id = Guid.NewGuid(), LicenseId = lic, Platform = "instagram", Username = "irem_k", UpdatedAt = DateTimeOffset.UtcNow };
            db.WpfCustomerProjections.Add(customer);
            await db.SaveChangesAsync();
            customerId = customer.Id;
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var post = await PostAsync(client, "ManualMatch", lic, txId, " İrem_K ");

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var match = await MatchAsync(txId);
        match.Should().NotBeNull("'İrem_K' ile 'irem_k' aynı kimlik anahtarına düşer");
        match!.ActualWpfCustomerId.Should().Be(customerId);
    }

    [Fact]
    public async Task Musteri_aramasi_yalniz_hareketin_lisansindaki_silinmemis_asil_musterileri_dondurur()
    {
        // Lisans hareketten türetilir: formda başka lisans gönderilse de o lisansın müşterisi gelmez. KVKK'yla silinmiş müşteri
        // ve birleşmiş kopya (yönlendirme, müşteri değil) listelenmez. Bilinmeyen hareket boş döner.
        var (lic, txId, _) = await SeedAsync();
        var (otherLic, otherTx, _) = await SeedAsync();
        var mine = await AddCustomerAsync(lic, "instagram", "arama_hedef_bir", fullName: "Deneme Kişi");
        await AddCustomerAsync(lic, "instagram", "arama_hedef_silinmis", purged: true);
        await AddCustomerAsync(lic, "tiktok", "arama_hedef_kopya", mergedInto: mine);
        var other = await AddCustomerAsync(otherLic, "instagram", "arama_hedef_baska");
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var token = await TokenAsync(client, lic);

        (await SearchAsync(client, token, txId, "arama_hedef"))
            .Should().Equal(new Hit(mine, "arama_hedef_bir", "Deneme Kişi", "instagram"));
        (await SearchAsync(client, token, txId, "arama_hedef", formLicenseId: otherLic)).Select(h => h.Id)
            .Should().Equal(new[] { mine }, "formdaki lisans okunmaz");
        (await SearchAsync(client, token, otherTx, "arama_hedef")).Select(h => h.Id).Should().Equal(other);
        (await SearchAsync(client, token, Guid.NewGuid(), "arama_hedef")).Should().BeEmpty();

        // Yanıtta yalnız gereken alanlar; istemci betiği bu adları okur.
        var resp = await SearchRawAsync(client, token, txId, "arama_hedef");
        using (var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
        {
            json.RootElement.EnumerateArray().Single().EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo("id", "username", "name", "platform");
        }
        // Arama POST'tur (sorgu URL'ye, dolayısıyla erişim günlüğüne girmez) ve antiforgery ister.
        (await SearchRawAsync(client, null, txId, "arama_hedef")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Musteri_aramasi_Turkce_karakter_ve_buyuk_kucuk_harf_duyarsiz_kullanici_adi_gorunen_ad_ve_ad_soyadda_arar()
    {
        var (lic, txId, _) = await SeedAsync();
        // Kurgusal adlar (repo herkese açık): Türkçe karakterleri (ş ü ç ğ ı I ö) taşır, gerçek bir kişiye benzemez.
        var id = await AddCustomerAsync(lic, "youtube", "sevgi_kuş34", fullName: "Şükran Çağdeneme Işıkörnek", displayName: "Gülöz Takmaad");
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var token = await TokenAsync(client, lic);

        var expected = new Hit(id, "sevgi_kuş34", "Şükran Çağdeneme Işıkörnek", "youtube");
        foreach (var q in new[] { "SEVGİ", "sevgi kus", "Kuş3", "sukran", "ÇAĞDENEME", "isikornek", "IŞIKÖRNEK", "gul oz", "GÜLÖZ", "takmaad", " şükran  çağ " })
            (await SearchAsync(client, token, txId, q)).Should().Equal(new[] { expected }, q);
        (await SearchAsync(client, token, txId, "sevgi_kuş35")).Should().BeEmpty("her parça bir alanda geçmeli");
    }

    [Fact]
    public async Task Musteri_aramasi_iki_karakterden_kisa_sorguda_bos_en_cok_on_sonuc_kullanici_adi_eslesmesi_once()
    {
        var (lic, txId, _) = await SeedAsync();
        for (var i = 12; i >= 1; i--) await AddCustomerAsync(lic, "instagram", $"zk_uye{i:00}");
        var byName = await AddCustomerAsync(lic, "tiktok", "baska_ad", fullName: "Zk Deneme");
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var token = await TokenAsync(client, lic);

        foreach (var q in new[] { "", " ", "z", " z ", "@z", "z." })
            (await SearchAsync(client, token, txId, q)).Should().BeEmpty($"'{q}' normalleşince 2 karakterden kısa");

        (await SearchAsync(client, token, txId, "zk")).Select(h => h.Username).Should()
            .Equal(Enumerable.Range(1, IndexModel.CustomerSearchMaxResults).Select(i => $"zk_uye{i:00}"),
                "en çok 10; kullanıcı adında eşleşenler addakinden önce, kullanıcı adına göre sıralı");
        (await SearchAsync(client, token, txId, "zk de")).Select(h => h.Id).Should().Equal(byName);
    }

    [Fact]
    public async Task Elle_esleme_secilen_musteri_kimligiyle_yapilir_ayni_ad_iki_platformda_olsa_da()
    {
        // Aramadan seçim kimliği taşır: kullanıcı adıyla belirsiz kalan (iki platform) kişi de eşlenebilir. Kimlik varken
        // kutudaki metin (seçimin etiketi) kullanıcı adı diye aranmaz.
        var (lic, txId, _) = await SeedAsync();
        await AddCustomerAsync(lic, "instagram", "iki_platform_x");
        var tiktok = await AddCustomerAsync(lic, "tiktok", "iki_platform_x");
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var post = await PostAsync(client, "ManualMatch", lic, txId, "iki_platform_x · tiktok", customerId: tiktok);

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, PathFor(lic), "success")).Should().Be(IndexModel.MatchedNoIbanMessage, "hareketin karşı IBAN'ı yok");
        var m = (await MatchAsync(txId))!;
        m.ActualWpfCustomerId.Should().Be(tiktok); m.Status.Should().Be(PaymentMatchStatus.ManualOnly);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
                .SingleAsync(a => a.TargetId == txId.ToString() && a.EventType == AuditEvents.BankMatchManual))
            .Details.Should().Be($"{{\"wpfCustomerId\":\"{tiktok}\"}}");
    }

    [Fact]
    public async Task Elle_esleme_baska_lisansin_silinmis_kopya_ya_da_bilinmeyen_musteri_kimligini_reddeder_kullanici_adina_dusmez()
    {
        // Kimlik formdan gelir: hareketin lisansında olmayan, KVKK'yla silinmiş ya da kopya kimlik reddedilir. Formdaki geçerli
        // kullanıcı adına geri düşülmez — yanlış kimlik başka bir müşteriyi sessizce eşlerdi.
        var (lic, txId, wpfId) = await SeedAsync();
        var (_, _, otherWpf) = await SeedAsync();
        var purged = await AddCustomerAsync(lic, "instagram", "silinen_kimlik", purged: true);
        var copy = await AddCustomerAsync(lic, "youtube", "ornek_musteri34", mergedInto: wpfId);
        var client = await _factory.CreateLoggedInAdminClientAsync();

        foreach (var id in new[] { otherWpf, purged, copy, Guid.NewGuid() })
        {
            var post = await PostAsync(client, "ManualMatch", lic, txId, "ornek_musteri34", customerId: id);
            post.StatusCode.Should().Be(HttpStatusCode.Redirect);
            (await ToastAsync(client, PathFor(lic), "danger")).Should().Be("Müşteri bulunamadı.", id.ToString());
        }
        (await MatchAsync(txId))?.ActualWpfCustomerId.Should().BeNull();
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
            .CountAsync(a => a.TargetId == txId.ToString())).Should().Be(0);
    }

    /// <summary>JS'siz tarayıcının gerçekten gönderdiği biçim: sayfanın elle eşle formundaki her alan, gizli CustomerId boş
    /// dizeyle ("CustomerId="). Boş kimlik null'a bağlanmalı ve eşleme kutudaki kullanıcı adıyla yapılmalı — öbür kullanıcı
    /// adı testleri alanı hiç göndermez, bu sözleşmeyi kilitlemez.</summary>
    [Fact]
    public async Task JS_siz_form_bos_CustomerId_alaniyla_gonderilince_kullanici_adiyla_esler()
    {
        var (lic, txId, wpfId) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var form = (await PageAsync(client, PathFor(lic))).QuerySelector($"tr[data-tx='{txId}'] form[data-form='manual-match']")!;
        var fields = form.QuerySelectorAll("input[name]").ToDictionary(i => i.GetAttribute("name")!, i => i.GetAttribute("value") ?? "");
        fields.Should().ContainKey("CustomerId").WhoseValue.Should().BeEmpty();
        fields["Username"] = "ornek_musteri34";
        var action = form.QuerySelector("button[formaction*='ManualMatch']")!.GetAttribute("formaction")!;

        var post = await client.PostAsync(action, new FormUrlEncodedContent(fields));

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, PathFor(lic), "success")).Should().Be(IndexModel.MatchedNoIbanMessage);
        var m = (await MatchAsync(txId))!;
        m.ActualWpfCustomerId.Should().Be(wpfId); m.Status.Should().Be(PaymentMatchStatus.ManualOnly);
    }

    [Fact]
    public async Task Elle_esleme_IBAN_ogrenince_ayni_IBANdan_kac_havalenin_bu_musteriye_onerildigini_soyler()
    {
        // 27.10 şikâyetinin yarısı görünmezlikti: sayfa her durumda yalnız "Eşlendi." diyordu. Öğrenilen IBAN aynı IBAN'ın
        // kararsız havalelerine uygulanır ve bildirim kaç havalenin bu müşteriye önerildiğini söyler (gölge mod: öneri, karar değil).
        var (lic, txId, wpfId) = await SeedAsync();
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var older = Tx(lic, 80m, "EFT GELEN", DateTimeOffset.UtcNow.AddDays(-3));
        // KVKK Ö-7 kesmesinden sonraki çekim: özetler geçerli IBAN'dan.
        older.CounterpartyIbanHash = hash; older.CounterpartyIbanHashValid = true;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.BankTransactions.Add(older);
            var tx = await db.BankTransactions.SingleAsync(t => t.Id == txId);
            tx.CounterpartyIbanHash = hash; tx.CounterpartyIbanHashValid = true;
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PaymentMatcher>().MatchAsync(older, CancellationToken.None);
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        (await PostAsync(client, "ManualMatch", lic, txId, customerId: wpfId)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var expected = IndexModel.ManualMatchedMessage(new ManualMatchResult(ManualMatchIbanOutcome.Learned, Reevaluated: 1, ProposedToCustomer: 1));
        expected.Should().Contain("1 havale bu müşteriye önerildi");
        (await ToastAsync(client, PathFor(lic), "success")).Should().Be(expected);
        var row = (await PageAsync(client, PathFor(lic))).QuerySelector($"tr[data-tx='{older.Id}']")!;
        row.QuerySelector("[data-cell='proposed']")!.TextContent.Should().Contain("ornek_musteri34").And.Contain("IbanMemory");
        row.QuerySelector("[data-cell='actual']")!.TextContent.Trim().Should().BeEmpty("otomatik karar yok");
    }

    [Fact]
    public async Task Elle_esleme_bir_IBANi_cok_havaleye_yayinca_bildirim_ortak_hesap_uyarisi_da_verir()
    {
        // Ortak ya da havuz IBAN'ı (e-para kuruluşu, aracı hesap) tek müşteriye eşlemek yanlışı geriye dönük çok satıra yayar.
        // Önerilen havale sayısı eşiği aşınca ayrıca uyarı şeridi çıkar: "N havale önerildi" toplu yanlış elle eşlemeye davet etmesin.
        var (lic, txId, wpfId) = await SeedAsync();
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        const int n = IndexModel.PropagationWarningThreshold + 1;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var older = Enumerable.Range(1, n).Select(i => Tx(lic, 80m + i, "EFT GELEN", DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(i))).ToList();
            foreach (var t in older) { t.CounterpartyIbanHash = hash; t.CounterpartyIbanHashValid = true; }
            db.BankTransactions.AddRange(older);
            var tx = await db.BankTransactions.SingleAsync(t => t.Id == txId);
            tx.CounterpartyIbanHash = hash; tx.CounterpartyIbanHashValid = true;
            await db.SaveChangesAsync();
            var matcher = scope.ServiceProvider.GetRequiredService<PaymentMatcher>();
            foreach (var t in older) await matcher.MatchAsync(t, CancellationToken.None);
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        (await PostAsync(client, "ManualMatch", lic, txId, customerId: wpfId)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var result = new ManualMatchResult(ManualMatchIbanOutcome.Learned, Reevaluated: n, ProposedToCustomer: n);
        var page = await PageAsync(client, PathFor(lic));
        page.QuerySelector(".alert-success.alert-dismissible")!.TextContent.Trim().Should().Be(IndexModel.ManualMatchedMessage(result));
        var warning = page.QuerySelector(".alert-warning.alert-dismissible")!.TextContent.Trim();
        warning.Should().Be(IndexModel.ManualMatchedWarning(result)).And.Contain(n.ToString());
        IndexModel.ManualMatchedWarning(result with { Reevaluated = n - 1, ProposedToCustomer = n - 1 })
            .Should().BeNull("eşikte uyarı yok");
    }

    [Fact]
    public async Task Elle_esle_kutusu_ayri_betikle_aranir_satir_ici_script_yok_kutu_JS_siz_kullanici_adi_gonderir()
    {
        // CSP: betik wwwroot'tan ('self'), satır içi script yok. Kutu JS'siz de çalışır: adı Username (eski tam eşitlik yolu),
        // seçilen müşterinin kimliği gizli CustomerId alanında.
        var (lic, txId, _) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var doc = await PageAsync(client, PathFor(lic));

        doc.QuerySelector("table[data-search-url]")!.GetAttribute("data-search-url").Should().Be($"{PagePath}?handler=CustomerSearch");
        var form = doc.QuerySelector($"tr[data-tx='{txId}'] form[data-form='manual-match']")!;
        var input = form.QuerySelector("input[data-customer-search]")!;
        input.GetAttribute("name").Should().Be("Username");
        input.GetAttribute("autocomplete").Should().Be("off");
        var list = doc.GetElementById(input.GetAttribute("aria-controls")!)!;
        list.GetAttribute("role").Should().Be("listbox");
        list.HasAttribute("hidden").Should().BeTrue();
        form.QuerySelector("input[type='hidden'][name='CustomerId'][data-customer-id]")!.GetAttribute("value").Should().BeNullOrEmpty();
        form.QuerySelector("input[name='__RequestVerificationToken']").Should().NotBeNull("arama POST'u belirteci formdan okur");
        doc.QuerySelectorAll("script:not([src])").Should().BeEmpty();
        var src = doc.QuerySelectorAll("script[src]").Select(s => s.GetAttribute("src")!)
            .Should().ContainSingle(s => s.StartsWith("/js/banka-eslestirme.js")).Subject;

        var js = await client.GetAsync(src);
        js.StatusCode.Should().Be(HttpStatusCode.OK);
        js.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
    }

    [Fact]
    public async Task Musteri_aramasi_sorguyu_ve_sonucu_gunluge_yazmaz()
    {
        using var factory = new LogApiFactory();
        var (lic, txId, _) = await SeedAsync(factory);
        var marker = $"gunluk{Guid.NewGuid():N}"[..16];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "instagram",
                Username = marker, FullName = "Deneme Günlükad", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = await factory.CreateLoggedInAdminClientAsync();

        var query = marker[..12];

        var hits = await SearchAsync(client, await TokenAsync(client, lic), txId, query);

        hits.Should().ContainSingle(h => h.Username == marker);
        factory.Log.Messages.Should().NotContain(m => m.Contains(query) || m.Contains("Günlükad"));
    }

    [Fact]
    public async Task Baska_lisansin_hareketi_elle_eslenmez_ve_karari_kaldirilmaz()
    {
        // Kimlikler formdan gelir: A lisansıyla gönderilen B'nin hareketi servis tarafından reddedilir, B'de hiçbir şey değişmez.
        var (licA, _, _) = await SeedAsync();
        var (licB, txB, wpfB) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        await PostAsync(client, "ManualMatch", licA, txB, "ornek_musteri34");
        (await ToastAsync(client, PathFor(licA), "danger")).Should().Be("Hareket bulunamadı.");
        (await MatchAsync(txB)).Should().BeNull("B'nin hareketine satır yazılmadı");

        await PostAsync(client, "ManualMatch", licB, txB, "ornek_musteri34");
        var decided = (await MatchAsync(txB))!;
        decided.ActualWpfCustomerId.Should().Be(wpfB);

        await PostAsync(client, "Unmatch", licA, txB);
        (await ToastAsync(client, PathFor(licA), "danger")).Should().Be("Hareket bulunamadı.");
        var after = (await MatchAsync(txB))!;
        after.ActualWpfCustomerId.Should().Be(wpfB); after.Status.Should().Be(decided.Status); after.UpdatedAt.Should().Be(decided.UpdatedAt);
    }

    [Fact]
    public async Task Ham_JSON_ve_hashler_sayfaya_cikmaz_karsi_IBAN_yalniz_maskeli()
    {
        var (lic, _, _) = await SeedAsync();
        var iban = BankHasherTests.TestIban();
        var ibanHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var taxIdHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var rawMarker = $"raw-{Guid.NewGuid():N}";
        var tx = Tx(lic, 75m, "FAST GELEN");
        tx.RawJson = $"{{\"marker\":\"{rawMarker}\",\"iban\":\"{iban}\"}}";
        tx.CounterpartyIbanHash = ibanHash; tx.CounterpartyIbanMasked = BankHasher.MaskIban(iban); tx.CounterpartyTaxIdHash = taxIdHash;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.BankTransactions.Add(tx);
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var html = await client.GetStringAsync(PathFor(lic));

        html.Should().NotContain(rawMarker).And.NotContain(ibanHash).And.NotContain(taxIdHash).And.NotContain(iban[4..]);
        (await ParseAsync(html)).QuerySelector($"tr[data-tx='{tx.Id}'] [data-cell='iban']")!.TextContent.Trim()
            .Should().Be(BankHasher.MaskIban(iban));
    }

    [Fact]
    public async Task Duz_GET_de_lisans_secili_gelmez_yalniz_Obifin_bagli_lisanslar_listelenir()
    {
        var (connected, _, _) = await SeedAsync();
        Guid unconnected;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            unconnected = AddLicense(db, connected: false);
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var plain = await PageAsync(client, PagePath);
        var select = plain.QuerySelector("select[name='licenseId']")!;
        select.HasAttribute("required").Should().BeTrue();
        select.QuerySelector("option")!.GetAttribute("value").Should().BeEmpty("ilk seçenek boş yer tutucu");
        select.QuerySelectorAll("option[selected]").Should().BeEmpty("düz GET'te hiçbir lisans seçili gelmez");
        var values = select.QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToList();
        values.Should().Contain(connected.ToString()).And.NotContain(unconnected.ToString());
        plain.QuerySelector("[data-box='metrics']").Should().BeNull();

        // Listede olmayan lisans seçilmiş sayılmaz: seçim kutusu ile gösterilen veri hep aynı lisans.
        var unlisted = await PageAsync(client, PathFor(unconnected));
        unlisted.QuerySelectorAll("select[name='licenseId'] option[selected]").Should().BeEmpty();
        unlisted.QuerySelector("[data-box='metrics']").Should().BeNull();

        var chosen = await PageAsync(client, PathFor(connected));
        chosen.QuerySelectorAll("select[name='licenseId'] option[selected]").Select(o => o.GetAttribute("value"))
            .Should().Equal(connected.ToString());
        chosen.QuerySelector("[data-box='metrics']").Should().NotBeNull();
    }

    [Fact]
    public async Task Sayfa_basina_50_hareket_POST_ayni_sayfaya_doner()
    {
        var (lic, _, _) = await SeedAsync();
        Guid anyTx;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var txs = Enumerable.Range(1, IndexModel.PageSize).Select(i => Tx(lic, i, "EFT GELEN", DateTimeOffset.UtcNow.AddMinutes(-i))).ToList();
            db.BankTransactions.AddRange(txs);
            await db.SaveChangesAsync();
            anyTx = txs[0].Id;
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var first = await PageAsync(client, PathFor(lic));
        first.QuerySelectorAll("tbody tr[data-tx]").Should().HaveCount(IndexModel.PageSize);
        first.QuerySelector("a[data-page='prev']").Should().BeNull();
        first.QuerySelector("a[data-page='next']")!.GetAttribute("href").Should().Contain("pageNo=2");

        var second = await PageAsync(client, PathFor(lic, 2));
        second.QuerySelectorAll("tbody tr[data-tx]").Should().HaveCount(1);
        second.QuerySelector("a[data-page='next']").Should().BeNull();
        second.QuerySelector("a[data-page='prev']").Should().NotBeNull();
        second.QuerySelector("tbody tr[data-tx] input[name='PageNo']")!.GetAttribute("value").Should().Be("2");

        var post = await PostAsync(client, "ManualMatch", lic, anyTx, "yok_boyle_biri", pageNo: 2);
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain($"licenseId={lic}").And.Contain("pageNo=2");
    }

    [Fact]
    public async Task Kaldirmadan_sonra_yeniden_hesap_duserse_kaldirma_gecerli_basari_bildirimi_ve_audit_yazilir()
    {
        // Kaldırma kaydedildikten sonra öneriyi yeniden hesaplamak düşer (DB kesintisi). Kaydedilmiş kaldırma "çakışma" diye
        // raporlanmaz, audit'i kaybolmaz: başarı bildirimi yeniden hesabın yapılamadığını söyler.
        using var factory = new RecomputeFailingApiFactory();
        var (lic, txId, wpfId) = await SeedAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        await PostAsync(client, "ManualMatch", lic, txId, "ornek_musteri34");
        using (var scope = factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().PaymentMatches.AsNoTracking()
                .SingleAsync(x => x.BankTransactionId == txId)).ActualWpfCustomerId.Should().Be(wpfId);
        }
        factory.Failure.Armed = true;

        var post = await PostAsync(client, "Unmatch", lic, txId);

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        factory.Failure.Fired.Should().Be(1);
        (await ToastAsync(client, PathFor(lic), "success")).Should().Be(IndexModel.UnmatchedNotRecomputedMessage);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var m = await db.PaymentMatches.AsNoTracking().SingleAsync(x => x.BankTransactionId == txId);
            m.ActualWpfCustomerId.Should().BeNull(); m.DecidedAt.Should().BeNull(); m.Status.Should().Be(PaymentMatchStatus.NoProposal);
            (await db.AuditLogs.CountAsync(a => a.TargetId == txId.ToString() && a.EventType == AuditEvents.BankMatchUnmatch))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task Bagi_silen_kaldirmanin_imha_kaydinda_yapan_yoneticinin_kimligidir()
    {
        // Öğreten havale 180 günlük kuralla silinmiş, bağın süresini yalnız bu hareket taşıyor: kaldırma bağı siler (KVKK K-6).
        // İmha kaydı budanmaz; kimin yaptığı yalnız 24 ayda budanan denetim satırında kalsa 3 yıllık kayıtta yapan bilinmezdi.
        // Yapan: yöneticinin sub kimliği (DataDestruction.ActorOf), kullanıcı adı değil (KVKK Ö-14).
        var (lic, txId, wpfId) = await SeedAsync();
        var hash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var source = Tx(lic, 90m, "EFT GELEN", DateTimeOffset.UtcNow.AddDays(-200));
        // Görev 11'den sonraki çekim: özetler geçerli IBAN'dan, bağı öğretir ve tazeler (KVKK Ö-7).
        source.CounterpartyIbanHash = hash; source.CounterpartyIbanHashValid = true;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.BankTransactions.Add(source);
            var tx = await db.BankTransactions.SingleAsync(t => t.Id == txId);
            tx.CounterpartyIbanHash = hash; tx.CounterpartyIbanHashValid = true;
            await db.SaveChangesAsync();
            var recon = scope.ServiceProvider.GetRequiredService<PaymentMatchReconciler>();
            await recon.ManualMatchAsync(lic, source.Id, wpfId, CancellationToken.None);
            await recon.ManualMatchAsync(lic, txId, wpfId, CancellationToken.None);
            db.PaymentMatches.RemoveRange(await db.PaymentMatches.Where(m => m.BankTransactionId == source.Id).ToListAsync());
            db.BankTransactions.Remove(await db.BankTransactions.SingleAsync(t => t.Id == source.Id));
            await db.SaveChangesAsync(); // saklama işinin 180 günlük silmesi
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        (await PostAsync(client, "Unmatch", lic, txId)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.CustomerIbanMemories.AsNoTracking().CountAsync(m => m.LicenseId == lic)).Should().Be(0);
        var adminId = (await vdb.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id;
        var record = (await vdb.DataDestructionRecords.AsNoTracking().Where(r => r.LicenseId == lic).ToListAsync())
            .Should().ContainSingle().Subject;
        (record.Target, record.Trigger, record.Actor).Should().Be(("CustomerIbanMemories", DataDestruction.Triggers.Retention,
            adminId.ToString()));
    }

    [Fact]
    public async Task Dekonta_bagli_satirin_Kaldir_onayi_dekontu_anar()
    {
        var (lic, txId, wpfId) = await SeedAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = txId, PaymentId = Guid.NewGuid(),
                Status = PaymentMatchStatus.ManualOnly, ActualWpfCustomerId = wpfId, DecidedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var doc = await PageAsync(client, PathFor(lic));

        doc.QuerySelector($"tr[data-tx='{txId}'] button[formaction*='Unmatch']")!.GetAttribute("onclick")
            .Should().Be($"return confirm('{IndexModel.UnmatchReceiptConfirmMessage}')");
        IndexModel.UnmatchReceiptConfirmMessage.Should().Contain("dekont");
        // Kaldırma başka havalenin öğrettiği IBAN bağını da silebilir (süresi yalnız bu havaleye dayanıyorsa, KVKK K-6):
        // yönetici geri alınamaz silmeyi bilerek onaylar.
        IndexModel.UnmatchConfirmMessage.Should().Contain("bağ da silinir");
        IndexModel.UnmatchReceiptConfirmMessage.Should().Contain("bağ da silinir");
        // Onay metni tek tırnaklı JS dizesine gömülür: kesme işareti dizeyi kırar.
        IndexModel.UnmatchReceiptConfirmMessage.Should().NotContain("'");
        IndexModel.UnmatchConfirmMessage.Should().NotContain("'");
    }

    [Fact]
    public async Task Silinmis_musteri_oneri_ve_gercek_hucrede_maskelenir_kullanici_adi_sayfaya_cikmaz()
    {
        // KVKK silmesi projeksiyonun kullanıcı adını tutar (ShopperPurgeService yalnız ad/telefon/adresi boşaltır, kanıtı
        // temizler): sayfa onu göstermemeli.
        var (lic, _, _) = await SeedAsync();
        var username = $"silinen_{Guid.NewGuid():N}";
        var tx = Tx(lic, 90m, "EFT GELEN");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var purged = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "instagram", Username = username,
                UpdatedAt = DateTimeOffset.UtcNow, PurgedAt = DateTimeOffset.UtcNow };
            db.WpfCustomerProjections.Add(purged);
            db.BankTransactions.Add(tx);
            db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, PaymentId = Guid.NewGuid(),
                Status = PaymentMatchStatus.ConfirmedByHuman, Layer = PaymentMatchLayer.UsernameInDescription, Confidence = PaymentMatcher.UsernameConfidence,
                ProposedWpfCustomerId = purged.Id, ActualWpfCustomerId = purged.Id, DecidedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var html = await client.GetStringAsync(PathFor(lic));

        html.Should().NotContain(username);
        var row = (await ParseAsync(html)).QuerySelector($"tr[data-tx='{tx.Id}']")!;
        row.QuerySelector("[data-cell='proposed'] b")!.TextContent.Trim().Should().Be(IndexModel.PurgedCustomerLabel);
        row.QuerySelector("[data-cell='actual']")!.TextContent.Should().Contain(IndexModel.PurgedCustomerLabel);
    }

    [Fact]
    public async Task Olcum_kutusu_Faz2_satirinda_yalniz_dekont_onayli_kararlari_sayar_sayfa_kararlarini_ayri_gosterir()
    {
        var (lic, _, wpfId) = await SeedAsync();
        var other = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            void Decided(PaymentMatchStatus status, Guid proposed, bool receipt)
            {
                var t = Tx(lic, 50m, "EFT GELEN");
                db.BankTransactions.Add(t);
                db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = t.Id,
                    PaymentId = receipt ? Guid.NewGuid() : null, Status = status, Layer = PaymentMatchLayer.UsernameInDescription,
                    ProposedWpfCustomerId = proposed, ActualWpfCustomerId = wpfId, DecidedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            }
            for (var i = 0; i < 3; i++) Decided(PaymentMatchStatus.ConfirmedByHuman, wpfId, receipt: true);
            Decided(PaymentMatchStatus.Contradicted, other, receipt: true);
            for (var i = 0; i < 2; i++) Decided(PaymentMatchStatus.ConfirmedByHuman, wpfId, receipt: false);
            Decided(PaymentMatchStatus.ManualOnly, other, receipt: false);
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var box = (await PageAsync(client, PathFor(lic))).QuerySelector("[data-box='metrics']")!;

        box.QuerySelector("[data-phase2='receipt']")!.TextContent.Trim()
            .Should().Be($"4/200, çelişki 1/4 ({PaymentMatchMetrics.Rate(1, 4)!.Value.ToString("P1", Tr)})");
        box.QuerySelector("[data-phase2='page']")!.TextContent.Trim().Should().Be("✓2 ✗1");
        box.QuerySelector("[data-fraction='overall']")!.TextContent.Trim().Should().Be("(çelişki 2/7)");
    }

    [Fact]
    public async Task Lisans_secili_sayfa_goruntulemesi_denetime_yazilir_secimsiz_sayfa_yazmaz()
    {
        // KVKK Ö-4 (Kurul 2018/10 3-c): açıklama, kanıt ve maske gösteren sayfanın her görüntülenmesi denetime girer. Hedef banka
        // modülünün öbür denetim satırları gibi lisansın kimliği (obifin-connection); "license" hedefi lisans ANAHTARI taşır.
        var (lic, _, _) = await SeedAsync();
        var unknown = Guid.NewGuid();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var before = DateTimeOffset.UtcNow;

        await client.GetStringAsync(PathFor(lic, pageNo: 2));
        await client.GetStringAsync(PagePath);           // lisans seçili değil: veri gösterilmez
        await client.GetStringAsync(PathFor(unknown));   // listede olmayan lisans seçilmiş sayılmaz

        // Olumsuz denetim sayıyla: seçimsiz istek hangi hedefle (boş, bilinmeyen, Guid.Empty) yazsa da yakalanır. Sınıfın
        // testleri sırayla koşar, bu testten sonraki görüntülemeler yalnız bu üç istek.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var views = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EventType == AuditEvents.BankPageView && a.OccurredAt >= before).ToListAsync();
        var view = views.Should().ContainSingle("veri gösterilmeyen görüntüleme yazılmaz").Subject;
        view.TargetId.Should().Be(lic.ToString());
        view.TargetType.Should().Be(AuditTargets.ObifinConnection);
        view.Details.Should().Be("{\"page\":\"banka-eslestirme\",\"pageNo\":2}");
    }

    [Fact]
    public async Task Asiri_buyuk_sayfa_numarasi_sinirlanir_sayfa_acilir()
    {
        // (PageNo - 1) * 50 int'te taşar; negatif Skip SQL Server'da 500 verirdi.
        var (lic, _, _) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var resp = await client.GetAsync(PathFor(lic, 50_000_000));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await ParseAsync(await resp.Content.ReadAsStringAsync());
        doc.QuerySelectorAll("tbody tr[data-tx]").Should().BeEmpty();
        doc.QuerySelector("a[data-page='prev']")!.GetAttribute("href").Should().Contain($"pageNo={IndexModel.MaxPageNo - 1}");
    }

    [Theory]
    [InlineData("ManualMatch")]
    [InlineData("Unmatch")]
    public async Task Banka_modulu_kapaliyken_sayfa_acilir_POST_hicbir_sey_yazmaz(string handler)
    {
        using var factory = new DisabledBankApiFactory();
        var (lic, txId, wpfId) = await SeedAsync(factory);
        PaymentMatch before;
        using (var scope = factory.Services.CreateScope())
        {
            // Kaldırmanın silebileceği bir karar; elle eşlemenin yazabileceği boş bir satır.
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var decided = handler == "Unmatch";
            db.PaymentMatches.Add(new PaymentMatch { Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = txId,
                Status = decided ? PaymentMatchStatus.ManualOnly : PaymentMatchStatus.NoProposal, Evidence = "no-signal",
                ActualWpfCustomerId = decided ? wpfId : null, DecidedAt = decided ? DateTimeOffset.UtcNow : null,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            before = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == txId);
        }
        var client = await factory.CreateLoggedInAdminClientAsync();

        var get = await client.GetAsync(PathFor(lic));

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ParseAsync(await get.Content.ReadAsStringAsync())).QuerySelector("[data-banner='bank-disabled']")!.TextContent.Trim()
            .Should().Be(BankHasher.DisabledMessage);

        var post = await PostAsync(client, handler, lic, txId, "ornek_musteri34");

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, PathFor(lic), "danger")).Should().Be(BankHasher.DisabledMessage, $"{handler} aynı mesajla döner");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var after = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == txId);
            after.Status.Should().Be(before.Status); after.ActualWpfCustomerId.Should().Be(before.ActualWpfCustomerId);
            after.UpdatedAt.Should().Be(before.UpdatedAt);
            (await db.AuditLogs.CountAsync(a => a.TargetType == AuditTargets.BankTransaction)).Should().Be(0);
        }
    }
}
