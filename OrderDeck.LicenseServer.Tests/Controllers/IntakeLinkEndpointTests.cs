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
