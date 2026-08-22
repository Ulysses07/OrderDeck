using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Bayat WhatsApp rezervasyonunun <b>devralınmasının</b> atomik olduğunu
/// doğrular.
///
/// <para><b>Kusur neydi:</b> idempotency satırı iki dakika boyunca "pending"
/// kaldıysa terk edilmiş sayılıp devralınıyordu. Devralma düz bir okuma +
/// yazmaydı: iki yeniden-deneme aynı bayat satırı okuyup ikisi de kendini
/// sahibi ilan edebiliyordu. Birincil anahtar yalnız yeni satır EKLEME yarışını
/// kapatır, var olan satırın UPDATE'ini değil. Sonuç: aynı <b>faturalı</b>
/// mesaj müşteriye iki kez gider ve idempotency kaydı hiçbir şey ifade
/// etmez — var olma sebebi tam olarak bunu önlemekti.</para>
///
/// <para><b>Neden gerçek SQL Server:</b> InMemory sağlayıcısında eşzamanlılık
/// yok; iki istek aynı satırı okuyup yazsa bile yarışmazlar. Bu hata orada
/// hiçbir zaman görünmez, test yeşil yanar ve para yolundaki açık açık
/// kalır.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class WhatsAppSendTakeoverConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public WhatsAppSendTakeoverConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Bayat_rezervasyonu_esazamanli_devralan_isteklerden_yalniz_biri_gonderir()
    {
        var (jwt, licenseId) = await SeedAsync();
        const string phone = "905551110101";
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, phone);

        // Beş dakikalık "pending": eşiğin (2 dk) üstünde, yani her istek bu
        // satırı devralınabilir görüyor.
        var key = Guid.NewGuid();
        await SeedStaleAttemptAsync(licenseId, key, DateTimeOffset.UtcNow.AddMinutes(-5));

        var responses = await PostSameKeyInParallelAsync(jwt, licenseId, phone, key);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK,
            "devralmayı kaybetmek istemci hatası değil; kaybeden kazananın sonucunu okur");

        (await CountMessagesAsync(licenseId)).Should().Be(1,
            "devralmayı yalnız bir istek kazanmalı — her kazanan bir FATURALI mesaj demek");
    }

    [Fact]
    public async Task Esazamanli_devralmada_tum_yanitlar_ayni_mesaji_isaret_eder()
    {
        var (jwt, licenseId) = await SeedAsync();
        const string phone = "905551110102";
        await ConnectAccountAsync(licenseId);
        await OpenServiceWindowAsync(licenseId, phone);

        var key = Guid.NewGuid();
        await SeedStaleAttemptAsync(licenseId, key, DateTimeOffset.UtcNow.AddMinutes(-5));

        var responses = await PostSameKeyInParallelAsync(jwt, licenseId, phone, key);

        // DB'deki mesaj sayısını değil, İSTEMCİNİN gördüğünü ölçüyor: her
        // kazanan kendi MessageId'sini döndürür, dolayısıyla birden fazla farklı
        // kimlik "birden fazla mesaj gitti" demektir. Kaybedenler ya kazananın
        // kimliğini ya da (henüz damgalanmadıysa) in_progress + null alır.
        var bodies = await Task.WhenAll(
            responses.Select(r => r.Content.ReadFromJsonAsync<SendResponse>()));
        var ids = bodies.Select(b => b!.MessageId).Where(id => id is not null).Distinct().ToArray();

        ids.Should().HaveCount(1,
            "aynı idempotency anahtarı tek bir mesaja karşılık gelmeli");

        // Satır kapanmış olmalı: "pending" kalsaydı iki dakika sonra yeniden
        // devralınabilir hale gelir ve mesaj bir kez daha giderdi.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.WaSendAttempts.AsNoTracking().SingleAsync(a => a.LicenseId == licenseId);
        row.Status.Should().Be("done");
        row.Ok.Should().BeTrue();
        row.MessageId.Should().Be(ids[0]);
    }

    private sealed record SendResponse(bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    // ── Belirteçlerin kendisi ───────────────────────────────────────────────
    //
    // Yukarıdaki iki test uçtan uca davranışı ölçüyor ama hangi kolonun
    // koruduğunu göstermiyor. Aşağıdaki ikisi doğrudan mekanizmayı ölçüyor:
    // ikisi de gerçek bir UPDATE cümlesinin WHERE'ine bakıyor.

    [Fact]
    public async Task Iki_devralmadan_ikincisi_eszamanlilik_hatasi_alir()
    {
        var (_, licenseId) = await SeedAsync();
        var key = Guid.NewGuid();
        await SeedStaleAttemptAsync(licenseId, key, DateTimeOffset.UtcNow.AddMinutes(-5));

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // İkisi de AYNI bayat hâli okuyor — sahadaki iki yeniden-deneme gibi.
        var rowA = await dbA.WaSendAttempts.SingleAsync(a => a.Id == key);
        var rowB = await dbB.WaSendAttempts.SingleAsync(a => a.Id == key);

        rowA.StartedAt = DateTimeOffset.UtcNow;
        await dbA.SaveChangesAsync();

        rowB.StartedAt = DateTimeOffset.UtcNow;
        var save = () => dbB.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "StartedAt eşzamanlılık belirteci olmasaydı ikisi de kazanır ve iki mesaj giderdi");
    }

    [Fact]
    public async Task Devralma_arasinda_sonuc_yazilirsa_devralma_reddedilir()
    {
        // StartedAt tek başına yetmiyor: bu senaryoda kazanan taraf StartedAt'i
        // HİÇ değiştirmiyor, yalnız Status'ü pending→done yapıyor. Status de
        // belirteç olmasaydı devralan yine kazanır ve ikinci mesajı gönderirdi.
        var (_, licenseId) = await SeedAsync();
        var key = Guid.NewGuid();
        await SeedStaleAttemptAsync(licenseId, key, DateTimeOffset.UtcNow.AddMinutes(-5));

        using var scopeSlow = _factory.Services.CreateScope();
        using var scopeTakeover = _factory.Services.CreateScope();
        var dbSlow = scopeSlow.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var dbTakeover = scopeTakeover.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var slowRow = await dbSlow.WaSendAttempts.SingleAsync(a => a.Id == key);
        var takeoverRow = await dbTakeover.WaSendAttempts.SingleAsync(a => a.Id == key);

        // İki dakikayı aşmış ama hâlâ hayatta olan ilk gönderim sonucunu yazıyor.
        slowRow.Status = "done";
        slowRow.Ok = true;
        slowRow.CompletedAt = DateTimeOffset.UtcNow;
        await dbSlow.SaveChangesAsync();

        takeoverRow.StartedAt = DateTimeOffset.UtcNow;
        var save = () => dbTakeover.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "tamamlanmış bir rezervasyon devralınamaz — mesaj çoktan gitti");
    }

    /// <summary>
    /// Aynı idempotency anahtarını N istemciden aynı anda gönderir.
    ///
    /// Isınma turu şart: yönlendirme, model bağlama, kimlik doğrulama ve EF
    /// sorguları ilk çağrıda derleniyor ve bu bedel gerçek turda istekleri
    /// birbirinden ayırıp yarış penceresini kapatıyor. Isınma başka bir lisans
    /// kimliğine gidiyor — sahiplik kontrolünde 404 ile dönüyor, yani hiçbir
    /// şey göndermeden tüm boru hattını ısıtıyor.
    /// </summary>
    private async Task<HttpResponseMessage[]> PostSameKeyInParallelAsync(
        string jwt, Guid licenseId, string phone, Guid key)
    {
        var clients = Enumerable.Range(0, ParallelAttempts)
            .Select(_ =>
            {
                var c = _factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                return c;
            })
            .ToArray();

        var warmupUrl = Url(Guid.NewGuid());
        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(warmupUrl, new
        {
            toPhone = phone, text = "ısınma", idempotencyKey = Guid.NewGuid(),
        })));

        var body = new { toPhone = phone, text = "devralma yarışı", idempotencyKey = key };
        return await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(Url(licenseId), body)));
    }

    private static string Url(Guid licenseId) => $"/api/v1/licenses/{licenseId}/whatsapp/send";

    private async Task<(string Jwt, Guid LicenseId)> SeedAsync()
    {
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-WAC-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (jwt, license.Id);
    }

    private async Task ConnectAccountAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<WhatsAppAccountService>();
        db.WhatsAppAccounts.Add(new WhatsAppAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WabaId = "waba-1",
            PhoneNumberId = $"pnid-{Guid.NewGuid():N}",
            DisplayPhoneNumber = "905550000000",
            AccessTokenProtected = accounts.ProtectToken("token-1234"),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task OpenServiceWindowAsync(Guid licenseId, string phone)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerPhone = phone,
            PhoneNumberId = "pnid-open",
            Status = "open",
            LastInboundAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedStaleAttemptAsync(Guid licenseId, Guid key, DateTimeOffset startedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WaSendAttempts.Add(new WaSendAttempt
        {
            Id = key,
            LicenseId = licenseId,
            Status = "pending",
            StartedAt = startedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> CountMessagesAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaMessages.CountAsync(m => m.LicenseId == licenseId);
    }
}
