using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// KVKK ve satış akışı — spec 2026-10-13 §9, §10.1-22 (akış ve silme kısmı), gerçek SQL Server.
/// <list type="bullet">
///   <item>Ayar kapalı (varsayılan, bugünkü politika): silme sipariş satırının metnine dokunmaz; akış asıl kaydı
///   silinmiş müşterinin siparişini okuma anında metinsiz verir. Satır değişmediği için önceden inmiş bilgisayarlara
///   yeniden gönderilmez — daha önce inmiş metin ancak ayar açıkken silinir.</item>
///   <item>Ayar açık: silme aynı kayıtta sipariş metnini boşaltır, <c>TextScrubbedAt</c> basar; rowversion ilerlediği
///   için boşaltma imleci geçmiş bilgisayarlara da iner.</item>
/// </list>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesFeedKvkkRelationalTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    private readonly SqlServerContainerFixture _sql;

    public SalesFeedKvkkRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    [Fact]
    public async Task Ayar_kapaliyken_silme_satira_dokunmaz_akis_metni_maskeler()
    {
        await using var ctx = await SetupAsync(scrubOrderText: false);
        var before = await ctx.PageAsync(afterSeq: 0);
        before.Order(ctx.PurgedOrder).DisplayName.Should().Be("Takma ad", "henüz silinmedi");

        await ctx.PurgeAsync();

        var row = await ctx.OrderRowAsync(ctx.PurgedOrder);
        row.DisplayName.Should().Be("Takma ad");
        row.MessageText.Should().Be("A1 alırım");
        row.TextScrubbedAt.Should().BeNull();

        var fresh = await ctx.PageAsync(afterSeq: 0);
        var masked = fresh.Order(ctx.PurgedOrder);
        masked.DisplayName.Should().BeNull();
        masked.MessageText.Should().BeEmpty();
        masked.Username.Should().Be(row.Username);
        masked.Code.Should().Be("A1");
        fresh.Order(ctx.OtherOrder).MessageText.Should().Be("A1 alırım", "başka müşteri maskelenmez");
        fresh.CustomerDrafts.Single(d => d.Id == ctx.PurgedCustomerHex).PurgedAt.Should().NotBeNull();

        (await ctx.PageAsync(before.NextAfterSeq)).Items.Should().BeEmpty(
            "satır değişmedi: önceden inmiş metin ayar kapalıyken silinmez");
    }

    [Fact]
    public async Task Ayar_acikken_silme_siparis_metnini_bosaltir_ve_akisla_iner()
    {
        await using var ctx = await SetupAsync(scrubOrderText: true);
        var before = await ctx.PageAsync(afterSeq: 0);
        var seqBefore = before.Items.Single(i => i.Order?.Id == ctx.PurgedOrder).ChangeSeq;

        await ctx.PurgeAsync();

        var row = await ctx.OrderRowAsync(ctx.PurgedOrder);
        row.DisplayName.Should().BeNull();
        row.MessageText.Should().BeEmpty();
        row.TextScrubbedAt.Should().NotBeNull();
        row.Code.Should().Be("A1");
        row.Price.Should().Be(100m);
        row.ChangeSeq.Should().BeGreaterThan(seqBefore);

        var after = await ctx.PageAsync(before.NextAfterSeq);
        after.Items.Select(i => i.Order?.Id).Should().Equal(ctx.PurgedOrder);
        var delivered = after.Order(ctx.PurgedOrder);
        delivered.DisplayName.Should().BeNull();
        delivered.MessageText.Should().BeEmpty();
        delivered.TextScrubbedAt.Should().Be(row.TextScrubbedAt);
        (await ctx.OrderRowAsync(ctx.OtherOrder)).MessageText.Should().Be("A1 alırım");
    }

    // ── Kurulum ──────────────────────────────────────────────────────────────

    private async Task<Context> SetupAsync(bool scrubOrderText)
    {
        var factory = new ScrubFactory(await _sql.CreateDatabaseAsync(), scrubOrderText);
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");

        var licenseId = Guid.NewGuid();
        var shopperId = Guid.NewGuid();
        var projectionId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Licenses.Add(new License
            {
                Id = licenseId, LicenseKey = "key-" + Guid.NewGuid().ToString("N"), CustomerId = customerId,
                SkuCode = "STD", ActivationSlots = 2, IssuedAt = now, ExpiresAt = now.AddYears(1),
            });
            db.Shoppers.Add(new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = shopperId, FullName = "Silinecek kişi", Phone = TestPhone.NewE164(),
                PasswordHash = $"hash-{Guid.NewGuid():N}", Address = "-", CreatedAt = now, UpdatedAt = now,
            });
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = projectionId, LicenseId = licenseId, Platform = "youtube",
                Username = $"silinen-{Guid.NewGuid():N}", DisplayName = "Takma ad", UpdatedAt = now,
            });
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = licenseId, Platform = "youtube",
                Username = "silinen", WpfCustomerId = projectionId, JoinedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var ctx = new Context(factory, client, licenseId, shopperId, projectionId.ToString("N"));
        var sessionId = Guid.NewGuid();
        await ctx.PushSessionAsync(sessionId, endedAt: null, release: false);
        ctx.PurgedOrder = await ctx.PushOrderAsync(sessionId, ctx.PurgedCustomerHex);
        ctx.OtherOrder = await ctx.PushOrderAsync(sessionId, Guid.NewGuid().ToString("N"));
        await ctx.PushSessionAsync(sessionId, StartedAt.AddHours(2), release: true);
        return ctx;
    }

    private sealed class ScrubFactory(string connectionString, bool scrub) : RelationalApiFactory(connectionString)
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:ScrubOrderTextOnPurge"] = scrub ? "true" : "false",
        };
    }

    private sealed class Context(
        RelationalApiFactory factory, HttpClient client, Guid licenseId, Guid shopperId, string purgedCustomerHex)
        : IAsyncDisposable
    {
        private readonly Guid _replicaId = Guid.NewGuid();

        public string PurgedCustomerHex { get; } = purgedCustomerHex;
        public Guid PurgedOrder { get; set; }
        public Guid OtherOrder { get; set; }

        public async Task PurgeAsync()
        {
            using var scope = factory.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<ShopperPurgeService>().PurgeAsync(shopperId, default))
                .Should().NotBeNull();
        }

        public async Task<Order> OrderRowAsync(Guid id)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
        }

        public async Task<SalesChangesPage> PageAsync(long afterSeq)
        {
            var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/sales/changes?afterSeq={afterSeq}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            return (await resp.Content.ReadFromJsonAsync<SalesChangesPage>())!;
        }

        public async Task PushSessionAsync(Guid sessionId, DateTimeOffset? endedAt, bool release)
        {
            var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/sessions/sync", new
            {
                sessions = new[]
                {
                    new
                    {
                        id = sessionId, title = "Akşam yayını", startedAt = StartedAt, endedAt, platforms = "youtube",
                        notes = (string?)null, release, machineName = "PC-A",
                    },
                },
                replicaId = _replicaId,
                protocol = 2,
                seenMode = 0,
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        }

        public async Task<Guid> PushOrderAsync(Guid sessionId, string customerHex)
        {
            var orderId = Guid.NewGuid();
            var resp = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/orders/sync", new
            {
                orders = new[]
                {
                    new
                    {
                        id = orderId, sessionId, customerId = customerHex, platform = "youtube",
                        username = $"u-{Guid.NewGuid():N}", displayName = "Takma ad", messageText = "A1 alırım",
                        code = "A1", price = 100m, addedAt = StartedAt.AddMinutes(10),
                        printedAt = (DateTimeOffset?)null, cancelledAt = (DateTimeOffset?)null,
                        cancelReason = (string?)null, isShippingFee = false, isBackupPromoted = false,
                        isTentativeBackup = false,
                    },
                },
                replicaId = _replicaId,
                protocol = 2,
                seenMode = 0,
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            return orderId;
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class SalesChangesPageTestExtensions
{
    public static SalesOrderItem Order(this SalesChangesPage page, Guid id) =>
        page.Items.Single(i => i.Order?.Id == id).Order!;
}
