using System.Globalization;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Satış komutlarının istemcisi (spec 2026-10-13 §4.6, §5.3, §5.7; W2-04): tel zarfı (replicaId, protocol 2, seenMode,
/// expected), niyet başına tek CommandId ve aynen yeniden deneme (sunucu bir kez uygular), stale'de güncel satırların
/// uygulanması ve yalnız iptal/geri alma/ilk baskıda tek kendiliğinden deneme, her 409'un sonucu ve Türkçe metni,
/// SalesConflictLog, 404, mode-off olayı, güncelleme sorusu, mod Off'ta sıfır istek, yanıt satırlarının gönderim
/// kuyruğuna girmeden yazılması. Sunucu <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class SalesCommandClientTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L);

    private sealed class License(string? key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private sealed class PromptSpy : IAppUpdatePrompt
    {
        public int Calls;
        public void CheckAndPrompt() => Interlocked.Increment(ref Calls);
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly Guid _otherReplica = Guid.NewGuid();
    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly PromptSpy _prompt = new();
    private readonly CapturingLogger<SalesCommandClient> _log = new();
    private readonly SalesCommandClient _client;
    private int _modeOffEvents;

    public SalesCommandClientTests()
    {
        new MigrationRunner(_db).Run();
        _server.Mode = 1;
        _server.MinClientVersion = "0.9.11";
        var license = new License(_server.Lisans);
        var http = new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        _api = new LicenseApiClient(http, new LicenseTokenStore());
        _envelope = TestSalesEnvelope.For(_db, license);
        _client = new SalesCommandClient(_api, _envelope, new SalesSyncStateRepository(_db), new LabelRepository(_db),
            new ShipmentRepository(_db), new SalesFeedRepository(_db), new SalesConflictLogRepository(_db), _prompt,
            license, new Clock(), _log);
        _client.ModeOffDetected += (_, _) => Interlocked.Increment(ref _modeOffEvents);
        Exec("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
    }

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
    }

    // ── kurulum ───────────────────────────────────────────────────────────

    private void Exec(string sql, object? param = null)
    {
        using var c = _db.Open();
        c.Execute(sql, param);
    }

    private T Scalar<T>(string sql, object? param = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, param)!;
    }

    private Guid RemoteCustomer()
    {
        var id = Guid.NewGuid();
        _server.SeedCustomer(id, "tiktok", "alici_ornek", "Takma Ad");
        return id;
    }

    /// <summary>Başka bilgisayarın yayını; <paramref name="released"/> ise devredilmiş (komut görebilir).</summary>
    private Guid RemoteSession(bool released = true, string machine = "PC-B")
    {
        var id = Guid.NewGuid();
        _server.SeedSession(id, T0, released ? T0.AddHours(1) : null, owner: _otherReplica, title: "Akşam yayını",
            ownerMachineName: machine);
        if (released) _server.ReleaseSession(id);
        return id;
    }

    private static FakeSalesServer.OrderBody OrderRow(Guid id, Guid sessionId, Guid customer, decimal price = 150m,
        bool printed = true, bool tentative = false, bool cancelled = false) =>
        new(id, sessionId, customer.ToString("N"), "tiktok", "alici_ornek", "Takma Ad", "MAVI XL aldım", "A1", price,
            T0.AddMinutes(1), printed ? T0.AddMinutes(2) : null, cancelled ? T0.AddMinutes(3) : null,
            cancelled ? "musteri" : null, false, tentative, tentative, null, null, null, null, null, null, null, null, null);

    private Guid RemoteOrder(Guid sessionId, Guid customer, decimal price = 150m, bool printed = true,
        bool tentative = false, bool cancelled = false)
    {
        var id = Guid.NewGuid();
        _server.SeedOrder(OrderRow(id, sessionId, customer, price, printed, tentative, cancelled), version: 1,
            origin: _otherReplica);
        return id;
    }

    /// <summary>Devredilmiş bir yayının bir etiketi: sunucuda ve (akışla) yerelde, sürüm 1.</summary>
    private async Task<Guid> RemoteLabelAsync(decimal price = 150m, bool printed = true, bool tentative = false,
        bool cancelled = false)
    {
        var order = RemoteOrder(RemoteSession(), RemoteCustomer(), price, printed, tentative, cancelled);
        await PullAsync();
        return order;
    }

    /// <summary>Sunucunun akışını yerele uygular (W2-03'ün akış yolu).</summary>
    private async Task PullAsync()
    {
        var feed = new SalesFeedRepository(_db);
        long after = 0;
        while (true)
        {
            var page = await _api.GetSalesChangesAsync(_server.LicenseId, after, 500, _envelope.ReplicaId!.Value, 1);
            if (page.Items!.Count == 0) return;
            var side = SalesFeedMapper.SideSessions(page);
            var drafts = SalesFeedMapper.Drafts(page);
            foreach (var item in page.Items)
                feed.ApplyItem(SalesFeedMapper.ToCore(item), side, drafts, new SalesFeedApplyOptions(true, false),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    .Status.Should().BeOneOf(SalesFeedApplyStatus.Applied, SalesFeedApplyStatus.Unchanged);
            after = page.NextAfterSeq;
        }
    }

    private sealed class LabelState
    {
        public decimal Price { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public long? PrintedAt { get; init; }
        public string? PrintBatchId { get; init; }
        public long? PrintSeq { get; init; }
        public long IsTentativeBackup { get; init; }
        public long? ServerVersion { get; init; }
        public long? SyncedAt { get; init; }
        public long Revision { get; init; }
    }

    private LabelState Local(Guid id)
    {
        using var c = _db.Open();
        return c.QuerySingle<LabelState>(
            @"SELECT Price, CancelledAt, CancelReason, PrintedAt, PrintBatchId, PrintSeq, IsTentativeBackup,
                     ServerVersion, SyncedAt, Revision
                FROM Label WHERE Id = @id", new { id = id.ToString("N") });
    }

    private static string N(Guid id) => id.ToString("N");

    private static string Hhmm(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private void NoFaults() => _server.Faults.Should().BeEmpty();

    // ── tel zarfı ve uygulama ─────────────────────────────────────────────

    [Fact]
    public async Task Zarf_kopya_protokol_kip_ve_beklenen_surumu_tasir_iptal_sunucuda_ve_yerelde_uygulanir()
    {
        var order = await RemoteLabelAsync();

        var intent = _client.Cancel([N(order)], "musteri vazgecti");
        var outcome = await _client.SendAsync(intent);

        outcome.Status.Should().Be(SalesCommandStatus.Applied);
        outcome.Message.Should().BeNull();
        outcome.ServerStatus.Should().Be(SalesCommandResponse.StatusApplied);
        outcome.Result!.Changed.Should().Equal(order);
        outcome.LabelIds.Should().Contain(N(order));

        var wire = _server.CommandCalls.Should().ContainSingle().Subject.Json;
        wire.GetProperty("commandId").GetGuid().Should().Be(intent.CommandId);
        wire.GetProperty("replicaId").GetGuid().Should().Be(_envelope.ReplicaId!.Value);
        wire.GetProperty("protocol").GetInt32().Should().Be(2);
        wire.GetProperty("seenMode").GetInt32().Should().Be(1);
        wire.GetProperty("kind").GetString().Should().Be("cancel");
        wire.GetProperty("payload").GetProperty("items").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(order);
        wire.GetProperty("payload").GetProperty("reason").GetString().Should().Be("musteri vazgecti");
        var expected = wire.GetProperty("expected").EnumerateArray().Should().ContainSingle().Subject;
        expected.GetProperty("orderId").GetGuid().Should().Be(order);
        expected.GetProperty("version").GetInt32().Should().Be(1, "akıştan inen ServerVersion");

        var server = _server.Order(order)!;
        server.Row.CancelledAt.Should().NotBeNull();
        server.CancelSetAt.Should().NotBeNull("komut damgası sunucu saatiyle");
        server.Version.Should().Be(2);
        var local = Local(order);
        local.CancelledAt.Should().NotBeNull();
        local.CancelReason.Should().Be("musteri vazgecti");
        local.ServerVersion.Should().Be(2);
        NoFaults();
    }

    [Fact]
    public async Task Yanit_satiri_ServerVersioni_ilerletir_gonderim_kuyruguna_girmez_Revision_ve_SyncedAt_ayni()
    {
        var order = await RemoteLabelAsync();
        var before = Local(order);

        (await _client.SendAsync(_client.Cancel([N(order)], "musteri vazgecti"))).IsApplied.Should().BeTrue();

        var after = Local(order);
        after.CancelledAt.Should().NotBeNull();
        after.ServerVersion.Should().Be(2);
        after.SyncedAt.Should().Be(before.SyncedAt, "DM8: akış ve komut yanıtı gönderim kuyruğuna dokunmaz");
        after.Revision.Should().Be(before.Revision);
        new LabelRepository(_db).GetUnsynced().Should().BeEmpty("yankı yok: satır yeniden gönderilmez");
    }

    [Fact]
    public async Task Zaman_asiminda_ayni_kimlik_ve_yukle_yeniden_dener_sunucu_bir_kez_uygular()
    {
        var order = await RemoteLabelAsync();
        var intent = _client.Cancel([N(order)], "musteri vazgecti");
        _server.LoseNextResponse(FakeSalesServer.Commands);

        var first = await _client.SendAsync(intent);

        first.Status.Should().Be(SalesCommandStatus.Offline);
        first.Message.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
        first.CanResend.Should().BeTrue();
        first.Intent.Should().BeSameAs(intent);
        _server.CommandLog(intent.CommandId)!.Applications.Should().Be(1, "sunucu komutu uyguladı, yanıt kayboldu");
        Local(order).CancelledAt.Should().BeNull("yanıt gelmedi");

        var second = await _client.SendAsync(first.Intent);

        second.Status.Should().Be(SalesCommandStatus.Applied);
        var calls = _server.CommandCalls;
        calls.Should().HaveCount(2);
        calls[1].Command.CommandId.Should().Be(intent.CommandId);
        calls[1].Json.GetProperty("payload").GetRawText().Should().Be(calls[0].Json.GetProperty("payload").GetRawText());
        calls[1].Json.GetProperty("expected").GetRawText().Should().Be(calls[0].Json.GetProperty("expected").GetRawText());
        var log = _server.CommandLog(intent.CommandId)!;
        log.Applications.Should().Be(1);
        log.Replays.Should().Be(1, "ikinci istek yeniden oynatıldı");
        _server.Order(order)!.Version.Should().Be(2, "komut bir kez uygulandı");
        Local(order).CancelledAt.Should().NotBeNull("yeniden oynatma güncel satırı döndü");
        Local(order).ServerVersion.Should().Be(2);
        NoFaults();
    }

    // ── stale ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Iptalde_stale_guncel_satiri_uygular_ve_taze_surumle_ayni_kimlikle_bir_kez_yeniden_dener()
    {
        var order = await RemoteLabelAsync();
        _server.MutateOrder(order, o => o with { Price = 175m });              // başka bilgisayarın fiyat düzeltmesi
        var intent = _client.Cancel([N(order)], "musteri vazgecti");

        var outcome = await _client.SendAsync(intent);

        outcome.Status.Should().Be(SalesCommandStatus.Applied);
        var calls = _server.CommandCalls;
        calls.Should().HaveCount(2, "stale'den sonra tam bir yeniden deneme");
        calls.Select(c => c.Command.CommandId).Should().OnlyContain(id => id == intent.CommandId);
        calls[1].Json.GetProperty("payload").GetRawText().Should().Be(calls[0].Json.GetProperty("payload").GetRawText());
        calls[0].Command.Expected!.Single()!.Version.Should().Be(1);
        calls[1].Command.Expected!.Single()!.Version.Should().Be(2, "taze sürüm stale'in current'ından");
        outcome.Intent.Expected.Single().Version.Should().Be(2, "sonraki yeniden deneme tazelenmiş niyetle");
        var local = Local(order);
        local.Price.Should().Be(175m, "current yerele yazıldı");
        local.CancelledAt.Should().NotBeNull();
        local.ServerVersion.Should().Be(3);
        NoFaults();
    }

    [Fact]
    public async Task Iptalde_ikinci_stale_yeniden_denenmez_tek_kendiliginden_deneme()
    {
        var order = await RemoteLabelAsync();
        _server.DuringPush = endpoint =>
        {
            if (endpoint == FakeSalesServer.SalesCommands) _server.MutateOrder(order, o => o with { Price = o.Price + 1m });
        };

        var outcome = await _client.SendAsync(_client.Cancel([N(order)], "musteri vazgecti"));

        outcome.Status.Should().Be(SalesCommandStatus.Stale);
        outcome.Message.Should().Be("Bu arada başka bir bilgisayarda değişti; plan yenilendi.");
        _server.CommandCalls.Should().HaveCount(2, "yalnız BİR kendiliğinden yeniden deneme");
        Local(order).Price.Should().Be(152m, "son current yazıldı");
        Local(order).CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task Yedek_onayinda_stale_current_uygulanir_sessiz_yeniden_deneme_yok()
    {
        var order = await RemoteLabelAsync(price: 90m, printed: false, tentative: true);
        _server.MutateOrder(order, o => o with { Price = 95m });

        var outcome = await _client.SendAsync(_client.ConfirmBackup(N(order), 120m));

        outcome.Status.Should().Be(SalesCommandStatus.Stale);
        outcome.Message.Should().Be("Bu arada başka bir bilgisayarda değişti; plan yenilendi.");
        _server.CommandCalls.Should().ContainSingle("fiyat ve yedekte sessiz yeniden deneme yok (§5.3)");
        _server.Order(order)!.Row.IsTentativeBackup.Should().BeTrue();
        var local = Local(order);
        local.Price.Should().Be(95m);
        local.IsTentativeBackup.Should().Be(1);
        local.ServerVersion.Should().Be(2);
    }

    [Fact]
    public async Task Ilk_baskida_stale_sonrasi_tek_deneme_marked_alreadyPrinted_cancelled_tipli_doner()
    {
        var session = RemoteSession();
        var customer = RemoteCustomer();
        var a = RemoteOrder(session, customer, printed: false);
        var b = RemoteOrder(session, customer, printed: false);
        var c = RemoteOrder(session, customer, printed: false);
        await PullAsync();
        _server.MutateOrder(b, o => o with { PrintedAt = T0.AddHours(2), PrintBatchId = "baska", PrintSeq = 1 });
        _server.MutateOrder(c, o => o with { CancelledAt = T0.AddHours(2), CancelReason = "musteri" });

        var outcome = await _client.SendAsync(_client.MarkPrinted("batch-1", [(N(a), 1), (N(b), 2), (N(c), 3)]));

        outcome.Status.Should().Be(SalesCommandStatus.Applied);
        _server.CommandCalls.Should().HaveCount(2);
        outcome.Result!.Marked.Should().Equal(a);
        outcome.Result.AlreadyPrinted.Should().Equal(b);
        outcome.Result.Cancelled.Should().Equal(c);
        var local = Local(a);
        local.PrintedAt.Should().NotBeNull();
        local.PrintBatchId.Should().Be("batch-1");
        local.PrintSeq.Should().Be(1);
        Local(b).PrintBatchId.Should().Be("baska", "başka bilgisayarın baskısı korunur");
        Local(c).CancelledAt.Should().NotBeNull();
        Local(c).PrintedAt.Should().BeNull("iptal edilmiş etiket basılmaz");
        NoFaults();
    }

    // ── öbür türler ───────────────────────────────────────────────────────

    [Fact]
    public async Task Geri_alma_iptali_kaldirir_damga_basilir()
    {
        var order = await RemoteLabelAsync(cancelled: true);
        Local(order).CancelledAt.Should().NotBeNull();

        var outcome = await _client.SendAsync(_client.Uncancel([N(order)]));

        outcome.Status.Should().Be(SalesCommandStatus.Applied);
        Local(order).CancelledAt.Should().BeNull();
        _server.Order(order)!.CancelSetAt.Should().NotBeNull("uncancel de iptal birimini damgalar (§2)");
        NoFaults();
    }

    [Fact]
    public async Task Yeniden_baski_yazmaz_beklenen_bos_kagit_sirasi_ve_duzeltmeler_doner()
    {
        var session = RemoteSession();
        var customer = RemoteCustomer();
        var a = RemoteOrder(session, customer);
        var b = RemoteOrder(session, customer);
        _server.MutateOrder(b, o => o with { PriceCorrectedAt = T0.AddHours(3) });
        await PullAsync();

        var intent = _client.Reprint([N(b), N(a)]);
        var outcome = await _client.SendAsync(intent);

        intent.Expected.Should().BeEmpty();
        _server.CommandCalls.Single().Json.GetProperty("expected").GetArrayLength().Should().Be(0);
        outcome.Status.Should().Be(SalesCommandStatus.Applied);
        outcome.ServerStatus.Should().Be(SalesCommandResponse.StatusUnchanged);
        outcome.Result!.ReprintIds.Should().Equal(b, a);
        outcome.Result.CorrectionIds.Should().Equal(b);
        _server.Order(a)!.Version.Should().Be(1, "yeniden baskı yazmaz");
        NoFaults();
    }

    [Fact]
    public void Kargo_kararinin_surumu_yerel_aynanin_ServerVersionindan_beklenen_kume_bos()
    {
        var shipments = new ShipmentRepository(_db);
        var customer = Guid.NewGuid().ToString("N");
        new CustomerRepository(_db).Insert(new Customer(customer, "tiktok", "alici_ornek", null, null, 1, 1, false, null,
            null, 0, 0m, null, null, null));
        var shipment = Guid.NewGuid();
        Exec("UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1");                  // yerel aynayı kurmak için
        shipments.Insert(new Shipment(N(shipment), customer, ShipmentStatus.Pending, 1000, null, null, 150m));
        Exec("UPDATE Shipment SET ServerVersion = 4 WHERE Id = @id", new { id = N(shipment) });
        Exec("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

        var intent = _client.ShipmentDecision(N(shipment), SalesShipmentDecisionPayload.Hold);

        intent.Kind.Should().Be("shipment-decision");
        intent.Expected.Should().BeEmpty();
        var payload = JsonDocument.Parse(intent.PayloadJson).RootElement;
        payload.GetProperty("shipmentId").GetGuid().Should().Be(shipment);
        payload.GetProperty("decision").GetString().Should().Be("hold");
        payload.GetProperty("expectedVersion").GetInt32().Should().Be(4);
    }

    [Fact]
    public void Niyet_beklenen_kumeyi_turune_gore_kurar_kume_turleri_disaridan_ister()
    {
        var label = Guid.NewGuid();
        var setCodePrice = new SalesSetCodePricePayload(Guid.NewGuid(), "A1", [], 100m, []);

        FluentActions.Invoking(() => _client.Prepare(setCodePrice)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => _client.Prepare(new SalesCancelPayload([label], "x"), [N(label)]))
            .Should().Throw<ArgumentException>();
        var plan = _client.Prepare(setCodePrice, [N(label)]);
        plan.Expected.Should().Equal(new[] { new SalesCommandExpected(label, 0) }, "sürümü bilinmeyen satır 0 ile gider");
        _client.Prepare(new SalesForceEndPayload(Guid.NewGuid())).Expected.Should().BeEmpty();
        var a = _client.Cancel([N(label)], "x");
        var b = _client.Cancel([N(label)], "x");
        a.CommandId.Should().NotBe(b.CommandId, "her niyet kendi kimliğini alır");
    }

    // ── 409'lar ───────────────────────────────────────────────────────────

    private static readonly DateTimeOffset SignalAt = new(2026, 10, 10, 18, 14, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("stale")]
    [InlineData("plan-stale")]
    [InlineData("session-live")]
    [InlineData("owner-active")]
    [InlineData("handing-over")]
    [InlineData("shipment-shipped")]
    [InlineData("client-update-required")]
    [InlineData("too-many-candidates")]
    [InlineData("content-conflict")]
    [InlineData("mode-off")]
    public async Task Her_409_kodu_sonuca_ve_tam_Turkce_metne_eslenir_yeniden_denenmez(string code)
    {
        var order = await RemoteLabelAsync(price: 90m, printed: false, tentative: true);
        var (extensions, status, text) = code switch
        {
            "stale" => (new Dictionary<string, object?>(), SalesCommandStatus.Stale,
                "Bu arada başka bir bilgisayarda değişti; plan yenilendi."),
            "plan-stale" => (new Dictionary<string, object?> { ["plan"] = Array.Empty<object>() },
                SalesCommandStatus.PlanStale, "Bu arada başka bir bilgisayarda değişti; plan yenilendi."),
            "session-live" => (new Dictionary<string, object?> { ["machine"] = "PC-A", ["ownerLastSeenAt"] = SignalAt },
                SalesCommandStatus.SessionLive, $"Bu yayın PC-A'da canlı (son sinyal {Hhmm(SignalAt)})"),
            "owner-active" => (new Dictionary<string, object?> { ["machine"] = "PC-A", ["lastSeenAt"] = SignalAt },
                SalesCommandStatus.OwnerActive, $"PC-A hâlâ sinyal veriyor (son {Hhmm(SignalAt)}); yayın buradan bitirilemez."),
            "handing-over" => (new Dictionary<string, object?> { ["machine"] = "PC-A", ["unsentLabelCount"] = 3 },
                SalesCommandStatus.HandingOver,
                "PC-A'da yayın bitti ama 3 etiket henüz gönderilmedi. PC-A açılıp internete bağlanınca yayın buradan düzenlenebilir olur."),
            "shipment-shipped" => (new Dictionary<string, object?> { ["orderId"] = order, ["shipmentId"] = Guid.NewGuid() },
                SalesCommandStatus.ShipmentShipped, "Bu etiket kargoya verilmiş bir dosyada; fiyatı değişmez."),
            "client-update-required" => (new Dictionary<string, object?>(), SalesCommandStatus.ClientUpdateRequired,
                "Bu işlem için uygulamayı güncelle."),
            "too-many-candidates" => (new Dictionary<string, object?> { ["count"] = 12001, ["max"] = 10000 },
                SalesCommandStatus.TooManyCandidates, "Bu işlem çok fazla etiketi kapsıyor (12001); destek ile iletişime geç."),
            "content-conflict" => (new Dictionary<string, object?>(), SalesCommandStatus.ContentConflict,
                "İşlem kaydedilemedi (iç hata); tekrar dene."),
            "mode-off" => (new Dictionary<string, object?>(), SalesCommandStatus.ModeOff,
                "Senkron kapatıldı; ekran yenileniyor."),
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };
        _server.EnqueueProblem(FakeSalesServer.Commands, 409, code, extensions);

        var outcome = await _client.SendAsync(_client.ConfirmBackup(N(order), 120m));

        outcome.Status.Should().Be(status);
        outcome.Message.Should().Be(text);
        outcome.Problem!.Code.Should().Be(code);
        outcome.IsApplied.Should().BeFalse();
        _server.CommandCalls.Should().ContainSingle("409'lar yeniden denenmez (stale yalnız iptal/geri alma/ilk baskıda)");
        _prompt.Calls.Should().Be(code == "client-update-required" ? 1 : 0);
        _modeOffEvents.Should().Be(code == "mode-off" ? 1 : 0);
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog")
            .Should().Be(code is "too-many-candidates" or "content-conflict" ? 1 : 0);
    }

    [Fact]
    public async Task Cok_fazla_aday_ve_icerik_catismasi_birer_SalesConflictLog_satiri_yazar_kisisel_veri_yok()
    {
        var order = await RemoteLabelAsync();
        _server.EnqueueProblem(FakeSalesServer.Commands, 409, "too-many-candidates",
            new Dictionary<string, object?> { ["count"] = 12001, ["max"] = 10000 });
        var tooMany = _client.Cancel([N(order)], "musteri vazgecti");
        (await _client.SendAsync(tooMany)).Status.Should().Be(SalesCommandStatus.TooManyCandidates);

        // Gerçek içerik çatışması: aynı kimliğe başka içerik (istemci hatası). İlk niyet uygulanır, ikincisi reddedilir.
        var first = _client.Cancel([N(order)], "musteri vazgecti");
        (await _client.SendAsync(first)).IsApplied.Should().BeTrue();
        var conflicting = _client.Cancel([N(order)], "baska bir neden") with { CommandId = first.CommandId };
        var conflict = await _client.SendAsync(conflicting);

        conflict.Status.Should().Be(SalesCommandStatus.ContentConflict);
        conflict.Message.Should().Be("İşlem kaydedilemedi (iç hata); tekrar dene.");
        _server.CommandCalls.Should().HaveCount(3, "content-conflict yeniden denenmez");
        using var c = _db.Open();
        var rows = c.Query<(string? LabelId, string Kind, string Reason)>(
            "SELECT LabelId, Kind, Reason FROM SalesConflictLog ORDER BY Id").ToList();
        rows.Select(r => r.Kind).Should().Equal(SalesConflictLog.CommandTooManyCandidates, SalesConflictLog.CommandContentConflict);
        rows[0].Reason.Should().Be($"cancel; command={tooMany.CommandId:N}; count=12001; max=10000");
        rows[1].Reason.Should().Be($"cancel; command={first.CommandId:N}");
        foreach (var row in rows)
        {
            row.LabelId.Should().BeNull();
            row.Reason.Should().NotContainAny("alici_ornek", "Takma Ad", "MAVI", "musteri", "neden");
        }
        _log.Entries.Select(e => e.Full).Should().NotContain(m => m.Contains("alici_ornek") || m.Contains("Takma Ad")
            || m.Contains("musteri vazgecti"), "günlükte yalnız kimlik ve sayı");
    }

    [Fact]
    public async Task Sunucu_gorunurlugu_session_live_makineyi_ve_sahibin_yerel_son_sinyalini_yazar()
    {
        // Başka bilgisayarın devredilmemiş yayını: sunucu satırı görür ama komutu kabul etmez (§4.6 adım 3).
        var session = RemoteSession(released: false, machine: "PC-A");
        var order = RemoteOrder(session, RemoteCustomer(), printed: false);
        await _api.GetSalesStateAsync(_server.LicenseId, _otherReplica, 1);       // sahibin kalp atışı
        var lastSeen = _server.ReplicaRow(_server.LicenseId, _otherReplica)!.LastSeenAt;

        var outcome = await _client.SendAsync(_client.Cancel([N(order)], "musteri vazgecti"));

        outcome.Status.Should().Be(SalesCommandStatus.SessionLive);
        outcome.Message.Should().Be($"Bu yayın PC-A'da canlı (son sinyal {Hhmm(lastSeen)})");
        outcome.Problem!.SessionId.Should().Be(session);
        _server.CommandCalls.Should().ContainSingle();
        _server.Order(order)!.Row.CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task Sunucu_mode_off_ModeOffDetected_yukseltir()
    {
        var order = await RemoteLabelAsync();
        _server.Mode = 0;

        var outcome = await _client.SendAsync(_client.Cancel([N(order)], "musteri vazgecti"));

        outcome.Status.Should().Be(SalesCommandStatus.ModeOff);
        outcome.Message.Should().Be("Senkron kapatıldı; ekran yenileniyor.");
        _modeOffEvents.Should().Be(1);
        _server.Order(order)!.Row.CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task Eski_istemci_client_update_required_guncelleme_sorusunu_bir_kez_acar()
    {
        var order = await RemoteLabelAsync();
        _server.MinClientVersion = "9.0.0";

        var outcome = await _client.SendAsync(_client.Cancel([N(order)], "musteri vazgecti"));

        outcome.Status.Should().Be(SalesCommandStatus.ClientUpdateRequired);
        outcome.Message.Should().Be("Bu işlem için uygulamayı güncelle.");
        _prompt.Calls.Should().Be(1);
        _server.CommandCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Guncelleme_sorusu_arka_plan_gonderiminde_acilmaz_operatorun_isleminde_surec_basina_bir_kez()
    {
        var order = await RemoteLabelAsync();
        _server.MinClientVersion = "9.0.0";

        var background = await _client.SendAsync(_client.Cancel([N(order)], "musteri vazgecti"), interactive: false);
        background.Status.Should().Be(SalesCommandStatus.ClientUpdateRequired);
        background.Message.Should().Be("Bu işlem için uygulamayı güncelle.", "satır içi metin yine döner");
        _prompt.Calls.Should().Be(0, "arka plan gönderimi modal soru açmaz");

        (await _client.SendAsync(_client.Cancel([N(order)], "musteri vazgecti"))).Status
            .Should().Be(SalesCommandStatus.ClientUpdateRequired);
        (await _client.SendAsync(_client.Uncancel([N(order)]))).Status
            .Should().Be(SalesCommandStatus.ClientUpdateRequired);

        _prompt.Calls.Should().Be(1, "operatörün işleminde süreç başına bir kez");
        _server.CommandCalls.Should().HaveCount(3);
    }

    // ── taşıma ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Uc_yoksa_404_EndpointMissing_sunucu_hatasi_Failed_400_Invalid()
    {
        var order = await RemoteLabelAsync();
        var intent = _client.Cancel([N(order)], "musteri vazgecti");

        _server.EnqueueStatus(FakeSalesServer.Commands, 404);
        var missing = await _client.SendAsync(intent);
        missing.Status.Should().Be(SalesCommandStatus.EndpointMissing);
        missing.Message.Should().Be("Sunucu güncelleniyor; biraz sonra tekrar dene.");
        missing.CanResend.Should().BeTrue();

        _server.EnqueueStatus(FakeSalesServer.Commands, 500);
        var failed = await _client.SendAsync(missing.Intent);
        failed.Status.Should().Be(SalesCommandStatus.Failed);
        failed.Message.Should().Be("Sunucu işlemi tamamlayamadı; biraz sonra tekrar dene.");
        failed.CanResend.Should().BeTrue();

        _server.EnqueueProblem(FakeSalesServer.Commands, 400, "unknown-order");
        var invalid = await _client.SendAsync(failed.Intent);
        invalid.Status.Should().Be(SalesCommandStatus.Invalid);
        invalid.CanResend.Should().BeFalse();

        Local(order).CancelledAt.Should().BeNull();
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(0);
    }

    [Fact]
    public async Task Cevrimdisi_Offline_doner_ve_niyeti_aynen_birakir()
    {
        var order = await RemoteLabelAsync();
        var intent = _client.Cancel([N(order)], "musteri vazgecti");
        _server.Offline = true;

        var outcome = await _client.SendAsync(intent);

        outcome.Status.Should().Be(SalesCommandStatus.Offline);
        outcome.Intent.Should().BeSameAs(intent);
    }

    // ── mod Off ve zarf ───────────────────────────────────────────────────

    [Fact]
    public async Task Yerel_mod_Off_hicbir_HTTP_istegi_gondermez()
    {
        Exec("UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1");
        var intent = _client.Cancel([N(Guid.NewGuid())], "musteri vazgecti");

        var outcome = await _client.SendAsync(intent);

        outcome.Status.Should().Be(SalesCommandStatus.NotReady);
        _server.SalesRequestCount.Should().Be(0, "mod Off'ta komut ucu da kalp atışı da yok");
        _server.LicenseRequestCount.Should().Be(0, "lisans çözümü de yok");
        _server.CommandCalls.Should().BeEmpty();
        _modeOffEvents.Should().Be(0);
    }

    [Fact]
    public async Task Zarf_hazir_degilse_ya_da_kimlik_bozuksa_HTTP_yok()
    {
        // Geri yüklemeden sonra hello gitmeden (§8.7) ...
        Exec("UPDATE _meta SET PreviousReplicaId = @p WHERE Id = 1", new { p = Guid.NewGuid().ToString("N") });
        (await _client.SendAsync(_client.Cancel([N(Guid.NewGuid())], "x"))).Status.Should().Be(SalesCommandStatus.NotReady);

        // ... ve kimlik bozukken (v1 zarfı komut taşıyamaz).
        Exec("UPDATE _meta SET PreviousReplicaId = NULL, ReplicaId = 'bozuk' WHERE Id = 1");
        (await _client.SendAsync(_client.Cancel([N(Guid.NewGuid())], "x"))).Status.Should().Be(SalesCommandStatus.NotReady);

        _server.SalesRequestCount.Should().Be(0);
        _server.LicenseRequestCount.Should().Be(0);
    }
}
