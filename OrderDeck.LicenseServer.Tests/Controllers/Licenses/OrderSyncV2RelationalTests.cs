using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using V2Result = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.V2Result;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>orders/sync</c> v2'nin birim kuralı gerçek SQL Server'da — spec 2026-10-13 §10.1-20, -22 (ekleme), -23. Birim
/// kararı, sipariş satırının <c>SyncVersion</c> jetonu altında ve defterle aynı SaveChanges'te yazılır; InMemory jetonu
/// uygulamadığı için damganın ve sürümün gerçekten korunduğu ancak burada kanıtlanır.
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class OrderSyncV2RelationalTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    private readonly SqlServerContainerFixture _sql;

    public OrderSyncV2RelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    // ── §10.1-20: K2 baseVersion ─────────────────────────────────────────────

    /// <summary>
    /// Komutun damgaladığı fiyat ve iptal (S2'nin komutları gibi elle basılır): v1 hiç yazamaz; bayat
    /// <c>baseVersion</c>'lı v2 yok sayılır ve güncel satırı alır; güncel sürümü gören v2 yeni kararı yazar, damgalar
    /// kalır (M14).
    /// </summary>
    [Fact]
    public async Task K2_guncel_surumlu_v2_damgali_birimi_yazar_bayat_ve_v1_yazamaz()
    {
        await using var ctx = await Context.CreateAsync(_sql);
        var item = OrderSyncV2Tests.NewItem(null);
        await ctx.PushV2Async(item);
        var commandAt = DateTimeOffset.UtcNow;
        await ctx.SeedAsync(item.Id, o =>
        {
            o.Price = 80m;
            o.PriceSetAt = commandAt;
            o.CancelledAt = commandAt;
            o.CancelReason = "komut";
            o.CancelSetAt = commandAt;
        });
        var seeded = await ctx.OrderAsync(item.Id);

        (await ctx.PushV1Async(item with { Price = 100m })).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterV1 = await ctx.OrderAsync(item.Id);
        afterV1.Price.Should().Be(80m, "v1 damgalı birimi yazamaz");
        afterV1.CancelledAt.Should().NotBeNull();

        var stale = (await ctx.PushV2Async(item with { Price = 100m, BaseVersion = seeded.SyncVersion })).Single();
        stale.Status.Should().Be("ignored");
        stale.IgnoredUnits.Should().BeEquivalentTo("price", "cancel");
        stale.Current!.Price.Should().Be(80m);
        stale.Current.CancelledAt.Should().NotBeNull();
        stale.Current.Version.Should().Be(stale.Version!.Value);
        (await ctx.OrderAsync(item.Id)).Price.Should().Be(80m);

        var fresh = (await ctx.PushV2Async(item with { Price = 90m, BaseVersion = stale.Version })).Single();
        fresh.Status.Should().Be("applied");
        var saved = await ctx.OrderAsync(item.Id);
        saved.Price.Should().Be(90m);
        saved.CancelledAt.Should().BeNull("güncel sürümü gören v2 iptali de geri alır");
        saved.PriceSetAt.Should().Be(seeded.PriceSetAt, "damga kalır");
        saved.CancelSetAt.Should().Be(seeded.CancelSetAt);
        saved.SyncVersion.Should().Be(fresh.Version!.Value);
    }

    // ── §10.1-20: K5 ──────────────────────────────────────────────────────────

    /// <summary>
    /// K5 (mod On): devredilmiş oturumda fiyat ve iptal değişikliği reddedilir — v2'ye <c>rejected</c>,
    /// <c>session-released</c> ve güncel satır; v1 yazamaz, yalnız protokolsüz istek izi düşer. Devredilmiş oturuma
    /// ekleme ve baskı doldurma kabul edilir.
    /// </summary>
    [Fact]
    public async Task K5_devredilmis_oturumda_v2_reddedilir_v1_yazamaz_iz_duser()
    {
        await using var ctx = await Context.CreateAsync(_sql, mode: SalesSyncMode.On);
        var sessionId = await ctx.SeedSessionAsync(
            owner: ctx.ReplicaId, endedAt: StartedAt.AddHours(2), releasedAt: StartedAt.AddHours(2));
        var item = OrderSyncV2Tests.NewItem(sessionId);
        (await ctx.PushV2Async(item, 1)).Single().Status.Should().Be("applied", "ekleme kabul edilir");

        var rejected = (await ctx.PushV2AsAsync(ctx.ReplicaId, 1,
            item with { Price = 90m, CancelledAt = StartedAt.AddHours(3), CancelReason = "iade", BaseVersion = 0 }))
            .Single();
        rejected.Status.Should().Be("rejected");
        rejected.Reason.Should().Be("session-released");
        rejected.IgnoredUnits.Should().BeEquivalentTo("price", "cancel");
        rejected.Current!.Price.Should().Be(100m);
        rejected.Current.CancelledAt.Should().BeNull();

        var printedAt = StartedAt.AddHours(4);
        (await ctx.PushV2AsAsync(ctx.ReplicaId, 1, item with { PrintedAt = printedAt, PrintBatchId = "b1", PrintSeq = 1 }))
            .Single().Status.Should().Be("applied", "K3 doldurma kabul edilir");

        (await ctx.LegacyTraceAsync()).Should().BeNull("ön koşul: protokolsüz istek yok");
        (await ctx.PushV1Async(item with { Price = 90m, PrintedAt = printedAt })).StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ctx.OrderAsync(item.Id);
        saved.Price.Should().Be(100m);
        saved.CancelledAt.Should().BeNull();
        saved.PrintedAt.Should().Be(printedAt);
        saved.PrintBatchId.Should().Be("b1");
        (await ctx.LegacyTraceAsync()).Should().NotBeNull("v1 için yalnız iz yazılır");
    }

    /// <summary>
    /// K5 istisnası (i): zorla bitirilmiş oturumda sahibin v2 push'u reddedilmez, K2 ile yargılanır — komutun
    /// damgaladığı fiyat kazanır (<c>ignored</c>), damgasız iptal push'tan alınır. Sahip olmayanın push'u reddedilir.
    /// </summary>
    [Fact]
    public async Task K5_istisna_i_zorla_bitirilmis_oturumda_sahip_K2_ile_yargilanir()
    {
        await using var ctx = await Context.CreateAsync(_sql, mode: SalesSyncMode.On);
        var sessionId = await ctx.SeedSessionAsync(
            owner: ctx.ReplicaId, endedAt: StartedAt.AddHours(2), releasedAt: StartedAt.AddHours(2),
            forceEndedBy: "cli");
        var owned = OrderSyncV2Tests.NewItem(sessionId);
        var foreign = OrderSyncV2Tests.NewItem(sessionId);
        await ctx.PushV2Async(owned, foreign);
        await ctx.SeedAsync(owned.Id, o => { o.Price = 80m; o.PriceSetAt = DateTimeOffset.UtcNow; o.SyncVersion = 5; });
        var cancelledAt = StartedAt.AddHours(3);

        var result = (await ctx.PushV2AsAsync(ctx.ReplicaId, 1,
            owned with { Price = 100m, CancelledAt = cancelledAt, CancelReason = "vazgeçti", BaseVersion = 0 }))
            .Single();
        result.Status.Should().Be("ignored");
        result.IgnoredUnits.Should().Equal("price");
        result.Reason.Should().Be("unit-stamped");
        var saved = await ctx.OrderAsync(owned.Id);
        saved.Price.Should().Be(80m, "komutun damgaladığı birim kazanır");
        saved.CancelledAt.Should().Be(cancelledAt, "damgasız birim sahibin push'undan alınır");

        var other = (await ctx.PushV2AsAsync(Guid.NewGuid(), 1, foreign with { CancelledAt = cancelledAt })).Single();
        other.Status.Should().Be("rejected");
        other.Reason.Should().Be("session-released");
        (await ctx.OrderAsync(foreign.Id)).CancelledAt.Should().BeNull();
    }

    /// <summary>
    /// K5 istisnası (ii), §4.0, M21: mod açılmadan önce var olan, geri yüklenmemiş ve modu görmemiş kopyanın
    /// <c>SeenMode = 0</c> push'u K2 ile yargılanır (damgasız iptal alınır). Yedekten geri yüklenmiş
    /// (<c>PreviousReplicaId</c>) ya da modu bu dönemde görmüş (<c>SeenModeOnAt ≥ ModeChangedAt</c>) kopyanın 0'ı On
    /// kurallarıyla yargılanır: reddedilir.
    /// </summary>
    [Fact]
    public async Task K5_istisna_ii_acilis_penceresi_K2_ile_gecmisi_tutmayan_reddedilir()
    {
        var modeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await using var ctx = await Context.CreateAsync(_sql, mode: SalesSyncMode.On, modeChangedAt: modeChangedAt);
        var before = modeChangedAt.AddDays(-2);
        var window = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var sawMode = Guid.NewGuid();
        await ctx.SeedReplicaAsync(window, before);
        await ctx.SeedReplicaAsync(restored, before, previousReplicaId: Guid.NewGuid());
        await ctx.SeedReplicaAsync(sawMode, before, seenModeOnAt: modeChangedAt.AddMinutes(5));
        var sessionId = await ctx.SeedSessionAsync(
            owner: Guid.NewGuid(), endedAt: StartedAt.AddHours(2), releasedAt: StartedAt.AddHours(2));
        var a = OrderSyncV2Tests.NewItem(sessionId);
        var b = OrderSyncV2Tests.NewItem(sessionId);
        var c = OrderSyncV2Tests.NewItem(sessionId);
        (await ctx.PushV1Async(a, b, c)).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelledAt = StartedAt.AddHours(3);

        (await ctx.PushV2AsAsync(window, 0, a with { CancelledAt = cancelledAt })).Single().Status
            .Should().Be("applied");
        (await ctx.PushV2AsAsync(restored, 0, b with { CancelledAt = cancelledAt })).Single().Reason
            .Should().Be("session-released");
        (await ctx.PushV2AsAsync(sawMode, 0, c with { CancelledAt = cancelledAt })).Single().Reason
            .Should().Be("session-released");

        (await ctx.OrderAsync(a.Id)).CancelledAt.Should().Be(cancelledAt);
        (await ctx.OrderAsync(b.Id)).CancelledAt.Should().BeNull();
        (await ctx.OrderAsync(c.Id)).CancelledAt.Should().BeNull();
    }

    // ── §10.1-22: eklemede KVKK ──────────────────────────────────────────────

    /// <summary>
    /// <c>SalesSync:ScrubOrderTextOnPurge</c> açıkken asıl projeksiyonu silinmiş müşteriye silmeden sonra gelen geç
    /// ekleme (v1 ya da v2; kopyanın Id'siyle de) metni boş yazar ve <c>TextScrubbedAt</c> basar; kullanıcı adı, kod ve
    /// fiyat kalır. Silinmemiş müşterinin metni kalır.
    /// </summary>
    [Fact]
    public async Task Ayar_acikken_silinmis_musteriye_gec_ekleme_metni_bos_yazar()
    {
        await using var ctx = await Context.CreateAsync(_sql, scrubOrderText: true);
        var (canonical, copy) = await ctx.SeedPurgedCustomerAsync();
        var v1 = OrderSyncV2Tests.NewItem(null) with { CustomerId = canonical };
        var v2 = OrderSyncV2Tests.NewItem(null) with { CustomerId = copy };
        var other = OrderSyncV2Tests.NewItem(null);

        (await ctx.PushV1Async(v1)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ctx.PushV2Async(v2, other);

        foreach (var id in new[] { v1.Id, v2.Id })
        {
            var row = await ctx.OrderAsync(id);
            row.DisplayName.Should().BeNull();
            row.MessageText.Should().BeEmpty();
            row.TextScrubbedAt.Should().NotBeNull();
            row.Code.Should().Be("A1");
            row.Price.Should().Be(100m);
            row.Username.Should().NotBeNullOrEmpty();
        }
        var kept = await ctx.OrderAsync(other.Id);
        kept.MessageText.Should().Be(other.MessageText);
        kept.TextScrubbedAt.Should().BeNull();
    }

    /// <summary>Ayar kapalıyken (varsayılan, bugünkü politika) geç ekleme metni saklar; akış yine okuma anında
    /// maskeler (§9).</summary>
    [Fact]
    public async Task Ayar_kapaliyken_gec_ekleme_metni_saklar_akis_maskeler()
    {
        await using var ctx = await Context.CreateAsync(_sql, scrubOrderText: false);
        var (canonical, _) = await ctx.SeedPurgedCustomerAsync();
        var sessionId = await ctx.SeedSessionAsync(owner: ctx.ReplicaId, endedAt: StartedAt.AddHours(2),
            releasedAt: StartedAt.AddHours(2));
        var item = OrderSyncV2Tests.NewItem(sessionId) with { CustomerId = canonical };

        await ctx.PushV2Async(item);

        var row = await ctx.OrderAsync(item.Id);
        row.MessageText.Should().Be(item.MessageText);
        row.DisplayName.Should().Be(item.DisplayName);
        row.TextScrubbedAt.Should().BeNull();
        var fed = (await ctx.PageAsync()).Order(item.Id);
        fed.MessageText.Should().BeEmpty();
        fed.DisplayName.Should().BeNull();
    }

    // ── §10.1-23: K3 ve komut iptali ─────────────────────────────────────────

    /// <summary>
    /// K3 (§4.2, §15-40): şu anda komutla iptal edilmiş siparişe gelen baskı alınmaz (<c>cancelled-elsewhere</c>,
    /// <c>ignoredUnits: [print]</c>); komutla iptal edilip komutla geri alınmış (damga dolu, iptal boş) siparişin
    /// baskısı alınır; aynı push'ta "basıldı, sonra iptal edildi" satırında damga yoktur ve baskı yazılır.
    /// </summary>
    [Fact]
    public async Task K3_komut_iptali_baskiyi_engeller_geri_alinmis_ve_ayni_push_yazilir()
    {
        await using var ctx = await Context.CreateAsync(_sql);
        var cancelled = OrderSyncV2Tests.NewItem(null);
        var uncancelled = OrderSyncV2Tests.NewItem(null);
        var samePush = OrderSyncV2Tests.NewItem(null);
        await ctx.PushV2Async(cancelled, uncancelled, samePush);
        var commandAt = DateTimeOffset.UtcNow;
        await ctx.SeedAsync(cancelled.Id, o => { o.CancelledAt = commandAt; o.CancelReason = "komut"; o.CancelSetAt = commandAt; });
        await ctx.SeedAsync(uncancelled.Id, o => o.CancelSetAt = commandAt);
        var printedAt = StartedAt.AddMinutes(30);

        var results = await ctx.PushV2Async(
            cancelled with
            {
                CancelledAt = commandAt, CancelReason = "komut", PrintedAt = printedAt, PrintBatchId = "b1", PrintSeq = 1,
            },
            uncancelled with { PrintedAt = printedAt, PrintBatchId = "b1", PrintSeq = 2 },
            samePush with
            {
                PrintedAt = printedAt, PrintBatchId = "b1", PrintSeq = 3,
                CancelledAt = printedAt.AddMinutes(5), CancelReason = "vazgeçti",
            });

        var c = results.Single(r => r.Id == cancelled.Id);
        c.Status.Should().Be("ignored");
        c.IgnoredUnits.Should().Equal("print");
        c.Reason.Should().Be("cancelled-elsewhere");
        c.Current!.PrintedAt.Should().BeNull();
        (await ctx.OrderAsync(cancelled.Id)).PrintedAt.Should().BeNull();

        results.Single(r => r.Id == uncancelled.Id).Status.Should().Be("applied");
        var u = await ctx.OrderAsync(uncancelled.Id);
        u.PrintedAt.Should().Be(printedAt);
        u.PrintSeq.Should().Be(2);

        results.Single(r => r.Id == samePush.Id).Status.Should().Be("applied");
        var s = await ctx.OrderAsync(samePush.Id);
        s.PrintedAt.Should().Be(printedAt);
        s.CancelledAt.Should().NotBeNull();
    }

    // ── Kurulum ──────────────────────────────────────────────────────────────

    private sealed class ScrubFactory(string connectionString, bool scrub) : RelationalApiFactory(connectionString)
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:ScrubOrderTextOnPurge"] = scrub ? "true" : "false",
        };
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly RelationalApiFactory _factory;
        private readonly HttpClient _client;

        private Context(RelationalApiFactory factory, HttpClient client, Guid licenseId)
        {
            _factory = factory;
            _client = client;
            LicenseId = licenseId;
        }

        public Guid LicenseId { get; }
        public Guid ReplicaId { get; } = Guid.NewGuid();

        public static async Task<Context> CreateAsync(
            SqlServerContainerFixture sql, bool scrubOrderText = false, SalesSyncMode mode = SalesSyncMode.Off,
            DateTimeOffset? modeChangedAt = null)
        {
            var factory = new ScrubFactory(await sql.CreateDatabaseAsync(), scrubOrderText);
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
                    SalesSyncModeChangedAt = modeChangedAt ?? (mode == SalesSyncMode.On ? now.AddDays(-1) : null),
                });
                await db.SaveChangesAsync();
            }

            return new Context(factory, client, licenseId);
        }

        private string Url(string path) => $"/api/v1/licenses/{LicenseId}/{path}";

        public Task<HttpResponseMessage> PushV1Async(params Item[] items) =>
            _client.PostAsJsonAsync(Url("orders/sync"), new { orders = items });

        public Task<List<V2Result>> PushV2Async(params Item[] items) => PushV2AsAsync(ReplicaId, 0, items);

        public Task<List<V2Result>> PushV2Async(Item item, int seenMode) => PushV2AsAsync(ReplicaId, seenMode, item);

        public async Task<List<V2Result>> PushV2AsAsync(Guid replicaId, int seenMode, params Item[] items)
        {
            var resp = await _client.PostAsJsonAsync(Url("orders/sync"),
                new { orders = items, format = 2, replicaId, protocol = 2, seenMode });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            return (await resp.Content.ReadFromJsonAsync<List<V2Result>>())!;
        }

        public async Task<SalesChangesPage> PageAsync()
        {
            var resp = await _client.GetAsync(Url("sales/changes?afterSeq=0"));
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            return (await resp.Content.ReadFromJsonAsync<SalesChangesPage>())!;
        }

        public async Task SeedAsync(Guid orderId, Action<Order> change)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            change(await db.Orders.SingleAsync(o => o.Id == orderId));
            await db.SaveChangesAsync();
        }

        public async Task<Order> OrderAsync(Guid id)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
        }

        /// <summary>Lisansın protokolsüz istek izi; satır yoksa null.</summary>
        public async Task<DateTimeOffset?> LegacyTraceAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            return await db.SalesLegacyTraces.AsNoTracking()
                .Where(t => t.LicenseId == LicenseId)
                .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
                .SingleOrDefaultAsync();
        }

        /// <summary>Asıl kaydı KVKK ile silinmiş bir müşteri ve onun birleştirilmiş kopyası (hex Id'ler).</summary>
        public async Task<(string Canonical, string Copy)> SeedPurgedCustomerAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var now = DateTimeOffset.UtcNow;
            var canonical = new WpfCustomerProjection
            {
                Id = Guid.NewGuid(), LicenseId = LicenseId, Platform = "youtube",
                Username = $"silinen-{Guid.NewGuid():N}", UpdatedAt = now, PurgedAt = now.AddMinutes(-5),
            };
            var copy = new WpfCustomerProjection
            {
                Id = Guid.NewGuid(), LicenseId = LicenseId, Platform = "youtube",
                Username = $"kopya-{Guid.NewGuid():N}", UpdatedAt = now, MergedIntoId = canonical.Id,
            };
            db.WpfCustomerProjections.AddRange(canonical, copy);
            await db.SaveChangesAsync();
            return (canonical.Id.ToString("N"), copy.Id.ToString("N"));
        }

        public async Task SeedReplicaAsync(
            Guid replicaId, DateTimeOffset firstSeenAt, Guid? previousReplicaId = null,
            DateTimeOffset? seenModeOnAt = null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfReplicas.Add(new WpfReplica
            {
                LicenseId = LicenseId,
                ReplicaId = replicaId,
                Protocol = 2,
                FirstSeenAt = firstSeenAt,
                LastSeenAt = firstSeenAt,
                PreviousReplicaId = previousReplicaId,
                SeenModeOnAt = seenModeOnAt,
            });
            await db.SaveChangesAsync();
        }

        public async Task<Guid> SeedSessionAsync(
            Guid? owner, DateTimeOffset? endedAt = null, DateTimeOffset? releasedAt = null,
            string? forceEndedBy = null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var session = new StreamSession
            {
                Id = Guid.NewGuid(),
                LicenseId = LicenseId,
                Title = "Akşam yayını",
                StartedAt = StartedAt,
                EndedAt = endedAt,
                Platforms = "youtube",
                UpdatedAt = StartedAt,
                OwnerReplicaId = owner,
                OwnerMachineName = owner is null ? null : "PC-A",
                ReleasedAt = releasedAt,
                ForceEndedBy = forceEndedBy,
            };
            db.StreamSessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
