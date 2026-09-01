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

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }
}
