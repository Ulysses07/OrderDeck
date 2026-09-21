using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Yayıncının kendi Netgsm/İYS kurulum ucu. Üç değişmez korunuyor:
/// (1) API şifresi panele ASLA dönmez, (2) uç owner-only — staff operatör
/// yayıncının SMS kimliklerini göremez/değiştiremez, (3) sorgu kiracıya
/// bağlı — bir yayıncı komşusunun Netgsm kimliklerini göremez.
/// </summary>
public sealed class PanelNetgsmAccountControllerTests : IDisposable
{
    // Her test KENDİ fabrikasını kurar (= kendi InMemory veritabanı).
    // Paylaşılan bir `IClassFixture` olmaz: `NetgsmAccount.BrandCode` GLOBAL
    // tekil, `LicenseId` de tekil (LicenseDbContext, `NetgsmAccount` eşlemesi).
    // Tek veritabanında biriken satırlar hem rastgele üretilen marka kodlarını
    // çakışmaya açar hem de kiracı izolasyonu testini komşu testlerin
    // satırlarına bağımlı kılar. Test başına bir fabrikanın bedeli bu
    // belirsizlikten ucuz.
    private readonly List<ApiFactory> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private ApiFactory NewFactory()
    {
        var f = new ApiFactory();
        _factories.Add(f);
        return f;
    }

    private sealed record Seed(HttpClient Client, Guid LicenseId);

    private static async Task<Seed> SeedTenantAsync(ApiFactory factory)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-PNA-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        return new Seed(client, license.Id);
    }

    /// <summary>Seed edilen satırın panelde görünmesi gereken değerleri.
    /// <paramref name="RawPassword"/> <b>düz metin şifredir</b> — şifre sızıntısı
    /// testi yanıtta gerçek sırrı arayabilsin diye döndürülüyor.</summary>
    private sealed record SeededAccount(
        string RawPassword,
        string UserCode,
        string Header,
        string BrandCode);

    /// <summary>Hesabı seed eder ve yazdığı değerleri döndürür. Değerler sabit
    /// değil, satır başına üretiliyor: görünümün alanları gerçekten o satırdan
    /// geliyor mu yoksa sabit mi dönüyor, ancak böyle ayırt edilebilir.</summary>
    private static async Task<SeededAccount> SeedAccountAsync(
        ApiFactory factory, Guid licenseId, NetgsmAccountStatus status,
        string? lastError = null, DateTimeOffset? lastVerifiedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var rawPassword = $"pw-{Guid.NewGuid():N}";
        // Başlık en fazla 11 karakter (Netgsm sınırı, kolonda da öyle eşlenmiş).
        var seeded = new SeededAccount(
            RawPassword: rawPassword,
            UserCode: Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            Header: $"OD{Random.Shared.Next(100_000, 999_999)}",
            BrandCode: Random.Shared.Next(100_000, 999_999).ToString());
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = seeded.UserCode,
            PasswordProtected = protector.ProtectPassword(rawPassword),
            Header = seeded.Header,
            BrandCode = seeded.BrandCode,
            Status = status,
            LastError = lastError,
            LastVerifiedAt = lastVerifiedAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return seeded;
    }

    /// <summary>ProblemDetails gövdesinden <c>title</c> alanını okur.
    /// (Kalıp: <c>PanelWhatsAppAccountControllerTests.TitleAsync</c>.)</summary>
    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    [Fact]
    public async Task Hesap_yoksa_bos_gorunum_doner()
    {
        var seed = await SeedTenantAsync(NewFactory());

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "kurulumu olmayan yayıncı 404 değil BOŞ form görmeli");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("none");
        doc.RootElement.GetProperty("passwordSet").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Sifre_panele_DONMEZ()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        var rawPassword = (await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Verified)).RawPassword;

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");
        var body = await resp.Content.ReadAsStringAsync();

        // 1) Gerçek sır gövdede olmamalı — ham da olsa şifrelenmiş hâliyle de.
        body.Should().NotContain(rawPassword, "düz metin şifre panele dönmez");

        using var doc = JsonDocument.Parse(body);

        // 2) `passwordSet` DIŞINDA şifreye benzeyen alan adı olmamalı.
        //    Ham metinde "assword" aramak işe yaramaz: `passwordSet`'in kendisi
        //    o dizgiyi içerir, test hiçbir doğru DTO ile yeşile dönemezdi.
        var leakyFields = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n.Contains("password", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals("passwordSet", StringComparison.OrdinalIgnoreCase))
            .ToList();
        leakyFields.Should().BeEmpty("yalnız passwordSet bayrağı dönebilir");

        doc.RootElement.GetProperty("passwordSet").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("status").GetString().Should().Be("verified");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Failed_hesapta_sms_kapali_ve_lastError_gorunur()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Failed,
            lastError: "İYS marka kodu bu Netgsm hesabına ait değil (kod 60).");

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("lastError").GetString().Should().Contain("kod 60");
    }

    [Fact]
    public async Task Disabled_hesapta_durum_disabled_ve_sms_kapali()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Disabled);

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("disabled",
            "emekliye ayrılmış kimlik panelde 'failed' görünürse yanlış teşhis olur — "
            + "yayıncı çalışan bir şifreyi tekrar tekrar girip durur");
        doc.RootElement.GetProperty("smsEnabled").GetBoolean().Should().BeFalse(
            "gönderim kapısı yalnız Verified'da açılır");
    }

    [Fact]
    public async Task Gorunum_form_alanlarini_satirdan_doner()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        var verifiedAt = DateTimeOffset.UtcNow.AddHours(-3);
        var acc = await SeedAccountAsync(
            factory, seed.LicenseId, NetgsmAccountStatus.Verified,
            lastVerifiedAt: verifiedAt);

        var resp = await seed.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("userCode").GetString().Should().Be(acc.UserCode,
            "panel formu bu alanlarla doluyor; boş dönen bir görünüm yayıncıya "
            + "kurulumu hiç yapılmamış gibi görünür");
        doc.RootElement.GetProperty("header").GetString().Should().Be(acc.Header,
            "İYS markası Netgsm tarafında BAŞLIKTAN çözülüyor — yanlış/boş başlık "
            + "yayıncının onaylarını başka bir markaya yazdırır");
        doc.RootElement.GetProperty("brandCode").GetString().Should().Be(acc.BrandCode);

        // Önce ValueKind: doğrudan `GetDateTimeOffset()` çağırmak, alan null
        // dönerse "element of type 'String'... has type 'Null'" diye opak bir
        // InvalidOperationException fırlatır — düşen testi okuyan kişi asıl
        // sorunun alanın boş dönmesi olduğunu göremez.
        var lastVerified = doc.RootElement.GetProperty("lastVerifiedAt");
        lastVerified.ValueKind.Should().NotBe(JsonValueKind.Null,
            "günlük yeniden doğrulama işinin izi panelde görünmeli");
        lastVerified.GetDateTimeOffset().Should()
            .BeCloseTo(verifiedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Baska_kiracinin_hesabini_GORMEZ()
    {
        var factory = NewFactory();
        var a = await SeedTenantAsync(factory);
        var b = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, b.LicenseId, NetgsmAccountStatus.Verified);

        var resp = await a.Client.GetAsync("/api/panel/netgsm/account");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("none",
            "B'nin kimlikleri A'nın panelinde görünmemeli");
        doc.RootElement.GetProperty("userCode").ValueKind.Should().Be(JsonValueKind.Null,
            "kiracı filtresi düşerse yanıt komşunun Netgsm abone numarasını taşır");
    }

    [Fact]
    public async Task Staff_operator_goremez()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);
        var staff = await PanelOperatorHelper.StaffClientAsync(factory, seed.Client);

        var resp = await staff.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "SMS kimlikleri yayıncının kendi faturalı hesabı — staff görmez");
    }

    [Fact]
    public async Task Aktif_lisansi_olmayan_musteri_400_alir()
    {
        var factory = NewFactory();
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var resp = await client.GetAsync("/api/panel/netgsm/account");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ayna_baslatma_dogrulanmis_hesapta_202()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);

        var resp = await seed.Client.PostAsync("/api/panel/netgsm/account/iys-mirror", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // 202 bir vaattir — iş gerçekten Hangfire kuyruğuna girmeli. ApiFactory
        // MemoryStorage kullanıyor ve sunucu koşmuyor: Enqueue edilen iş
        // "enqueued" durumda bekler, monitoring API'den okunabilir.
        var monitoring = factory.Services.GetRequiredService<Hangfire.JobStorage>().GetMonitoringApi();
        monitoring.EnqueuedJobs("default", 0, 1000).Should().Contain(j =>
            j.Value.Job.Type == typeof(IysMirrorImportJob)
            && j.Value.Job.Args.Contains((object)seed.LicenseId),
            "202 bir vaattir — iş gerçekten kuyruğa girmeli");
    }

    [Fact]
    public async Task Ayna_dogrulanmamis_hesapta_409()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Failed);

        var resp = await seed.Client.PostAsync("/api/panel/netgsm/account/iys-mirror", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var monitoring = factory.Services.GetRequiredService<Hangfire.JobStorage>().GetMonitoringApi();
        monitoring.EnqueuedJobs("default", 0, 1000).Should().NotContain(j =>
            j.Value.Job.Type == typeof(IysMirrorImportJob)
            && j.Value.Job.Args.Contains((object)seed.LicenseId),
            "kapı yalnız status'u değil yan etkiyi de engellemeli");
    }

    [Fact]
    public async Task Ayna_staff_operatore_kapali_403()
    {
        var factory = NewFactory();
        var seed = await SeedTenantAsync(factory);
        await SeedAccountAsync(factory, seed.LicenseId, NetgsmAccountStatus.Verified);
        var staff = await PanelOperatorHelper.StaffClientAsync(factory, seed.Client);

        var resp = await staff.PostAsync("/api/panel/netgsm/account/iys-mirror", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleAsync(resp)).Should().Be("owner-only",
            "403'ün kaynağı OwnerOnly() olmalı — ileride araya girecek başka bir " +
            "filtre bu kodu sessizce üstlenmemeli");

        var monitoring = factory.Services.GetRequiredService<Hangfire.JobStorage>().GetMonitoringApi();
        monitoring.EnqueuedJobs("default", 0, 1000).Should().NotContain(j =>
            j.Value.Job.Type == typeof(IysMirrorImportJob)
            && j.Value.Job.Args.Contains((object)seed.LicenseId),
            "kapı yalnız status'u değil yan etkiyi de engellemeli");
    }

    [Fact]
    public async Task Ayna_aktif_lisans_yoksa_400()
    {
        var factory = NewFactory();
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);

        var resp = await client.PostAsync("/api/panel/netgsm/account/iys-mirror", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("no-active-license");
    }
}
