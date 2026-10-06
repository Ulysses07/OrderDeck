using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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
        string? username = null, int? pageNo = null)
    {
        var fields = new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString(), ["TransactionId"] = transactionId.ToString() };
        if (username is not null) fields["Username"] = username;
        if (pageNo is { } p) fields["PageNo"] = p.ToString();
        return await client.PostAsync($"{PagePath}?handler={handler}", await FormAsync(client, PathFor(licenseId), fields));
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
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = DateTimeOffset.UtcNow };
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

        var post = await PostAsync(client, "ManualMatch", lic, txId, "ayse_gul34");
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var m = (await MatchAsync(txId))!;
        m.Status.Should().Be(PaymentMatchStatus.ManualOnly); m.ActualWpfCustomerId.Should().Be(wpfId);
        var doc = await PageAsync(client, path);
        doc.QuerySelector($"tr[data-tx='{txId}'] [data-cell='actual']")!.TextContent.Should().Contain("ayse_gul34");
        doc.QuerySelector("[data-box='metrics'] [data-platform='youtube']")!.TextContent.Trim()
            .Should().Be("youtube ✓0 ✗0 elle 1 · çelişki —", "önerisiz karar gerçek müşterinin platformunda elle sayılır");
        doc.QuerySelector($"tr[data-tx='{txId}'] button[formaction*='Unmatch']")!.GetAttribute("onclick")
            .Should().Be($"return confirm('{IndexModel.UnmatchConfirmMessage}')", "kaldırma arayüzden geri alınamaz");

        // Karara bağlı harekete ikinci elle eşleme: servisin mesajı gösterilir, karar değişmez.
        await PostAsync(client, "ManualMatch", lic, txId, "ayse_gul34");
        (await ToastAsync(client, path, "danger")).Should().Be(PaymentMatchReconciler.AlreadyDecidedMessage);
        (await MatchAsync(txId))!.ActualWpfCustomerId.Should().Be(wpfId);

        await PostAsync(client, "Unmatch", lic, txId);
        m = (await MatchAsync(txId))!;
        m.Status.Should().Be(PaymentMatchStatus.NoProposal); m.ActualWpfCustomerId.Should().BeNull();
        (await ToastAsync(client, path, "success")).Should().Be(IndexModel.UnmatchedMessage);

        // Audit ayrıntısı yalnız kullanıcı adı: açıklama, tutar, IBAN girmez.
        using var scope = _factory.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
            .Where(a => a.TargetId == txId.ToString()).ToListAsync();
        audits.Should().OnlyContain(a => a.TargetType == AuditTargets.BankTransaction);
        audits.Should().ContainSingle(a => a.EventType == AuditEvents.BankMatchManual, "reddedilen ikinci eşleme audit yazmaz")
            .Which.Details.Should().Be("{\"username\":\"ayse_gul34\"}");
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
                     ("ayse_gul3", IndexModel.UnknownUsernameMessage),
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
                Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ayse_gul34",
                MergedIntoId = wpfId, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var post = await PostAsync(client, "ManualMatch", lic, txId, "ayse_gul34");

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var m = await MatchAsync(txId);
        m.Should().NotBeNull("kullanıcı adı tek bir müşteriye (asıl kayıt) çözülmeli");
        m!.ActualWpfCustomerId.Should().Be(wpfId);
    }

    [Fact]
    public async Task Baska_lisansin_hareketi_elle_eslenmez_ve_karari_kaldirilmaz()
    {
        // Kimlikler formdan gelir: A lisansıyla gönderilen B'nin hareketi servis tarafından reddedilir, B'de hiçbir şey değişmez.
        var (licA, _, _) = await SeedAsync();
        var (licB, txB, wpfB) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        await PostAsync(client, "ManualMatch", licA, txB, "ayse_gul34");
        (await ToastAsync(client, PathFor(licA), "danger")).Should().Be("Hareket bulunamadı.");
        (await MatchAsync(txB)).Should().BeNull("B'nin hareketine satır yazılmadı");

        await PostAsync(client, "ManualMatch", licB, txB, "ayse_gul34");
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
        await PostAsync(client, "ManualMatch", lic, txId, "ayse_gul34");
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

        var post = await PostAsync(client, handler, lic, txId, "ayse_gul34");

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
