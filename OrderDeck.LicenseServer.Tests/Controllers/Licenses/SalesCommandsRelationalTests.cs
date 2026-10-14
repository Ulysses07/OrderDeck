using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;
using V2Result = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.V2Result;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST sales/commands</c>'ın eşzamanlılık sözleşmesi gerçek SQL Server'da — spec 2026-10-13 §4.6 adım 5–6, §10.1-5,
/// §10.1-13. Komut siparişi <c>SyncVersion</c> jetonu altında ve tek işlemde yazar; InMemory jetonu ve işlemi
/// uygulamadığı için yarışın hakemi ancak burada kanıtlanır.
///
/// <para>Yarış sırayla kurulur (zamanlamayla değil): bir isteğin asıl kaydetmesi diske inmeden hemen önce kanca öbür
/// isteği baştan sona koşturur (<see cref="ShippingSettingsRelationalTests"/> deseni). Kanca yalnız seçilen kaydetmede
/// tetiklenir; isteklerin kalp atışı (kopya satırı) ondan önce ayrı kaydedilir.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesCommandsRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connectionString = null!;
    private CommandHookFactory _factory = null!;
    private Kit _kit = null!;

    public SalesCommandsRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connectionString = await _sql.CreateDatabaseAsync();
        _factory = new CommandHookFactory(_connectionString);
        _kit = await Kit.CreateAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Before = null;
        _kit.Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── §10.1-5: komut ile sahibin geç push'u ────────────────────────────────

    /// <summary>
    /// §10.1-5 (a): komutun yazımı diske inmeden hemen önce sahibin geç push'u (zorla bitirilmiş yayın, K5 istisnası i)
    /// aynı satırı yazar. Push kazanır; komut jetona çarpar, işlem tümüyle geri alınır ve 409 <c>stale</c> güncel satırı
    /// taşır. Yalnız biri 409; komutun hiçbir yazımı (iptal, damga, günlük) kalmaz.
    /// </summary>
    [Fact]
    public async Task Push_komutun_yazimindan_once_gelirse_komut_409_stale_push_kazanir()
    {
        var order = await SeedForceEndedOrderAsync();
        HttpResponseMessage? push = null;
        _factory.Hook.When = db => db.ChangeTracker.Entries<SalesCommandLog>().Any();
        _factory.Hook.Before = async () => push = await OwnerPushAsync(ItemOf(order) with { Price = 90m });

        var command = await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order));

        push.Should().NotBeNull("kanca push'u komutun yazımından önce koşturmalı");
        push!.StatusCode.Should().Be(HttpStatusCode.OK, await push.Content.ReadAsStringAsync());
        command.StatusCode.Should().Be(HttpStatusCode.Conflict, await command.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(command);
        problem.GetProperty("title").GetString().Should().Be("stale");
        var current = problem.GetProperty("current").EnumerateArray().Should().ContainSingle().Subject;
        current.GetProperty("price").GetDecimal().Should().Be(90m, "taze satır push'un yazımını gösterir");
        current.GetProperty("version").GetInt32().Should().Be(2);

        var saved = await _kit.OrderAsync(order.Id);
        saved.Price.Should().Be(90m);
        saved.CancelledAt.Should().BeNull("komutun yazımı geri alındı");
        saved.CancelSetAt.Should().BeNull();
        saved.SyncVersion.Should().Be(2);
        (await _kit.LogsAsync()).Should().BeEmpty("reddedilen komut günlüğe girmez");
    }

    /// <summary>
    /// §10.1-5 (b): push'un yazımı diske inmeden hemen önce komut baştan sona koşar. Komut kazanır; push jetona çarpar ve
    /// 409 <c>sync-conflict</c> alır (istemci yeniden gönderir). Yalnız biri 409.
    /// </summary>
    [Fact]
    public async Task Komut_pushun_yaziminden_once_gelirse_push_409_komut_kazanir()
    {
        var order = await SeedForceEndedOrderAsync();
        HttpResponseMessage? command = null;
        _factory.Hook.When = db => db.ChangeTracker.Entries<Order>().Any(e => e.State == EntityState.Modified)
            && !db.ChangeTracker.Entries<SalesCommandLog>().Any();
        _factory.Hook.Before = async () => command = await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order));

        var push = await OwnerPushAsync(ItemOf(order) with { Price = 90m });

        command.Should().NotBeNull("kanca komutu push'un yazımından önce koşturmalı");
        command!.StatusCode.Should().Be(HttpStatusCode.OK, await command.Content.ReadAsStringAsync());
        push.StatusCode.Should().Be(HttpStatusCode.Conflict, await push.Content.ReadAsStringAsync());
        (await Kit.ProblemAsync(push)).GetProperty("title").GetString().Should().Be("sync-conflict");

        var saved = await _kit.OrderAsync(order.Id);
        saved.CancelledAt.Should().NotBeNull();
        saved.CancelSetAt.Should().NotBeNull();
        saved.Price.Should().Be(100m, "push'un yazımı geri alındı");
        saved.SyncVersion.Should().Be(2);
        (await _kit.LogsAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// §10.1-5, K2, K5 istisnası (i): komuttan sonra zorla bitirilmiş yayının sahibi bayat <c>baseVersion</c>'lı v2
    /// push'uyla damgalı iptali geri alamaz — <c>ignored</c> ve güncel satır; damgasız fiyat değişikliği push'tan
    /// alınır (push toptan reddedilmez).
    /// </summary>
    [Fact]
    public async Task Komuttan_sonra_sahibin_bayat_pushu_damgali_iptali_degistiremez()
    {
        var order = await SeedForceEndedOrderAsync();
        (await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await _kit.OrderAsync(order.Id);

        var push = await OwnerPushAsync(ItemOf(order) with { Price = 95m, BaseVersion = order.SyncVersion });

        push.StatusCode.Should().Be(HttpStatusCode.OK, await push.Content.ReadAsStringAsync());
        var result = (await push.Content.ReadFromJsonAsync<List<V2Result>>())!.Single();
        result.Status.Should().Be("ignored");
        result.IgnoredUnits.Should().Equal("cancel");
        result.Current!.CancelledAt.Should().Be(cancelled.CancelledAt);
        result.Current.CancelSetAt.Should().Be(cancelled.CancelSetAt);

        var saved = await _kit.OrderAsync(order.Id);
        saved.CancelledAt.Should().Be(cancelled.CancelledAt, "damgalı birimi bayat push değiştiremez");
        saved.CancelReason.Should().Be("customer");
        saved.Price.Should().Be(95m, "damgasız birim sahibin push'undan alınır");
    }

    // ── §10.1-13: mod On'da protokolsüz push ─────────────────────────────────

    /// <summary>§10.1-13: mod On'da protokolsüz (≤0.9.10) push komutun iptalini geri alamaz; lisansın eski sürüm izi
    /// (<c>SalesLegacyTraces</c>) yazılır.</summary>
    [Fact]
    public async Task Mod_On_da_protokolsuz_push_komut_iptalini_geri_alamaz_iz_yazilir()
    {
        var order = await _kit.SeedOrderAsync(await _kit.SeedSessionAsync(Kit.Phase.Released));
        (await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await _kit.OrderAsync(order.Id);
        (await LegacyTraceAsync()).Should().BeNull("ön koşul: protokolsüz istek yok");

        var push = await _kit.SendToAsync(OrdersUrl, new { orders = new[] { ItemOf(order) } }, userAgent: null);

        push.StatusCode.Should().Be(HttpStatusCode.OK, await push.Content.ReadAsStringAsync());
        var saved = await _kit.OrderAsync(order.Id);
        saved.CancelledAt.Should().Be(cancelled.CancelledAt, "v1 damgalı birimi yazamaz");
        saved.CancelSetAt.Should().Be(cancelled.CancelSetAt);
        (await LegacyTraceAsync()).Should().NotBeNull("protokolsüz istek izi iki kipte yazılır");
    }

    // ── Aynı komutun eşzamanlı ikinci kopyası ────────────────────────────────

    /// <summary>
    /// Aynı <c>CommandId</c> eşzamanlı iki kez: ikincisi birincinin yazımından önce baştan sona koşar ve kaydeder. Birinci
    /// sipariş jetonuna (yazacak satır varsa) ya da günlüğün birincil anahtarına (yoksa) çarpar, günlüğü yeniden okur ve
    /// kayıtlı sonucu oynatır: komut bir kez uygulanır, iki istek de 200 ve aynı sonucu alır.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ayni_komut_eszamanli_iki_kez_bir_kez_uygulanir_ikisi_de_ayni_sonucu_alir(bool alreadyCancelled)
    {
        var order = await _kit.SeedOrderAsync(await _kit.SeedSessionAsync(Kit.Phase.Released), o =>
        {
            if (alreadyCancelled)
            {
                o.CancelledAt = Kit.StartedAt.AddHours(3);
                o.CancelReason = "customer";
            }
        });
        var body = _kit.Cancel(Guid.NewGuid(), "customer", order);
        HttpResponseMessage? rival = null;
        _factory.Hook.When = db => db.ChangeTracker.Entries<SalesCommandLog>().Any();
        _factory.Hook.Before = async () => rival = await _kit.SendAsync(body);

        var first = await _kit.SendAsync(body);

        rival.Should().NotBeNull("kanca ikinci kopyayı birincinin yazımından önce koşturmalı");
        rival!.StatusCode.Should().Be(HttpStatusCode.OK, await rival.Content.ReadAsStringAsync());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var rivalBody = await Kit.ReadAsync(rival);
        var firstBody = await Kit.ReadAsync(first);
        firstBody.Status.Should().Be(rivalBody.Status);
        firstBody.Result.GetRawText().Should().Be(rivalBody.Result.GetRawText());
        firstBody.Status.Should().Be(alreadyCancelled ? "unchanged" : "applied");

        (await _kit.LogsAsync()).Should().ContainSingle();
        (await _kit.OrderAsync(order.Id)).SyncVersion.Should().Be(alreadyCancelled ? 1 : 2, "komut bir kez uygulanır");
    }

    // ── Uygulama kilidi ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SalesCommandLock"/>: kilit işleme aittir — tutan işlem sürerken aynı kaynağı isteyen ikinci işlem
    /// zaman aşımında 409 <c>stale</c> alır (sınırsız beklemez); ilk işlem bitince kilit serbesttir. İşlemsiz çağrı
    /// programlama hatasıdır.
    /// </summary>
    [Fact]
    public async Task Uygulama_kilidi_islem_boyunca_tutulur_zaman_asiminda_409_stale()
    {
        var resource = $"ship:{_kit.LicenseId:N}:{Guid.NewGuid():N}";
        await using var holder = NewDb();
        await using var held = await holder.Database.BeginTransactionAsync();
        await SalesCommandLock.AcquireAsync(holder, resource, TimeSpan.FromSeconds(1), default);

        await using var waiter = NewDb();
        await using (var waiting = await waiter.Database.BeginTransactionAsync())
        {
            var blocked = () => SalesCommandLock.AcquireAsync(waiter, resource, TimeSpan.FromMilliseconds(200), default);
            (await blocked.Should().ThrowAsync<SalesCommandRejection>()).Which.Code.Should().Be("stale");

            await held.CommitAsync();
            await SalesCommandLock.AcquireAsync(waiter, resource, TimeSpan.FromSeconds(1), default);
            await waiting.RollbackAsync();
        }

        var withoutTransaction = () => SalesCommandLock.AcquireAsync(waiter, resource, TimeSpan.FromSeconds(1), default);
        await withoutTransaction.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private string OrdersUrl => $"/api/v1/licenses/{_kit.LicenseId}/orders/sync";

    /// <summary>Zorla bitirilmiş, devredilmiş bir yayının siparişi: sahibin v2 push'u K2 ile yargılanır (K5 istisnası i).</summary>
    private async Task<Order> SeedForceEndedOrderAsync() =>
        await _kit.SeedOrderAsync(await _kit.SeedSessionAsync(Kit.Phase.Released, forceEndedBy: "cli"));

    /// <summary>Yayın sahibinin (PC-A) v2 push'u.</summary>
    private Task<HttpResponseMessage> OwnerPushAsync(Item item) =>
        _kit.SendToAsync(OrdersUrl, new
        {
            orders = new[] { item },
            format = 2,
            replicaId = _kit.OwnerReplicaId,
            protocol = 2,
            seenMode = 1,
        });

    /// <summary>Siparişin push öğesi; <c>baseVersion</c> satırın şimdiki sürümü.</summary>
    private static Item ItemOf(Order o) => new(
        Id: o.Id,
        SessionId: o.SessionId,
        CustomerId: o.CustomerId,
        Platform: o.Platform,
        Username: o.Username,
        DisplayName: o.DisplayName,
        MessageText: o.MessageText,
        Code: o.Code,
        Price: o.Price,
        AddedAt: o.AddedAt,
        PrintedAt: o.PrintedAt,
        CancelledAt: o.CancelledAt,
        CancelReason: o.CancelReason,
        IsShippingFee: o.IsShippingFee,
        IsBackupPromoted: o.IsBackupPromoted,
        IsTentativeBackup: o.IsTentativeBackup,
        BaseVersion: o.SyncVersion);

    private async Task<DateTimeOffset?> LegacyTraceAsync() =>
        await _kit.ReadDbAsync(db => db.SalesLegacyTraces.AsNoTracking()
            .Where(t => t.LicenseId == _kit.LicenseId)
            .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
            .SingleOrDefaultAsync());

    private LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connectionString).Options);

    /// <summary>Yalnız <see cref="When"/>'i tutan kaydetmenin hemen öncesinde bir kez koşar; yeniden girmez (kanca
    /// gövdesindeki rakip istek kendi kaydetmesinde onu tetiklemez).</summary>
    private sealed class PredicateSaveHook : SaveChangesInterceptor
    {
        private bool _running;

        public Func<DbContext, bool>? When { get; set; }

        public Func<Task>? Before { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Before is { } hook && When is { } when && !_running && when(eventData.Context!))
            {
                Before = null;
                _running = true;
                try { await hook(); }
                finally { _running = false; }
            }
            return result;
        }
    }

    private sealed class CommandHookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public PredicateSaveHook Hook { get; } = new();

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
