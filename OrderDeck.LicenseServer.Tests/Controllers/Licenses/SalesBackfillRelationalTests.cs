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
using BackfillResult = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesBackfillTests.BackfillResult;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>sales/backfill</c>'in birim ayrışması gerçek SQL Server'da — spec 2026-10-13 §10.1-26, §4.3, §8.2-4, M20. Mevcut
/// satırda birim farkı yazılmaz, <c>diverged</c> olarak döner; <c>localWins</c> yalnız mod Off'ta, geri yüklenmemiş
/// özgün kopyada ve damgasız birimde <c>true</c>. Sayılar kopya satırında birikir (artırım tek koşulsuz UPDATE; ancak
/// burada kanıtlanır). Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesBackfillRelationalTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);

    private readonly SqlServerContainerFixture _sql;

    public SalesBackfillRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    /// <summary>
    /// F05 öncesi kayıp güncellemeler (iptal, baskı, fiyat yerelde var, sunucuda yok): sunucu satırı hiç değişmez
    /// (alanlar ve sürüm), üçü de <c>diverged</c> döner ve özgün kopyada mod Off'ta yerel kazanır. Aynı parti yeniden
    /// gönderilince sayılar kopya satırında birikir.
    /// </summary>
    [Fact]
    public async Task Ayrisma_sunucuyu_yazmaz_ozgun_kopyada_yerel_kazanir_sayilar_birikir()
    {
        await using var ctx = await Context.CreateAsync(_sql);
        await ctx.HelloAsync(ctx.ReplicaId);
        var cancel = OrderSyncV2Tests.NewItem(null);
        var print = OrderSyncV2Tests.NewItem(null);
        var price = OrderSyncV2Tests.NewItem(null);
        await ctx.PushV2Async(ctx.ReplicaId, cancel, print, price);
        var before = await ctx.OrdersAsync(cancel.Id, print.Id, price.Id);
        Item[] local =
        [
            cancel with { CancelledAt = At, CancelReason = "vazgeçti" },
            print with { PrintedAt = At, PrintBatchId = "b1", PrintSeq = 1 },
            price with { Price = 80m, PriceCorrectedAt = At, PaidPrice = 100m },
        ];

        var result = await ctx.BackfillAsync(ctx.ReplicaId, local);

        result.Diverged.Should().BeEquivalentTo(new[]
        {
            new { Id = cancel.Id, Units = new[] { "cancel" }, LocalWins = true },
            new { Id = print.Id, Units = new[] { "print" }, LocalWins = true },
            new { Id = price.Id, Units = new[] { "price" }, LocalWins = true },
        });
        result.Orders.Select(o => o.Status).Should().AllBe("unchanged");
        (await ctx.OrdersAsync(cancel.Id, print.Id, price.Id)).Should().BeEquivalentTo(before,
            o => o.Excluding(x => x.License).Excluding(x => x.Session),
            "sunucu ayrışan satırı yazmaz: birimler, yeni alanlar ve sürüm aynı");
        var replica = await ctx.ReplicaAsync(ctx.ReplicaId);
        replica.BackfillDivergedLocal.Should().Be(3);
        replica.BackfillDivergedServer.Should().Be(0);

        await ctx.BackfillAsync(ctx.ReplicaId, local);

        (await ctx.ReplicaAsync(ctx.ReplicaId)).BackfillDivergedLocal.Should().Be(6, "sayılar birikir");
    }

    /// <summary>
    /// Yerel kazanmaz (sunucu kazanır, §8.7, §4.3): geri yüklenmiş kopya (<c>PreviousReplicaId</c> dolu), ayrışan birimi
    /// komutla damgalanmış satır ve mod On. Sunucu satırı yine yazılmaz; sayılar sunucu tarafına eklenir.
    /// </summary>
    [Fact]
    public async Task Geri_yuklenmis_kopyada_damgali_birimde_ve_mod_Onda_yerel_kazanmaz()
    {
        await using var off = await Context.CreateAsync(_sql);
        var restored = Guid.NewGuid();
        await off.HelloAsync(off.ReplicaId);
        await off.HelloAsync(restored, previousReplicaId: Guid.NewGuid());
        var restoredRow = OrderSyncV2Tests.NewItem(null);
        var stamped = OrderSyncV2Tests.NewItem(null);
        await off.PushV2Async(off.ReplicaId, restoredRow, stamped);
        await off.SeedAsync(stamped.Id, o => o.CancelSetAt = DateTimeOffset.UtcNow);

        var fromRestored = await off.BackfillAsync(restored, restoredRow with { CancelledAt = At });
        var fromOriginal = await off.BackfillAsync(off.ReplicaId, stamped with { CancelledAt = At });

        fromRestored.Diverged.Single().LocalWins.Should().BeFalse("geri yüklenmiş veritabanında yerel değer bayat olabilir");
        fromOriginal.Diverged.Single().LocalWins.Should().BeFalse("damgalı birim yalnız komutla değişir");
        (await off.OrdersAsync(restoredRow.Id, stamped.Id)).Should().OnlyContain(o => o.CancelledAt == null);
        (await off.ReplicaAsync(restored)).BackfillDivergedServer.Should().Be(1);
        (await off.ReplicaAsync(off.ReplicaId)).BackfillDivergedServer.Should().Be(1);

        await using var on = await Context.CreateAsync(_sql, SalesSyncMode.On);
        await on.HelloAsync(on.ReplicaId);
        var onRow = OrderSyncV2Tests.NewItem(null);
        await on.PushV2Async(on.ReplicaId, onRow);

        var fromOn = await on.BackfillAsync(on.ReplicaId, onRow with { CancelledAt = At });

        fromOn.Diverged.Single().LocalWins.Should().BeFalse("mod On'da sunucu kazanır; ilk tur sunucunun değerini indirir");
        (await on.OrdersAsync(onRow.Id)).Single().CancelledAt.Should().BeNull();
        var replica = await on.ReplicaAsync(on.ReplicaId);
        replica.BackfillDivergedServer.Should().Be(1);
        replica.BackfillDivergedLocal.Should().Be(0);
    }

    /// <summary>
    /// Lisans değiştiren PC (§3.1, §4.3): aynı yayıncının eski lisansı L1'de oturum ve siparişler var; PC yeni lisans
    /// L2'ye geçer ve Ws ilk açılışta bütün yerel geçmişi L2'ye geri doldurur. StreamSessions ve Orders'ın birincil
    /// anahtarı yalnız Id olduğu için L1'deki Id'yi eklemek birincil anahtara çarpardı (500, parti her denemede aynı
    /// yerde düşer, <c>backfillCompleted</c> hiç gelmez). Beklenen: 200; L1'de var olan oturum ve sipariş Id'leri ve
    /// L1 oturumuna bağlı yeni siparişler satır başına <c>foreign-license</c>; öbür satırlar yazılır; L1 değişmez.
    /// Oturumları taşımayan sonraki sipariş partisi de 400 <c>unknown-session</c> almaz.
    /// </summary>
    [Fact]
    public async Task Baska_lisansta_var_olan_Idler_500_degil_satir_basina_reddedilir()
    {
        await using var ctx = await Context.CreateAsync(_sql);
        await ctx.HelloAsync(ctx.ReplicaId);
        var (oldLicense, oldSession, oldOrderId) = await ctx.SeedPreviousLicenseAsync();
        var oldOrder = OrderSyncV2Tests.NewItem(oldSession) with { Id = oldOrderId };
        var newSession = SalesBackfillTests.NewSession(At);
        var newOrderInOldSession = OrderSyncV2Tests.NewItem(oldSession);
        var good = OrderSyncV2Tests.NewItem(newSession.Id);

        var result = await ctx.BackfillAsync(ctx.ReplicaId,
            [SalesBackfillTests.NewSession(At, release: true) with { Id = oldSession }, newSession],
            [oldOrder with { SessionId = newSession.Id }, newOrderInOldSession, good]);

        result.Sessions.Single(s => s.Id == oldSession).Reason.Should().Be("foreign-license");
        result.Sessions.Single(s => s.Id == newSession.Id).Should()
            .BeEquivalentTo(new { Reason = (string?)null, OwnerReplicaId = (Guid?)ctx.ReplicaId });
        result.Orders.Where(o => o.Id != good.Id).Should().HaveCount(2)
            .And.OnlyContain(o => o.Status == "rejected" && o.Reason == "foreign-license");
        result.Orders.Single(o => o.Id == good.Id).Status.Should().Be("inserted");

        var rows = await ctx.SnapshotAsync(oldSession, oldOrderId, newOrderInOldSession.Id, good.Id);
        rows.OldSessionLicense.Should().Be(oldLicense);
        rows.OldSessionOwner.Should().BeNull();
        rows.OldSessionReleasedAt.Should().BeNull("başka lisansın oturumu devredilmez");
        rows.OldOrderLicense.Should().Be(oldLicense);
        rows.OldOrderSession.Should().Be(oldSession);
        rows.OldOrderOrigin.Should().BeNull();
        rows.RejectedNewOrderExists.Should().BeFalse();
        rows.GoodOrderLicense.Should().Be(ctx.LicenseId);

        var later = await ctx.BackfillAsync(ctx.ReplicaId, oldOrder, OrderSyncV2Tests.NewItem(oldSession));
        later.Orders.Should().HaveCount(2)
            .And.OnlyContain(o => o.Status == "rejected" && o.Reason == "foreign-license");
    }

    // ── Kurulum ──────────────────────────────────────────────────────────────

    private sealed class Context : IAsyncDisposable
    {
        private readonly RelationalApiFactory _factory;
        private readonly HttpClient _client;

        private Context(RelationalApiFactory factory, HttpClient client, Guid customerId, Guid licenseId)
        {
            _factory = factory;
            _client = client;
            CustomerId = customerId;
            LicenseId = licenseId;
        }

        public Guid CustomerId { get; }
        public Guid LicenseId { get; }
        public Guid ReplicaId { get; } = Guid.NewGuid();

        public static async Task<Context> CreateAsync(SqlServerContainerFixture sql, SalesSyncMode mode = SalesSyncMode.Off)
        {
            var factory = new RelationalApiFactory(await sql.CreateDatabaseAsync());
            var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
            var licenseId = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
                var now = DateTimeOffset.UtcNow;
                db.Licenses.Add(new License
                {
                    Id = licenseId, LicenseKey = "key-" + Guid.NewGuid().ToString("N"), CustomerId = customerId,
                    SkuCode = "STD", ActivationSlots = 2, IssuedAt = now, ExpiresAt = now.AddYears(1),
                    SalesSyncMode = mode,
                    SalesSyncModeChangedAt = mode == SalesSyncMode.On ? now.AddDays(-1) : null,
                });
                await db.SaveChangesAsync();
            }

            return new Context(factory, client, customerId, licenseId);
        }

        private string Url(string path) => $"/api/v1/licenses/{LicenseId}/{path}";

        public async Task HelloAsync(Guid replicaId, Guid? previousReplicaId = null)
        {
            var resp = await _client.PostAsJsonAsync(Url("sales/hello"), new
            {
                replicaId, previousReplicaId, machineName = "PC-A", hardwareFingerprint = $"fp-{Guid.NewGuid():N}",
                protocol = 2, featureLevel = 1, seenMode = 0,
            });
            resp.StatusCode.Should().Be(HttpStatusCode.NoContent, await resp.Content.ReadAsStringAsync());
        }

        public async Task PushV2Async(Guid replicaId, params Item[] items)
        {
            var resp = await _client.PostAsJsonAsync(Url("orders/sync"),
                new { orders = items, format = 2, replicaId, protocol = 2, seenMode = 0 });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        }

        public Task<BackfillResult> BackfillAsync(Guid replicaId, params Item[] orders) =>
            BackfillAsync(replicaId, [], orders);

        public async Task<BackfillResult> BackfillAsync(
            Guid replicaId, SalesBackfillTests.SessionItem[] sessions, Item[] orders)
        {
            var resp = await _client.PostAsJsonAsync(Url("sales/backfill"),
                SalesBackfillTests.Body(replicaId, sessions, orders, []));
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            return (await resp.Content.ReadFromJsonAsync<BackfillResult>())!;
        }

        /// <summary>Aynı yayıncının eski lisansı: bitmiş, sahipsiz (v1) bir oturum ve ona bağlı bir sipariş.</summary>
        public async Task<(Guid LicenseId, Guid SessionId, Guid OrderId)> SeedPreviousLicenseAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var now = DateTimeOffset.UtcNow;
            var license = new License
            {
                Id = Guid.NewGuid(), LicenseKey = "key-" + Guid.NewGuid().ToString("N"), CustomerId = CustomerId,
                SkuCode = "STD", ActivationSlots = 2, IssuedAt = now.AddYears(-1), ExpiresAt = now.AddDays(-1),
            };
            var session = new StreamSession
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, Title = "Eski yayın", StartedAt = At.AddDays(-30),
                EndedAt = At.AddDays(-30).AddHours(2), Platforms = "youtube", UpdatedAt = At.AddDays(-30),
            };
            var order = new Order
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, SessionId = session.Id,
                CustomerId = Guid.NewGuid().ToString("N"), Platform = "youtube", Username = $"u-{Guid.NewGuid():N}",
                MessageText = "A1 alırım", Code = "A1", Price = 100m, AddedAt = At.AddDays(-30),
                UpdatedAt = At.AddDays(-30),
            };
            db.Licenses.Add(license);
            db.StreamSessions.Add(session);
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return (license.Id, session.Id, order.Id);
        }

        public sealed record Snapshot(
            Guid OldSessionLicense, Guid? OldSessionOwner, DateTimeOffset? OldSessionReleasedAt,
            Guid OldOrderLicense, Guid? OldOrderSession, Guid? OldOrderOrigin,
            bool RejectedNewOrderExists, Guid? GoodOrderLicense);

        public async Task<Snapshot> SnapshotAsync(
            Guid oldSessionId, Guid oldOrderId, Guid rejectedNewOrderId, Guid goodOrderId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var session = await db.StreamSessions.AsNoTracking().SingleAsync(s => s.Id == oldSessionId);
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == oldOrderId);
            var good = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == goodOrderId);
            return new Snapshot(
                session.LicenseId, session.OwnerReplicaId, session.ReleasedAt,
                order.LicenseId, order.SessionId, order.OriginReplicaId,
                await db.Orders.AnyAsync(o => o.Id == rejectedNewOrderId),
                good?.LicenseId);
        }

        public async Task SeedAsync(Guid orderId, Action<Order> change)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            change(await db.Orders.SingleAsync(o => o.Id == orderId));
            await db.SaveChangesAsync();
        }

        public async Task<List<Order>> OrdersAsync(params Guid[] ids)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var rows = await db.Orders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToListAsync();
            return ids.Select(id => rows.Single(o => o.Id == id)).ToList();
        }

        public async Task<WpfReplica> ReplicaAsync(Guid replicaId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            return await db.WpfReplicas.AsNoTracking()
                .SingleAsync(r => r.LicenseId == LicenseId && r.ReplicaId == replicaId);
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
