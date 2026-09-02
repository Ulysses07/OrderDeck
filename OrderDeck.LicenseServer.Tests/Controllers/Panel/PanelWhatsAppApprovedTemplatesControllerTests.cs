using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Panelin onaylı şablon listesi. Graph sahtelenmiş — sınanan şey Meta'nın
/// cevabı değil, ucun o cevaba ne YAPTIĞI: hangi lisansın WABA'sına sorduğu,
/// hesabı olmayanı nasıl ayırdığı ve Meta düşünce ne döndüğü.
/// </summary>
public sealed class PanelWhatsAppApprovedTemplatesControllerTests : IDisposable
{
    private readonly List<TemplateApiFactory> _factories = [];

    private sealed class FakeTemplateCatalog : IWhatsAppTemplateCatalog
    {
        public GraphResult<IReadOnlyList<WabaTemplate>> Result =
            GraphResult<IReadOnlyList<WabaTemplate>>.Success([]);

        public string? SeenWabaId;
        public string? SeenToken;

        public Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListApprovedAsync(
            string wabaId, string businessToken, CancellationToken ct)
        {
            SeenWabaId = wabaId;
            SeenToken = businessToken;
            return Task.FromResult(Result);
        }

        public Task<GraphResult<IReadOnlyList<WabaTemplate>>> ListAllAsync(
            string wabaId, string businessToken, CancellationToken ct)
        {
            SeenWabaId = wabaId;
            SeenToken = businessToken;
            return Task.FromResult(Result);
        }

        public Task<GraphResult<WhatsAppTemplateCreated>> CreateAsync(
            string wabaId, string businessToken, string name, string category, string language,
            WhatsAppTemplateDraft draft, CancellationToken ct) =>
            throw new NotSupportedException("Bu test yalnız listeyi kullanıyor.");

        public Task<GraphResult<bool>> UpdateAsync(
            string templateId, string businessToken, WhatsAppTemplateDraft draft, CancellationToken ct) =>
            throw new NotSupportedException("Bu test yalnız listeyi kullanıyor.");

        public Task<GraphResult<bool>> DeleteAsync(
            string wabaId, string businessToken, string templateId, string name, CancellationToken ct) =>
            throw new NotSupportedException("Bu test yalnız listeyi kullanıyor.");
    }

    private sealed class TemplateApiFactory : ApiFactory
    {
        public FakeTemplateCatalog Catalog { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IWhatsAppTemplateCatalog>(Catalog));
        }
    }

    private sealed record Seed(HttpClient Client, Guid LicenseId, TemplateApiFactory Factory)
    {
        public FakeTemplateCatalog Catalog => Factory.Catalog;
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
            LicenseKey = "LDK-TPL-" + Guid.NewGuid().ToString("N")[..12],
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

    private sealed record TemplateDto(
        string Name,
        string Language,
        string Category,
        string? HeaderText,
        string BodyText,
        string? FooterText,
        List<string> Buttons,
        int ParameterCount,
        List<string> ParameterExamples,
        string? UnsupportedReason);

    private static Task<HttpResponseMessage> ListAsync(Seed s) =>
        s.Client.GetAsync("/api/panel/whatsapp-approved-templates");

    [Fact]
    public async Task The_broadcasters_own_waba_is_the_one_asked()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s, "WABA_42");
        s.Catalog.Result = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            new WabaTemplate(
                "1001", "odeme_hatirlatma", "tr", "UTILITY", "APPROVED", "Sipariş bilgisi",
                "Merhaba {{1}}, {{2}} TL", "OrderDeck", [new WhatsAppTemplateButton("QUICK_REPLY", "Tamam", null, null)], 2, ["Ayşe", "250"], null, null),
        ]);

        var resp = await ListAsync(s);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = (await resp.Content.ReadFromJsonAsync<List<TemplateDto>>())!;
        var t = list.Single();
        t.Name.Should().Be("odeme_hatirlatma");
        t.BodyText.Should().Be("Merhaba {{1}}, {{2}} TL");
        t.ParameterCount.Should().Be(2);
        t.ParameterExamples.Should().Equal("Ayşe", "250");
        t.Buttons.Should().Equal("Tamam");
        t.UnsupportedReason.Should().BeNull();

        // Kimlikler istekten değil lisansın kendi hesabından geliyor.
        s.Catalog.SeenWabaId.Should().Be("WABA_42");
        s.Catalog.SeenToken.Should().Be("BIZ_TOKEN");
    }

    [Fact]
    public async Task An_unsendable_template_arrives_with_its_reason()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.Result = GraphResult<IReadOnlyList<WabaTemplate>>.Success([
            new WabaTemplate(
                "1002", "kargo", "tr", "UTILITY", "APPROVED", null, "Kargonuz yolda.", null,
                [], 0, [], WhatsAppTemplateShape.HeaderMedia, null),
        ]);

        var list = (await (await ListAsync(s)).Content.ReadFromJsonAsync<List<TemplateDto>>())!;

        // Gizlenmiyor: sebebi gören yayıncı şablonu Meta'da düzeltebilir.
        list.Single().UnsupportedReason.Should().Be(WhatsAppTemplateShape.HeaderMedia);
    }

    [Fact]
    public async Task Without_a_connected_account_meta_is_never_asked()
    {
        var s = await SeedAsync();

        var resp = await ListAsync(s);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("no-whatsapp-account");
        s.Catalog.SeenWabaId.Should().BeNull();
    }

    [Fact]
    public async Task A_meta_failure_is_not_reported_as_an_empty_list()
    {
        // Boş liste dönmek "onaylı şablonunuz yok" demek olurdu; yayıncı o zaman
        // olmayan bir sorunu Meta'da aramaya başlar.
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);
        s.Catalog.Result = GraphResult<IReadOnlyList<WabaTemplate>>.Failure(
            "190", "Session has expired.");

        var resp = await ListAsync(s);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("whatsapp-templates-read-failed");
        body.Should().Contain("190");
    }

    [Fact]
    public async Task An_anonymous_caller_gets_nothing()
    {
        var s = await SeedAsync();
        await ConnectWhatsAppAsync(s);

        var anon = s.Factory.CreateClient();
        var resp = await anon.GetAsync("/api/panel/whatsapp-approved-templates");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        s.Catalog.SeenToken.Should().BeNull();
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }
}
