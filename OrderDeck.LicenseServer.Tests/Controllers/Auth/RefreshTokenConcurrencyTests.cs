using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Auth;

/// <summary>
/// Refresh token rotasyonunun gerçekten <b>tek kullanımlık</b> olduğunu
/// doğrular.
///
/// Bu testler neden gerçek SQL Server istiyor: InMemory sağlayıcısında
/// eşzamanlılık semantiği yok — iki istek aynı satırı okuyup yazsa bile
/// yarışmazlar. Yani buradaki hata InMemory'de <b>hiçbir zaman</b>
/// görünmez; test yeşil yanar, üretimdeki açık kapalı kalmaz.
///
/// Neden önemli: tek kullanımlık rotasyonun tek varlık sebebi token
/// hırsızlığını yakalamaktır. Çalınan token kullanıldığında meşru
/// kullanıcının bir sonraki yenilemesi patlamalı ve hırsızlık böylece
/// açığa çıkmalıdır. İki eşzamanlı yenileme aynı token'dan iki geçerli
/// token üretebiliyorsa hırsız ve kurban yan yana, süresiz oturumda
/// kalır — mekanizma sessizce hiçbir işe yaramaz.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RefreshTokenConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;
    private const string Password = "conc-password-123";

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public RefreshTokenConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ayni_refresh_token_esazamanli_kullanilirsa_yalnizca_biri_gecerli_olur()
    {
        var client = _factory.CreateClient();
        var refreshToken = await RegisterShopperAsync(client);

        // Aynı token'la aynı anda N yenileme. Sunucu bunları ayrı isteklerde,
        // ayrı DbContext'lerle karşılar — yani gerçek yarış.
        var responses = await PostRefreshInParallelAsync(refreshToken);

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        accepted.Should().Be(1,
            "tek kullanımlık rotasyon buysa aynı token'dan yalnız bir yeni oturum doğabilir; " +
            "birden fazlası, çalınan bir token'ın meşru olanla yan yana yaşayabildiği anlamına gelir");

        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized)
            .Should().Be(ParallelAttempts - 1);
    }

    [Fact]
    public async Task Esazamanli_yenileme_tek_bir_yeni_token_satiri_birakir()
    {
        var client = _factory.CreateClient();
        var refreshToken = await RegisterShopperAsync(client);

        await PostRefreshInParallelAsync(refreshToken);

        // HTTP kodları doğru olsa bile DB'de fazladan canlı token kalmamalı:
        // istek 500 alıp satırı yine de yazmış olabilir.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var live = await db.ShopperRefreshTokens.CountAsync(t => t.RevokedAt == null);
        live.Should().Be(1, "yenileme sonrası ayakta tam olarak bir token kalmalı");
    }

    /// <summary>
    /// Aynı refresh token'ı N istemciden aynı anda gönderir.
    ///
    /// İstemciler bilerek döngüden önce kuruluyor: <c>CreateClient</c> ilk
    /// çağrıda sunucuyu ayağa kaldırıyor ve her çağrıda ölçülebilir gecikme
    /// ekliyor. Bu gecikme istekleri birbirinden ayırır, yarış penceresini
    /// daraltır ve testin bozuk kodda bile şansa yeşil yanmasına yol açar.
    /// </summary>
    private async Task<HttpResponseMessage[]> PostRefreshInParallelAsync(string refreshToken)
    {
        var clients = Enumerable.Range(0, ParallelAttempts)
            .Select(_ => _factory.CreateClient())
            .ToArray();

        // Geçersiz token'la bir ısınma turu: yönlendirme, model bağlama,
        // controller ve EF sorgusu ilk çağrıda JIT edilir ve bu bedel gerçek
        // turda istekleri birbirinden ayırırdı. Isınma olmadan test bozuk
        // kodda bile yeşil yanabiliyor (ölçüldü).
        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            "/api/v1/shopper/auth/refresh", new { refreshToken = "isinma-gecersiz" })));

        return await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            "/api/v1/shopper/auth/refresh", new { refreshToken })));
    }

    private async Task<string> RegisterShopperAsync(HttpClient client)
    {
        var code = await SeedBroadcasterCodeAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", new
        {
            broadcasterCode = code,
            fullName = "Concurrency Shopper",
            phone = "+90500" + Random.Shared.Next(1_000_000, 9_999_999),
            password = Password,
            address = "Ankara",
            platform = "youtube",
            username = "concshopper",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "kayıt ön koşulu tutmalı");
        var body = await resp.Content.ReadFromJsonAsync<AuthBody>();
        return body!.RefreshToken;
    }

    private sealed record AuthBody(string AccessToken, string RefreshToken);

    private async Task<string> SeedBroadcasterCodeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"conc-{Guid.NewGuid():N}@x",
            Name = "Yayıncı",
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "cnc" + Guid.NewGuid().ToString("N")[..8];
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return code;
    }
}
