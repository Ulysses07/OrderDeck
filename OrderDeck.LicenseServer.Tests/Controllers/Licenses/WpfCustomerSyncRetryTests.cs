using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Sync ucu eşzamanlılık çakışmasında partiyi 409'la reddetmez, BİR KEZ taze
/// okumayla baştan uygular (A5 — A4 kalite incelemesi: çakışmanın tipik
/// kaynağı birleştiricinin dokunduğu bir sipariş satırı).
///
/// <para>Gerçek SQL Server şart: InMemory'de kayıt işlemi yok — çakışan bir
/// SaveChanges'in o ana kadar uyguladığı satırlar geri alınmaz ve yeniden
/// deneme, yarım kalmış bir ara durumu görürdü. Burada ilk denemenin kopya
/// satırı da sipariş taşıması da işlemle birlikte geri alınır; testin
/// kanıtladığı şey yeniden denemenin ikisini de taze okumayla bir kez daha
/// yapması. Testcontainers kullandığı için canlı yayın sırasında
/// ÇALIŞTIRILMAZ.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class WpfCustomerSyncRetryTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private readonly RivalOrderWriteInterceptor _rival = new();
    private RetryRelationalFactory _factory = null!;

    public WpfCustomerSyncRetryTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        var cs = await _sql.CreateDatabaseAsync();
        _rival.ConnectionString = cs;
        _factory = new RetryRelationalFactory(cs, _rival);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record RedirectBody(Guid Id, Guid CanonicalId);
    private sealed record SyncBody(int Synced, int RetroactiveMatches, List<RedirectBody> Redirects);

    private static object Item(Guid id, string username) => new
    {
        id, platform = "tiktok", username, updatedAt = DateTimeOffset.UtcNow, format = 2,
    };

    [Fact]
    public async Task Tasinan_siparis_eszamanli_degisirse_parti_bir_kez_yeniden_uygulanir()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var licenseId = await SeedLicenseAsync(customerId);
        var url = $"/api/v1/licenses/{licenseId}/wpf-customers/sync";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        (await client.PostAsJsonAsync(url, new { customers = new[] { Item(a, "mehmet") } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // B bilgisayarı bu kişiye kendi Id'siyle (b) daha önce sipariş göndermiş.
        var orderId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.Add(new Order
            {
                Id = orderId, LicenseId = licenseId, CustomerId = b.ToString("N"),
                Platform = "tiktok", Username = "Mehmet", MessageText = "A1", Price = 10m,
                AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // b kopya olarak bağlanırken birleştirici siparişi asıl kayda taşır.
        // Sync okuduktan SONRA, kaydetmeden ÖNCE eşzamanlı bir orders/sync aynı
        // siparişi günceller (fiyat + SyncVersion++): ilk kayıt çakışır.
        _rival.Arm(orderId, rivalPrice: 25m);

        var resp = await client.PostAsJsonAsync(url, new { customers = new[] { Item(b, "Mehmet") } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "tek çakışma bir kez taze okumayla yeniden denenir");
        _rival.Fired.Should().BeTrue("rakip yazım gerçekten araya girdi");
        _rival.SavesWithMovedOrder.Should().Be(2, "ilk kayıt çakıştı, yeniden deneme indi");
        var body = await resp.Content.ReadFromJsonAsync<SyncBody>();
        body!.Synced.Should().Be(1);
        body.Redirects.Should().ContainSingle("ilk denemenin yönlendirmesi yanıtta birikmemeli")
            .Which.Should().Be(new RedirectBody(b, a));

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var order = await vdb.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        order.CustomerId.Should().Be(a.ToString("N"), "yeniden deneme siparişi asıl kayda taşıdı");
        order.Price.Should().Be(25m, "eşzamanlı yazanın değeri kaldı: taşıma taze satıra uygulandı");
        order.SyncVersion.Should().Be(1, "birleştirici jetonu artırmaz; değer rakibin yazdığı");
        var rows = await vdb.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId).ToListAsync();
        rows.Should().HaveCount(2, "ilk denemenin kopya satırı geri alındı, yeniden deneme bir kez ekledi");
        rows.Single(p => p.Id == b).MergedIntoId.Should().Be(a);
    }

    private async Task<Guid> SeedLicenseAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            // LicenseKey HasMaxLength(40) — gerçek SQL'de tam Guid taşar.
            LicenseKey = "sync-retry-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    /// <summary>
    /// İzleyicide taşınmış (Modified) hedef sipariş varken koşan her kaydetmeyi
    /// sayar; İLKİNDE siparişi AYRI bir bağlamdan günceller — gerçek bir
    /// eşzamanlı orders/sync yazımı gibi fiyatı değiştirip SyncVersion'ı artırır.
    /// SavingChangesAsync, EF kayıt işlemini açmadan ÖNCE koşar: rakip yazım
    /// commit olur, sync'in UPDATE'i eski jetonla 0 satıra düşer →
    /// DbUpdateConcurrencyException → işlem geri alınır. Rakip bağlam bu
    /// interceptor'ı taşımaz, kendini yeniden tetiklemez.
    /// </summary>
    private sealed class RivalOrderWriteInterceptor : SaveChangesInterceptor
    {
        private Guid? _orderId;
        private decimal _rivalPrice;
        private int _fired;
        private int _saves;

        public string ConnectionString { get; set; } = "";
        public bool Fired => Volatile.Read(ref _fired) == 1;
        public int SavesWithMovedOrder => Volatile.Read(ref _saves);

        public void Arm(Guid orderId, decimal rivalPrice)
        {
            _rivalPrice = rivalPrice;
            _orderId = orderId;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_orderId is { } id
                && eventData.Context is { } ctx
                && ctx.ChangeTracker.Entries<Order>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.Id == id))
            {
                Interlocked.Increment(ref _saves);
                if (Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    await using var rival = new LicenseDbContext(
                        new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(ConnectionString).Options);
                    var order = await rival.Orders.SingleAsync(o => o.Id == id, cancellationToken);
                    order.Price = _rivalPrice;
                    order.SyncVersion++;
                    order.UpdatedAt = DateTimeOffset.UtcNow;
                    await rival.SaveChangesAsync(cancellationToken);
                }
            }

            return result;
        }
    }

    private sealed class RetryRelationalFactory(string connectionString, RivalOrderWriteInterceptor rival)
        : RelationalApiFactory(connectionString)
    {
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(rival);
    }
}
