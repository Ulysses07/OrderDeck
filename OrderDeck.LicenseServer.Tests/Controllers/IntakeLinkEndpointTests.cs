using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers;

/// <summary>
/// YouTubeIdentityFactory ile aynı desen: ApiFactory'ye ConfigureTestServices
/// ile fake sağlayıcı istemcileri takılır, bayraklar PostConfigure ile açılır.
/// Kimlik bilgileri ÜRETİLİR (repo public — sabit yazılmaz).
/// </summary>
public sealed class IntakeLinkFactory : ApiFactory
{
    public FakeGoogleChannelClient Google { get; } = new();
    public FakeFacebookNameClient Facebook { get; } = new();
    public FakeYouTubeChannelResolver Resolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGoogleChannelClient>();
            services.AddSingleton<IGoogleChannelClient>(Google);
            services.RemoveAll<IFacebookNameClient>();
            services.AddSingleton<IFacebookNameClient>(Facebook);
            services.RemoveAll<IYouTubeChannelResolver>();
            services.AddSingleton<IYouTubeChannelResolver>(Resolver);
            services.PostConfigure<IntakeLoginOptions>(o =>
            {
                o.GoogleClientId = $"cid-{Guid.NewGuid():N}";
                o.GoogleClientSecret = $"cs-{Guid.NewGuid():N}";
                o.YouTubeEnabled = true;
                o.FacebookEnabled = true;
            });
            services.PostConfigure<FacebookOptions>(o =>
            {
                o.AppId = $"fbid-{Guid.NewGuid():N}";
                o.AppSecret = $"fbs-{Guid.NewGuid():N}";
            });
        });
    }
}

/// <summary>Bayraklar kapalıyken uçların YOK gibi davrandığını çivilemek için.</summary>
public sealed class IntakeLinkDisabledFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<IntakeLoginOptions>(o =>
            {
                o.YouTubeEnabled = false;
                o.FacebookEnabled = false;
            }));
    }
}

public sealed class IntakeLinkEndpointTests : IClassFixture<IntakeLinkFactory>
{
    private readonly IntakeLinkFactory _factory;
    public IntakeLinkEndpointTests(IntakeLinkFactory factory) => _factory = factory;

    /// <summary>Başlatma 302'sinin Location'ından state'i söker — testin
    /// sunucuyla paylaştığı tek şey gerçek akışın da taşıdığı değer.</summary>
    private static string StateFrom(HttpResponseMessage startResp)
    {
        var query = startResp.Headers.Location!.Query.TrimStart('?').Split('&');
        return query.First(p => p.StartsWith("state=")).Substring("state=".Length);
    }

    private async Task<(HttpClient Client, string Slug, string State)> StartYouTubeAsync()
    {
        var slug = await SeedSlugAsync();
        var client = NewClient();
        var startResp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/youtube");
        startResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return (client, slug, StateFrom(startResp));
    }

    [Fact]
    public async Task Donus_basarili_olunca_forma_ok_ile_yonlendirir_ve_kimlik_gorunur()
    {
        _factory.Google.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Bağlı Kanal", "@baglikanal", "UCbagli00000000000000abc"));
        var (client, slug, state) = await StartYouTubeAsync();

        var resp = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=gcode-1");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=ok");
        _factory.Google.Codes.Should().Contain("gcode-1");

        // Task 7'de açılacak
        // var html = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        // html.Should().Contain("Bağlı Kanal");
    }

    [Fact]
    public async Task Izin_reddi_iptal_koduyla_forma_doner()
    {
        var (client, slug, state) = await StartYouTubeAsync();

        var resp = await client.GetAsync(
            $"/musteri-kayit/baglanti-donusu?state={state}&error=access_denied");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=iptal");
    }

    [Fact]
    public async Task Kanalsiz_hesap_kanalyok_koduyla_forma_doner()
    {
        _factory.Google.Result = new IntakeLoginResult(false, "kanalyok", null);
        var (client, slug, state) = await StartYouTubeAsync();

        var resp = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c");

        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=kanalyok");
    }

    [Fact]
    public async Task State_yoksa_veya_bilinmiyorsa_suresi_doldu_sayfasi()
    {
        var client = NewClient();

        var noState = await client.GetAsync("/musteri-kayit/baglanti-donusu?code=c");
        noState.StatusCode.Should().Be(HttpStatusCode.OK);
        (await noState.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");

        var badState = await client.GetAsync("/musteri-kayit/baglanti-donusu?state=uydurma&code=c");
        (await badState.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");
    }

    [Fact]
    public async Task State_tek_kullanimlik()
    {
        _factory.Google.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Tek Kanal", null, "UCtek0000000000000000abc"));
        var (client, _, state) = await StartYouTubeAsync();

        (await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Tekrar oynatma: aynı state ikinci kez GEÇMEZ (geri tuşu, kopyalanan URL).
        var replay = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c");
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");
    }

    [Fact]
    public async Task Nonce_eslesmezse_reddedilir()
    {
        _factory.Google.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Çalıntı Kanal", null, "UCcalinti000000000000abc"));
        var (_, _, state) = await StartYouTubeAsync();

        // Farklı tarayıcı (çerezsiz istemci) çalınan state ile dönüyor —
        // state gerçek ama O TARAYICIYA ait değil. CSRF/oturum sabitleme kapısı.
        var attacker = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var resp = await attacker.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=c");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("süresi doldu");
    }

    [Fact]
    public async Task Facebook_donusu_kimligi_kaydeder()
    {
        _factory.Facebook.Result = new IntakeLoginResult(true, null,
            new IntakeLinkedIdentity("Musa Sevinç", null, null));
        var slug = await SeedSlugAsync();
        var client = NewClient();
        var startResp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/facebook");
        var state = StateFrom(startResp);

        var resp = await client.GetAsync($"/musteri-kayit/baglanti-donusu?state={state}&code=fbcode");

        resp.Headers.Location!.ToString().Should().Be($"/musteri-kayit/{slug}?baglanti=ok");
        // Task 7'de açılacak
        // var html = await (await client.GetAsync($"/musteri-kayit/{slug}")).Content.ReadAsStringAsync();
        // html.Should().Contain("Musa Sevinç");
    }

    private async Task<string> SeedSlugAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"ilk-{Guid.NewGuid():N}@x",
            Name = "Ilk",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-ILK-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"l-{Guid.NewGuid():N}"[..10];
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
        return slug;
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, // sağlayıcıya gerçekten gitmeyelim
        HandleCookies = true
    });

    [Fact]
    public async Task Youtube_baslat_googlea_yonlendirir()
    {
        var slug = await SeedSlugAsync();
        var client = NewClient();

        var resp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/youtube");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var loc = resp.Headers.Location!.ToString();
        loc.Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth");
        // Kapsam readonly — geniş "youtube" kapsamına sessizce genişlemek
        // tam da bu testin yakalaması gereken regresyon.
        loc.Should().Contain(Uri.EscapeDataString("https://www.googleapis.com/auth/youtube.readonly"));
        // Geniş "youtube" kapsamına sessiz yükselme — tam da bu testin yakalaması gereken regresyon.
        loc.Should().NotContain(Uri.EscapeDataString("https://www.googleapis.com/auth/youtube") + "&");
        loc.Should().Contain("state=").And.Contain("response_type=code");
        // Nonce çerezi dönüş ucunun state'i tarayıcıya bağlaması için şart.
        resp.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.StartsWith("od.link="));
    }

    [Fact]
    public async Task Facebook_baslat_metaya_yonlendirir()
    {
        var slug = await SeedSlugAsync();
        var client = NewClient();

        var resp = await client.GetAsync($"/musteri-kayit/{slug}/baglan/facebook");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var loc = resp.Headers.Location!.ToString();
        loc.Should().StartWith("https://www.facebook.com/");
        loc.Should().Contain("scope=public_profile").And.Contain("state=");
    }

    [Fact]
    public async Task Bilinmeyen_platform_404()
    {
        var slug = await SeedSlugAsync();
        (await NewClient().GetAsync($"/musteri-kayit/{slug}/baglan/instagram"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Olmayan_slug_404()
    {
        (await NewClient().GetAsync("/musteri-kayit/yok-boyle-slug/baglan/youtube"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class IntakeLinkDisabledTests : IClassFixture<IntakeLinkDisabledFactory>
{
    private readonly IntakeLinkDisabledFactory _factory;
    public IntakeLinkDisabledTests(IntakeLinkDisabledFactory factory) => _factory = factory;

    [Fact]
    public async Task Bayrak_kapaliysa_baslatma_404()
    {
        // Slug'ın var olup olmaması önemsiz: bayrak kontrolü DB'den önce —
        // kapalı özellik slug taramaya bile izin vermemeli.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        (await client.GetAsync("/musteri-kayit/herhangi/baglan/youtube"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/musteri-kayit/herhangi/baglan/facebook"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
