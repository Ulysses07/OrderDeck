using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Instagram;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

public sealed class IntakeFormPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormPageTests(ApiFactory factory) => _factory = factory;

    private async Task<(string slug, Guid customerId)> SeedConfigAsync(
        bool licenseActive = true, bool formActive = true, string whatsAppPhone = "+905551234567")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"if-{Guid.NewGuid():N}@x",
            Name = "If",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        if (licenseActive)
        {
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-IFP-" + Guid.NewGuid().ToString("N"),
                CustomerId = customer.Id,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
        }
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = whatsAppPhone,
            IsActive = formActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (slug, customer.Id);
    }

    [Fact]
    public async Task Get_form_page_returns_200_with_form_when_active()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Instagram");
        html.Should().Contain("E-posta");
        html.Should().Contain("Tamamla");
    }

    [Fact]
    public async Task Get_form_page_email_alani_gizlenmeden_render_edilir()
    {
        // Prod'da e-posta input'u görünmez geldi ve form sessizce gönderilemez
        // oldu: Razor, tag helper'lı input'taki placeholder="ornek@@eposta.com"
        // değerini parçalayıp araya başka bir öğenin hidden=" kalıbını bastı
        // (SDK 10.0.4xx). Bu test e-posta input'unun tek parça, hidden'sız ve
        // placeholder'ı bozulmamış render edildiğini sabitler.
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient();

        var html = await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync();

        var m = System.Text.RegularExpressions.Regex.Match(
            html, "<input[^>]*id=\"Input_Email\"[^>]*>");
        m.Success.Should().BeTrue("e-posta input'u sayfada olmalı");
        m.Value.Should().NotContain("hidden", "e-posta alanı asla gizli render edilmemeli");
        m.Value.Should().Contain("placeholder=\"ornek&#64;eposta.com\"",
            "placeholder tek parça kalmalı — parçalanması derleyici hatasının belirtisi");
    }

    [Fact]
    public async Task Get_form_page_returns_410_when_form_inactive()
    {
        var (slug, _) = await SeedConfigAsync(formActive: false);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Get_form_page_returns_410_when_license_expired()
    {
        var (slug, _) = await SeedConfigAsync(licenseActive: false);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Post_submit_with_valid_input_redirects_to_wa_me()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Anti-forgery token
        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "bilalcanli",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // POST WhatsApp'a değil, formun kendi yoluna dönüyor (POST-Redirect-GET).
        // wa.me linki onay ekranında; CSP `form-action` yönlendirme zincirini
        // denetlediği için gönderim akışında dış adres bilerek yok.
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResp.Headers.Location!.ToString().Should().Be($"/r/{slug}");

        var okResp = await client.GetAsync($"/r/{slug}");
        okResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var okHtml = await okResp.Content.ReadAsStringAsync();
        okHtml.Should().Contain("Kaydın alındı");
        okHtml.Should().Contain("https://wa.me/905551234567?text=");

        // Submission persisted?
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.Username == "bilalcanli")
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.City.Should().Be("İstanbul");
        sub.District.Should().Be("Kadıköy");
        sub.Address.Should().Be("Atatürk Cad. No:12");
    }

    [Fact]
    public async Task Confirmation_page_is_one_shot_so_refresh_does_not_resubmit()
    {
        // Sahada gözlenen kusur: ekranda geri bildirim olmayınca müşteri
        // "Tamamla"ya üst üste basıyordu ve DB'ye aynı kayıttan 10+ kopya
        // düşüyordu. Onay ekranı hem geri bildirim veriyor hem POST-Redirect-GET
        // sayesinde F5'i zararsız kılıyor.
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());
        await client.PostAsync($"/r/{slug}?handler=Submit", BuildValidForm(antiForgery, slug));

        (await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync())
            .Should().Contain("Kaydın alındı");

        // İkinci GET (F5): onay tüketildi, form geri geliyor ve yeni kayıt yok.
        var refreshHtml = await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync();
        refreshHtml.Should().NotContain("Kaydın alındı");
        refreshHtml.Should().Contain("intakeForm");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.IntakeFormSubmissions
            .CountAsync(s => s.Config.CustomerId == customerId);
        count.Should().Be(1);
    }

    [Fact]
    public async Task Confirmation_page_shown_without_link_when_broadcaster_has_no_phone()
    {
        // Numara tanımsızsa `wa.me/?text=...` hiçbir sohbet açmıyor. Link
        // kurmak yerine onay ekranını linksiz gösteriyoruz — müşteri en azından
        // kaydının geçtiğini görüyor, boş ekranla kalmıyor.
        var (slug, _) = await SeedConfigAsync(whatsAppPhone: "");
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", BuildValidForm(antiForgery, slug));

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var html = await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync();
        html.Should().Contain("Kaydın alındı");
        html.Should().NotContain("waLink");
        html.Should().NotContain("wa.me");
    }

    private static FormUrlEncodedContent BuildValidForm(string antiForgery, string slug)
        => new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "bilalcanli",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });

    private static FormUrlEncodedContent BuildMinimalForm(string antiForgery, string slug)
        => new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });

    [Fact]
    public async Task Gecerli_ig_tokeni_kimligi_baglar()
    {
        // Arrange
        var (slug, customerId) = await SeedConfigAsync();
        var igTokenService = _factory.Services.GetRequiredService<IntakeIgTokenService>();
        var igUser = $"ig_{Guid.NewGuid():N}"[..20];
        var token = igTokenService.Create(slug, igUser);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // GET ?ig= → bağlı kimlik çipi HTML'de görünmeli
        var getResp = await client.GetAsync($"/r/{slug}?ig={Uri.EscapeDataString(token)}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await getResp.Content.ReadAsStringAsync();
        html.Should().Contain(igUser, "IG kullanıcı adı çipte görünmeli");
        html.Should().Contain("linked-chip", "bağlı kimlik çipi render edilmeli");
        html.Should().Contain("Instagram hesabın bağlandı", "bağlantı banner'ı çıkmalı");

        // POST submit → InstagramUsername kayda yazılmış olmalı
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(html);
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit",
            BuildMinimalForm(antiForgery, slug));
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.InstagramUsername.Should().Be(igUser);
    }

    [Fact]
    public async Task Yanlis_sluga_ait_token_yok_sayilir()
    {
        // Arrange: iki ayrı slug; token başka slug'a ait
        var (slug1, _) = await SeedConfigAsync();
        var (slug2, _) = await SeedConfigAsync();
        var igTokenService = _factory.Services.GetRequiredService<IntakeIgTokenService>();
        var token = igTokenService.Create(slug1, "birkullanici");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        // slug2'ye gönderilen slug1 token'ı → form normal açılır, çip yok, hata yok
        var getResp = await client.GetAsync($"/r/{slug2}?ig={Uri.EscapeDataString(token)}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await getResp.Content.ReadAsStringAsync();
        html.Should().NotContain("linked-chip", "yanlış slug token'ı çip üretmemeli");
        html.Should().NotContain("Instagram hesabın bağlandı", "bağlantı banner'ı çıkmamalı");
        html.Should().Contain("intakeForm", "form normal render edilmeli");
    }

    [Fact]
    public async Task Bozuk_token_yok_sayilir()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        // Bozuk ?ig= → form normal açılır, hata ekranı YOK
        var getResp = await client.GetAsync($"/r/{slug}?ig=curcuna");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await getResp.Content.ReadAsStringAsync();
        html.Should().NotContain("linked-chip", "bozuk token çip üretmemeli");
        html.Should().Contain("intakeForm", "form normal render edilmeli");
    }

    [Fact]
    public async Task Post_submit_honeypot_filled_silently_returns_200_and_does_not_persist()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.Username"] = "bot",
            ["Input.FullName"] = "Bot Bot",
            ["Input.Address"] = "spam",
            ["website"] = "http://bot-spam.example"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // Silent: 200, NOT redirect
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.Username == "bot")
            .FirstOrDefaultAsync();
        sub.Should().BeNull();
    }

    [Fact]
    public async Task Post_submit_with_no_platform_username_returns_page_with_error()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        // All platform usernames empty → "at least one" validation error.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.FullName"] = "Bilal",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Adres",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // ModelState invalid → return Page() (200 with errors), Razor convention
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await postResp.Content.ReadAsStringAsync());
        html.Should().Contain("En az bir platform");
    }

    /// <summary>
    /// Sahadaki en sık hata: müşteri profil adresini yapıştırıyor. Eskiden
    /// HandleValidator bunu reddediyordu; artık sunucu kullanıcı adını kendisi
    /// çıkarıyor ve DB'ye temiz handle düşüyor.
    /// </summary>
    [Fact]
    public async Task Post_submit_instagram_profil_adresini_kullanici_adina_cevirir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "https://www.instagram.com/bilalcanli/?igsh=MWx5",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.InstagramUsername.Should().Be("bilalcanli");
    }

    [Fact]
    public async Task Post_submit_tiktok_video_adresini_kullanici_adina_cevirir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.TikTokUsername"] = "https://www.tiktok.com/@edanur/video/7412345678901234567",
            ["Input.FullName"] = "Eda Nur",
            ["Input.Email"] = "eda@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.TikTokUsername.Should().Be("edanur");
    }

    /// <summary>
    /// Çözülemeyen adres SESSİZCE geçmemeli. Gönderi adresindeki kod kullanıcı adı
    /// sanılırsa kayıt tamamen alakasız bir değere bağlanır.
    /// </summary>
    [Fact]
    public async Task Post_submit_instagram_gonderi_adresi_hata_ile_doner()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "https://www.instagram.com/p/Cxyz123",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await postResp.Content.ReadAsStringAsync());
        html.Should().Contain("gönderi adresi");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.IntakeFormSubmissions.CountAsync(s => s.Config.CustomerId == customerId);
        count.Should().Be(0);
    }

    /// <summary>
    /// Alan sınırı 64'te kalsaydı uzun profil adresleri model binding'de,
    /// yani parser daha çalışmadan reddedilirdi.
    /// </summary>
    [Fact]
    public async Task Post_submit_uzun_profil_adresi_uzunluk_hatasina_takilmaz()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var longUrl = "https://www.tiktok.com/@birazuzunkullaniciadi/video/7412345678901234567?lang=tr";
        longUrl.Length.Should().BeGreaterThan(64);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.TikTokUsername"] = longUrl,
            ["Input.FullName"] = "Eda Nur",
            ["Input.Email"] = "eda@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.TikTokUsername.Should().Be("birazuzunkullaniciadi");
    }

    /// <summary>
    /// Kanal kimliği bir handle DEĞİL, o yüzden hem "en az bir platform"
    /// kuralında hem legacy Username seçiminde ayrıca sayılması gerekiyor;
    /// bu test ikisini birden çiviliyor.
    /// </summary>
    [Fact]
    public async Task Post_submit_sadece_youtube_kanal_adresi_ile_kayit_gecer()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.YouTubeUsername"] = "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // "En az bir platform" hatası çıkmamalı — kanal adresi yeterli sayılmalı.
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        // Handle yok; legacy Username kanal kimliğine düşmeli.
        sub!.Username.Should().Be("UCabcdefghijklmnopqrstuv");
    }

    /// <summary>
    /// Yanlış platforma yapıştırılan adres açık hata vermeli; kayıt oluşmamalı.
    /// </summary>
    [Fact]
    public async Task Post_submit_yanlis_kutuya_yapistirilan_adres_reddedilir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            // Instagram adresi TikTok kutusuna yapıştırıldı.
            ["Input.TikTokUsername"] = "https://www.instagram.com/bilalcanli",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await postResp.Content.ReadAsStringAsync());
        html.Should().Contain("Bu bir Instagram adresi");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.IntakeFormSubmissions.CountAsync(s => s.Config.CustomerId == customerId);
        count.Should().Be(0);
    }

    /// <summary>
    /// Facebook kuralı yalnız uzunluk sınırı koyuyor; boşluğa ve Türkçe
    /// karaktere izin veriyor. Eşleşme görünen ada dayalı olduğu için
    /// Facebook kutusuna girilen metne adres çözümlemesi uygulanmıyor.
    /// </summary>
    [Fact]
    public async Task Post_submit_facebook_gorunen_ad_ile_kayit_gecer()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.FacebookUsername"] = "Bilal Canlı",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.FacebookUsername.Should().Be("Bilal Canlı");
    }

    /// <summary>
    /// Facebook adres çözümlemesinin dışında tutuluyor — kutuya adres benzeri
    /// bir metin girilse bile görünen ad sayılır, "yanlış kutu" hatası VERMEZ.
    /// Diğer üç platformdan farklı olan bu dal, kaldırıldığında hiçbir test
    /// düşmüyordu; davranışı burada çiviliyoruz.
    /// </summary>
    [Fact]
    public async Task Post_submit_facebook_kutusuna_adres_girilse_de_adres_hatasi_vermez()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.FacebookUsername"] = "https://www.instagram.com/bilalcanli",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId)
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.FacebookUsername.Should().Be("https://www.instagram.com/bilalcanli");
    }
}
