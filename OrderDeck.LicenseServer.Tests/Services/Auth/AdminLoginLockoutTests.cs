using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

/// <summary>
/// Hesap kilidi testleri.
///
/// <para>Korunan şey oturum güvenliği DEĞİL — parola zaten Argon2id ile
/// doğrulanıyor. Korunan şey KAYNAK: her doğrulama 64 MB bellek ve gözle
/// görülür CPU harcıyor ve bu maliyet yalnız kullanıcı adı var olduğunda
/// ödeniyor. IP başına oran sınırı bu toplamı bağlamıyor (saldırgan IP
/// döndürür); hesap düzeyinde kilit bağlıyor.</para>
/// </summary>
public sealed class AdminLoginLockoutTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminLoginLockoutTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Kimlik bilgileri çalışma anında üretiliyor; kaynakta parolaya benzeyen
    /// sabit bir dizge BIRAKILMIYOR.
    ///
    /// <para>Sır tarayıcısı bir test fixture'ıyla gerçek bir kimlik bilgisini
    /// ayırt edemiyor; ikisi de ona aynı görünüyor. Repo public olduğu için
    /// uyarıyı allowlist'le susturmanın bedeli ağır: aynı kural, test
    /// dosyasına yanlışlıkla yapıştırılmış GERÇEK bir anahtarı da görünmez
    /// yapar. Sabit değeri hiç yazmamak hem uyarıyı kaynağında bitiriyor hem
    /// tarayıcıyı keskin bırakıyor.</para>
    /// </summary>
    private static string NewCredential(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Doğrulanamayacak bir parola. Değeri önemsiz, yalnız seed
    /// edilenden farklı olması gerekiyor.</summary>
    private static readonly string Wrong = NewCredential("wrong");

    private async Task<(string Username, string Password)> SeedAdminAsync()
    {
        var username = NewCredential("admin");
        var password = NewCredential("pw");
        await AdminLoginHelper.EnsureAdminSeededAsync(_factory, username, password);
        return (username, password);
    }

    /// <summary>Her deneme kendi scope'unda: üretimde her istek yeni bir
    /// DbContext görüyor, aynı scope'da izlenen varlığı yeniden kullanmak
    /// sayacın gerçekten kalıcı olduğunu test etmezdi.</summary>
    private async Task<AdminLoginService.Result> AuthenticateAsync(string username, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AdminLoginService>();
        return await svc.AuthenticateAsync(username, password, CancellationToken.None);
    }

    private async Task<AdminUser> ReadAdminAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.AdminUsers.AsNoTracking().SingleAsync(a => a.Username == username);
    }

    private async Task MutateAdminAsync(string username, Action<AdminUser> mutate)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var admin = await db.AdminUsers.SingleAsync(a => a.Username == username);
        mutate(admin);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Bes_hatali_denemeden_sonra_hesap_kilitleniyor()
    {
        var (username, _) = await SeedAdminAsync();

        for (var i = 0; i < 5; i++)
        {
            var r = await AuthenticateAsync(username, Wrong);
            r.Outcome.Should().Be(AdminLoginService.Outcome.InvalidCredentials,
                "eşiğe ulaşana kadar sonuç kilit değil, geçersiz kimlik olmalı");
        }

        var sixth = await AuthenticateAsync(username, Wrong);
        sixth.Outcome.Should().Be(AdminLoginService.Outcome.LockedOut);
        sixth.LockedUntil.Should().NotBeNull();
    }

    [Fact]
    public async Task Kilitli_hesapta_DOGRU_parola_bile_dogrulanmiyor()
    {
        // Asıl iddia bu: kilit "yanlış parolayı reddet" değil, "parolaya hiç
        // bakma" anlamına geliyor. Argon2 çağrısı kilit kontrolünden SONRA
        // olduğu için doğru parolanın da geri çevrilmesi, pahalı yolun
        // gerçekten atlandığının gözlemlenebilir kanıtı.
        var (username, password) = await SeedAdminAsync();
        for (var i = 0; i < 5; i++) await AuthenticateAsync(username, Wrong);

        var result = await AuthenticateAsync(username, password);

        result.Outcome.Should().Be(AdminLoginService.Outcome.LockedOut);
        result.Admin.Should().BeNull();
    }

    [Fact]
    public async Task Kilit_suresi_dolunca_dogru_parola_yeniden_geciyor()
    {
        var (username, password) = await SeedAdminAsync();
        for (var i = 0; i < 5; i++) await AuthenticateAsync(username, Wrong);
        await MutateAdminAsync(username, a => a.LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await AuthenticateAsync(username, password);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Kilit_suresi_dolsa_bile_sayac_korunuyor_ve_kilit_tirmaniyor()
    {
        // Sayaç kilit bitiminde sıfırlansaydı tırmanma hiç devreye girmezdi:
        // saldırgan her turda 5 deneme + 1 dakika bekleme döngüsüne girer,
        // maliyet sabit kalırdı. Testin bakması gereken şey ikinci kilidin
        // BİRİNCİDEN UZUN olması.
        var (username, _) = await SeedAdminAsync();
        for (var i = 0; i < 5; i++) await AuthenticateAsync(username, Wrong);
        var firstLock = (await ReadAdminAsync(username)).LockedOutUntil!.Value;
        var firstDuration = firstLock - DateTimeOffset.UtcNow;

        await MutateAdminAsync(username, a => a.LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(-1));
        var next = await AuthenticateAsync(username, Wrong);

        next.Outcome.Should().Be(AdminLoginService.Outcome.InvalidCredentials);
        var admin = await ReadAdminAsync(username);
        admin.FailedLoginAttempts.Should().Be(6, "kilit dolunca sayaç sıfırlanmamalı");
        (admin.LockedOutUntil!.Value - DateTimeOffset.UtcNow)
            .Should().BeGreaterThan(firstDuration, "ikinci kilit birinciden uzun olmalı");
    }

    [Fact]
    public async Task Kilit_suresi_on_bes_dakikada_tavan_yapiyor()
    {
        // 2^(n-5) dakika: n=9'da 16 dakika eder, tavan devreye girer.
        // Tavan olmasaydı ısrarlı bir saldırgan hesabı günlerce kilitleyebilirdi
        // — kilidin kendisi hizmet dışı bırakma aracına dönüşürdü.
        var (username, _) = await SeedAdminAsync();
        await MutateAdminAsync(username, a => a.FailedLoginAttempts = 20);

        await AuthenticateAsync(username, Wrong);

        var admin = await ReadAdminAsync(username);
        (admin.LockedOutUntil!.Value - DateTimeOffset.UtcNow)
            .Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Basarili_giris_sayaci_ve_kilidi_temizliyor()
    {
        var (username, password) = await SeedAdminAsync();
        for (var i = 0; i < 3; i++) await AuthenticateAsync(username, Wrong);

        var result = await AuthenticateAsync(username, password);

        result.IsSuccess.Should().BeTrue();
        var admin = await ReadAdminAsync(username);
        admin.FailedLoginAttempts.Should().Be(0);
        admin.LockedOutUntil.Should().BeNull();
        admin.LastLoginAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Bilinmeyen_kullanici_adi_kilit_durumu_uretmiyor()
    {
        var result = await AuthenticateAsync(NewCredential("yok"), Wrong);

        result.Outcome.Should().Be(AdminLoginService.Outcome.InvalidCredentials);
        result.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Razor_sayfasindan_kazanilan_kilit_API_kapisini_da_kapatiyor()
    {
        // Aynı kimlik bilgisine iki kapı açılıyor. Kilidi yalnız birine koymak
        // kilit değil tabela olurdu: saldırgan diğer kapıdan aynı hesaba
        // sınırsız Argon2 tetiklemeye devam ederdi. Bu test iki kapının TEK
        // servisi paylaştığının uçtan uca kanıtı.
        var (username, password) = await SeedAdminAsync();

        var page = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        for (var i = 0; i < 5; i++)
        {
            var getResp = await page.GetAsync("/admin/login");
            var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Username"] = username,
                ["Input.Password"] = Wrong
            });
            (await page.PostAsync("/admin/login", form)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var api = _factory.CreateClient();
        var apiResp = await api.PostAsJsonAsync("/api/v1/admin/auth/login",
            new { username, password });

        apiResp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Kilitli_hesapta_giris_sayfasi_kalan_sureyi_soyluyor()
    {
        var (username, password) = await SeedAdminAsync();
        for (var i = 0; i < 5; i++) await AuthenticateAsync(username, Wrong);

        var client = _factory.CreateClient();
        var getResp = await client.GetAsync("/admin/login");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = password
        });
        var postResp = await client.PostAsync("/admin/login", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var alert = AdminLoginHelper.ExtractAlertText(await postResp.Content.ReadAsStringAsync());
        alert.Should().Contain("Çok fazla hatalı deneme");
        alert.Should().NotContain("Geçersiz kullanıcı adı");
    }
}
