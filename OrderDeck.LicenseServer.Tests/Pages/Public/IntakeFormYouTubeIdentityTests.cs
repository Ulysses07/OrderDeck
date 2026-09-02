using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

/// <summary>
/// Gerçek YouTube API'sinin yerine sahteyi koyar. ApiFactory'de servis geçersiz
/// kılma için hazır bir kanca YOK (yalnız ExtraConfig / ConfigureDatabase var),
/// bu yüzden ConfigureWebHost genişletiliyor. ConfigureTestServices uygulamanın
/// kayıtlarından SONRA koştuğu için tekil kayıt güvenle değiştirilebiliyor.
/// ApiFactory'nin kendisine dokunulmuyor — 40+ test dosyası ona bağlı.
/// </summary>
public sealed class YouTubeIdentityFactory : ApiFactory
{
    public FakeYouTubeChannelResolver Resolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IYouTubeChannelResolver>();
            services.AddSingleton<IYouTubeChannelResolver>(Resolver);
        });
    }
}

public sealed class IntakeFormYouTubeIdentityTests : IClassFixture<YouTubeIdentityFactory>
{
    private const string RealChannelId = "UCabcdefghijklmnopqrstuv";

    // Adres yolunun testleri için: müşterinin YAPIŞTIRDIĞI kimlik ve API'nin
    // GERİ DÖNDÜĞÜ kimlik bilerek FARKLI. Sunucunun girdiyi körü körüne
    // yankılamadığı ancak ikisi ayrıldığında çivilenebiliyor.
    private const string PastedChannelId = "UCpasted0000000000000abc";
    private const string CanonicalChannelId = "UCcanonical00000000000xy";

    private readonly YouTubeIdentityFactory _factory;
    public IntakeFormYouTubeIdentityTests(YouTubeIdentityFactory factory) => _factory = factory;

    private async Task<(string slug, Guid customerId)> SeedConfigAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"yti-{Guid.NewGuid():N}@x",
            Name = "Yti",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-YTI-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"y-{Guid.NewGuid():N}"[..10];
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

    private static async Task<string> TokenAsync(HttpClient client, string slug)
        => AdminLoginHelper.ExtractAntiForgeryToken(
            await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync());

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

    /// <summary>
    /// PLANIN EN ÖNEMLİ TESTİ. İstemci uydurma bir channelId gönderiyor; sunucu
    /// onu YOK SAYIP handle'ı kendisi çözmeli. Bu olmadan onay kutusu süs:
    /// JS'i atlayan her istek kaydı istediği kimliğe bağlar.
    /// </summary>
    [Fact]
    public async Task Sunucu_istemciden_gelen_channelId_ye_guvenmez()
    {
        _factory.Resolver.ByHandle["orderdeck"] =
            new YouTubeChannel(true, true, "OrderDeck", null, RealChannelId);

        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "orderdeck"),
            ("Input.YouTubeConfirmed", "true"),
            ("Input.YouTubeChannelId", "UCzzzzzzzzzzzzzzzzzzzzzz")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var sub = await LatestAsync(customerId);
        sub.Should().NotBeNull();
        sub!.YouTubeChannelId.Should().Be(RealChannelId);
    }

    /// <summary>
    /// "test1234" yerine "test" yazan müşteri sorunu: "test" GERÇEK bir yabancının
    /// kanalı, yani doğrulama yeşil ✓ verir ve kayıt yabancıya bağlanır. Onay
    /// kutusu tam bu yüzden zorunlu — kartta gördüğü ad kendisine ait değilse
    /// onaylamaz ve hatayı yakalar.
    /// </summary>
    [Fact]
    public async Task Onay_kutusu_isaretlenmeden_gonderim_engellenir()
    {
        _factory.Resolver.ByHandle["yabanci"] =
            new YouTubeChannel(true, true, "Yabancı Kanal", null, RealChannelId);

        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "yabanci")));

        // 200 = Page() döndü, yani ModelState geçersiz. Hata METNİNİ burada
        // aramıyoruz: Input.YouTubeConfirmed için doğrulama alanı Task 5'te
        // ekleniyor, mesajın ekranda göründüğü orada elle doğrulanıyor.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LatestAsync(customerId)).Should().BeNull();
    }

    [Fact]
    public async Task Kanal_bulunamazsa_gonderim_engellenir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "boylebirkanalyok"),
            ("Input.YouTubeConfirmed", "true")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        html.Should().Contain("kanalı bulunamadı");

        (await LatestAsync(customerId)).Should().BeNull();
    }

    /// <summary>
    /// API "kanal var" diyor ama kimlik döndürmüyor (beklenmedik gövde —
    /// YouTubeChannelResolver id alanını bulamazsa null bırakıyor). Onaylatacak
    /// kimlik yok: müşteri kilitlenmez, kayıt handle ile alınır. Sunucuda
    /// uyarı günlüğe düşer; sessizce geçmez.
    /// </summary>
    [Fact]
    public async Task Kimliksiz_kanal_gonderimi_engellemez()
    {
        _factory.Resolver.ByHandle["kimliksiz"] =
            new YouTubeChannel(true, true, "Kimliksiz Kanal", null, null);

        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "kimliksiz"),
            ("Input.YouTubeConfirmed", "true")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var sub = await LatestAsync(customerId);
        sub.Should().NotBeNull();
        sub!.YouTubeUsername.Should().Be("kimliksiz");
        sub.YouTubeChannelId.Should().BeNull();
    }

    /// <summary>
    /// Kota bitmesi/ağ arızası BİZİM sorunumuz. Müşteriyi kilitlemek yerine kayıt
    /// alınır; channelId boş kalır, eşleştirme handle üzerinden yürür (bugünkü hâl).
    /// </summary>
    [Fact]
    public async Task Api_ulasilamazsa_gonderim_engellenmez()
    {
        _factory.Resolver.ForceUnavailable = true;
        try
        {
            var (slug, customerId) = await SeedConfigAsync();
            var client = NewClient();
            var token = await TokenAsync(client, slug);

            var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
                ("Input.YouTubeUsername", "orderdeck")));

            resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

            var sub = await LatestAsync(customerId);
            sub.Should().NotBeNull();
            sub!.YouTubeUsername.Should().Be("orderdeck");
            sub.YouTubeChannelId.Should().BeNull();
        }
        finally
        {
            _factory.Resolver.ForceUnavailable = false;
        }
    }

    /// <summary>
    /// DEĞİŞTİ: eskiden bu test "kanal adresi kimliğin kendisidir, API'ye gitmeye
    /// ve onay istemeye gerek yok" davranışını çiviliyordu. Onay kuralının o
    /// boşluğu artık kapalı: müşteri YANLIŞ kanalın sayfasından kopyalarsa
    /// (yapıştırdığı UC… yapısal olarak kusursuz ama başkasına ait) hiçbir şey
    /// yakalamıyordu. Testin niyeti aynı — "kanal adresi yapıştırılan gönderim
    /// kabul ediliyor" — ama artık adres yolu da handle yolu gibi çözülüyor:
    /// varlık sorgusu YAPILIR, kart çizilir, onay istenir.
    ///
    /// Ayrıca çağrının "id:" önekiyle kaydedilmesi, adresin yanlışlıkla
    /// forHandle sorgusuna düşmediğini gösteriyor.
    /// </summary>
    [Fact]
    public async Task Kanal_adresi_yapistirilinca_api_ye_gidilir()
    {
        _factory.Resolver.ById[PastedChannelId] =
            new YouTubeChannel(true, true, "Adres Kanalı", null, CanonicalChannelId);

        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);
        var callsBefore = _factory.Resolver.Calls.Count;

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", $"https://www.youtube.com/channel/{PastedChannelId}"),
            ("Input.YouTubeConfirmed", "true")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        _factory.Resolver.Calls.Skip(callsBefore).Should()
            .ContainSingle().Which.Should().Be("id:" + PastedChannelId);

        (await LatestAsync(customerId)).Should().NotBeNull();
    }

    /// <summary>
    /// Adres yolunun asıl açığı: müşteri YANLIŞ kanalın sayfasından kopyalıyor.
    /// Yapıştırılan UC… kusursuz görünür, hiçbir biçim kontrolüne takılmaz ve
    /// kayıt sessizce yabancıya bağlanır. Onay kutusu bunu yakalayan tek şey —
    /// kartta gördüğü ad kendisine ait değilse müşteri onaylamaz.
    /// </summary>
    [Fact]
    public async Task Kanal_adresinde_de_onay_kutusu_zorunlu()
    {
        _factory.Resolver.ById[PastedChannelId] =
            new YouTubeChannel(true, true, "Yabancı Adres Kanalı", null, CanonicalChannelId);

        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", $"https://www.youtube.com/channel/{PastedChannelId}")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        // Kanalın ADI hatada geçmeli: müşteri neyi onaylamadığını görmezse
        // yanlış kanalı yakalayamaz.
        html.Should().Contain("Yabancı Adres Kanalı");

        (await LatestAsync(customerId)).Should().BeNull();
    }

    /// <summary>
    /// Onay verildiğinde kaydedilen kimlik API'nin DÖNDÜRDÜĞÜ değer olmalı,
    /// müşterinin yapıştırdığı değer değil. Sahte çözümleyici bilerek farklı bir
    /// kimlik döndürüyor: sunucu girdiyi yankılıyor olsaydı bu test kırmızı olurdu.
    /// </summary>
    [Fact]
    public async Task Kanal_adresi_onaylaninca_apinin_dondurdugu_kimlik_kaydedilir()
    {
        _factory.Resolver.ById[PastedChannelId] =
            new YouTubeChannel(true, true, "Onaylı Kanal", null, CanonicalChannelId);

        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", $"https://www.youtube.com/channel/{PastedChannelId}"),
            ("Input.YouTubeConfirmed", "true")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var sub = await LatestAsync(customerId);
        sub.Should().NotBeNull();
        sub!.YouTubeChannelId.Should().Be(CanonicalChannelId);
        sub.YouTubeChannelId.Should().NotBe(PastedChannelId);
    }

    /// <summary>
    /// Kota/ağ arızası adres yolunda da müşteriyi kilitlemez. Handle yolundan
    /// tek farkı: elimizde adresten çıkmış, yapısal olarak geçerli bir kimlik
    /// VAR — arıza yüzünden onu atmak kaydı gereksiz yere kimliksiz bırakırdı.
    /// </summary>
    [Fact]
    public async Task Kanal_adresinde_api_ulasilamazsa_adresten_cikan_kimlik_korunur()
    {
        _factory.Resolver.ForceUnavailable = true;
        try
        {
            var (slug, customerId) = await SeedConfigAsync();
            var client = NewClient();
            var token = await TokenAsync(client, slug);

            var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
                ("Input.YouTubeUsername", $"https://www.youtube.com/channel/{PastedChannelId}")));

            resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

            var sub = await LatestAsync(customerId);
            sub.Should().NotBeNull();
            sub!.YouTubeChannelId.Should().Be(PastedChannelId);
        }
        finally
        {
            _factory.Resolver.ForceUnavailable = false;
        }
    }

    /// <summary>
    /// Adres var olmayan bir kanala işaret ediyor (kanal kapanmış, kimlik eksik
    /// kopyalanmış). "Baktık, yok" cevabı bloke eder — sessizce kimliksiz kayıt
    /// açmak müşteriye hiçbir şey söylemeden eşleşmeyi bozardı.
    /// </summary>
    [Fact]
    public async Task Kanal_adresi_bos_bir_kimlige_isaret_ediyorsa_gonderim_engellenir()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var token = await TokenAsync(client, slug);

        // ById'de tanımlı DEĞİL → sahte "Available:true, Exists:false" döner.
        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "https://www.youtube.com/channel/UCyokboyleKanal000000000"),
            ("Input.YouTubeConfirmed", "true")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        html.Should().Contain("Bu adresteki YouTube kanalını bulamadık");

        (await LatestAsync(customerId)).Should().BeNull();
    }

    /// <summary>
    /// Onay kutusu, kanal kartı çizilmeden EKRANDA OLMAMALI. Görmediği bir kanalı
    /// onaylayabilen müşteride bu özelliğin hiçbir anlamı kalmaz — "gördüğün adı
    /// onayla" bağı korumanın tamamı.
    ///
    /// Razor'ın boolean nitelik davranışına güvenmek yerine çıktının kendisine
    /// bakıyoruz: HTML'de niteliğin DEĞERİ değil VARLIĞI belirleyici. Nitelik
    /// hiç basılmazsa kutu hep görünür (bu test), hep basılırsa — hidden="False"
    /// dahil — kutu kalıcı gizli kalır ve kimse onaylayamaz (kardeş test
    /// Kanal_bulununca_onay_kutusu_gorunur_gelir). İkisi birlikte gerekli.
    ///
    /// Kapsam sınırı: bu testler niteliği bağlıyor, GİZLENMEYİ değil.
    /// IntakeForm.cshtml'deki ".yt-confirm[hidden] { display:none }" satırı
    /// silinirse ikisi de yeşil kalır ama kutu yine hep görünür olur — yazar
    /// kuralı tarayıcının [hidden] kuralını köken önceliğiyle eziyor.
    /// </summary>
    [Fact]
    public async Task Onay_kutusu_kanal_karti_yokken_gizli_gelir()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = NewClient();

        var html = await (await client.GetAsync($"/r/{slug}")).Content.ReadAsStringAsync();

        var label = LabelTag(html);
        label.Should().Contain("hidden",
            "kanal kartı yokken onay kutusu ekranda olmamalı");
    }

    /// <summary>
    /// Aynı bağın diğer yönü: sunucu kanalı bulup kartı çizdiyse (onay
    /// işaretlenmediği için sayfa geri döndü) kutu GÖRÜNÜR olmalı. Gizli kalırsa
    /// müşteri "kanalı onayla" hatasını görür ama onaylayacak bir şey bulamaz.
    /// </summary>
    [Fact]
    public async Task Kanal_bulununca_onay_kutusu_gorunur_gelir()
    {
        _factory.Resolver.ByHandle["gorunur"] =
            new YouTubeChannel(true, true, "Görünür Kanal", null, RealChannelId);

        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var token = await TokenAsync(client, slug);

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.YouTubeUsername", "gorunur")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();

        LabelTag(html).Should().NotContain("hidden",
            "sunucu kanalı bulduysa onaylanacak kutu ekranda olmalı");
    }

    /// <summary>
    /// Onay kutusunun etiketinin açılış etiketini kesip çıkarır. Nitelik varlığını
    /// arayacağımız için sayfanın geri kalanındaki "hidden" geçişleri (gizli
    /// alanlar, honeypot CSS'i) sonucu kirletmemeli.
    /// </summary>
    private static string LabelTag(string html)
    {
        var start = html.IndexOf("id=\"ytConfirmWrap\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "onay kutusunun etiketi sayfada olmalı");
        var open = html.LastIndexOf('<', start);
        var close = html.IndexOf('>', start);
        return html[open..(close + 1)];
    }

    /// <summary>
    /// YouTube kutusu boşken hiçbir doğrulama tetiklenmemeli — Instagram'la kayıt
    /// olan müşteri YouTube yüzünden engellenemez.
    /// </summary>
    [Fact]
    public async Task Youtube_bos_ise_dogrulama_calismaz()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = NewClient();
        var token = await TokenAsync(client, slug);
        var callsBefore = _factory.Resolver.Calls.Count;

        var resp = await client.PostAsync($"/r/{slug}?handler=Submit", Form(token, slug,
            ("Input.InstagramUsername", "bilalcanli")));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        _factory.Resolver.Calls.Count.Should().Be(callsBefore);
        (await LatestAsync(customerId)).Should().NotBeNull();
    }
}
