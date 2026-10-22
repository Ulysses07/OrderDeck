using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>KVKK K-3: kayıt formu yayıncının veri sorumlusu bilgilerini (girildiyse) aydınlatma paragrafının dışında gösterir;
/// gönderim gösterilen sürümü kayda yazar (m. 10/1-a ispatı, plan karar 24).</summary>
public sealed class IntakeFormDataControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormDataControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<string> SeedAsync(bool withProfile)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"dcf-{Guid.NewGuid():N}@example.test", Name = "Dcf",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow };
        db.Customers.Add(customer);
        db.Licenses.Add(new License { Id = Guid.NewGuid(), LicenseKey = $"LDK-DCF-{Guid.NewGuid():N}", CustomerId = customer.Id,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        var slug = $"d-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig { Id = Guid.NewGuid(), CustomerId = customer.Id, Slug = slug,
            WhatsAppPhone = TestPhone.NewE164(), IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        if (withProfile)
        {
            db.DataControllerProfiles.Add(new DataControllerProfile { CustomerId = customer.Id, LegalName = "Örnek Ticaret Ltd. Şti.",
                PostalAddress = "Örnek Mah. 1. Sok. No:2 İstanbul", KvkkEmail = "kvkk@example.test", KepAddress = "ornek@kep.example.test",
                Version = 1, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel" });
            db.DataControllerProfileVersions.Add(new DataControllerProfileVersion { Id = Guid.NewGuid(), CustomerId = customer.Id,
                Version = 1, LegalName = "Örnek Ticaret Ltd. Şti.", PostalAddress = "Örnek Mah. 1. Sok. No:2 İstanbul",
                KvkkEmail = "kvkk@example.test", KepAddress = "ornek@kep.example.test", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "panel" });
        }
        await db.SaveChangesAsync();
        return slug;
    }

    /// <summary>Başka bir yayıncı, bilgisinin iki sürümüyle (1 ve 2): formun yayıncısında 2. sürüm yok.</summary>
    private async Task SeedForeignBroadcasterWithTwoVersionsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"dcx-{Guid.NewGuid():N}@example.test", Name = "Dcx",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow, EmailConfirmedAt = DateTimeOffset.UtcNow };
        db.Customers.Add(customer);
        db.DataControllerProfiles.Add(new DataControllerProfile { CustomerId = customer.Id, LegalName = "Başka Yayıncı",
            PostalAddress = "Başka Mah. 3. Sok. No:4 Ankara", KvkkEmail = "baska@example.test", Version = 2,
            UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "panel" });
        foreach (var version in new[] { 1, 2 })
            db.DataControllerProfileVersions.Add(new DataControllerProfileVersion { Id = Guid.NewGuid(), CustomerId = customer.Id,
                Version = version, LegalName = "Başka Yayıncı", PostalAddress = "Başka Mah. 3. Sok. No:4 Ankara",
                KvkkEmail = "baska@example.test", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "panel" });
        await db.SaveChangesAsync();
    }

    /// <summary>Kayıt formunun gönderimi (IntakeFormKvkkTests'in alanları; kimlik bilgisi yok, değerler üretilir).</summary>
    private static FormUrlEncodedContent Submission(string token, string slug, string? dataControllerVersion)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token), new("Slug", slug),
            new("Input.InstagramUsername", $"dc_{Guid.NewGuid():N}"[..20]), new("Input.FullName", "Örnek Müşteri"),
            new("Input.Email", $"musteri-{Guid.NewGuid():N}@example.com"), new("Input.City", "İstanbul"),
            new("Input.District", "Kadıköy"), new("Input.Address", "Atatürk Cad. No:12"),
            new("Input.Phone", TestPhone.NewNational()), new("Input.Tckn", TestTckn.NewValid()),
            new("Input.WhatsAppConsent", "true"), new("Input.WhatsAppConsent", "false"),
            new("Input.KvkkAcknowledged", "true"), new("Input.KvkkAcknowledged", "false"),
            new("Input.KvkkTextVersion", KvkkNotice.TextVersion),
        };
        if (dataControllerVersion is not null) fields.Add(new("Input.DataControllerVersion", dataControllerVersion));
        return new FormUrlEncodedContent(fields);
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static string ShownVersion(string html)
        => Regex.Match(html, "name=\"Input\\.DataControllerVersion\" value=\"(\\d+)\"").Groups[1].Value;

    private async Task<int?> SavedVersionAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var sub = await scope.ServiceProvider.GetRequiredService<LicenseDbContext>().IntakeFormSubmissions.AsNoTracking()
            .SingleAsync(s => s.Config.Slug == slug);
        return sub.DataControllerVersion;
    }

    [Fact]
    public async Task Bilgi_girildiyse_blok_cizilir_aydinlatma_paragrafinin_disinda()
    {
        var slug = await SeedAsync(withProfile: true);
        var html = await _factory.CreateClient().GetStringAsync($"/musteri-kayit/{slug}");
        var doc = await BrowsingContext.New(AngleSharp.Configuration.Default).OpenAsync(r => r.Content(html));

        var block = doc.QuerySelector("[data-block='data-controller']")!;
        block.TextContent.Should().Contain("Örnek Ticaret Ltd. Şti.").And.Contain("Örnek Mah. 1. Sok. No:2 İstanbul")
            .And.Contain("kvkk@example.test").And.Contain("ornek@kep.example.test");
        block.ClassName.Should().NotBe("kvkk", "aydınlatma paragrafının özeti (KvkkNotice.TextVersion) bu bloğu içermez");
        block.Closest("form#intakeForm").Should().NotBeNull("gösterilen sürümün gizli alanı gönderimle gitmeli");
        doc.QuerySelector("form#intakeForm input[type='hidden'][name='Input.DataControllerVersion']")!
            .GetAttribute("value").Should().Be("1");
    }

    [Fact]
    public async Task Bilgi_girilmediyse_blok_yok()
    {
        var slug = await SeedAsync(withProfile: false);
        var html = await _factory.CreateClient().GetStringAsync($"/musteri-kayit/{slug}");
        html.Should().NotContain("data-block=\"data-controller\"");
        html.Should().NotContain("Input.DataControllerVersion", "gösterilmeyen blok gösterilmiş sayılmasın");
    }

    [Theory]
    [InlineData("shown")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    public async Task Gonderim_sayfanin_gosterdigi_surumu_yazar_var_olmayan_surum_yazilmaz(string posted)
    {
        // KVKK m. 10/1-a, ispat yükü veri sorumlusunda (karar 24): kayıt anında hangi kimliğin gösterildiği sonradan ispatlanabilmeli.
        var slug = await SeedAsync(withProfile: true);
        await SeedForeignBroadcasterWithTwoVersionsAsync();
        var client = NewClient();
        var html = await client.GetStringAsync($"/r/{slug}");
        var shown = ShownVersion(html);
        shown.Should().Be("1", "blok gösterildi, sürümü gizli alanda");

        var sent = posted switch { "shown" => shown, "unknown" => "99", _ => "2" };
        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Submission(AdminLoginHelper.ExtractAntiForgeryToken(html), slug, sent));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await SavedVersionAsync(slug)).Should().Be(posted == "shown" ? 1 : null,
            "yalnız o yayıncının var olan bir sürümü yazılır; başka yayıncının sürüm numarası bu yayıncıda yoksa yazılmaz");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.0")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public async Task Gelmeyen_ya_da_bozuk_surum_kaydi_engellemez_surum_yazilmaz(string? sent)
    {
        // Gizli alanı sayfa hep tam sayıyla basar; bu yola eski sekme (alan yok) ya da elle kurulmuş istek düşer. Kayıt
        // engellenmemeli (bağlayıcı hatası sayfayı sessizce geri döndürürdü), ama gösterilmiş sayılmamalı.
        var slug = await SeedAsync(withProfile: true);
        var client = NewClient();
        var html = await client.GetStringAsync($"/r/{slug}");

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit",
            Submission(AdminLoginHelper.ExtractAntiForgeryToken(html), slug, sent));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "kayıt açılır");
        (await SavedVersionAsync(slug)).Should().BeNull();
    }
}
