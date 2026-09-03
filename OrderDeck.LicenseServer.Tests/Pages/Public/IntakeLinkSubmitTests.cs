using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using OrderDeck.LicenseServer.Tests.Controllers;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// Faz 2'nin kayıt tarafı: OAuth ile bağlanmış kimlik gönderime nasıl yansır.
/// Kural: bağlı kimlik elle girdiyi YENER ve API'ye tekrar sorulmaz.
/// </summary>
public sealed class IntakeLinkSubmitTests : IClassFixture<IntakeLinkFactory>
{
    private readonly IntakeLinkFactory _factory;
    public IntakeLinkSubmitTests(IntakeLinkFactory factory) => _factory = factory;

    private async Task<(string Slug, Guid CustomerId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"lnk-{Guid.NewGuid():N}@x",
            Name = "Lnk",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-LNK-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = "+905551234567",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (slug, customer.Id);
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static string StateFrom(HttpResponseMessage startResp)
    {
        var query = System.Web.HttpUtility.ParseQueryString(startResp.Headers.Location!.Query);
        return query["state"]!;
    }

    /// <summary>Start → callback koşturur; client'ın çerezinde kimlik kalır.</summary>
    private async Task<HttpClient> LinkAsync(string platform, string slug, IntakeLoginResult result)
    {
        if (platform == "youtube") _factory.Google.Result = result;
        else _factory.Facebook.Result = result;
        var client = NewClient();
        var startResp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/{platform}");
        await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={StateFrom(startResp)}&code=c");
        return client;
    }

    private static async Task<string> TokenAsync(HttpClient client, string slug)
        => AdminLoginHelper.ExtractAntiForgeryToken(
            await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync());

    private static FormUrlEncodedContent Form(string token, string slug, params (string Key, string Value)[] extra)
    {
        var d = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Slug"] = slug,
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        };
        foreach (var (k, v) in extra) d[k] = v;
        return new FormUrlEncodedContent(d);
    }

    private async Task<IntakeFormSubmission?> LatestAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .OrderByDescending(s => s.SubmittedAt)
            .FirstOrDefaultAsync();
    }

    [Fact]
    public async Task Bagli_youtube_tek_basina_yeter_channelId_ve_handle_yazilir()
    {
        var (slug, customerId) = await SeedAsync();
        var client = await LinkAsync("youtube", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCsubmit0001")));
        var callsBefore = _factory.Resolver.Calls.Count;

        // Hiçbir kullanıcı adı alanı gönderilmiyor — bağlı kimlik platform
        // şartını TEK BAŞINA sağlamalı.
        var resp = await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sub = await LatestAsync(customerId);
        sub!.YouTubeChannelId.Should().Be("UCsubmit0001");
        sub.YouTubeUsername.Should().Be("bilalkanal");
        // OAuth kimliği kanıtlı — resolver'a HİÇ gidilmemeli (kota + tutarlılık).
        _factory.Resolver.Calls.Count.Should().Be(callsBefore);
    }

    [Fact]
    public async Task Bagliyken_elle_yazilan_youtube_yok_sayilir()
    {
        var (slug, customerId) = await SeedAsync();
        var client = await LinkAsync("youtube", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCsubmit0002")));
        var callsBefore = _factory.Resolver.Calls.Count;

        // JS'siz istek / eski sekme elle değer gönderebilir. Bağlı kimlik yener:
        // çözülmez, doğrulanmaz, kayda girmez.
        var resp = await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug,
                ("Input.YouTubeUsername", "baskasininkanali")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sub = await LatestAsync(customerId);
        sub!.YouTubeChannelId.Should().Be("UCsubmit0002");
        sub.YouTubeUsername.Should().Be("bilalkanal");
        _factory.Resolver.Calls.Count.Should().Be(callsBefore);
    }

    [Fact]
    public async Task Bagli_facebook_gorunen_adi_bosluklu_kaydeder()
    {
        var (slug, customerId) = await SeedAsync();
        var client = await LinkAsync("facebook", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Musa Sevinç", null, null)));

        var resp = await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        // Görünen ad HandleValidator'dan GEÇMEZ: boşluk/Türkçe karakter serbest.
        // Chat satırı da görünen adla düşüyor — eşleşme bunun üzerinden.
        (await LatestAsync(customerId))!.FacebookUsername.Should().Be("Musa Sevinç");
    }

    [Fact]
    public async Task Kayit_sonrasi_kimlikler_temizlenir()
    {
        var (slug, _) = await SeedAsync();
        var client = await LinkAsync("youtube", slug, new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bilal Kanal", "@bilalkanal", "UCsubmit0003")));

        (await client.PostAsync($"/musteri-kayit/{slug}?handler=Submit",
            Form(await TokenAsync(client, slug), slug)))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Aynı tarayıcıdan ikinci kayıt (örn. aile üyesi) öncekinin kimliğiyle
        // AÇILMAMALI — kimlik tek gönderimlik.
        var html = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        html.Should().NotContain("linked-chip");
    }
}
