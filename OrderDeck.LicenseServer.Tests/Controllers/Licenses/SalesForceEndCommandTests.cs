using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;
using V2Result = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.V2Result;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>Alt sürümü tanımlı, sahip sessizliği eşiği 30 dk (<c>SalesSync:ForceEndSilenceMinutes</c>) olan sunucu.</summary>
public sealed class SalesSyncThirtyMinuteApiFactory : ApiFactory
{
    protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
    {
        ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
        ["SalesSync:ForceEndSilenceMinutes"] = "30",
    };
}

/// <summary>
/// <c>force-end</c> komutu — spec 2026-10-13 §2 (Devir b), §4.6, §5.3, §11 Y7, Y8, M18, §12 risk 4 ve 5 (InMemory).
/// Yalnız CANLI yayın ve yalnız sahibi <c>ForceEndSilenceMinutes</c> sustuktan sonra bitirilir; devrediliyor yayın
/// (etiketleri sahipte bekliyor) devralınmaz. Bitirme yayını aynı işlemde devreder. Sahibin kapanışıyla yarış ve akışa
/// giriş gerçek SQL Server'da (<see cref="SalesCommandsRelationalTests"/>).
/// </summary>
public sealed class SalesForceEndCommandTests
    : IClassFixture<SalesSyncConfiguredApiFactory>, IClassFixture<SalesSyncThirtyMinuteApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _factory;
    private readonly SalesSyncThirtyMinuteApiFactory _thirty;

    public SalesForceEndCommandTests(SalesSyncConfiguredApiFactory factory, SalesSyncThirtyMinuteApiFactory thirty)
    {
        _factory = factory;
        _thirty = thirty;
    }

    /// <summary>Mod Off'ta komut ucu hiçbir şey yazmaz (bugünkü davranış, §4.6 adım 1).</summary>
    [Fact]
    public async Task ForceEnd_mod_Off_409_mode_off_hicbir_sey_yazilmaz()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-20));

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("mode-off");
        await ShouldBeUntouchedLiveAsync(kit, session);
    }

    /// <summary>Y7: sahip 5 dk önce sinyal verdi — 409 <c>owner-active</c>, son sinyal ve makine adıyla (§5.3 metni);
    /// hiçbir şey yazılmaz.</summary>
    [Fact]
    public async Task ForceEnd_sahip_5_dk_once_goruldu_409_owner_active()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-5);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, lastSeen);

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("owner-active");
        problem.GetProperty("lastSeenAt").GetDateTimeOffset().Should().Be(lastSeen);
        problem.GetProperty("machine").GetString().Should().Be("PC-A");
        problem.GetProperty("sessionId").GetGuid().Should().Be(session);
        await ShouldBeUntouchedLiveAsync(kit, session);
    }

    /// <summary>
    /// Sahip 20 dk sessiz (varsayılan eşik 15): yayın bitirilir ve aynı işlemde devredilir. <c>EndedAt</c> sunucu saati
    /// (istemci saati yok), <c>ForceEndedBy</c> çağıran kopya, <c>ReleasedAt</c> aynı an; yayının bütün siparişleri o anla
    /// damgalanır (DM3) ama birimleri ve sürümleri değişmez. Yayın <c>sales/state</c>'in canlı listesinden çıkar; başka
    /// yayına dokunulmaz. Yanıt yayını güncel hâliyle taşır; sipariş taşımaz.
    /// </summary>
    [Fact]
    public async Task ForceEnd_sahip_20_dk_sessiz_yayini_bitirir_ve_ayni_anda_devreder()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-20));
        var printed = await kit.SeedOrderAsync(session);
        var unprinted = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);
        var otherSession = await kit.SeedSessionAsync(Kit.Phase.Live, owner: Guid.NewGuid());
        var other = await kit.SeedOrderAsync(otherSession);
        (await LiveSessionIdsAsync(kit)).Should().Contain(session);
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        var after = DateTimeOffset.UtcNow;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        body.Status.Should().Be("applied");
        body.Orders.Should().BeEmpty();
        body.Result.TryGetProperty("alreadyReleased", out _).Should().BeFalse();
        var item = body.Sessions.Should().ContainSingle().Subject;
        item.Id.Should().Be(session);
        item.ForceEndedBy.Should().Be(kit.ReplicaId.ToString());
        item.EndedAt.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(after);
        item.ReleasedAt.Should().Be(item.EndedAt, "devir bitirmeyle aynı anda");

        var saved = await kit.SessionAsync(session);
        saved.Should().BeEquivalentTo(new
        {
            item.EndedAt,
            item.ReleasedAt,
            ForceEndedBy = kit.ReplicaId.ToString(),
            OwnerReplicaId = (Guid?)kit.OwnerReplicaId,
            UpdatedAt = item.EndedAt!.Value,
        });
        foreach (var order in new[] { printed, unprinted })
            (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(
                new { SessionReleasedAt = saved.ReleasedAt, SyncVersion = 1, order.PrintedAt, order.Price },
                "devir damgası birime ve sürüme dokunmaz");
        (await kit.OrderAsync(other.Id)).SessionReleasedAt.Should().BeNull("başka yayın canlı kalır");
        (await LiveSessionIdsAsync(kit)).Should().NotContain(session).And.Contain(otherSession);

        var log = (await kit.LogsAsync()).Should().ContainSingle().Subject;
        log.Kind.Should().Be("force-end");
        log.Status.Should().Be("applied");
        log.ReplicaId.Should().Be(kit.ReplicaId);
    }

    /// <summary>Eşik ayardan okunur (Y7, <c>SalesSync:ForceEndSilenceMinutes</c> = 30): 20 dk'lık sessizlik yetmez, 40
    /// dk yeter.</summary>
    [Theory]
    [InlineData(20, false)]
    [InlineData(40, true)]
    public async Task ForceEnd_sessizlik_esigi_ayardan_okunur(int silentMinutes, bool ends)
    {
        var kit = await Kit.CreateAsync(_thirty);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-silentMinutes));

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        if (ends)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            (await kit.SessionAsync(session)).ReleasedAt.Should().NotBeNull();
        }
        else
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("owner-active");
            await ShouldBeUntouchedLiveAsync(kit, session);
        }
    }

    /// <summary>
    /// Y8, §15-43: devrediliyor yayın (bitti ama sahibin etiketleri gönderilmedi) sahip ne kadar sessiz olursa olsun
    /// bitirilmez — 409 <c>handing-over</c>, sahibin bildirdiği gönderilmemiş etiket sayısı, makine ve son sinyalle.
    /// Hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task ForceEnd_devrediliyor_yayinda_409_handing_over_gonderilmemis_sayiyla()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.HandingOver, unsentLabelCount: 4);
        var lastSeen = DateTimeOffset.UtcNow.AddHours(-2);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, lastSeen);
        var order = await kit.SeedOrderAsync(session);

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("handing-over");
        problem.GetProperty("unsentLabelCount").GetInt32().Should().Be(4);
        problem.GetProperty("machine").GetString().Should().Be("PC-A");
        problem.GetProperty("ownerLastSeenAt").GetDateTimeOffset().Should().Be(lastSeen);
        problem.GetProperty("sessionId").GetGuid().Should().Be(session);

        (await kit.SessionAsync(session)).Should().BeEquivalentTo(new
        {
            EndedAt = (DateTimeOffset?)Kit.StartedAt.AddHours(2),
            ReleasedAt = (DateTimeOffset?)null,
            ForceEndedBy = (string?)null,
            UnsentLabelCount = (int?)4,
            UpdatedAt = Kit.StartedAt,
        });
        (await kit.OrderAsync(order.Id)).SessionReleasedAt.Should().BeNull();
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Zaten devredilmiş yayın: etkisiz başarı (iptal edilmiş siparişe <c>cancel</c> gibi) — 200 <c>unchanged</c>,
    /// <c>result.alreadyReleased = true</c>, yayın güncel hâliyle; <c>ForceEndedBy</c> ve devir anı değişmez. Sahibin
    /// sinyaline bakılmaz: devredilmiş yayının bitirilecek bir şeyi yok.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("cli")]
    public async Task ForceEnd_devredilmis_yayinda_etkisiz_200_alreadyReleased(string? forceEndedBy)
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released, forceEndedBy: forceEndedBy);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow);
        var before = await kit.SessionAsync(session);

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        body.Status.Should().Be("unchanged");
        body.Result.GetProperty("alreadyReleased").GetBoolean().Should().BeTrue();
        var item = body.Sessions.Should().ContainSingle().Subject;
        item.Should().BeEquivalentTo(new { Id = session, before.EndedAt, before.ReleasedAt, ForceEndedBy = forceEndedBy });
        (await kit.SessionAsync(session)).Should().BeEquivalentTo(
            new { before.EndedAt, before.ReleasedAt, ForceEndedBy = forceEndedBy, before.UpdatedAt });
        (await kit.LogsAsync()).Should().ContainSingle().Which.Status.Should().Be("unchanged");
    }

    /// <summary>
    /// Sahipsiz (eski sürümde açılmış, miras) canlı yayın: sahibin sessizliği ölçülemez — kapalı-hata, 409
    /// <c>owner-active</c> ve boş son sinyal. Onu kapatmak Burak'ın CLI'ının işi (§4.10 "sahibi hiç kalmamış").
    /// </summary>
    [Fact]
    public async Task ForceEnd_sahipsiz_canli_yayinda_409_owner_active()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.WithDbAsync(async db =>
            (await db.StreamSessions.SingleAsync(s => s.Id == session)).OwnerReplicaId = null);

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("owner-active");
        problem.GetProperty("lastSeenAt").ValueKind.Should().Be(JsonValueKind.Null);
        await ShouldBeUntouchedLiveAsync(kit, session);
    }

    /// <summary>Sahibi yazılı ama sahip kopyanın satırı hiç yok (sinyal vermemiş): sessiz sayılır, yayın bitirilir.</summary>
    [Fact]
    public async Task ForceEnd_sahip_kopya_satiri_yoksa_sessiz_sayilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);

        var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.SessionAsync(session);
        saved.ForceEndedBy.Should().Be(kit.ReplicaId.ToString());
        saved.ReleasedAt.Should().NotBeNull();
    }

    /// <summary>Aynı CommandId'nin yeniden gönderimi: komut yeniden uygulanmaz, aynı durum ve sonuç, yayın güncel
    /// hâliyle (§4.6 adım 2).</summary>
    [Fact]
    public async Task ForceEnd_ayni_CommandId_yeniden_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-20));
        var body = kit.ForceEnd(Guid.NewGuid(), session);

        var first = await Kit.ReadAsync(await kit.SendAsync(body));
        var ended = await kit.SessionAsync(session);
        var again = await kit.SendAsync(body);

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        var replay = await Kit.ReadAsync(again);
        replay.Status.Should().Be(first.Status).And.Be("applied");
        replay.Result.GetRawText().Should().Be(first.Result.GetRawText());
        replay.Sessions.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Id = session, ended.EndedAt, ended.ReleasedAt, ended.ForceEndedBy });
        (await kit.SessionAsync(session)).Should().BeEquivalentTo(
            new { ended.EndedAt, ended.ReleasedAt, ended.UpdatedAt });
        (await kit.LogsAsync()).Should().ContainSingle();
    }

    /// <summary>Onaysız ya da bozuk yük 400 <c>invalid-payload</c>: <c>confirm</c> true olmalı (§4.6), yayın Id'si dolu,
    /// sipariş sürümü yok. Hiçbir şey yazılmaz.</summary>
    [Theory]
    [InlineData("confirm-false")]
    [InlineData("confirm-missing")]
    [InlineData("session-empty")]
    [InlineData("expected-given")]
    public async Task ForceEnd_onaysiz_ya_da_bozuk_yuk_400_invalid_payload(string shape)
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-20));
        var body = shape switch
        {
            "confirm-false" => kit.ForceEnd(Guid.NewGuid(), session, confirm: false),
            "confirm-missing" => kit.ForceEnd(Guid.NewGuid(), session, confirm: null),
            "session-empty" => kit.ForceEnd(Guid.NewGuid(), Guid.Empty),
            _ => kit.Body(Guid.NewGuid(), "force-end", new { sessionId = session, confirm = true },
                [(Guid.NewGuid(), 1)]),
        };

        var resp = await kit.SendAsync(body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("invalid-payload");
        await ShouldBeUntouchedLiveAsync(kit, session);
    }

    /// <summary>Yayın bu lisansta yoksa (hiç yok ya da aynı yayıncının başka lisansında) 400 <c>unknown-session</c>;
    /// başka lisansın yayınına dokunulmaz.</summary>
    [Fact]
    public async Task ForceEnd_bilinmeyen_ya_da_baska_lisansin_yayini_400_unknown_session()
    {
        var kit = await Kit.CreateAsync(_factory);
        var foreign = await kit.SecondLicenseAsync();
        var foreignSession = await foreign.SeedSessionAsync(Kit.Phase.Live);

        foreach (var sessionId in new[] { Guid.NewGuid(), foreignSession })
        {
            var resp = await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), sessionId));

            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("unknown-session");
        }
        await ShouldBeUntouchedLiveAsync(foreign, foreignSession);
    }

    /// <summary>
    /// M18, K5 istisnası (i), risk 5: zorla bitirmeden sonra sahip (aslında yayında) bağlanınca yeni etiketi kabul
    /// edilir ve devir anını kopyalar (akışa girer, DM3); damgasız birim değişikliği push'tan alınır; başka bilgisayarın
    /// sonradan komutla damgaladığı birim (iptal) kazanır — <c>ignored</c> ve güncel satır. Sahibin kendi kapanışı
    /// devri görür (<c>ReleasedAt</c> döner) ve zorla bitirmenin <c>EndedAt</c>'ini değiştirmez (§4.1 tekdüzelik).
    /// </summary>
    [Fact]
    public async Task ForceEnd_sonrasi_sahibin_pushu_K2_ile_yargilanir_kapanisi_EndedAti_degistirmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Live);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, DateTimeOffset.UtcNow.AddMinutes(-20));
        var stamped = await kit.SeedOrderAsync(session);
        var unstamped = await kit.SeedOrderAsync(session);
        (await kit.SendAsync(kit.ForceEnd(Guid.NewGuid(), session))).StatusCode.Should().Be(HttpStatusCode.OK);
        var ended = await kit.SessionAsync(session);
        (await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", stamped))).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await kit.OrderAsync(stamped.Id);
        var added = OrderSyncV2Tests.NewItem(session);

        var push = await kit.SendToAsync($"/api/v1/licenses/{kit.LicenseId}/orders/sync", new
        {
            orders = new[] { added, ItemOf(unstamped) with { Price = 90m }, ItemOf(stamped) },
            format = 2,
            replicaId = kit.OwnerReplicaId,
            protocol = 2,
            seenMode = 1,
        });

        push.StatusCode.Should().Be(HttpStatusCode.OK, await push.Content.ReadAsStringAsync());
        var results = (await push.Content.ReadFromJsonAsync<List<V2Result>>())!.ToDictionary(r => r.Id);
        results[added.Id].Should().BeEquivalentTo(new { Status = "applied", SessionReleasedAt = ended.ReleasedAt });
        results[unstamped.Id].Status.Should().Be("applied", "damgasız birim push'tan alınır (K5 istisnası i)");
        var ignored = results[stamped.Id];
        ignored.Status.Should().Be("ignored");
        ignored.IgnoredUnits.Should().Equal("cancel");
        ignored.Current!.CancelledAt.Should().Be(cancelled.CancelledAt);
        (await kit.OrderAsync(added.Id)).SessionReleasedAt.Should().Be(ended.ReleasedAt);
        (await kit.OrderAsync(unstamped.Id)).Price.Should().Be(90m);
        (await kit.OrderAsync(stamped.Id)).CancelledAt.Should().Be(cancelled.CancelledAt, "komutun kararı kazanır");

        var close = await kit.SendToAsync(
            kit.SessionsUrl, kit.OwnerClose(session, Kit.StartedAt.AddHours(3), release: true));

        close.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
        (await close.Content.ReadFromJsonAsync<List<SessionReply>>())!.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = session, ended.EndedAt, ended.ReleasedAt });
        (await kit.SessionAsync(session)).Should().BeEquivalentTo(
            new { ended.EndedAt, ended.ReleasedAt, ended.ForceEndedBy });
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary><c>sessions/sync</c> yanıtının bu testlerin okuduğu alanları.</summary>
    internal sealed record SessionReply(Guid Id, DateTimeOffset? EndedAt, DateTimeOffset? ReleasedAt, string Status);

    private sealed record StateBody(List<StateSession> LiveSessions);

    private sealed record StateSession(Guid Id, string State);

    private static async Task<List<Guid>> LiveSessionIdsAsync(Kit kit)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/licenses/{kit.LicenseId}/sales/state");
        request.Headers.UserAgent.ParseAdd(Kit.WpfUserAgent);
        var resp = await kit.Client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<StateBody>())!.LiveSessions.Select(s => s.Id).ToList();
    }

    /// <summary>Canlı yayın hiç yazılmadı: bitmedi, devredilmedi, zorla bitirme izi yok, günlük boş.</summary>
    private static async Task ShouldBeUntouchedLiveAsync(Kit kit, Guid sessionId)
    {
        (await kit.SessionAsync(sessionId)).Should().BeEquivalentTo(new
        {
            EndedAt = (DateTimeOffset?)null,
            ReleasedAt = (DateTimeOffset?)null,
            ForceEndedBy = (string?)null,
            UpdatedAt = Kit.StartedAt,
        });
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>Siparişin sahip push öğesi; <c>baseVersion</c> satırın tohumlandığı andaki sürümü.</summary>
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
}
