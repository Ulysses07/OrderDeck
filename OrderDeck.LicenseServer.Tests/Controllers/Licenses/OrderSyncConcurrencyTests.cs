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

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Aynı sipariş partisinin eşzamanlı iki kez gönderilmesi. Gerçekçi senaryo:
/// WPF'in outbox'ı isteği zaman aşımı sanıp yeniden yolluyor, ilki hâlâ
/// sunucuda; ya da bir senkron turu bitmeden sonraki tur başlıyor.
///
/// Neden gerçek SQL Server: <c>StockLedgerWriter</c> "bugüne kadarki hareket
/// toplamını" okuyup farkı yazıyor, okuma ile yazma arasında pencere var ve
/// hakem yalnızca <c>Order.SyncVersion</c> eşzamanlılık jetonu olabilir.
/// InMemory sağlayıcısında jeton hiç uygulanmaz — bozuk kodda bile yeşil yanar.
///
/// Neden önemli: bu yarışın bedeli hata değil, SESSİZ stok şişmesi. İptal
/// edilen bir siparişin telafisi (+1) iki kez yazılırsa defter satılmamış bir
/// adet uydurur; kimse fark etmez, tek belirti sayımda tutmayan raf olur.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OrderSyncConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    private string _jwt = "";
    private Guid _licenseId;
    private Guid _productId;

    private static readonly DateTimeOffset SoldAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CancelledAt = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    public OrderSyncConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _jwt = jwt;
        await SeedLicenseAndProductAsync(customerId);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Satılmış siparişin iptali eşzamanlı N kez gelirse defter tam olarak BİR
    /// telafi yazmalı. Ölçüt bilerek HTTP kodu değil bakiye: kod dağılımı
    /// zamanlamaya bağlı (geç gelen istek jetonu güncel okur, farkı sıfır
    /// bulur ve haklı olarak 200 alır), bakiye ise bağlı değil — jeton
    /// çalışıyorsa her koşulda 0, çalışmıyorsa artıda.
    /// </summary>
    [Fact]
    public async Task Esazamanli_iptal_stok_telafisini_iki_kez_yazmaz()
    {
        var orderId = Guid.NewGuid();

        var sale = await PostAsync(NewClient(), OrderBatch(orderId, cancelled: false));
        sale.StatusCode.Should().Be(HttpStatusCode.OK, "satışın senkronu ön koşul");
        (await LedgerBalanceAsync(orderId)).Should().Be(-1, "satış deftere düşmeliydi");

        var responses = await PostInParallelAsync(OrderBatch(orderId, cancelled: true));

        responses.Should().OnlyContain(
            r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict,
            "kaybeden istek anlaşılır bir 409 almalı; ham eşzamanlılık istisnası 500'e " +
            "dönüşüyor ve 500 hem alarm üretir hem istemciye hiçbir şey anlatmaz");

        (await MovementCountAsync(orderId)).Should().Be(2,
            "bir satış (−1) ve tam bir telafi (+1); fazlası aynı iptalin ikinci kez yazıldığı anlamına gelir");
        (await LedgerBalanceAsync(orderId)).Should().Be(0,
            "iptal edilen sipariş stoğu ne düşürmeli ne şişirmeli; artıda kalan bakiye " +
            "satılmamış adet uydurur ve bunu yalnız fiziksel sayım yakalar");
    }

    /// <summary>
    /// 409 alan istemcinin yapması gereken şey partiyi aynen yeniden
    /// göndermek — WPF'in outbox'ı zaten bunu yapıyor, çünkü hata gören
    /// partiyi "senkronlandı" işaretlemiyor. Bu testin iddiası: o yeniden
    /// gönderim güvenli, yani ikinci tur deftere hiçbir şey eklemiyor.
    /// Yakınsamasaydı 409 tavsiyesi sonsuz döngüye çevirirdi.
    /// </summary>
    [Fact]
    public async Task Catisma_sonrasi_yeniden_gonderim_deftere_bir_sey_eklemez()
    {
        var orderId = Guid.NewGuid();

        (await PostAsync(NewClient(), OrderBatch(orderId, cancelled: false)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "satışın senkronu ön koşul");
        await PostInParallelAsync(OrderBatch(orderId, cancelled: true));

        var retry = await PostAsync(NewClient(), OrderBatch(orderId, cancelled: true));

        retry.StatusCode.Should().Be(HttpStatusCode.OK,
            "çakışma geçtikten sonra aynı parti sorunsuz geçmeli");
        (await MovementCountAsync(orderId)).Should().Be(2,
            "mutabakat farkı sıfır bulmalı; yeniden gönderim yeni hareket doğurmamalı");
        (await LedgerBalanceAsync(orderId)).Should().Be(0);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>
    /// İstemciler ve ısınma turu bilerek gerçek turdan önce: <c>CreateClient</c>
    /// ve ilk istek yönlendirme/model bağlama/EF sorgu derlemesini JIT eder.
    /// Bu bedel istekleri birbirinden ayırır, yarış penceresini daraltır ve
    /// test bozuk kodda bile şansa yeşil yanabilir.
    /// </summary>
    private async Task<HttpResponseMessage[]> PostInParallelAsync(object batch)
    {
        var clients = Enumerable.Range(0, ParallelAttempts).Select(_ => NewClient()).ToArray();

        // Isınma: sahibi olmadığımız bir lisans → 404, veriye dokunmaz.
        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/v1/licenses/{Guid.NewGuid()}/orders/sync", batch)));

        return await Task.WhenAll(clients.Select(c => PostAsync(c, batch)));
    }

    private Task<HttpResponseMessage> PostAsync(HttpClient client, object batch)
        => client.PostAsJsonAsync($"/api/v1/licenses/{_licenseId}/orders/sync", batch);

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        return client;
    }

    /// <summary>
    /// Tek siparişlik parti. <c>catalogAware</c> açık — sunucu stok hareketini
    /// yalnız bu bayrakla üretiyor.
    /// </summary>
    private object OrderBatch(Guid orderId, bool cancelled) => new
    {
        orders = new[]
        {
            new
            {
                id = orderId,
                sessionId = (Guid?)null,
                customerId = Guid.NewGuid().ToString("N"),
                platform = "youtube",
                username = "yaris",
                displayName = (string?)null,
                messageText = "A1",
                code = "A1",
                price = 100m,
                addedAt = SoldAt,
                printedAt = SoldAt,
                cancelledAt = cancelled ? CancelledAt : (DateTimeOffset?)null,
                cancelReason = cancelled ? "test" : null,
                isShippingFee = false,
                isBackupPromoted = false,
                isTentativeBackup = false,
                productId = (Guid?)_productId,
                productVariantId = (Guid?)null,
            }
        },
        catalogAware = true,
    };

    private async Task<int> LedgerBalanceAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.OrderId == orderId)
            .SumAsync(m => m.Quantity);
    }

    private async Task<int> MovementCountAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StockMovements.AsNoTracking().CountAsync(m => m.OrderId == orderId);
    }

    private async Task SeedLicenseAndProductAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Code = "P" + Guid.NewGuid().ToString("N")[..8],
            Name = "Yarış Ürünü",
            NameSearch = "yaris urunu",
            DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);

        await db.SaveChangesAsync();
        _licenseId = license.Id;
        _productId = product.Id;
    }
}
