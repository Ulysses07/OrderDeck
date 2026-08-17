using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// Bulgu 5.1 — kimlik devralma.
///
/// Bir shopper, yayıncının müşteri kaydına yalnızca (Platform, Kullanıcı adı)
/// beyan ederek bağlanabiliyordu. Bu bilgi ürünün kendi tasarımı gereği herkese
/// açık: izleyici sipariş vermek için sohbete ürün kodunu yazar, kullanıcı adı
/// da yanında görünür. Yani saldırgan sohbetten bir ad alıp o adla kaydolunca
/// kurbanın sipariş geçmişini ve bakiye/hesap hareketlerini okuyabiliyordu.
///
/// Testler saldırıyı taklit ederek yazıldı: kurbanın verisi gerçekten
/// tohumlanıyor ve saldırganın ucu görmediği doğrulanıyor. "Link kuruldu mu"
/// gibi bir iç ayrıntıyı değil, sızan verinin kendisini ölçmelerinin nedeni bu
/// — bağlantı mekaniği değişse bile testler anlamını koruyor.
/// </summary>
public class ShopperIdentityTakeoverTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperIdentityTakeoverTests(ApiFactory factory) => _factory = factory;

    private sealed record RegisterRequest(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username,
        string? Email = null, string? Tc = null);

    private sealed record AuthResponse(
        string AccessToken, DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken, DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId, object[] Broadcasters);

    private sealed record OrderItem(
        Guid Id, Guid? SessionId, string? SessionTitle, string Platform,
        string MessageText, string? Code, decimal Price, DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt, DateTimeOffset? CancelledAt, bool IsShippingFee);

    private sealed record OrdersResponse(OrderItem[] Items, string? NextCursor);

    private const string Platform = "youtube";
    private const string VictimUsername = "kurban_izleyici";

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<(Guid LicenseId, string Code)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"tko-{Guid.NewGuid():N}@x.test",
            Name = "Tko-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "tko-" + Guid.NewGuid().ToString("N")[..8];
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, code);
    }

    /// <summary>
    /// Yayıncının defterindeki gerçek müşteri: bir sipariş ve bir bakiye kaydıyla.
    /// <paramref name="phone"/> null verilirse yayıncı telefonu hiç kaydetmemiş
    /// demektir (sohbetten gelen, form doldurmamış müşteri).
    /// </summary>
    private async Task<Guid> SeedVictimCustomerAsync(Guid licenseId, string? phone)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var wpfId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfId,
            LicenseId = licenseId,
            Platform = Platform,
            Username = VictimUsername,
            FullName = "Kurban Müşteri",
            Phone = phone,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = wpfId.ToString("N"),
            Platform = Platform,
            Username = VictimUsername,
            MessageText = "kurbanın gizli siparişi",
            Price = 250m,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.CustomerBalances.Add(new CustomerBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WpfCustomerId = wpfId,
            Balance = 1234m,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return wpfId;
    }

    private async Task<(string Token, Guid ShopperId)> RegisterAsync(
        HttpClient client, string code, string phone, string username,
        HttpStatusCode expected = HttpStatusCode.Created)
    {
        var req = new RegisterRequest(
            code, "Kayıt Olan", phone, "Password1!", "İstanbul", Platform, username);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(expected);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    // ── Saldırı ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Baskasinin_kullanici_adiyla_kaydolan_kurbanin_verisini_goremez()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var victimPhone = UniquePhone();
        await SeedVictimCustomerAsync(licenseId, victimPhone);

        // Saldırgan sohbette gördüğü kullanıcı adını yazıyor, ama telefonu kendi.
        var (token, _) = await RegisterAsync(client, code, UniquePhone(), VictimUsername);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orders = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders");
        orders.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await orders.Content.ReadFromJsonAsync<OrdersResponse>();
        body!.Items.Should().BeEmpty("kullanıcı adı herkese açık; tek başına kurbanın siparişlerini açmamalı");

        var balance = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/balance");
        balance.StatusCode.Should().Be(HttpStatusCode.NotFound, "bakiye de aynı kanıtsız bağlantıdan besleniyordu");
    }

    [Fact]
    public async Task Kanitsiz_kayit_yayincinin_defterinde_taklit_satir_acmaz()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        await SeedVictimCustomerAsync(licenseId, UniquePhone());

        await RegisterAsync(client, code, UniquePhone(), VictimUsername);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && p.Username == VictimUsername)
            .ToListAsync();

        rows.Should().ContainSingle(
            "kanıt gelmediğinde otomatik projeksiyon açılsaydı, yayıncının müşteri "
            + "listesinde gerçek müşteriyi taklit eden ikinci bir kayıt belirirdi");
    }

    // ── Meşru kullanım ──────────────────────────────────────────────────────

    [Fact]
    public async Task Telefonu_eslesen_gercek_musteri_kendi_verisini_gorur()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var victimPhone = UniquePhone();
        await SeedVictimCustomerAsync(licenseId, victimPhone);

        var (token, _) = await RegisterAsync(client, code, victimPhone, VictimUsername);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orders = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders");
        var body = await orders.Content.ReadFromJsonAsync<OrdersResponse>();
        body!.Items.Should().ContainSingle().Which.MessageText.Should().Be("kurbanın gizli siparişi");
    }

    [Fact]
    public async Task Telefon_farkli_yazilmis_olsa_da_eslesir()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        // Yayıncı defterinde eski, normalize edilmemiş biçim; shopper E.164 yazıyor.
        var digits = Random.Shared.Next(10_000_000, 99_999_999).ToString();
        await SeedVictimCustomerAsync(licenseId, $"055{digits[..1]} {digits[1..4]} {digits[4..]}");

        var (token, _) = await RegisterAsync(client, code, $"+9055{digits}", VictimUsername);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orders = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders");
        var body = await orders.Content.ReadFromJsonAsync<OrdersResponse>();
        body!.Items.Should().ContainSingle(
            "karşılaştırma iki tarafı da normalize etmezse serbest metin dönemindeki "
            + "satırlar sessizce eşleşmez ve meşru müşteri verisini göremez");
    }

    // ── Kurtarma yolu ───────────────────────────────────────────────────────

    [Fact]
    public async Task Yayinci_telefonu_sonradan_girince_bekleyen_baglanti_kurulur()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        // Yayıncının defterinde telefon yok → kanıt yok → bağlantı beklemede.
        var wpfId = await SeedVictimCustomerAsync(licenseId, phone: null);
        var shopperPhone = UniquePhone();
        var (token, shopperId) = await RegisterAsync(client, code, shopperPhone, VictimUsername);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/balance"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "önce beklemede olmalı");

        // Yayıncı WPF'te telefonu giriyor; sync onu sunucuya taşıyor.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var projection = await db.WpfCustomerProjections.FirstAsync(p => p.Id == wpfId);
            projection.Phone = shopperPhone;
            await db.SaveChangesAsync();

            var link = await db.ShopperBroadcasterLinks
                .FirstAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId);
            link.WpfCustomerId.Should().BeNull("kayıt anında kanıt yoktu");
        }

        await RunRetroactiveMatchAsync(licenseId, wpfId, shopperPhone);

        (await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/balance"))
            .StatusCode.Should().Be(HttpStatusCode.OK, "telefon girildikten sonra bağlantı kurulmalı");
    }

    [Fact]
    public async Task Geriye_donuk_eslestirme_telefon_uyusmuyorsa_baglamaz()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var wpfId = await SeedVictimCustomerAsync(licenseId, phone: null);

        var (token, _) = await RegisterAsync(client, code, UniquePhone(), VictimUsername);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Yayıncı GERÇEK müşterinin telefonunu giriyor — saldırganınkiyle uyuşmuyor.
        await RunRetroactiveMatchAsync(licenseId, wpfId, UniquePhone());

        (await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/balance"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "kapı burada olmasaydı kayıt/katılma düzeltmeleri boşa giderdi: beklemedeki "
                + "bağlantı ilk WPF sync'inde sessizce bağlanırdı");
    }

    /// <summary>
    /// WPF sync ucunu gerçek HTTP üzerinden çağırır — geriye dönük eşleştirme
    /// orada yaşıyor ve testin doğruladığı şey tam olarak o kod yolu.
    /// </summary>
    private async Task RunRetroactiveMatchAsync(Guid licenseId, Guid wpfId, string phone)
    {
        var (client, callerCustomerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            // Lisansı, sync ucunu çağıran kimliğe devret: uç yalnızca kendi
            // lisansına yazmaya izin veriyor (sahiplik kontrolü ayrıca test edilmiş).
            var license = await db.Licenses.FirstAsync(l => l.Id == licenseId);
            license.CustomerId = callerCustomerId;
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                Customers = new[]
                {
                    new
                    {
                        Id = wpfId,
                        Platform,
                        Username = VictimUsername,
                        FullName = "Kurban Müşteri",
                        Phone = phone,
                        Address = (string?)null,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    }
                }
            });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
