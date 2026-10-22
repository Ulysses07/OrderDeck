using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using BankaVeriTalepleriModel = OrderDeck.LicenseServer.Pages.Admin.BankaVeriTalepleri.IndexModel;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

/// <summary>IBAN ile banka veri talebi sayfası (KVKK K-7, Ö-11): talep açılır, hareketler listelenir, dışa aktarım yalnız
/// doğrulanmış erişim talebinde ve tam IBAN içermez, silme yalnız silme talebinde metni siler ve talebi kapatır, kapatma sonuç
/// kategorisiyle; hepsi ve sayfanın her görüntülenmesi denetime yazılır. Sistemin kapattığı talep son tarihe kadar açık listede
/// görünür.</summary>
public sealed class AdminBankaVeriTalepleriPageTests : IClassFixture<ApiFactory>
{
    private const string PagePath = "/admin/banka-veri-talepleri";
    private readonly ApiFactory _factory;
    public AdminBankaVeriTalepleriPageTests(ApiFactory factory) => _factory = factory;

    /// <summary>Banka anahtarı boş: modül kapalı açılır (BankHasher kurucuda düşer).</summary>
    private sealed class DisabledBankApiFactory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig
            => new Dictionary<string, string?> { ["OrderDeck:Bank:HashKey"] = "" };
    }

    /// <summary>Obifin bağlantılı lisans + bir IBAN'dan gelen havale. Kaydedilen hareketin IBAN'ı ve maskesi üretimdeki
    /// kurallarla (özet, kısaltma).</summary>
    private async Task<(Guid LicenseId, Guid TxId, string Iban)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<BankHasher>();
        var lic = AddLicense(db);
        var iban = BankHasherTests.TestIban();
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000),
            ObifinAccountId = 6, BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 90m, Currency = "TL",
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-1), FetchedAt = DateTimeOffset.UtcNow.AddDays(-1), Description = "HAVALE talep",
            CounterpartyIbanHash = hasher.HashIban(iban), CounterpartyIbanMasked = BankHasher.MaskIban(iban) };
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return (lic, tx.Id, iban);
    }

    /// <summary>Müşteri + lisans + Obifin bağlantısı (korunan alanlar boş); kaydetmez.</summary>
    private static Guid AddLicense(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"bv-{Guid.NewGuid():N}@x", Name = "Bv", PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-BV-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        db.ObifinConnections.Add(new ObifinConnection { Id = Guid.NewGuid(), LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"api-{Guid.NewGuid():N}@x", Status = ObifinConnectionStatus.Verified, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow });
        return lic;
    }

    /// <summary>Doğrudan yazılmış talep (sayfanın açamadığı durumlar: sistemin kapattığı, modül kapalıyken açık kalan).</summary>
    private static async Task<BankSubjectRequest> SeedRequestAsync(ApiFactory factory, Action<BankSubjectRequest>? shape = null)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new BankSubjectRequest { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), Kind = BankSubjectRequestKind.Access,
            IbanHash = $"ozet-{Guid.NewGuid():N}", IbanMasked = BankHasher.MaskIban(BankHasherTests.TestIban()), ReceivedAt = now.AddDays(-2), DueAt = now.AddDays(28),
            CreatedAt = now.AddDays(-2), CreatedBy = Guid.NewGuid().ToString() };
        shape?.Invoke(request);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.BankSubjectRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static async Task<FormUrlEncodedContent> FormAsync(HttpClient client, string getPath, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = AdminLoginHelper.ExtractAntiForgeryToken(await client.GetStringAsync(getPath));
        return new FormUrlEncodedContent(fields);
    }

    private static async Task<IDocument> ParseAsync(string html)
        => await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

    /// <summary>Sayfanın bildirim şeridi (TempData, _ToastPartial) — kind: "success" | "danger". GET TempData'yı tüketir.</summary>
    private static async Task<string?> ToastAsync(HttpClient client, string path, string kind)
        => (await ParseAsync(await client.GetStringAsync(path))).QuerySelector($".alert-{kind}.alert-dismissible")?.TextContent;

    private static Guid IdFrom(HttpResponseMessage created)
    {
        var location = created.Headers.Location!.ToString();
        return Guid.Parse(location[(location.IndexOf("id=", StringComparison.Ordinal) + 3)..]);
    }

    private static async Task<(Guid Id, string DetailPath)> CreateAsync(HttpClient client, Guid licenseId, string iban, string kind,
        string? receivedOn = null)
    {
        var fields = new Dictionary<string, string> { ["LicenseId"] = licenseId.ToString(), ["Iban"] = iban, ["Kind"] = kind };
        if (receivedOn is not null) fields["ReceivedOn"] = receivedOn;
        var created = await client.PostAsync($"{PagePath}?handler=Create", await FormAsync(client, PagePath, fields));
        created.StatusCode.Should().Be(HttpStatusCode.Redirect);
        created.Headers.Location!.ToString().Should().Contain("id=", "talep açılınca ayrıntısına dönülür");
        var id = IdFrom(created);
        return (id, $"{PagePath}?id={id}");
    }

    [Fact]
    public async Task Girissiz_login_e_yonlenir()
    {
        var resp = await _factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(PagePath);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Erisim_talebi_acilir_hareketleri_listelenir_dogrulamasiz_disa_aktarilmaz_disa_aktarim_tam_IBAN_icermez_yanitlanip_kapanir()
    {
        var (lic, txId, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var spaced = string.Join(" ", iban.Chunk(4).Select(c => new string(c)));

        var (id, detailPath) = await CreateAsync(client, lic, spaced, "Access", "2026-10-19");

        var html = await client.GetStringAsync(detailPath);
        html.Should().NotContain(iban[4..20], "IBAN sayfaya düz basılmaz");
        var doc = await ParseAsync(html);
        doc.QuerySelector($"tr[data-tx='{txId}']").Should().NotBeNull();
        doc.QuerySelector("form[data-form='export']").Should().NotBeNull("erişim talebinde dışa aktarım var");
        doc.QuerySelector("form[data-form='erase']").Should().BeNull("erişim talebinde silme düğmesi yok");
        doc.QuerySelector("form[data-form='answer']").Should().BeNull("doğrulanmamış talep 'yanıtlandı' kapanamaz");

        var unverified = await client.PostAsync($"{PagePath}?handler=Export&id={id}", await FormAsync(client, detailPath, new()));
        unverified.StatusCode.Should().Be(HttpStatusCode.Redirect, "doğrulama yöntemi kaydedilmeden dışa aktarım yok (KVKK Ö-11)");
        (await ToastAsync(client, detailPath, "danger")).Should().Contain(BankSubjectRequestService.NotVerifiedMessage);

        (await client.PostAsync($"{PagePath}?handler=Verify&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Verification"] = "WetSignatureAndBankDocument" }))).StatusCode
            .Should().Be(HttpStatusCode.Redirect);

        var export = await client.PostAsync($"{PagePath}?handler=Export&id={id}", await FormAsync(client, detailPath, new()));
        export.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var csv = await export.Content.ReadAsStringAsync();
        csv.Should().Contain(BankHasher.MaskIban(iban)).And.Contain("HAVALE talep").And.NotContain(iban[4..20]);

        (await ParseAsync(await client.GetStringAsync(detailPath))).QuerySelector("form[data-form='answer']")
            .Should().NotBeNull("doğrulanmış erişim talebi 'yanıtlandı' kapanır");
        (await client.PostAsync($"{PagePath}?handler=Close&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Resolution"] = "Answered" }))).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var request = await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == id);
        (request.Kind, request.Verification, request.Resolution).Should()
            .Be((BankSubjectRequestKind.Access, BankSubjectVerification.WetSignatureAndBankDocument, BankSubjectResolution.Answered));
        request.ClosedAt.Should().NotBeNull(); request.IbanHash.Should().BeNull();
        TimeZoneInfo.ConvertTime(request.ReceivedAt, ObifinConnectionService.TrZone).ToString("yyyy-MM-dd").Should().Be("2026-10-19");
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == txId)).Description.Should().Be("HAVALE talep",
            "erişim talebi veriye dokunmaz");
        var events = await db.AuditLogs.AsNoTracking().Where(a => a.TargetId == id.ToString()).Select(a => a.EventType).ToListAsync();
        events.Should().Contain(new[] { AuditEvents.BankSubjectCreate, AuditEvents.BankSubjectVerify, AuditEvents.BankSubjectExport,
            AuditEvents.BankSubjectClose, AuditEvents.BankPageView });
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EventType == AuditEvents.BankSubjectVerify && a.TargetId == id.ToString()))
            .Details.Should().Contain("WetSignatureAndBankDocument");
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EventType == AuditEvents.BankSubjectClose && a.TargetId == id.ToString()))
            .Details.Should().Contain("Answered");
        // Denetim ayrıntısına IBAN (ne düz ne kısaltılmış) girmez.
        var (middle, masked) = (iban[4..20], BankHasher.MaskIban(iban));
        (await db.AuditLogs.AsNoTracking().Where(a => a.TargetId == id.ToString()).Select(a => a.Details).ToListAsync())
            .Should().NotContain(d => d != null && (d.Contains(middle) || d.Contains(masked)));
    }

    [Fact]
    public async Task Silme_talebinde_disa_aktarim_yok_sil_ve_kapat_metni_siler_talebi_kapatir_imha_olayi_yazar()
    {
        var (lic, txId, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var (id, detailPath) = await CreateAsync(client, lic, iban, "Erasure");

        var doc = await ParseAsync(await client.GetStringAsync(detailPath));
        doc.QuerySelector("form[data-form='export']").Should().BeNull("silme talebinde dışa aktarım yok (KVKK m. 4/2-ç)");
        doc.QuerySelector("form[data-form='erase']").Should().NotBeNull();
        doc.QuerySelector("form[data-form='answer']").Should().BeNull("'yanıtlandı' yalnız erişim talebinde");

        // Düğme yok, ama doğrudan istek de reddedilir: servis kuralı sayfada bildirime çevrilir.
        (await client.PostAsync($"{PagePath}?handler=Export&id={id}", await FormAsync(client, detailPath, new()))).StatusCode
            .Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, detailPath, "danger")).Should().Contain(BankSubjectRequestService.ExportWrongKindMessage);

        (await client.PostAsync($"{PagePath}?handler=Erase&id={id}", await FormAsync(client, detailPath, new()))).StatusCode
            .Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, detailPath, "danger")).Should().Contain(BankSubjectRequestService.NotVerifiedMessage,
            "doğrulama yöntemi kaydedilmeden silme yok (KVKK Ö-11)");
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankTransactions.AsNoTracking()
                .SingleAsync(t => t.Id == txId)).Description.Should().Be("HAVALE talep");

        await client.PostAsync($"{PagePath}?handler=Verify&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Verification"] = "ElectronicSignatureAndBankDocument" }));
        var erased = await client.PostAsync($"{PagePath}?handler=Erase&id={id}", await FormAsync(client, detailPath, new()));
        erased.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ToastAsync(client, detailPath, "success")).Should().Contain(BankSubjectRequestService.OtherCopiesNotice);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == txId)).Description.Should().BeNull();
        var request = await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == id);
        (request.Resolution, request.IbanHash).Should().Be((BankSubjectResolution.Erased, (string?)null));
        request.ClosedAt.Should().NotBeNull();
        var erase = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EventType == AuditEvents.BankSubjectErase
            && a.TargetType == AuditTargets.BankSubjectRequest && a.TargetId == id.ToString());
        erase.Details.Should().Contain("\"transactions\":1");
        AuditEvents.DestructionEvents.Should().Contain(AuditEvents.BankSubjectErase, "silme 36 ay saklanır");
        AuditEvents.DestructionEvents.Should().NotContain(new[] { AuditEvents.BankSubjectCreate, AuditEvents.BankSubjectVerify,
            AuditEvents.BankSubjectExport, AuditEvents.BankSubjectClose }, "imha olmayan olaylar 24 ayda budanır");

        (await ParseAsync(await client.GetStringAsync(detailPath))).QuerySelectorAll("form[data-form]")
            .Select(f => f.GetAttribute("data-form")).Should().BeEquivalentTo(new[] { "create" }, "kapalı talepte eylem yok");
    }

    [Fact]
    public async Task Gecersiz_IBAN_hata_seridi_verir_talep_acmaz()
    {
        var (lic, _, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        await client.PostAsync($"{PagePath}?handler=Create", await FormAsync(client, PagePath, new Dictionary<string, string>
        {
            ["LicenseId"] = lic.ToString(), ["Iban"] = iban[..10] + "******" + iban[16..], ["Kind"] = "Access",
        }));

        (await ToastAsync(client, PagePath, "danger")).Should().Contain(BankSubjectRequestService.InvalidIbanMessage);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankSubjectRequests.AnyAsync(r => r.LicenseId == lic))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Tur_secilmeden_talep_acilmaz()
    {
        // Tür sonradan değişmez ve hakkı belirler (erişim: dışa aktarım; silme: sil ve kapat). Seçilmeyen tür sessizce "erişim"e
        // düşmesin.
        var (lic, _, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();

        await client.PostAsync($"{PagePath}?handler=Create", await FormAsync(client, PagePath, new Dictionary<string, string>
        {
            ["LicenseId"] = lic.ToString(), ["Iban"] = iban, ["Kind"] = "",
        }));

        (await ToastAsync(client, PagePath, "danger")).Should().Contain(BankaVeriTalepleriModel.NoKindMessage);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankSubjectRequests.AnyAsync(r => r.LicenseId == lic))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Dogrulanamayan_talep_kategoriyle_reddedilir_yanitlandi_secilemez_kayitta_yonetici_kimligi_durur()
    {
        // KVKK m. 13/3, Silme Yönetmeliği md. 12/1-c: gerekçeli ret ayrı sonuç; talep kaydına yanlış sonuç düşmez. Ö-14: satır
        // süresiz, yönetici sütunlarında kullanıcı adı yok.
        var (lic, _, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var (id, detailPath) = await CreateAsync(client, lic, iban, "Access");

        await client.PostAsync($"{PagePath}?handler=Close&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Resolution"] = "Answered" }));
        (await ToastAsync(client, detailPath, "danger")).Should().Contain(BankSubjectRequestService.ResolutionNotAllowedMessage);
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().BankSubjectRequests.AsNoTracking()
                .SingleAsync(r => r.Id == id)).ClosedAt.Should().BeNull("doğrulanmamış erişim talebi 'yanıtlandı' kapanamaz");

        var options = (await ParseAsync(await client.GetStringAsync(detailPath)))
            .QuerySelectorAll("form[data-form='reject'] option").Select(o => o.GetAttribute("value")).ToList();
        options.Should().Equal("", "RejectedNotVerified", "RejectedLegalGround", "Withdrawn");
        (await client.PostAsync($"{PagePath}?handler=Close&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Resolution"] = "RejectedNotVerified" }))).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var request = await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == id);
        (request.Resolution, request.IbanHash).Should().Be((BankSubjectResolution.RejectedNotVerified, (string?)null));
        request.Outcome.Should().Be(BankSubjectRequestService.OutcomeText(BankSubjectResolution.RejectedNotVerified));
        var adminId = (await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id.ToString();
        (request.CreatedBy, request.ClosedBy).Should().Be((adminId, adminId), "yöneticinin kimliği, kullanıcı adı değil (Ö-14)");
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.EventType == AuditEvents.BankSubjectClose && a.TargetId == id.ToString()))
            .Details.Should().Contain("RejectedNotVerified");
    }

    [Fact]
    public async Task Dogrulanmis_talepte_dogrulanamadi_secenegi_yok_dogrulayan_yonetici_kimligi()
    {
        var (lic, _, iban) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var (id, detailPath) = await CreateAsync(client, lic, iban, "Erasure");
        await client.PostAsync($"{PagePath}?handler=Verify&id={id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Verification"] = "RegisteredContactAndBankDocument" }));

        var doc = await ParseAsync(await client.GetStringAsync(detailPath));
        doc.QuerySelectorAll("form[data-form='reject'] option").Select(o => o.GetAttribute("value"))
            .Should().Equal("", "RejectedLegalGround", "Withdrawn");
        doc.QuerySelector("[data-cell='verification']")!.TextContent.Should().Contain(
            BankaVeriTalepleriModel.VerificationLabel(BankSubjectVerification.RegisteredContactAndBankDocument));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var adminId = (await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == "admin")).Id.ToString();
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == id)).VerifiedBy.Should().Be(adminId);
    }

    [Fact]
    public async Task Sayfa_goruntulemesi_denetime_yazilir_hedef_gosterilen_talep()
    {
        // KVKK Ö-4 (Görev 16 kuralı): liste de maske gösterir, her görüntüleme yazılır. Hedef sayfanın gösterdiği talep; ?id= verilip
        // talep bulunamazsa hedef boş. Ayrıntı yalnız sayfa adı ve talep numarası.
        var request = await SeedRequestAsync(_factory);
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var before = DateTimeOffset.UtcNow;

        await client.GetStringAsync(PagePath);
        await client.GetStringAsync($"{PagePath}?id={request.Id}");
        await client.GetStringAsync($"{PagePath}?id={Guid.NewGuid()}");
        await client.GetStringAsync($"{PagePath}?closed=true");

        using var scope = _factory.Services.CreateScope();
        var views = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().AuditLogs.AsNoTracking()
            .Where(a => a.EventType == AuditEvents.BankPageView && a.OccurredAt >= before).OrderBy(a => a.OccurredAt).ToListAsync();
        views.Should().HaveCount(4).And.OnlyContain(a => a.TargetType == AuditTargets.BankSubjectRequest);
        views.Select(a => a.TargetId).Should().Equal(null, request.Id.ToString(), null, null);
        views.Select(a => a.Details).Should().Equal(
            "{\"page\":\"banka-veri-talepleri\"}",
            $"{{\"page\":\"banka-veri-talepleri\",\"requestId\":\"{request.Id}\"}}",
            "{\"page\":\"banka-veri-talepleri\"}",
            "{\"page\":\"banka-veri-talepleri\"}");
    }

    [Fact]
    public async Task Sistemin_kapattigi_talep_son_tarihe_kadar_acik_listede_yanit_bekleniyor_ile_gorunur()
    {
        // KVKK m. 13/2: bağlantı kaldırma, hizmet sonu ya da yayıncı silmesi talebi kapatır (DataDeleted), ama başvurana yanıt
        // yükümlülüğü son tarihe kadar sürer. Eylem yok (talep kapalı), sonuç metni görünür.
        var now = DateTimeOffset.UtcNow;
        void Closed(BankSubjectRequest r, BankSubjectResolution resolution, string outcome)
        {
            r.ClosedAt = now.AddHours(-1); r.ClosedBy = "system"; r.IbanHash = null; r.Resolution = resolution; r.Outcome = outcome;
        }
        var awaiting = await SeedRequestAsync(_factory, r => Closed(r, BankSubjectResolution.DataDeleted,
            BankConnectionTerminator.RemovedRequestOutcome));
        var pastDue = await SeedRequestAsync(_factory, r =>
        {
            Closed(r, BankSubjectResolution.DataDeleted, BankConnectionTerminator.ServiceEndRequestOutcome);
            r.ReceivedAt = now.AddDays(-40); r.DueAt = now.AddDays(-10);
        });
        var withdrawn = await SeedRequestAsync(_factory, r => Closed(r, BankSubjectResolution.Withdrawn,
            BankSubjectRequestService.OutcomeText(BankSubjectResolution.Withdrawn)));
        var open = await SeedRequestAsync(_factory);
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var list = await ParseAsync(await client.GetStringAsync(PagePath));

        var row = list.QuerySelector($"tr[data-request='{awaiting.Id}']");
        row.Should().NotBeNull("son tarihi geçmemiş, sistemin kapattığı talep açık listede");
        row!.QuerySelector("[data-badge='awaiting-reply']")!.TextContent.Should().Contain("yanıt bekleniyor");
        row.TextContent.Should().Contain(BankConnectionTerminator.RemovedRequestOutcome);
        row.QuerySelector("form").Should().BeNull();
        list.QuerySelector($"tr[data-request='{open.Id}'] [data-badge='awaiting-reply']").Should().BeNull();
        list.QuerySelector($"tr[data-request='{open.Id}']").Should().NotBeNull();
        list.QuerySelector($"tr[data-request='{pastDue.Id}']").Should().BeNull("son tarihi geçen, sistemin kapattığı talep");
        list.QuerySelector($"tr[data-request='{withdrawn.Id}']").Should().BeNull("yöneticinin kapattığı talep açık listede değil");

        var closedList = await ParseAsync(await client.GetStringAsync($"{PagePath}?closed=true"));
        foreach (var r in new[] { awaiting, pastDue, withdrawn })
            closedList.QuerySelector($"tr[data-request='{r.Id}']").Should().NotBeNull();
        closedList.QuerySelector($"tr[data-request='{open.Id}']").Should().BeNull();

        var detail = await ParseAsync(await client.GetStringAsync($"{PagePath}?id={awaiting.Id}"));
        detail.QuerySelectorAll("form[data-form]").Select(f => f.GetAttribute("data-form")).Should().Equal("create");
        detail.QuerySelector("[data-cell='outcome']")!.TextContent.Should().Contain(BankConnectionTerminator.RemovedRequestOutcome);
    }

    [Fact]
    public async Task Gecikmis_acik_talep_son_tarihiyle_isaretlenir()
    {
        var now = DateTimeOffset.UtcNow;
        var late = await SeedRequestAsync(_factory, r => { r.ReceivedAt = now.AddDays(-31); r.DueAt = now.AddDays(-1); });
        var client = await _factory.CreateLoggedInAdminClientAsync();

        var cell = (await ParseAsync(await client.GetStringAsync(PagePath)))
            .QuerySelector($"tr[data-request='{late.Id}'] [data-cell='due']")!.TextContent;

        cell.Should().Contain(TimeZoneInfo.ConvertTime(late.DueAt, ObifinConnectionService.TrZone).ToString("dd.MM.yyyy"))
            .And.Contain("gecikti");
    }

    [Fact]
    public async Task Banka_modulu_kapaliyken_sayfa_acilir_yeni_talep_acilmaz_acik_talep_kapatilir()
    {
        // Talep açmak BankHasher ister (IBAN özetlenir); arama, kapatma ve silme istemez: anahtarsız modülde açık talepler
        // yine işlenir, sayfa 500 vermez.
        using var factory = new DisabledBankApiFactory();
        var request = await SeedRequestAsync(factory);
        var client = await factory.CreateLoggedInAdminClientAsync();
        var detailPath = $"{PagePath}?id={request.Id}";

        (await ParseAsync(await client.GetStringAsync(PagePath))).QuerySelector("[data-banner='bank-disabled']").Should().NotBeNull();
        await client.PostAsync($"{PagePath}?handler=Create", await FormAsync(client, PagePath, new Dictionary<string, string>
        {
            ["LicenseId"] = request.LicenseId.ToString(), ["Iban"] = BankHasherTests.TestIban(), ["Kind"] = "Access",
        }));
        (await ToastAsync(client, PagePath, "danger")).Should().Contain(BankHasher.DisabledMessage);

        (await client.PostAsync($"{PagePath}?handler=Close&id={request.Id}", await FormAsync(client, detailPath,
            new Dictionary<string, string> { ["Resolution"] = "Withdrawn" }))).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.BankSubjectRequests.AsNoTracking().CountAsync()).Should().Be(1, "yeni talep açılmadı");
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync()).Resolution.Should().Be(BankSubjectResolution.Withdrawn);
    }
}
