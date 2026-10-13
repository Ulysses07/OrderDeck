using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Instagram;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using OrderDeck.LicenseServer.Tests.Controllers;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// Bağlı kimlik çipindeki "Bağlantıyı kaldır" butonu ile örtük gönderim.
///
/// Metin kutusunda Enter'a (mobilde klavyenin "Git"ine) basmak formu, formun
/// VARSAYILAN butonuna tıklanmış gibi gönderir; varsayılan buton, sahibi o form
/// olan ilk gönderim butonudur (ağaç sırasıyla — HTML spec, "implicit
/// submission"). Çipler formun en üstünde, yani unlink butonu formun içindeyse
/// Enter kaydı göndermek yerine bağlantıyı kaldırıp sayfayı boşaltıyordu.
/// </summary>
public sealed class IntakeFormUnlinkButtonTests : IClassFixture<IntakeLinkFactory>
{
    private readonly IntakeLinkFactory _factory;
    public IntakeFormUnlinkButtonTests(IntakeLinkFactory factory) => _factory = factory;

    [Theory]
    [InlineData("instagram")]
    [InlineData("youtube")]
    [InlineData("facebook")]
    public async Task Enter_ile_ortuk_gonderim_kaydi_gonderir_baglantiyi_kaldirmaz(string platform)
    {
        var slug = await SeedSlugAsync();
        var (_, html) = await LinkAsync(platform, slug);
        var doc = await ParseAsync(html);

        // Test boşa dönmesin: çip ve butonu gerçekten sayfada.
        UnlinkButton(doc, platform).Should().NotBeNull("bağlı kimliğin çipi çizilmeli");

        var form = (IHtmlFormElement)doc.GetElementById("intakeForm")!;
        var defaultButton = DefaultButton(doc, form);

        defaultButton.Should().NotBeNull("formun bir varsayılan butonu olmalı");
        defaultButton!.HasAttribute("formaction").Should().BeFalse(
            "Enter başka bir handler'a gitmemeli — gönderim formun kendi action'ına");
        defaultButton.TextContent.Should().Contain("Tamamla");
    }

    /// <summary>Butonu ayrı forma taşımak kaldırmayı bozmasın: tarayıcının
    /// yapacağı gibi BUTONUN sahibi olan formun alanları butonun formaction'ına
    /// gönderilir — anti-forgery token'ı o formda yoksa istek 400 alır.</summary>
    [Theory]
    [InlineData("instagram")]
    [InlineData("youtube")]
    [InlineData("facebook")]
    public async Task Baglantiyi_kaldir_butonu_sahibi_olan_formla_kimligi_siler(string platform)
    {
        var slug = await SeedSlugAsync();
        var (client, html) = await LinkAsync(platform, slug);
        var doc = await ParseAsync(html);

        var button = UnlinkButton(doc, platform)!;
        var owner = FormOwner(doc, button);
        owner.Should().NotBeNull();

        // İşaretsiz kutular tarayıcıda gönderilmez; burada önemleri de yok.
        var fields = doc.QuerySelectorAll("input[name]").OfType<IHtmlInputElement>()
            .Where(i => i.Type != "checkbox" && FormOwner(doc, i) == owner)
            .Select(i => new KeyValuePair<string, string>(i.Name!, i.Value));
        var resp = await client.PostAsync(button.GetAttribute("formaction"),
            new FormUrlEncodedContent(fields));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var after = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        after.Should().NotContain("linked-chip");
    }

    // ─── Tarayıcı kuralları (HTML spec) ────────────────────────────────────

    /// <summary>Sahibi bu form olan ilk gönderim butonu, ağaç sırasıyla.</summary>
    private static IElement? DefaultButton(IDocument doc, IHtmlFormElement form)
        => doc.QuerySelectorAll("button, input[type=submit], input[type=image]")
            .Where(IsSubmitButton)
            .FirstOrDefault(b => FormOwner(doc, b) == form);

    /// <summary>type'sız ya da type="submit" button; input[type=submit|image].</summary>
    private static bool IsSubmitButton(IElement e)
        => e is IHtmlInputElement
           || string.Equals(e.GetAttribute("type") ?? "submit", "submit", StringComparison.OrdinalIgnoreCase);

    /// <summary>form="id" özniteliği varsa o id'li form; yoksa en yakın üst form.</summary>
    private static IHtmlFormElement? FormOwner(IDocument doc, IElement e)
    {
        var id = e.GetAttribute("form");
        if (id is not null) return doc.GetElementById(id) as IHtmlFormElement;
        return e.Closest("form") as IHtmlFormElement;
    }

    private static IElement? UnlinkButton(IDocument doc, string platform)
        => doc.QuerySelectorAll("button.unlink")
            .FirstOrDefault(b => (b.GetAttribute("formaction") ?? "").Contains($"platform={platform}"));

    // ─── Kurulum ────────────────────────────────────────────────────────────

    /// <summary>Kimliği bağlar ve formun o istemcide görünen hâlini döner.
    /// Instagram yolu "!kayıt → DM" token'ı; diğerleri sahte OAuth dönüşü.</summary>
    private async Task<(HttpClient Client, string Html)> LinkAsync(string platform, string slug)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        if (platform == "instagram")
        {
            var token = _factory.Services.GetRequiredService<IntakeIgTokenService>()
                .Create(slug, $"ig_{Guid.NewGuid():N}"[..16]);
            var resp = await client.GetAsync($"/musteri-kayit/{slug}?ig={Uri.EscapeDataString(token)}");
            return (client, await resp.Content.ReadAsStringAsync());
        }

        var result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bağlı Hesap", "@baglihesap", platform == "youtube" ? "UCunlink0001" : null));
        if (platform == "youtube") _factory.Google.Result = result;
        else _factory.Facebook.Result = result;

        var start = await client.GetAsync($"/musteri-kayit/{slug}/baglan/{platform}");
        var state = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"];
        await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c");
        return (client, await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync());
    }

    private async Task<string> SeedSlugAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"ul-{Guid.NewGuid():N}@x",
            Name = "Ul",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = $"LDK-UL-{Guid.NewGuid():N}",
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"ul-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = TestPhone.NewE164(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return slug;
    }

    private static async Task<IDocument> ParseAsync(string html)
        => await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));
}
