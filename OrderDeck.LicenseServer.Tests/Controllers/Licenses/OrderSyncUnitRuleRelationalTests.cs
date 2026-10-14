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
/// Spec 2026-10-13 §10.1-14: v1 (≤0.9.10) güncellemesi ProductId'yi NULL'lamaz ve stoğu geri sarmaz; defter
/// paketten değil birleştirilmiş satırdan beslenir (§4.2, M4). Gerçek SQL Server: defter yazıcısı ile sipariş
/// satırı aynı SaveChanges'te ve <c>SyncVersion</c> jetonu altında yazılıyor; InMemory jetonu uygulamadığı için
/// defterin hangi durumdan beslendiği orada ancak yarım kanıtlanır.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OrderSyncUnitRuleRelationalTests : IAsyncLifetime
{
    private static readonly DateTimeOffset SoldAt = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _licenseId;
    private Guid _productId;
    private Guid _variantId;

    public OrderSyncUnitRuleRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        await SeedLicenseAndCatalogAsync(customerId);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>(a) Katalog bilmeyen istemcinin güncellemesi ürün bağını silmez. Bugün <c>:285-286</c> pakettekini
    /// (alan hiç gönderilmediği için null) yazıyordu.</summary>
    [Fact]
    public async Task Katalog_bilmeyen_guncelleme_urun_bagini_korur()
    {
        var orderId = Guid.NewGuid();
        (await PushAsync(Item(orderId, _productId, _variantId), catalogAware: true))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await PushAsync(Item(orderId, null, null) with { Price = 120m }, catalogAware: false))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await LoadAsync(orderId);
        saved.ProductId.Should().Be(_productId);
        saved.ProductVariantId.Should().Be(_variantId);
        saved.Price.Should().Be(120m, "birim yine yazılır");
        (await MovementsAsync(orderId)).Should().ContainSingle()
            .Which.Quantity.Should().Be(-1);
    }

    /// <summary>(b) Katalog bilen istemcinin ProductId = null güncellemesi ürün bağını silmez ve +1 telafi
    /// yazmaz: defter birleştirilmiş satırı görür (bağ duruyor, iptal yok → hareket farkı yok).</summary>
    [Fact]
    public async Task Katalog_bilen_bos_urunlu_guncelleme_bagi_ve_defteri_korur()
    {
        var orderId = Guid.NewGuid();
        (await PushAsync(Item(orderId, _productId, _variantId), catalogAware: true))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await PushAsync(Item(orderId, null, null), catalogAware: true))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await LoadAsync(orderId);
        saved.ProductId.Should().Be(_productId);
        saved.ProductVariantId.Should().Be(_variantId);
        var movements = await MovementsAsync(orderId);
        movements.Should().ContainSingle("ürün bağı kalkmadı; defterin telafi yazması stoğu şişirirdi");
        movements.Sum(m => m.Quantity).Should().Be(-1);
    }

    /// <summary>(c) Komutla iptal edilmiş (CancelSetAt ∧ CancelledAt) siparişi v1 gönderimi geri alamaz (K2) ve
    /// defter yerinde kalır: birleştirilmiş satır iptalli olduğu için satış yeniden düşülmez.</summary>
    [Fact]
    public async Task Komutla_iptal_edilmis_siparisi_v1_geri_alamaz_defter_yerinde()
    {
        var orderId = Guid.NewGuid();
        var sold = Item(orderId, _productId, _variantId);
        (await PushAsync(sold, catalogAware: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelledAt = SoldAt.AddDays(1);
        (await PushAsync(sold with { CancelledAt = cancelledAt, CancelReason = "iade" }, catalogAware: true))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await StampCancelAsync(orderId);
        (await MovementsAsync(orderId)).Sum(m => m.Quantity).Should().Be(0, "ön koşul: satış ve telafi");

        (await PushAsync(sold, catalogAware: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await LoadAsync(orderId);
        saved.CancelledAt.Should().Be(cancelledAt);
        saved.CancelReason.Should().Be("iade");
        var movements = await MovementsAsync(orderId);
        movements.Should().HaveCount(2, "satış (−1) ve telafi (+1); yeniden düşüm yazılmamalı");
        movements.Sum(m => m.Quantity).Should().Be(0);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>İptal birimine komut damgası basar (S2'nin <c>cancel</c> komutu gibi).</summary>
    private async Task StampCancelAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var order = await db.Orders.SingleAsync(o => o.Id == orderId);
        order.CancelSetAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private sealed record V1Item(
        Guid Id,
        Guid? SessionId,
        string CustomerId,
        string Platform,
        string Username,
        string? DisplayName,
        string MessageText,
        string? Code,
        decimal Price,
        DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt,
        string? CancelReason,
        bool IsShippingFee,
        bool IsBackupPromoted,
        bool IsTentativeBackup,
        Guid? ProductId,
        Guid? ProductVariantId);

    private static readonly string CustomerHex = Guid.NewGuid().ToString("N");

    private static V1Item Item(Guid orderId, Guid? productId, Guid? variantId) => new(
        Id: orderId,
        SessionId: null,
        CustomerId: CustomerHex,
        Platform: "youtube",
        Username: "izleyici",
        DisplayName: null,
        MessageText: "A1",
        Code: "A1",
        Price: 100m,
        AddedAt: SoldAt,
        PrintedAt: SoldAt,
        CancelledAt: null,
        CancelReason: null,
        IsShippingFee: false,
        IsBackupPromoted: false,
        IsTentativeBackup: false,
        ProductId: productId,
        ProductVariantId: variantId);

    /// <summary><paramref name="catalogAware"/> false ise bayrak hiç gönderilmez — eski istemci gibi.</summary>
    private Task<HttpResponseMessage> PushAsync(V1Item item, bool catalogAware)
    {
        var url = $"/api/v1/licenses/{_licenseId}/orders/sync";
        return catalogAware
            ? _client.PostAsJsonAsync(url, new { orders = new[] { item }, catalogAware = true })
            : _client.PostAsJsonAsync(url, new { orders = new[] { item } });
    }

    private async Task<Order> LoadAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<List<StockMovement>> MovementsAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StockMovements.AsNoTracking().Where(m => m.OrderId == orderId).ToListAsync();
    }

    private async Task SeedLicenseAndCatalogAsync(Guid customerId)
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
            Name = "Birim Ürünü",
            NameSearch = "birim urunu",
            DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);

        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = "M",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.ProductVariants.Add(variant);

        await db.SaveChangesAsync();
        _licenseId = license.Id;
        _productId = product.Id;
        _variantId = variant.Id;
    }
}
