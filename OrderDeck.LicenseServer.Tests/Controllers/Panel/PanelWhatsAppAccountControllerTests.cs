using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Embedded Signup'ın uçtan uca panel yolu. Graph sahtelenmiş — burada
/// doğrulanan şey Meta'nın davranışı değil, BİZİM sıralamamız: token takası
/// tutmadan satır açılmamalı, abonelik atlanmamalı, token tarayıcıya dönmemeli.
/// </summary>
public sealed class PanelWhatsAppAccountControllerTests : IDisposable
{
    // Her test KENDİ fabrikasını kurar. Paylaşılan bir fixture olmaz:
    // WhatsAppAccount.PhoneNumberId GLOBAL unique, yani ilk testin bağladığı
    // "PNID_1" sonraki testlere 409 döndürürdü.
    private readonly List<OnboardingApiFactory> _factories = [];

    /// <summary>Her adımı ayrı ayrı başarılı/başarısız kılabilen sahte Graph.</summary>
    private sealed class FakeOnboardingClient : IWhatsAppOnboardingClient
    {
        public GraphResult<string> Exchange = GraphResult<string>.Success("BIZ_TOKEN");
        public GraphResult<bool> Subscribe = GraphResult<bool>.Success(true);
        public GraphResult<WhatsAppPhoneNumberInfo> Phone =
            GraphResult<WhatsAppPhoneNumberInfo>.Success(
                new WhatsAppPhoneNumberInfo("+90 555 111 22 33", "Emar Global"));
        public GraphResult<bool> Register = GraphResult<bool>.Success(true);
        public GraphResult<bool> Unsubscribe = GraphResult<bool>.Success(true);
        public GraphResult<string> Sync = GraphResult<string>.Success("REQ_1");

        public string? SeenCode;
        public string? SeenWabaId;
        public string? SeenPin;
        public string? SeenUnsubscribedWabaId;
        public readonly List<string> SeenSyncTypes = [];

        public Task<GraphResult<string>> ExchangeCodeAsync(string code, CancellationToken ct)
        {
            SeenCode = code;
            return Task.FromResult(Exchange);
        }

        public Task<GraphResult<bool>> SubscribeAppAsync(string wabaId, string token, CancellationToken ct)
        {
            SeenWabaId = wabaId;
            return Task.FromResult(Subscribe);
        }

        public Task<GraphResult<bool>> UnsubscribeAppAsync(string wabaId, string token, CancellationToken ct)
        {
            SeenUnsubscribedWabaId = wabaId;
            return Task.FromResult(Unsubscribe);
        }

        public Task<GraphResult<WhatsAppPhoneNumberInfo>> ReadPhoneNumberAsync(
            string wabaId, string phoneNumberId, string token, CancellationToken ct) =>
            Task.FromResult(Phone);

        public Task<GraphResult<bool>> RegisterPhoneNumberAsync(
            string phoneNumberId, string pin, string token, CancellationToken ct)
        {
            SeenPin = pin;
            return Task.FromResult(Register);
        }

        public Task<GraphResult<string>> SyncSmbAppDataAsync(
            string phoneNumberId, string syncType, string token, CancellationToken ct)
        {
            SeenSyncTypes.Add(syncType);
            return Task.FromResult(Sync);
        }
    }

    /// <summary>Graph'ı sahteyle değiştiren fabrika. <c>CustomerAuthHelper</c>
    /// <see cref="ApiFactory"/> istiyor, o yüzden türetiliyor — <c>WithWebHostBuilder</c>
    /// taban tipi döndürdüğü için oradan geçmiyor.</summary>
    private sealed class OnboardingApiFactory : ApiFactory
    {
        private readonly bool _signupConfigured;

        /// <param name="signupConfigured">false = <c>AppId</c>/<c>ConfigId</c> boş,
        /// yani sunucu Embedded Signup için hiç yapılandırılmamış.</param>
        public OnboardingApiFactory(bool signupConfigured = true)
            => _signupConfigured = signupConfigured;

        public FakeOnboardingClient Graph { get; } = new();

        /// <summary>Uç boş yapılandırmayı 503 saydığı için varsayılan fabrika bu
        /// iki anahtarı vermek zorunda; yoksa config'i okuyan test yanlış
        /// sebeple kırmızıya döner. Değerler sahte, sunucu onları kullanmıyor.</summary>
        protected override IDictionary<string, string?> ExtraConfig =>
            _signupConfigured
                ? new Dictionary<string, string?>
                {
                    ["OrderDeck:WhatsApp:AppId"] = "1234567890",
                    ["OrderDeck:WhatsApp:EmbeddedSignupConfigId"] = "9876543210",
                }
                : new Dictionary<string, string?>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IWhatsAppOnboardingClient>(Graph));
        }
    }

    private OnboardingApiFactory NewFactory(bool signupConfigured = true)
    {
        var factory = new OnboardingApiFactory(signupConfigured);
        _factories.Add(factory);
        return factory;
    }

    private sealed record Seed(
        HttpClient Client, Guid LicenseId, FakeOnboardingClient Graph, OnboardingApiFactory Factory);

    private async Task<Seed> SeedAsync() => await SeedTenantAsync(NewFactory());

    /// <summary>Verilen fabrikaya bir yayıncı daha ekler. "Her teste bir fabrika"
    /// kuralı PhoneNumberId'nin global unique olmasından geliyordu; çapraz-tenant
    /// senaryosu tam da bunu sınadığı için TEK store'da İKİ lisans gerekiyor.</summary>
    private static async Task<Seed> SeedTenantAsync(OnboardingApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PWA-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        return new Seed(client, license.Id, factory.Graph, factory);
    }

    private sealed record OperatorLoginResp(string Token);

    /// <summary>Yayıncının davet ettiği bir personel adına giriş yapmış client.
    /// Rol bilerek "staff": "stock" zaten genel kapıya takıldığı için sahiplik
    /// kuralını yalnız staff sınayabilir.</summary>
    private static async Task<HttpClient> StaffClientAsync(Seed seed)
    {
        var email = $"op-{Guid.NewGuid():N}@example.com";
        var password = "pwd-" + Guid.NewGuid().ToString("N");

        var invite = await seed.Client.PostAsJsonAsync("/api/panel/operators",
            new { email, name = "Personel", password, role = "staff" });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);

        var client = seed.Factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/v1/auth/operator-login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await login.Content.ReadFromJsonAsync<OperatorLoginResp>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);
        return client;
    }

    /// <summary>Satırdaki şifreli PIN'i çözer — testin "kaydedilen PIN hangisi"
    /// sorusunu ancak DB'den okuyarak cevaplayabildiği yer.</summary>
    private static string? StoredPin(Seed seed)
    {
        using var scope = seed.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<WhatsAppAccountService>();
        var row = db.WhatsAppAccounts.Single(a => a.LicenseId == seed.LicenseId);
        return row.TwoStepPinProtected is null ? null : accounts.TryUnprotectPin(row.TwoStepPinProtected);
    }

    // wabaId/phoneNumberId Meta'da saf rakam ve uç bunu doğruluyor — sahte
    // değerler de rakam olmak zorunda.
    private static object Body => new { code = "CODE_1", wabaId = "1001", phoneNumberId = "2002" };

    [Fact]
    public async Task A_completed_signup_connects_the_account_without_revealing_the_token()
    {
        var seed = await SeedAsync();

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().NotContain("BIZ_TOKEN");
        json.Should().Contain("+90 555 111 22 33");

        seed.Graph.SeenCode.Should().Be("CODE_1");
        seed.Graph.SeenWabaId.Should().Be("1001");
        seed.Graph.SeenPin.Should().HaveLength(6).And.MatchRegex("^[0-9]{6}$");
    }

    [Fact]
    public async Task A_failed_code_exchange_leaves_no_account_behind()
    {
        var seed = await SeedAsync();
        seed.Graph.Exchange = GraphResult<string>.Failure("100", "Invalid verification code format.");

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        // Yarım satır bırakmak en kötüsü olurdu: panel "bağlı" gösterir,
        // gönderim sessizce başarısız olur.
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await TitleAsync(resp)).Should().Be("whatsapp-code-exchange-failed");

        var status = await seed.Client.GetAsync("/api/panel/whatsapp/account");
        status.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_failed_subscription_is_fatal_because_webhooks_would_never_arrive()
    {
        var seed = await SeedAsync();
        seed.Graph.Subscribe = GraphResult<bool>.Failure("200", "Permissions error");

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await TitleAsync(resp)).Should().Be("whatsapp-subscribe-failed");

        // Yarım satır sessiz veri bozulması olurdu: panel "bağlı" gösterir,
        // webhook'lar ise abonelik olmadığı için o numaraya HİÇ düşmez.
        using var scope = seed.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WhatsAppAccounts.Any(a => a.LicenseId == seed.LicenseId).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_registration_still_connects_but_records_the_error()
    {
        var seed = await SeedAsync();
        seed.Graph.Register = GraphResult<bool>.Failure("133005", "Two step verification PIN mismatch.");

        var resp = await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        // Numara zaten kayıtlıysa register hata verir ama hesap ÇALIŞIR —
        // bunu ölümcül saymak sorunsuz yayıncıyı bağlanamaz hâle getirirdi.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().Contain("133005");
    }

    [Fact]
    public async Task A_rejected_pin_never_replaces_the_stored_one()
    {
        var seed = await SeedAsync();

        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPin = seed.Graph.SeenPin;
        firstPin.Should().NotBeNullOrEmpty();

        // İkinci bağlanma (token yenilemenin belgeli yolu) — Meta PIN'i reddediyor.
        seed.Graph.Register = GraphResult<bool>.Failure("133005", "Two step verification PIN mismatch.");
        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Reddedilen PIN yazılsaydı numara bir daha register edilemezdi:
        // Meta hep ilk PIN'i ister, kurtarma yolu yalnız Meta desteği.
        StoredPin(seed).Should().Be(firstPin);
    }

    [Fact]
    public async Task A_second_signup_reuses_the_stored_pin_instead_of_inventing_one()
    {
        var seed = await SeedAsync();

        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPin = seed.Graph.SeenPin;

        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Yeni PIN üretmek register'ı garanti başarısız kılardı — Meta yeniden
        // kayıtta numaranın MEVCUT PIN'ini bekliyor.
        seed.Graph.SeenPin.Should().Be(firstPin);
    }

    [Fact]
    public async Task Switching_to_a_new_number_does_not_carry_the_old_numbers_pin()
    {
        var seed = await SeedAsync();

        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPin = seed.Graph.SeenPin;

        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup",
                new { code = "CODE_2", wabaId = "1001", phoneNumberId = "3003" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // PIN numaraya ait, lisansa değil. Eskisini yeni numaraya göndermek
        // 133005 üretir; hata yüzünden yeni PIN saklanmaz ve satırdaki PIN eski
        // numaranınki kalır — her denemede aynı yanlış PIN gider, çıkış yok.
        seed.Graph.SeenPin.Should().NotBe(firstPin);
        StoredPin(seed).Should().Be(seed.Graph.SeenPin);
    }

    // Meta id'leri saf rakam. Doğrulanmazsa iki ayrı şey oluyor: değer Graph
    // yoluna ("{Root}/{wabaId}/subscribed_apps") aynen giriyor — sorgu dizesi
    // eklenebiliyor, ".." parçaları Uri tarafından başka bir düğüme
    // normalleştiriliyor — ve uzun değer nvarchar(64)'e sığmayıp UpsertAsync'te
    // patlıyor, yani /register PIN'i yazdıktan SONRA 500.
    [Theory]
    [InlineData("1001", "2002?fields=id")]
    [InlineData("..%2F..%2Fme", "2002")]
    [InlineData("1001", "2002/messages")]
    [InlineData("1001", "999999999999999999999999999999999999")]
    [InlineData("WABA_1", "2002")]
    public async Task A_meta_id_that_is_not_digits_is_refused_before_any_meta_call(
        string wabaId, string phoneNumberId)
    {
        var seed = await SeedAsync();

        var resp = await seed.Client.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup",
            new { code = "CODE_1", wabaId, phoneNumberId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-embedded-signup-payload");
        seed.Graph.SeenCode.Should().BeNull();
    }

    [Fact]
    public async Task A_long_register_error_is_trimmed_to_fit_its_column()
    {
        var seed = await SeedAsync();
        seed.Graph.Register = GraphResult<bool>.Failure("133005", new string('x', 2_000));

        var resp = await seed.Client.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup", Body);

        // LastError nvarchar(500). Kırpılmasaydı SaveChangesAsync patlardı ve
        // hesap satırı ZATEN yazılmış olduğu için panel, çalışan bir hesap
        // için 500 görürdü. InMemory MaxLength'i uygulamıyor — bu yüzden
        // uzunluk burada elle ölçülüyor.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = seed.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = db.WhatsAppAccounts.Single(a => a.LicenseId == seed.LicenseId);
        row.LastError.Should().StartWith("register: 133005");
        row.LastError!.Length.Should().BeLessThanOrEqualTo(500);
    }

    /// <summary>Numarayı Business App'te yaşıyor gösteren Graph cevabı.</summary>
    private static GraphResult<WhatsAppPhoneNumberInfo> Coexistence =>
        GraphResult<WhatsAppPhoneNumberInfo>.Success(
            new WhatsAppPhoneNumberInfo("+90 555 111 22 33", "Emar Global", "SMB_APP"));

    [Fact]
    public async Task A_coexistence_number_is_never_registered_because_it_already_is()
    {
        var seed = await SeedAsync();
        seed.Graph.Phone = Coexistence;

        var resp = await seed.Client.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Asıl mesele: /register'a HİÇ gidilmemeli. Gidilseydi yayıncının günlük
        // kullandığı telefondaki numaraya yeni bir iki adımlı PIN yazmaya
        // kalkardık — en iyi ihtimalle hata, en kötüsünde yayıncıyı kendi
        // telefonundaki uygulamadan kilitleyen bir değişiklik.
        seed.Graph.SeenPin.Should().BeNull();
        StoredPin(seed).Should().BeNull();

        // Kayıt yerine senkron borcu: Meta onboarding'den sonra 24 saat içinde
        // ikisi de başlatılmazsa müşterinin offboard edilmesini şart koşuyor.
        seed.Graph.SeenSyncTypes.Should().Equal(
            WhatsAppSmbSyncTypes.History, WhatsAppSmbSyncTypes.Contacts);
    }

    [Fact]
    public async Task A_number_without_a_platform_type_is_still_registered()
    {
        var seed = await SeedAsync();

        var resp = await seed.Client.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Meta alanı hiç göndermezse coexistence VARSAYILMIYOR: register'ı
        // gereksiz yere atlamak, kaydı hiç yapılmamış bir numarayı sessizce
        // gönderemez hâlde bırakırdı.
        seed.Graph.SeenPin.Should().NotBeNullOrEmpty();
        seed.Graph.SeenSyncTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_coexistence_sync_is_recorded_but_still_connects()
    {
        var seed = await SeedAsync();
        seed.Graph.Phone = Coexistence;
        seed.Graph.Sync = GraphResult<string>.Failure("100", "Unsupported request");

        var resp = await seed.Client.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup", Body);

        // Senkron tutmasa da hesap ÇALIŞIR: gelen mesajlar akar, gönderim akar.
        // Ölümcül saymak, yalnız arşiv aktarımı yüzünden bağlanmayı iptal etmek
        // olurdu. Kayıp yalnızca geçmiş sohbetler + rehber adları.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // İkincisi birincinin başarısızlığına RAĞMEN denenmeli: rehber senkronu
        // geçmişe bağlı değil, ikisi ayrı ayrı 24 saatlik pencereye tabi.
        seed.Graph.SeenSyncTypes.Should().Equal(
            WhatsAppSmbSyncTypes.History, WhatsAppSmbSyncTypes.Contacts);

        using var scope = seed.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = db.WhatsAppAccounts.Single(a => a.LicenseId == seed.LicenseId);
        row.LastError.Should().StartWith("senkron: ");
        row.LastError.Should().Contain(WhatsAppSmbSyncTypes.History);
        row.LastError.Should().Contain(WhatsAppSmbSyncTypes.Contacts);
    }

    [Fact]
    public async Task An_over_long_display_number_from_meta_fails_at_the_boundary()
    {
        var seed = await SeedAsync();
        seed.Graph.Phone = GraphResult<WhatsAppPhoneNumberInfo>.Success(
            new WhatsAppPhoneNumberInfo(new string('9', 64), "Emar Global"));

        var resp = await seed.Client.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup", Body);

        // DisplayPhoneNumber nvarchar(20). Sınırda durmasaydı /register PIN'i
        // yazdıktan sonra DbUpdateException gelirdi — saklanmayan PIN yine
        // numarayı kilitlerdi. Numara bilerek kanonikleştirilmiyor, yalnız
        // uzunluk bakılıyor.
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await TitleAsync(resp)).Should().Be("whatsapp-phone-read-failed");
        seed.Graph.SeenPin.Should().BeNull();
    }

    [Fact]
    public async Task A_number_owned_by_another_tenant_is_refused_before_any_meta_call()
    {
        var first = await SeedAsync();
        (await first.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // İkinci yayıncı AYNI store'da: ajans birden çok yayıncıyı tek Meta
        // Business altında yönetiyorsa ya da aynı müşterinin iki lisansı varsa
        // bu istek kötü niyet olmadan gelir.
        var second = await SeedTenantAsync(first.Factory);

        // İlk bağlanmanın izlerini sil ki "hiç çağrılmadı" ölçülebilsin.
        first.Graph.SeenCode = null;
        first.Graph.SeenWabaId = null;
        first.Graph.SeenPin = null;

        var resp = await second.Client.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("phone-number-id-taken");

        // Asıl mesele: Graph'a HİÇ gidilmemeli. Gidilseydi 30 saniyelik code
        // yanar, uygulama abone edilir ve /register asıl sahibin numarasına
        // sakladığımız PIN'den BAŞKA bir PIN yazardı — 409'la birlikte attığımız
        // o PIN yüzünden numara bir daha register edilemez, kurtarma yolu
        // yalnız Meta desteği.
        first.Graph.SeenCode.Should().BeNull();
        first.Graph.SeenWabaId.Should().BeNull();
        first.Graph.SeenPin.Should().BeNull();
    }

    [Fact]
    public async Task Without_an_active_license_the_answer_is_a_titled_400()
    {
        var factory = NewFactory();
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("no-active-license");
    }

    [Fact]
    public async Task The_panel_can_read_the_signup_configuration_but_never_the_app_secret()
    {
        var seed = await SeedAsync();

        var resp = await seed.Client.GetAsync("/api/panel/whatsapp/account/signup-config");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("appId", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("configId", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("graphApiVersion", out _).Should().BeTrue();
        // App Secret sunucuda kalır; panele sızarsa herkes tenant token'ı üretebilir.
        doc.RootElement.TryGetProperty("appSecret", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_unconfigured_server_says_so_instead_of_handing_out_empty_ids()
    {
        var seed = await SeedTenantAsync(NewFactory(signupConfigured: false));

        var resp = await seed.Client.GetAsync("/api/panel/whatsapp/account/signup-config");

        // 200 + boş id'ler panelin FB SDK'sını yine de açardı: yayıncı,
        // sebebi sunucu yapılandırması olan anlaşılmaz bir Meta hatası görürdü.
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleAsync(resp)).Should().Be("embedded-signup-not-configured");
    }

    [Fact]
    public async Task One_broadcaster_never_sees_another_ones_connection()
    {
        var first = await SeedAsync();
        (await first.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await SeedTenantAsync(first.Factory);

        var mine = await first.Client.GetAsync("/api/panel/whatsapp/account");
        var theirs = await second.Client.GetAsync("/api/panel/whatsapp/account");

        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        (await mine.Content.ReadAsStringAsync()).Should().Contain("+90 555 111 22 33");

        // Sorgu lisansa göre daraltılmasaydı B, A'nın numarasını ve WABA id'sini
        // görürdü — tek store'da iki yayıncı gerçek üretim şekli.
        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_staff_operator_cannot_touch_the_whatsapp_connection()
    {
        var seed = await SeedAsync();
        var staff = await StaffClientAsync(seed);

        var connect = await staff.PostAsJsonAsync(
            "/api/panel/whatsapp/account/embedded-signup", Body);
        var read = await staff.GetAsync("/api/panel/whatsapp/account");
        var config = await staff.GetAsync("/api/panel/whatsapp/account/signup-config");
        var disconnect = await staff.DeleteAsync("/api/panel/whatsapp/account");

        // Numara bağlamak/koparmak hesap düzeyinde bir karar; personelin
        // yayıncının WhatsApp kimliğini devralmasının yolu olmamalı.
        foreach (var resp in new[] { connect, read, config, disconnect })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await TitleAsync(resp)).Should().Be("owner-only");
        }

        // Asıl mesele 403 değil: personelin isteği Meta'ya HİÇ gitmemeli.
        seed.Graph.SeenCode.Should().BeNull();
        seed.Graph.SeenUnsubscribedWabaId.Should().BeNull();
    }

    [Fact]
    public async Task Disconnecting_unsubscribes_at_meta_and_stops_the_panel_showing_a_number()
    {
        var seed = await SeedAsync();
        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await seed.Client.DeleteAsync("/api/panel/whatsapp/account");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // Satırı yalnız DB'den silmek yetmezdi: uygulamamız WABA'ya abone
        // kalırsa o numaraya gelen mesajlar sessizce webhook'umuza düşmeye
        // devam eder — artık kime ait olduğunu bilmediğimiz hâlde.
        seed.Graph.SeenUnsubscribedWabaId.Should().Be("1001");

        (await seed.Client.GetAsync("/api/panel/whatsapp/account"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disconnecting_keeps_the_row_so_the_pin_survives_a_reconnect()
    {
        var seed = await SeedAsync();
        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPin = seed.Graph.SeenPin;

        (await seed.Client.DeleteAsync("/api/panel/whatsapp/account"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Kopar = satırı sil olsaydı şifreli PIN de giderdi ve yeniden bağlanan
        // yayıncı yeni bir PIN gönderirdi: Meta numaranın MEVCUT PIN'ini istediği
        // için 133005 döner ve numara bir daha register edilemez — kurtarma yolu
        // yalnız Meta desteği.
        seed.Graph.SeenPin.Should().Be(firstPin);
        StoredPin(seed).Should().Be(firstPin);
    }

    [Fact]
    public async Task Disconnecting_clears_the_stored_token_so_nothing_can_send_anymore()
    {
        var seed = await SeedAsync();
        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await seed.Client.DeleteAsync("/api/panel/whatsapp/account"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = seed.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = db.WhatsAppAccounts.Single(a => a.LicenseId == seed.LicenseId);

        // Satır PIN için duruyor; ama token da dursaydı "kopardım" diyen
        // yayıncının numarasından hâlâ mesaj gönderilebilirdi.
        row.Status.Should().Be("disconnected");
        row.DisconnectedAt.Should().NotBeNull();
        row.AccessTokenProtected.Should().BeEmpty();
        row.TwoStepPinProtected.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_failed_meta_unsubscribe_still_disconnects_but_says_what_is_left_over()
    {
        var seed = await SeedAsync();
        (await seed.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        seed.Graph.Unsubscribe = GraphResult<bool>.Failure("190", "Access token has expired.");

        var resp = await seed.Client.DeleteAsync("/api/panel/whatsapp/account");

        // Meta'yı ölümcül saymak yayıncıyı yanlış numarada KİLİTLERDİ: token
        // süresi dolmuşsa abonelik hiçbir zaman kaldırılamaz, yani numara hiçbir
        // zaman koparılamazdı. Yerel kopma her hâlükârda olur, kalan iş söylenir.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().Contain("190");

        (await seed.Client.GetAsync("/api/panel/whatsapp/account"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disconnecting_without_a_connected_number_is_a_404_not_a_crash()
    {
        var seed = await SeedAsync();

        var resp = await seed.Client.DeleteAsync("/api/panel/whatsapp/account");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        seed.Graph.SeenUnsubscribedWabaId.Should().BeNull();
    }

    [Fact]
    public async Task One_broadcaster_can_never_disconnect_another_ones_number()
    {
        var first = await SeedAsync();
        (await first.Client.PostAsJsonAsync("/api/panel/whatsapp/account/embedded-signup", Body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await SeedTenantAsync(first.Factory);

        var resp = await second.Client.DeleteAsync("/api/panel/whatsapp/account");

        // Lisansa daraltılmasaydı B'nin "kopar" düğmesi A'nın canlı hattını
        // düşürürdü — tek store'da iki yayıncı gerçek üretim şekli.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        first.Graph.SeenUnsubscribedWabaId.Should().BeNull();
        (await first.Client.GetAsync("/api/panel/whatsapp/account"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
    }
}
