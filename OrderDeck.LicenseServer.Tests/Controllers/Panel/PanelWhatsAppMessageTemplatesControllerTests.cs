using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public sealed class PanelWhatsAppMessageTemplatesControllerTests : IDisposable
{
    private readonly List<TemplateApiFactory> _factories = [];

    /// <summary>Katalog sahtesi: hem döndüreceği listeyi hem gördüğü yazma
    /// çağrılarını tutuyor — sahiplik kontrolünün gerçekten listeye baktığını
    /// ancak böyle kanıtlayabiliyoruz.</summary>
    private sealed class FakeCatalog : IWhatsAppTemplateCatalog
    {
        public GraphResult<IReadOnlyList<WabaTemplate>> All =
            GraphResult<IReadOnlyList<WabaTemplate>>.Success([]);

        public GraphResult<WhatsAppTemplateCreated> CreateResult =
            GraphResult<WhatsAppTemplateCreated>.Success(new WhatsAppTemplateCreated("NEW", "PENDING"));

        public GraphResult<bool> WriteResult = GraphResult<bool>.Success(true);

        public string? SeenWabaId;
        public string? SeenToken;
        public (string Name, string Category, string Language, WhatsAppTemplateDraft Draft)? Created;
        public (string TemplateId, WhatsAppTemplateDraft Draft)? Updated;
        public (string TemplateId, string Name)? Deleted;

        public Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
            string wabaId, string businessToken, CancellationToken ct)
        {
            SeenWabaId = wabaId;
            SeenToken = businessToken;
            return Task.FromResult(All);
        }

        public Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListApprovedAsync(
            string wabaId, string businessToken, CancellationToken ct) =>
            ListAllAsync(wabaId, businessToken, ct);

        public Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
            string wabaId, string businessToken, string name, string category, string language,
            WhatsAppTemplateDraft draft, CancellationToken ct)
        {
            Created = (name, category, language, draft);
            return Task.FromResult(CreateResult);
        }

        public Task<GraphResult<bool>> UpdateAsync(
            string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct)
        {
            Updated = (templateId, draft);
            return Task.FromResult(WriteResult);
        }

        public Task<GraphResult<bool>> DeleteAsync(
            string wabaId, string businessToken, string templateId, string name, CancellationToken ct)
        {
            Deleted = (templateId, name);
            return Task.FromResult(WriteResult);
        }
    }

    private sealed class TemplateApiFactory : ApiFactory
    {
        public FakeCatalog Catalog { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IWhatsAppTemplateCatalog>(Catalog));
        }
    }

    private sealed record Seed(HttpClient Client, Guid LicenseId, TemplateApiFactory Factory)
    {
        public FakeCatalog Catalog => Factory.Catalog;
    }

    private async Task<Seed> SeedAsync()
    {
        var factory = new TemplateApiFactory();
        _factories.Add(factory);

        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-MTPL-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        return new Seed(client, license.Id, factory);
    }

    /// <summary>Lisansa bir WhatsApp hesabı bağlar. Token şifreli saklandığı için
    /// satırı elle yazmak yetmiyor; koruyucu servisten geçmesi gerekiyor.</summary>
    private static async Task ConnectWhatsAppAsync(Seed s, string wabaId = "WABA_1")
    {
        using var scope = s.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<WhatsAppAccountService>();

        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = s.LicenseId,
            WabaId = wabaId,
            PhoneNumberId = "PNID_" + Guid.NewGuid().ToString("N")[..8],
            DisplayPhoneNumber = "+90 555 111 22 33",
            AccessTokenProtected = accounts.ProtectToken("BIZ_TOKEN"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static WabaTemplate Template(
        string id = "T1", string name = "kargo", string status = "APPROVED",
        string? rejected = null) =>
        new(id, name, "tr", "UTILITY", status, null, "Kargonuz yolda.", null,
            [], 0, [], null, rejected);

    private sealed record ButtonDto(string Type, string Text, string? Url, string? PhoneNumber);

    private sealed record TemplateDto(
        string Id, string Name, string Language, string Category, string Status,
        string? RejectedReason, string? HeaderText, string BodyText, string? FooterText,
        List<ButtonDto> Buttons, List<string> BodyExamples, string? UnsupportedReason);

    [Fact]
    public async Task Liste_onay_bekleyeni_ve_reddedileni_de_donduruyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, "WABA_42");
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            Template("T1", "kargo"),
            Template("T2", "kampanya", "REJECTED", "INVALID_FORMAT"),
        ]);

        var resp = await s.Client.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<TemplateDto>>();
        Assert.Equal(2, list!.Count);
        Assert.Equal("REJECTED", list[1].Status);
        Assert.Equal("INVALID_FORMAT", list[1].RejectedReason);
        Assert.Equal("WABA_42", s.Catalog.SeenWabaId);
        Assert.Equal("BIZ_TOKEN", s.Catalog.SeenToken);
    }

    [Fact]
    public async Task Whatsapp_bagli_degilse_503()
    {
        var s = await SeedAsync();

        var resp = await s.Client.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Meta_hatasi_502_olarak_doner()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Failure("190", "Session has expired.");

        var resp = await s.Client.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    private sealed record ButtonReq(string Type, string Text, string? Url, string? PhoneNumber);

    private sealed record DraftReq(
        string? HeaderText, string BodyText, string? FooterText,
        List<string>? BodyExamples, List<ButtonReq>? Buttons);

    private sealed record CreateReq(string Name, string Category, DraftReq Draft);

    private static CreateReq NewTemplate(
        string name = "siparis_hazir", string category = "UTILITY",
        string body = "Merhaba {{1}}, siparişiniz hazır.", List<string>? examples = null) =>
        new(name, category, new DraftReq(null, body, null, examples ?? ["Ayşe"], null));

    private static Task<HttpResponseMessage> CreateAsync(Seed s, CreateReq req) =>
        s.Client.PostAsJsonAsync("/api/panel/whatsapp-message-templates", req);

    [Fact]
    public async Task Olusturma_metaya_ad_kategori_ve_dili_geciriyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);

        var resp = await CreateAsync(s, NewTemplate());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var created = s.Catalog.Created!.Value;
        Assert.Equal("siparis_hazir", created.Name);
        Assert.Equal("UTILITY", created.Category);
        Assert.Equal("tr", created.Language);
        Assert.Equal(["Ayşe"], created.Draft.BodyExamples);
    }

    // Doğrulama Graph'a çıkmadan yerelde: Meta'nın 132000 hatası okunmaz ve
    // reddedilen şablon WABA'nın kalite notunu düşürüyor.
    [Theory]
    [InlineData("Sipariş Hazır", "UTILITY")]      // geçersiz ad
    [InlineData("siparis_hazir", "AUTHENTICATION")] // geçersiz kategori
    public async Task Gecersiz_ad_veya_kategori_400_ve_metaya_gitmiyor(string name, string category)
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);

        var resp = await CreateAsync(s, NewTemplate(name, category));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(s.Catalog.Created);
    }

    [Fact]
    public async Task Eksik_ornek_degeri_400()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);

        var resp = await CreateAsync(s, NewTemplate(examples: []));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(s.Catalog.Created);
    }

    [Fact]
    public async Task Ayni_ad_hatasi_metadan_502_olarak_geciyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.CreateResult = GraphResult<WhatsAppTemplateCreated>.Failure(
            "100", "Template name already exists");

        var resp = await CreateAsync(s, NewTemplate());

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Contains("already exists", await resp.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> UpdateAsync(Seed s, string id, DraftReq draft) =>
        s.Client.PostAsJsonAsync($"/api/panel/whatsapp-message-templates/{id}", draft);

    private static DraftReq EditedDraft() =>
        new(null, "Kargonuz bugün çıktı.", null, [], null);

    [Fact]
    public async Task Duzenleme_yalniz_bilesenleri_metaya_geciriyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await UpdateAsync(s, "T1", EditedDraft());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("T1", s.Catalog.Updated!.Value.TemplateId);
        Assert.Equal("Kargonuz bugün çıktı.", s.Catalog.Updated!.Value.Draft.BodyText);
    }

    // Meta'nın düzenleme ucu WABA kapsamlı değil: kimliği doğrudan geçirseydik
    // yayıncı, kimliğini bildiği BAŞKA bir yayıncının şablonunu düzenlerdi.
    [Fact]
    public async Task Baska_wabanin_sablonu_duzenlenemiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await UpdateAsync(s, "BASKASININ", EditedDraft());

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(s.Catalog.Updated);
    }

    // Meta onay bekleyen şablonu düzenlemeye hiç izin vermiyor; isteği yollamak
    // yayıncıya anlaşılmaz bir Graph hatası gösterirdi.
    [Fact]
    public async Task Onay_bekleyen_sablon_duzenlenemiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            Template("T1", "kargo", "PENDING"),
        ]);

        var resp = await UpdateAsync(s, "T1", EditedDraft());

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Null(s.Catalog.Updated);
    }

    [Fact]
    public async Task Duzenlemede_de_dogrulama_calisiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await UpdateAsync(s, "T1", new DraftReq(null, "   ", null, [], null));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(s.Catalog.Updated);
    }

    // Silme ucu adı da istiyor; adı istekten alsaydık yayıncı bir şablonun
    // kimliğiyle başka bir şablonun adını eşleştirip yanlış satırı sildirebilirdi.
    [Fact]
    public async Task Silmede_ad_istekten_degil_listeden_aliniyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            Template("T1", "kargo_bildirimi"),
        ]);

        var resp = await s.Client.DeleteAsync("/api/panel/whatsapp-message-templates/T1");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(("T1", "kargo_bildirimi"), s.Catalog.Deleted);
    }

    [Fact]
    public async Task Baska_wabanin_sablonu_silinemiyor()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.All = GraphResult<IReadOnlyList<WabaTemplate>>.Success([Template("T1", "kargo")]);

        var resp = await s.Client.DeleteAsync("/api/panel/whatsapp-message-templates/BASKASININ");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Null(s.Catalog.Deleted);
    }

    // ─── Yetki testleri ────────────────────────────────────────────────────────

    [Fact]
    public async Task Kimliksiz_istek_401()
    {
        var factory = new TemplateApiFactory();
        _factories.Add(factory);

        var resp = await factory.CreateClient().GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // Şablon oluşturmak marka adına mesaj yazmak demek ve reddedilen şablon
    // WABA'nın kalite notunu düşürüyor — stok elemanı bu bölümde işi yok.
    [Fact]
    public async Task Stok_elemani_403()
    {
        // Sahip lisanslı olmalı: lisanssız müşteride davet ucu "no-license" ile
        // 400 döner, operatör hiç oluşmaz ve 403 iddiası sınanmadan geçerdi.
        var s = await SeedAsync();

        // Stok operatörü davet et
        var email    = $"op-{Guid.NewGuid():N}@example.com";
        var password = $"pwd-{Guid.NewGuid():N}";
        var invite   = await s.Client.PostAsJsonAsync("/api/panel/operators",
            new { email, name = "Depo", password, role = "stock" });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);

        // Stok operatörü olarak giriş yap
        var anon  = s.Factory.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/operator-login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var body  = await login.Content.ReadFromJsonAsync<OperatorLoginResp>();
        anon.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body!.Token);

        // Stok elemanı şablon listesine erişememeli
        var resp = await anon.GetAsync("/api/panel/whatsapp-message-templates");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        // Meta'ya hiç çıkılmadığını doğrula
        Assert.Null(s.Catalog.SeenWabaId);
        Assert.Null(s.Catalog.SeenToken);
    }

    private sealed record OperatorLoginResp(
        string Token, DateTimeOffset ExpiresAt, Guid OperatorId, Guid TenantCustomerId,
        string Email, string Name, string Role);

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }
}
