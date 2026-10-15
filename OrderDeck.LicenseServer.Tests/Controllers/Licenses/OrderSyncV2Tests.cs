using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>orders/sync</c>'in v2 biçimi (<c>format = 2</c>) — spec 2026-10-13 §4.2: yeni alanlar, ekleme ekleri
/// (kaynak kopya, başka kopyanın canlı yayını), öğe başına yanıt ve <c>current</c>. Birim kuralının kendisi ortak
/// vektörlerde; burada ucun biçimi ve yanıtı sabitlenir. Bugünkü istemcinin (v1) yanıtı değişmez.
/// </summary>
public sealed class OrderSyncV2Tests : IClassFixture<SessionOwnershipSyncTests.LogApiFactory>
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AddedAt = StartedAt.AddMinutes(10);

    private readonly SessionOwnershipSyncTests.LogApiFactory _factory;

    public OrderSyncV2Tests(SessionOwnershipSyncTests.LogApiFactory factory) => _factory = factory;

    // ── Ekleme ────────────────────────────────────────────────────────────────

    /// <summary>v2 eklemesi yerelde kalan 048 alanlarını, ebeveyni ve gönderen kopyayı (<c>OriginReplicaId</c>:
    /// kargo bağı bilinen sipariş, M22) yazar. Yanıt öğe başına: uygulandı, satırın sürümü; <c>current</c> yalnız
    /// yok sayılan ya da reddedilen satırda.</summary>
    [Fact]
    public async Task V2_ekleme_yeni_alanlari_ve_kaynak_kopyayi_yazar()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var batch = $"b-{Guid.NewGuid():N}"[..12];
        var item = NewItem(null) with
        {
            ParentOrderId = parentId,
            PrintedAt = AddedAt.AddMinutes(1),
            PrintBatchId = batch,
            PrintSeq = 4,
            PriceCorrectedAt = AddedAt.AddMinutes(2),
            PaidPrice = 120m,
        };

        var result = (await PushV2Async(client, licenseId, replicaId, item)).Single();

        result.Id.Should().Be(item.Id);
        result.Status.Should().Be("applied");
        result.IgnoredUnits.Should().BeEmpty();
        result.Reason.Should().BeNull();
        result.Current.Should().BeNull("uygulanan satırda current gönderilmez");
        var saved = await LoadAsync(item.Id);
        result.Version.Should().Be(saved.SyncVersion);
        saved.ParentOrderId.Should().Be(parentId);
        saved.PrintBatchId.Should().Be(batch);
        saved.PrintSeq.Should().Be(4);
        saved.PriceCorrectedAt.Should().Be(item.PriceCorrectedAt);
        saved.PaidPrice.Should().Be(120m);
        saved.OriginReplicaId.Should().Be(replicaId);
    }

    /// <summary>Kaynak kopya ve v2 alanları yalnız <c>format = 2</c>'de yazılır: protokolsüz istek de, zarfı taşıyıp
    /// biçimi bildirmeyen istek de v1 biçimidir — bağı bilinmeyen sipariş (<c>OriginReplicaId</c> boş, M22).</summary>
    [Fact]
    public async Task V1_ve_bicimsiz_zarfli_ekleme_kaynak_kopya_ve_v2_alanlarini_yazmaz()
    {
        var (client, licenseId) = await SetupAsync();
        var legacy = NewItem(null) with { ParentOrderId = Guid.NewGuid(), PrintBatchId = "b1", PaidPrice = 5m };
        var enveloped = NewItem(null) with { ParentOrderId = Guid.NewGuid(), PrintBatchId = "b2", PaidPrice = 5m };

        (await client.PostAsJsonAsync(Url(licenseId), new { orders = new[] { legacy } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(Url(licenseId),
                new { orders = new[] { enveloped }, replicaId = Guid.NewGuid(), protocol = 2, seenMode = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var id in new[] { legacy.Id, enveloped.Id })
        {
            var saved = await LoadAsync(id);
            saved.OriginReplicaId.Should().BeNull();
            saved.ParentOrderId.Should().BeNull();
            saved.PrintBatchId.Should().BeNull();
            saved.PaidPrice.Should().BeNull();
        }
    }

    // ── Güncelleme ve yanıt ──────────────────────────────────────────────────

    /// <summary>
    /// K2 (§4.2, M14): damgalı birimi yalnız satırın güncel sürümünü gördüğünü kanıtlayan v2 push'u yazar. Bayat
    /// <c>baseVersion</c> yok sayılır; yanıt hangi birimin yazılmadığını ve sunucunun güncel satırını
    /// (<c>current</c>, akış öğesi biçiminde) taşır. Güncel sürümle gelen yeni karar yazılır, damga kalır.
    /// </summary>
    [Fact]
    public async Task V2_bayat_surum_ignored_ve_current_doner_guncel_surum_yazar()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var item = NewItem(null);
        await PushV2Async(client, licenseId, replicaId, item);
        var stampedAt = DateTimeOffset.UtcNow;
        await SeedAsync(item.Id, o => { o.Price = 80m; o.PriceSetAt = stampedAt; o.SyncVersion = 7; });

        var stale = (await PushV2Async(client, licenseId, replicaId, item with { Price = 100m, BaseVersion = 6 }))
            .Single();

        stale.Status.Should().Be("ignored");
        stale.IgnoredUnits.Should().Equal("price");
        stale.Reason.Should().Be("unit-stamped");
        stale.Current.Should().NotBeNull();
        stale.Current!.Price.Should().Be(80m);
        stale.Current.PriceSetAt.Should().Be(stampedAt);
        stale.Current.Version.Should().Be(8, "işlenen her satırda sürüm ilerler");
        stale.Version.Should().Be(8);
        (await LoadAsync(item.Id)).Price.Should().Be(80m);

        var fresh = (await PushV2Async(client, licenseId, replicaId, item with { Price = 90m, BaseVersion = 8 }))
            .Single();

        fresh.Status.Should().Be("applied");
        fresh.Current.Should().BeNull();
        var saved = await LoadAsync(item.Id);
        saved.Price.Should().Be(90m);
        saved.PriceSetAt.Should().Be(stampedAt, "push damgayı silmez");
        fresh.Version.Should().Be(saved.SyncVersion);
    }

    /// <summary>
    /// K3 (§4.2): şu anda komutla iptal edilmiş siparişe gelen baskı alınmaz; v2'ye <c>ignoredUnits: [print]</c>,
    /// <c>cancelled-elsewhere</c> döner — istemci "kâğıdı imha et" der.
    /// </summary>
    [Fact]
    public async Task V2_komutla_iptal_edilmis_siparise_baski_cancelled_elsewhere()
    {
        var (client, licenseId) = await SetupAsync();
        var replicaId = Guid.NewGuid();
        var item = NewItem(null);
        await PushV2Async(client, licenseId, replicaId, item);
        var commandAt = DateTimeOffset.UtcNow;
        await SeedAsync(item.Id, o => { o.CancelledAt = commandAt; o.CancelReason = "komut"; o.CancelSetAt = commandAt; });

        // İstemci iptali görmüş (akıştan) ama kâğıt ondan önce çıkmış: yalnız baskı birimi farklı.
        var result = (await PushV2Async(client, licenseId, replicaId, item with
        {
            CancelledAt = commandAt, CancelReason = "komut",
            PrintedAt = AddedAt.AddMinutes(1), PrintBatchId = "b1", PrintSeq = 1,
        })).Single();

        result.Status.Should().Be("ignored");
        result.IgnoredUnits.Should().Equal("print");
        result.Reason.Should().Be("cancelled-elsewhere");
        result.Current!.CancelledAt.Should().Be(commandAt);
        (await LoadAsync(item.Id)).PrintedAt.Should().BeNull();
    }

    /// <summary>Bugünkü istemcinin yanıtı değişmez (§4.2 Yanıt): protokolsüz ve biçimsiz zarflı istek eski
    /// <c>SyncedOrderDto</c> listesini alır — v2 alanı sızmaz.</summary>
    [Fact]
    public async Task V1_yaniti_eski_DTO_listesi_olarak_kalir()
    {
        var (client, licenseId) = await SetupAsync();
        var legacy = new
        {
            id = Guid.NewGuid(), sessionId = (Guid?)null, customerId = $"c-{Guid.NewGuid():N}", platform = "youtube",
            username = "izleyici", displayName = (string?)null, messageText = "A1", code = "A1", price = 100m,
            addedAt = AddedAt, printedAt = (DateTimeOffset?)null, cancelledAt = (DateTimeOffset?)null,
            cancelReason = (string?)null, isShippingFee = false, isBackupPromoted = false, isTentativeBackup = false,
        };
        string[] expected =
        [
            "id", "sessionId", "customerId", "platform", "username", "displayName", "messageText", "code", "price",
            "addedAt", "printedAt", "cancelledAt", "cancelReason", "isShippingFee", "isBackupPromoted",
            "isTentativeBackup", "productId", "productVariantId", "updatedAt",
        ];

        foreach (var body in new object[]
                 {
                     new { orders = new[] { legacy } },
                     new { orders = new[] { legacy }, replicaId = Guid.NewGuid(), protocol = 2, seenMode = 0 },
                 })
        {
            var resp = await client.PostAsJsonAsync(Url(licenseId), body);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            var row = doc.RootElement.EnumerateArray().Single();
            row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(expected);
            row.GetProperty("id").GetGuid().Should().Be(legacy.id);
        }
    }

    // ── Başka kopyanın canlı yayını ──────────────────────────────────────────

    /// <summary>
    /// §4.2 ekleme dalı: v2'de başka kopyanın CANLI oturumuna ekleme satır başına reddedilir
    /// (<c>session-owned-elsewhere</c>); satır yazılmaz, bildirim üretmez, paketin öbür satırları uygulanır. Kendi
    /// canlı oturumu, başka kopyanın devrediliyor oturumu ve miras (sahipsiz) canlı oturum reddedilmez. Protokolsüz
    /// istek çağıranı bilmediği için reddedilmez.
    /// </summary>
    [Fact]
    public async Task Baska_kopyanin_canli_oturumuna_v2_ekleme_satir_basina_reddedilir()
    {
        var (client, licenseId) = await SetupAsync();
        var caller = Guid.NewGuid();
        var other = Guid.NewGuid();
        var othersLive = await SeedSessionAsync(licenseId, other);
        var othersHandingOver = await SeedSessionAsync(licenseId, other, endedAt: StartedAt.AddHours(2));
        var ownLive = await SeedSessionAsync(licenseId, caller);
        var legacyLive = await SeedSessionAsync(licenseId, owner: null);
        var rejected = NewItem(othersLive) with { PrintedAt = AddedAt.AddMinutes(1) };
        var accepted = new[] { NewItem(othersHandingOver), NewItem(ownLive), NewItem(legacyLive) };
        var pushesBefore = _factory.Push.Sent.Count;

        var results = await PushV2Async(client, licenseId, caller, accepted.Prepend(rejected).ToArray());

        var r = results.Single(x => x.Id == rejected.Id);
        r.Status.Should().Be("rejected");
        r.Reason.Should().Be("session-owned-elsewhere");
        r.Current.Should().BeNull("sunucuda satır yok");
        r.Version.Should().BeNull();
        (await ExistsAsync(rejected.Id)).Should().BeFalse();
        _factory.Push.Sent.Count.Should().Be(pushesBefore, "reddedilen basılmış satır bildirim üretmez");
        foreach (var a in accepted)
        {
            results.Single(x => x.Id == a.Id).Status.Should().Be("applied");
            (await ExistsAsync(a.Id)).Should().BeTrue();
        }

        var legacy = NewItem(othersLive);
        (await client.PostAsJsonAsync(Url(licenseId), new { orders = new[] { legacy } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ExistsAsync(legacy.Id)).Should().BeTrue("protokolsüz istekte çağıran bilinmez");
    }

    /// <summary>§8.7: geri yüklenmiş kopya, ilk görüldüğünden beri susan önceki kopyasının canlı yayınına etiket
    /// yazmaya devam eder — o yayının kuyruğu artık bu kopyada. Konuşan önceki kopyanın yayını başkasınındır.</summary>
    [Fact]
    public async Task Sessiz_onceki_kopyanin_canli_oturumuna_ekleme_reddedilmez()
    {
        var (client, licenseId) = await SetupAsync();
        var previous = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var restoredSince = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedReplicaAsync(licenseId, previous, firstSeenAt: restoredSince.AddDays(-3), lastSeenAt: restoredSince.AddMinutes(-5));
        await SeedReplicaAsync(licenseId, restored, firstSeenAt: restoredSince, previousReplicaId: previous);
        var live = await SeedSessionAsync(licenseId, previous);
        var item = NewItem(live);

        (await PushV2Async(client, licenseId, restored, item)).Single().Status.Should().Be("applied");
        (await ExistsAsync(item.Id)).Should().BeTrue();
    }

    /// <summary>
    /// §4.2 ekleme dalı, §8.7, Y7, Y8: geri yüklenmiş kopyanın, önceki kopyasının canlı yayınına eklemesi başka makinede
    /// yalnız önceki kopya <c>ForceEndSilenceMinutes</c> (15) sustuktan sonra kabul edilir; öncesinde
    /// <c>session-owned-elsewhere</c> ile reddedilir. Aynı makinede (aynı parmak izi) hemen kabul edilir.
    /// </summary>
    [Theory]
    [InlineData(false, 5, true)]
    [InlineData(false, 20, false)]
    [InlineData(true, 1, false)]
    public async Task Geri_yuklenmis_kopyanin_canli_oturuma_eklemesi_makine_kuralina_gore(
        bool sameMachine, int silentMinutes, bool rejected)
    {
        var (client, licenseId) = await SetupAsync();
        var (previous, restored) = await SeedRestoredAsync(licenseId, silentMinutes, sameMachine);
        var live = await SeedSessionAsync(licenseId, previous);
        var item = NewItem(live);

        var result = (await PushV2Async(client, licenseId, restored, item)).Single();

        result.Status.Should().Be(rejected ? "rejected" : "applied");
        result.Reason.Should().Be(rejected ? "session-owned-elsewhere" : null);
        (await ExistsAsync(item.Id)).Should().Be(!rejected);
    }

    /// <summary>
    /// K5 istisnası (i), §8.7, Y8 (mod On): zorla bitirilmiş ve devredilmiş oturumda geri yüklenmiş kopyanın push'u
    /// ancak sahibin yerine geçebiliyorsa K2 ile yargılanır (damgasız iptal alınır). Oturum zorla bitirildiğinde
    /// canlıydı (M18), kural canlı oturumunki: başka makinedeki kopya önceki kopya 15 dk susmadan sahip sayılmaz,
    /// push'u <c>session-released</c> ile reddedilir; aynı makinede hemen sahip sayılır.
    /// </summary>
    [Theory]
    [InlineData(false, 5, false)]
    [InlineData(false, 20, true)]
    [InlineData(true, 1, true)]
    public async Task K5_istisna_i_geri_yuklenmis_kopyada_makine_kuralina_gore(
        bool sameMachine, int silentMinutes, bool judgedByK2)
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var (previous, restored) = await SeedRestoredAsync(licenseId, silentMinutes, sameMachine);
        var endedAt = StartedAt.AddHours(2);
        var forceEnded = await SeedSessionAsync(licenseId, previous, endedAt: endedAt, releasedAt: endedAt,
            forceEndedBy: "cli");
        var item = NewItem(forceEnded);
        var inserted = (await PushV2SeenAsync(client, licenseId, restored, seenMode: 1, item)).Single();
        inserted.Status.Should().Be("applied", "devredilmiş oturuma ekleme kabul edilir");
        var cancelledAt = StartedAt.AddHours(3);

        var result = (await PushV2SeenAsync(client, licenseId, restored, seenMode: 1,
            item with { CancelledAt = cancelledAt, CancelReason = "vazgeçti", BaseVersion = inserted.Version }))
            .Single();

        result.Status.Should().Be(judgedByK2 ? "applied" : "rejected");
        result.Reason.Should().Be(judgedByK2 ? null : "session-released");
        (await LoadAsync(item.Id)).CancelledAt.Should().Be(judgedByK2 ? cancelledAt : null);
    }

    /// <summary>
    /// K5 istisnası (i), §8.7, Y8, §4.10 (CLI <c>force-end</c>): oturum devrediliyor hâlindeyken zorla bitirildiyse
    /// (sahibin kapanışı <c>EndedAt</c>'i önce yazmıştı; devir anı ondan farklı) devralma yargısı canlı oturumun değil,
    /// devrediliyor oturumunkidir. Başka makinedeki geri yüklenmiş kopya önceki kopya ne kadar sessiz olursa olsun sahip
    /// sayılmaz: yedeğindeki bayat damgasız değişiklik K5 ile reddedilir. Aynı makinede kopya hemen sahiptir.
    /// </summary>
    [Theory]
    [InlineData(false, 60, false)]
    [InlineData(true, 1, true)]
    public async Task K5_istisna_i_devrediliyorken_zorla_bitirilmis_oturumu_baska_makine_devralmaz(
        bool sameMachine, int silentMinutes, bool judgedByK2)
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var (previous, restored) = await SeedRestoredAsync(licenseId, silentMinutes, sameMachine);
        var ownerEndedAt = StartedAt.AddHours(2);
        var forceEnded = await SeedSessionAsync(licenseId, previous, endedAt: ownerEndedAt,
            releasedAt: ownerEndedAt.AddDays(1), forceEndedBy: "cli");
        var item = NewItem(forceEnded);
        var inserted = (await PushV2SeenAsync(client, licenseId, restored, seenMode: 1, item)).Single();
        inserted.Status.Should().Be("applied", "devredilmiş oturuma ekleme kabul edilir");
        var cancelledAt = StartedAt.AddHours(3);

        var result = (await PushV2SeenAsync(client, licenseId, restored, seenMode: 1,
            item with { CancelledAt = cancelledAt, CancelReason = "vazgeçti", BaseVersion = inserted.Version }))
            .Single();

        result.Status.Should().Be(judgedByK2 ? "applied" : "rejected");
        result.Reason.Should().Be(judgedByK2 ? null : "session-released");
        (await LoadAsync(item.Id)).CancelledAt.Should().Be(judgedByK2 ? cancelledAt : null);
    }

    // ── Doğrulama ─────────────────────────────────────────────────────────────

    /// <summary>Biçim ve öğe alanları görünür 400 alır, hiçbir şey yazılmaz: v2 zarfsız <c>format = 2</c> (çağıranı
    /// bilinmeyen v2 satırı), bilinmeyen biçim, kolonu aşan baskı grubu.</summary>
    [Fact]
    public async Task Gecersiz_bicim_ve_oge_alani_400()
    {
        var (client, licenseId) = await SetupAsync();
        var a = NewItem(null);
        var b = NewItem(null);
        var c = NewItem(null) with { PrintBatchId = new string('b', 41) };

        await Expect400(new { orders = new[] { a }, format = 2 }, "invalid-format");
        await Expect400(new { orders = new[] { b }, format = 3, replicaId = Guid.NewGuid(), protocol = 2, seenMode = 0 },
            "invalid-format");
        await Expect400(new { orders = new[] { c }, format = 2, replicaId = Guid.NewGuid(), protocol = 2, seenMode = 0 },
            "invalid-order");
        foreach (var id in new[] { a.Id, b.Id, c.Id })
            (await ExistsAsync(id)).Should().BeFalse();

        async Task Expect400(object body, string title)
        {
            var resp = await client.PostAsJsonAsync(Url(licenseId), body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await resp.Content.ReadAsStringAsync()).Should().Contain(title);
        }
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    internal sealed record Item(
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
        Guid? ProductId = null,
        Guid? ProductVariantId = null,
        int? BaseVersion = null,
        Guid? ParentOrderId = null,
        string? PrintBatchId = null,
        int? PrintSeq = null,
        DateTimeOffset? PriceCorrectedAt = null,
        decimal? PaidPrice = null,
        Guid? ShipmentId = null);

    internal sealed record V2Result(
        Guid Id,
        int? Version,
        Guid? ShipmentId,
        DateTimeOffset? SessionReleasedAt,
        string Status,
        List<string> IgnoredUnits,
        string? Reason,
        SalesOrderItem? Current);

    internal static Item NewItem(Guid? sessionId) => new(
        Id: Guid.NewGuid(),
        SessionId: sessionId,
        CustomerId: Guid.NewGuid().ToString("N"),
        Platform: "youtube",
        Username: $"u-{Guid.NewGuid():N}",
        DisplayName: "İzleyici",
        MessageText: "A1 alırım",
        Code: "A1",
        Price: 100m,
        AddedAt: AddedAt,
        PrintedAt: null,
        CancelledAt: null,
        CancelReason: null,
        IsShippingFee: false,
        IsBackupPromoted: false,
        IsTentativeBackup: false);

    private static string Url(Guid licenseId) => $"/api/v1/licenses/{licenseId}/orders/sync";

    private static Task<List<V2Result>> PushV2Async(
        HttpClient client, Guid licenseId, Guid replicaId, params Item[] items) =>
        PushV2SeenAsync(client, licenseId, replicaId, seenMode: 0, items);

    private static async Task<List<V2Result>> PushV2SeenAsync(
        HttpClient client, Guid licenseId, Guid replicaId, int seenMode, params Item[] items)
    {
        var resp = await client.PostAsJsonAsync(Url(licenseId),
            new { orders = items, format = 2, replicaId, protocol = 2, seenMode });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<V2Result>>())!;
    }

    private async Task SeedAsync(Guid orderId, Action<Order> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        change(await db.Orders.SingleAsync(o => o.Id == orderId));
        await db.SaveChangesAsync();
    }

    private async Task<Order> LoadAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<bool> ExistsAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AnyAsync(o => o.Id == orderId);
    }

    private async Task SeedReplicaAsync(
        Guid licenseId, Guid replicaId, DateTimeOffset firstSeenAt, DateTimeOffset? lastSeenAt = null,
        Guid? previousReplicaId = null, string? fingerprint = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WpfReplicas.Add(new WpfReplica
        {
            LicenseId = licenseId,
            ReplicaId = replicaId,
            Protocol = 2,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = lastSeenAt ?? firstSeenAt,
            PreviousReplicaId = previousReplicaId,
            HardwareFingerprint = fingerprint,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Geri yüklenmiş kopya ve önceki kopyası: önceki kopya <paramref name="silentMinutes"/> önce son kez
    /// konuştu, yeni kopya ondan sonra ilk kez görüldü (§8.7'nin "sustu" koşulu tutar). Parmak izleri aynı makine ise
    /// eşit, değilse farklı (üretilmiş).</summary>
    private async Task<(Guid Previous, Guid Restored)> SeedRestoredAsync(
        Guid licenseId, int silentMinutes, bool sameMachine)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = Guid.NewGuid();
        var restored = Guid.NewGuid();
        var previousLastSeen = now.AddMinutes(-silentMinutes);
        var previousFingerprint = $"fp-{Guid.NewGuid():N}";
        await SeedReplicaAsync(licenseId, previous, firstSeenAt: now.AddDays(-3), lastSeenAt: previousLastSeen,
            fingerprint: previousFingerprint);
        await SeedReplicaAsync(licenseId, restored, firstSeenAt: previousLastSeen.AddSeconds(silentMinutes * 30),
            previousReplicaId: previous,
            fingerprint: sameMachine ? previousFingerprint : $"fp-{Guid.NewGuid():N}");
        return (previous, restored);
    }

    private async Task<Guid> SeedSessionAsync(
        Guid licenseId, Guid? owner, DateTimeOffset? endedAt = null, DateTimeOffset? releasedAt = null,
        string? forceEndedBy = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Akşam yayını",
            StartedAt = StartedAt,
            EndedAt = endedAt,
            Platforms = "youtube",
            UpdatedAt = StartedAt,
            OwnerReplicaId = owner,
            OwnerMachineName = owner is null ? null : "PC",
            ReleasedAt = releasedAt,
            ForceEndedBy = forceEndedBy,
        };
        db.StreamSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync(SalesSyncMode mode = SalesSyncMode.Off)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-V2-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? DateTimeOffset.UtcNow.AddDays(-1) : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }
}
