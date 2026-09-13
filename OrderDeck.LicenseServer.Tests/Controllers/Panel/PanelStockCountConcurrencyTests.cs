using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// Mutlak sayım read/compute/write yarışını gerçek SQL Server'da ölçer.
/// Testcontainers kullandığı için canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PanelStockCountConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private readonly FirstCountSaveGate _gate = new();
    private CountRelationalFactory _factory = null!;

    public PanelStockCountConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new CountRelationalFactory(
            await _sql.CreateDatabaseAsync(), _gate);

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Eszamanli_ayni_sayim_bakiyeyi_iki_kez_duzeltmez()
    {
        var seed = await SeedAsync();
        await EntryAsync(seed.Client, seed.ProductId, seed.VariantId, 10);

        var (first, second) = await RaceCountsAsync(seed, 5, 5);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BalanceAsync(seed.LicenseId, seed.ProductId, seed.VariantId))
            .Should().Be(5);
    }

    [Fact]
    public async Task Eszamanli_farkli_sayimlardan_kaybeden_retry_edebilir()
    {
        var seed = await SeedAsync();
        await EntryAsync(seed.Client, seed.ProductId, seed.VariantId, 10);

        var (first, second) = await RaceCountsAsync(seed, 5, 7);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BalanceAsync(seed.LicenseId, seed.ProductId, seed.VariantId))
            .Should().Be(5);

        (await CountAsync(seed.Client, seed.ProductId, seed.VariantId, 7))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(seed.LicenseId, seed.ProductId, seed.VariantId))
            .Should().Be(7);
    }

    [Fact]
    public async Task Sayim_ile_gercek_giris_birbirinin_deltasini_kaybetmez()
    {
        var seed = await SeedAsync();
        await EntryAsync(seed.Client, seed.ProductId, seed.VariantId, 10);

        var firstTask = CountAsync(seed.Client, seed.ProductId, seed.VariantId, 5);
        var entry = await WhileFirstCountHeldAsync(
            firstTask,
            () => EntryAsync(
                    NewClient(seed.Jwt), seed.ProductId, seed.VariantId, 3)
                );
        var first = await firstTask;

        entry.StatusCode.Should().Be(HttpStatusCode.Created);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(seed.LicenseId, seed.ProductId, seed.VariantId))
            .Should().Be(8);
    }

    [Fact]
    public async Task Sayim_ile_siparis_dusumu_birbirinin_deltasini_kaybetmez()
    {
        var seed = await SeedAsync();
        await EntryAsync(seed.Client, seed.ProductId, seed.VariantId, 10);

        var firstTask = CountAsync(seed.Client, seed.ProductId, seed.VariantId, 5);
        var sale = await WhileFirstCountHeldAsync(
            firstTask,
            () => SaleAsync(seed with { Client = NewClient(seed.Jwt) }));
        var first = await firstTask;

        sale.IsSuccessStatusCode.Should().BeTrue();
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceAsync(seed.LicenseId, seed.ProductId, seed.VariantId))
            .Should().Be(4);
    }

    [Fact]
    public async Task Farkli_varyant_urun_ve_tenant_sayimlari_birbirini_kilitlemez()
    {
        var first = await SeedAsync();
        var secondTenant = await SeedAsync();
        var otherProduct = await AddProductAsync(first.LicenseId);

        var blockedTask = CountAsync(first.Client, first.ProductId, first.VariantId, 2);
        var independent = await WhileFirstCountHeldAsync(
            blockedTask,
            () => Task.WhenAll(
                    CountAsync(NewClient(first.Jwt), first.ProductId, null, 3),
                    CountAsync(NewClient(first.Jwt), otherProduct, null, 4),
                    CountAsync(NewClient(secondTenant.Jwt),
                        secondTenant.ProductId, secondTenant.VariantId, 5)));
        var blocked = await blockedTask;

        independent.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
        blocked.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private sealed record Seed(
        HttpClient Client, string Jwt, Guid LicenseId, Guid ProductId, Guid VariantId);

    private async Task<Seed> SeedAsync()
    {
        var (client, customerId, jwt) =
            await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            // LicenseKey HasMaxLength(40) — gerçek SQL'de tam Guid taşar (bkz. CustomerPurgeRelationalTests deseni).
            LicenseKey = "stock-race-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
        };
        var product = NewProduct(license.Id, "race-main", now);
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = "M",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(license, product, variant);
        await db.SaveChangesAsync();
        return new Seed(client, jwt, license.Id, product.Id, variant.Id);
    }

    private async Task<(HttpResponseMessage First, HttpResponseMessage Second)> RaceCountsAsync(
        Seed seed, int firstQuantity, int secondQuantity)
    {
        var firstTask = CountAsync(
            seed.Client, seed.ProductId, seed.VariantId, firstQuantity);
        var second = await WhileFirstCountHeldAsync(
            firstTask,
            () => CountAsync(
                    NewClient(seed.Jwt), seed.ProductId, seed.VariantId, secondQuantity)
                );

        return (await firstTask, second);
    }

    private async Task<T> WhileFirstCountHeldAsync<T>(
        Task<HttpResponseMessage> firstCount,
        Func<Task<T>> concurrentAction)
    {
        Task<T>? concurrentTask = null;
        try
        {
            await _gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
            concurrentTask = concurrentAction();
            return await concurrentTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            _gate.Release();
            await ObserveAsync(firstCount);
            if (concurrentTask is not null)
                await ObserveAsync(concurrentTask);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Asıl timeout/hata korunur; amaç arka plan görevini gözlemlemektir.
        }
    }

    private HttpClient NewClient(string jwt)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private async Task<Guid> AddProductAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var product = NewProduct(
            // Products.Code HasMaxLength(CatalogLimits.ProductCode) — tam Guid gerçek SQL'de taşar.
            licenseId, "race-" + Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static Product NewProduct(Guid licenseId, string code, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        Code = code,
        Name = "Yarış ürünü",
        DefaultPrice = 100m,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Task<HttpResponseMessage> EntryAsync(
        HttpClient client, Guid productId, Guid? variantId, int quantity)
        => client.PostAsJsonAsync("/api/panel/stock/entries", new
        {
            productId,
            productVariantId = variantId,
            quantity,
        });

    private static Task<HttpResponseMessage> CountAsync(
        HttpClient client, Guid productId, Guid? variantId, int countedQuantity)
        => client.PostAsJsonAsync("/api/panel/stock/counts", new
        {
            productId,
            productVariantId = variantId,
            countedQuantity,
        });

    private static Task<HttpResponseMessage> SaleAsync(Seed seed)
        => seed.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{seed.LicenseId}/orders/sync",
            new
            {
                catalogAware = true,
                orders = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        customerId = Guid.NewGuid().ToString("N"),
                        platform = "youtube",
                        username = "stock-race-shopper",
                        messageText = "race-main M",
                        code = "race-main",
                        price = 100m,
                        addedAt = DateTimeOffset.UtcNow,
                        isShippingFee = false,
                        isBackupPromoted = false,
                        isTentativeBackup = false,
                        productId = seed.ProductId,
                        productVariantId = (Guid?)seed.VariantId,
                    }
                }
            });

    private async Task<int> BalanceAsync(Guid licenseId, Guid productId, Guid? variantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StockMovements
            .Where(m => m.LicenseId == licenseId
                && m.ProductId == productId
                && m.ProductVariantId == variantId)
            .SumAsync(m => m.Quantity);
    }

    private sealed class FirstCountSaveGate
    {
        private readonly TaskCompletionSource<bool> _arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task Arrived => _arrived.Task;
        public void Release() => _release.TrySetResult(true);

        public async Task HoldFirstAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _seen) != 1) return;
            _arrived.TrySetResult(true);
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private sealed class FirstCountSaveInterceptor : SaveChangesInterceptor
    {
        private readonly FirstCountSaveGate _gate;
        public FirstCountSaveInterceptor(FirstCountSaveGate gate) => _gate = gate;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null
                && eventData.Context.ChangeTracker.Entries<StockMovement>()
                    .Any(e => e.State == EntityState.Added
                        && e.Entity.Reason == StockMovementReason.CountAdjustment))
            {
                await _gate.HoldFirstAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CountRelationalFactory : RelationalApiFactory
    {
        private readonly FirstCountSaveGate _gate;

        public CountRelationalFactory(string connectionString, FirstCountSaveGate gate)
            : base(connectionString) => _gate = gate;

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(new FirstCountSaveInterceptor(_gate));
    }
}
