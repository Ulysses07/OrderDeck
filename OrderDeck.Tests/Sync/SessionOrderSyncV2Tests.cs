using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// v2 etiket gönderimi (spec 2026-10-13 §4.2, §5.2; W1-06): zarf ve her v2 alanı, <c>baseVersion = ServerVersion</c>,
/// <c>applied</c> onayının yazdıkları, uçuşta değişen satır (F05), onayın kuyruğa almaması (DM8), eski sunucunun
/// yanıtı, <c>ignored</c>/<c>rejected</c>'ın onayı (W1-08: sunucunun değeri uygulanır), tek gönderim kapısı, canlı
/// yolun ağı beklememesi ve
/// zarfın kimlik kuralları (bozuk kimlikte v1, geri yüklemeden sonra hello beklenir). Sunucu
/// <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class SessionOrderSyncV2Tests : IDisposable
{
    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Clock : IClock
    {
        public long Now = 1_760_000_000L;
        public long UnixNow() => Now;
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly License _license;
    private readonly Clock _clock = new();
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly CapturingLogger<SessionOrderSyncService> _log = new();
    private readonly CapturingLogger<SalesSyncEnvelope> _envelopeLog = new();
    private readonly SalesSyncEnvelope _envelope;
    private readonly SessionOrderSyncService _svc;
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _addedAt = 1_760_000_100L;

    public SessionOrderSyncV2Tests()
    {
        new MigrationRunner(_db).Run();
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        _license = new License(_server.Lisans);
        new CustomerRepository(_db).Insert(new Customer(_customerId, "tiktok", "ornek_alici", "Örnek Alıcı",
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
        _sessions.Insert(new StreamSession(_sessionId, "Örnek yayın", 1_760_000_000L, null, new[] { "tiktok" }, null));
        _envelope = TestSalesEnvelope.For(_db, _license, _envelopeLog);
        _svc = new SessionOrderSyncService(Api(), _sessions, _labels, _license, _envelope, new HandoverGate(),
            new SalesPushApply(_db), _clock, _log);
    }

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
    }

    private LicenseApiClient Api() => new(
        new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
        new LicenseTokenStore());

    private Guid ReplicaId => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

    private string AddLabel(decimal price = 100m, string? code = "A1", long? printedAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _labels.Insert(new Label(id, _sessionId, _customerId, "tiktok", "ornek_alici", "alıyorum", code, price,
            _addedAt++, printedAt, DisplayName: "Örnek Alıcı"));
        return id;
    }

    private sealed record LocalRow(long? SyncedAt, long? StockSyncedAt, long? ServerVersion, long Revision);

    private LocalRow Local(string id)
    {
        using var c = _db.Open();
        return c.QuerySingle<LocalRow>(
            "SELECT SyncedAt, StockSyncedAt, ServerVersion, Revision FROM Label WHERE Id = @id", new { id });
    }

    private void Sql(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    private static JsonElement OrderJson(FakeSalesServer.PushCall call, string id) =>
        call.Json.GetProperty("orders").EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == Guid.Parse(id));

    // ── gövde ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task V2_govdesi_zarfi_ve_her_v2_alanini_dogru_cevirir()
    {
        var parent = AddLabel(250m, "A7", printedAt: 1_760_000_150L);
        var backup = AddLabel(240m, "A7");
        var shipment = Guid.NewGuid().ToString("N");
        var product = Guid.NewGuid().ToString("N");
        const string batch = "00000000000000aa0123456789abcdef";
        Sql(@"UPDATE Label SET ParentLabelId = @parent, IsBackupPromoted = 1, IsTentativeBackup = 1, ShipmentId = @shipment,
                     PrintedAt = 1760000200, PrintBatchId = @batch, PrintSeq = 3, PriceCorrectedAt = 1760000300,
                     PaidPrice = 260.5, ProductId = @product, CancelledAt = 1760000400, CancelReason = 'vazgeçti'
              WHERE Id = @backup", new { parent, shipment, batch, product, backup });

        await _svc.SyncOnceAsync();

        _server.Faults.Should().BeEmpty("sunucu zarfı ve biçimi kabul etmeli");
        var call = _server.Calls(FakeSalesServer.OrdersSync).Single();
        var root = call.Json;
        root.GetProperty("replicaId").GetGuid().Should().Be(ReplicaId);
        root.GetProperty("protocol").GetInt32().Should().Be(2);
        root.GetProperty("seenMode").GetInt32().Should().Be(0);
        root.GetProperty("format").GetInt32().Should().Be(2);
        root.GetProperty("catalogAware").GetBoolean().Should().BeTrue();

        var o = OrderJson(call, backup);
        o.GetProperty("sessionId").GetGuid().Should().Be(Guid.Parse(_sessionId));
        o.GetProperty("baseVersion").ValueKind.Should().Be(JsonValueKind.Null, "hiç onaylanmamış satır");
        o.GetProperty("parentOrderId").GetGuid().Should().Be(Guid.Parse(parent));
        o.GetProperty("shipmentId").GetGuid().Should().Be(Guid.Parse(shipment));
        o.GetProperty("productId").GetGuid().Should().Be(Guid.Parse(product));
        o.GetProperty("printBatchId").GetString().Should().Be(batch);
        o.GetProperty("printSeq").GetInt32().Should().Be(3);
        o.GetProperty("printedAt").GetDateTimeOffset().Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_760_000_200L));
        o.GetProperty("priceCorrectedAt").GetDateTimeOffset().Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_760_000_300L));
        o.GetProperty("cancelledAt").GetDateTimeOffset().Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_760_000_400L));
        o.GetProperty("paidPrice").GetDecimal().Should().Be(260.5m);
        o.GetProperty("price").GetDecimal().Should().Be(240m);
        o.GetProperty("isTentativeBackup").GetBoolean().Should().BeTrue();
        o.GetProperty("isBackupPromoted").GetBoolean().Should().BeTrue();

        var p = OrderJson(call, parent);
        p.GetProperty("parentOrderId").ValueKind.Should().Be(JsonValueKind.Null);
        p.GetProperty("shipmentId").ValueKind.Should().Be(JsonValueKind.Null,
            "bağsız etiketin boş bağı da bir değerdir (K4, mod Off)");
        p.GetProperty("printBatchId").ValueKind.Should().Be(JsonValueKind.Null);

        var stored = _server.Order(Guid.Parse(backup))!;
        stored.OriginReplicaId.Should().Be(ReplicaId);
        stored.Row.PrintBatchId.Should().Be(batch);
    }

    [Fact]
    public async Task Sunucu_sinirini_asan_basis_kimligi_bos_gider_parti_dusmez()
    {
        var id = AddLabel();
        Sql("UPDATE Label SET PrintBatchId = @batch, PrintSeq = 1, PrintedAt = 1760000200 WHERE Id = @id",
            new { id, batch = new string('a', 41) });

        await _svc.SyncOnceAsync();

        _server.Faults.Should().BeEmpty("uzun kimlik partinin tamamını 400'e düşürürdü");
        OrderJson(_server.Calls(FakeSalesServer.OrdersSync).Single(), id)
            .GetProperty("printBatchId").ValueKind.Should().Be(JsonValueKind.Null);
        Local(id).SyncedAt.Should().NotBeNull();
    }

    // ── onay ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Applied_onayi_SyncedAt_StockSyncedAt_ve_ServerVersioni_yazar_sonraki_gonderimin_baseVersioni_olur()
    {
        var id = AddLabel();
        var before = Local(id);

        await _svc.SyncOnceAsync();

        var acked = Local(id);
        acked.SyncedAt.Should().Be(_clock.Now);
        acked.StockSyncedAt.Should().Be(_clock.Now);
        acked.ServerVersion.Should().Be(0, "sunucu eklemede sürüm 0 bildirir");
        acked.Revision.Should().Be(before.Revision, "onay kuyruğa almaz (DM8)");

        _labels.MarkPrintedBatch(new[] { (id, 1) }, 1_760_000_500L, "0000000000000001ffffffffffffffff");
        await _svc.SyncOnceAsync();

        var second = _server.Calls(FakeSalesServer.OrdersSync)[^1];
        OrderJson(second, id).GetProperty("baseVersion").GetInt32().Should().Be(0,
            "baseVersion = son applied onayının sürümü");
        Local(id).ServerVersion.Should().Be(1);
        _server.Order(Guid.Parse(id))!.Version.Should().Be(1);
    }

    [Fact]
    public async Task ServerVersion_yazimi_kuyruga_almaz_sonraki_turda_istek_gitmez()
    {
        var id = AddLabel();
        await _svc.SyncOnceAsync();
        var acked = Local(id);

        acked.ServerVersion.Should().NotBeNull();
        _labels.HasUnsynced().Should().BeFalse("ServerVersion hiçbir tetikleyici listesinde değil (DM8)");
        await _svc.SyncOnceAsync();
        _server.Calls(FakeSalesServer.OrdersSync).Should().ContainSingle("kuyruk boş: ikinci tur etiket göndermez");
        Local(id).Should().Be(acked);
    }

    [Fact]
    public async Task Ucusta_degisen_satira_hicbir_sey_yazilmaz_sonraki_tur_yeniden_gonderir()
    {
        var id = AddLabel();
        var cancelled = false;
        _server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.OrdersSync || cancelled) return;
            cancelled = true;
            _labels.MarkCancelled(new[] { id }, 1_760_000_600L, "uçuşta");
        };

        await _svc.SyncOnceAsync();

        var afterFirst = Local(id);
        afterFirst.SyncedAt.Should().BeNull("uçuştaki iptal onayla ezilmemeli (F05)");
        afterFirst.ServerVersion.Should().BeNull("değişmiş satıra sürüm de yazılmaz");

        await _svc.SyncOnceAsync();

        var calls = _server.Calls(FakeSalesServer.OrdersSync);
        calls.Should().HaveCount(2);
        OrderJson(calls[1], id).GetProperty("cancelledAt").GetDateTimeOffset()
            .Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_760_000_600L));
        var final = Local(id);
        final.SyncedAt.Should().NotBeNull();
        final.ServerVersion.Should().Be(1);
        _server.Order(Guid.Parse(id))!.Row.CancelledAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("v1-listesi")]
    [InlineData("bos-liste")]
    public async Task Eski_sunucunun_yaniti_bugunku_gibi_onaylanir_surum_yazilmaz(string response)
    {
        _server.OrdersResponse = response == "v1-listesi"
            ? FakeSalesServer.PushResponseKind.V1List
            : FakeSalesServer.PushResponseKind.EmptyList;
        var a = AddLabel();
        var b = AddLabel(150m, "B2");

        var result = await _svc.SyncOnceAsync();

        result.OrdersPushed.Should().Be(2);
        foreach (var id in new[] { a, b })
        {
            Local(id).SyncedAt.Should().NotBeNull("eski sunucu: bugünkü gibi onay");
            Local(id).ServerVersion.Should().BeNull("eski sunucu sürüm bildirmez");
        }
        _labels.HasUnsynced().Should().BeFalse();
        _log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Yok_sayilan_ve_reddedilen_satira_sunucunun_degeri_uygulanir_surumuyle_onaylanir_sayisi_gunluge_duser()
    {
        // W1-08 geçiş dönemini kapattı: ignored/rejected artık sürümsüz onaylanmaz; current uygulanır (ayrıntılar
        // SessionOrderSyncPushOutcomeTests ve SalesPushApplyTests).
        var ignored = AddLabel();
        var rejected = AddLabel(150m, "B2");
        var applied = AddLabel(175m, "C3");
        await _svc.SyncOnceAsync();
        foreach (var id in new[] { ignored, rejected, applied }) Local(id).ServerVersion.Should().Be(0);

        _labels.MarkCancelled(new[] { ignored, rejected, applied }, 1_760_000_700L, "örnek");
        _server.OrderVerdicts[Guid.Parse(ignored)] = new("ignored", "unit-stamped", new[] { "cancel" });
        _server.OrderVerdicts[Guid.Parse(rejected)] = new("rejected", "session-released", new[] { "cancel" });

        await _svc.SyncOnceAsync();

        Local(ignored).SyncedAt.Should().NotBeNull();
        Local(rejected).SyncedAt.Should().NotBeNull();
        Local(ignored).ServerVersion.Should().Be(1, "sunucunun güncel sürümü (current.Version)");
        Local(rejected).ServerVersion.Should().Be(0);
        Local(applied).ServerVersion.Should().Be(1);
        CancelledAt(ignored).Should().BeNull("sunucu iptali tutmadı: yerel değer sunucununkine döner");
        CancelledAt(rejected).Should().BeNull();
        CancelledAt(applied).Should().Be(1_760_000_700L);
        _labels.HasUnsynced().Should().BeFalse("uygulama kuyruğa almaz (DM8)");
        _log.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("1 etiket yok sayıldı").And.Contain("1 etiket reddedildi");
    }

    private long? CancelledAt(string id)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long?>("SELECT CancelledAt FROM Label WHERE Id = @id", new { id });
    }

    // ── gönderim kapısı ────────────────────────────────────────────────────

    [Fact]
    public async Task Eszamanli_turlar_tek_kapidan_gecer_sunucu_ayni_anda_tek_orders_sync_gorur()
    {
        var ids = Enumerable.Range(0, 250).Select(_ => AddLabel()).ToHashSet();
        var hold = _server.Hold(FakeSalesServer.OrdersSync);

        var tours = Enumerable.Range(0, 3).Select(_ => Task.Run(() => _svc.SyncOnceAsync())).ToList();
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        _server.Calls(FakeSalesServer.OrdersSync).Should().ContainSingle("öbür turlar kapıda bekler");

        hold.Release();
        await Task.WhenAll(tours).WaitAsync(TimeSpan.FromSeconds(30));

        _server.MaxInFlight(FakeSalesServer.OrdersSync).Should().Be(1);
        var sent = _server.Calls(FakeSalesServer.OrdersSync)
            .SelectMany(c => c.Orders.Orders!.Select(o => o.Id.ToString("N"))).ToList();
        sent.Should().OnlyHaveUniqueItems("aynı parti iki kez gitmez");
        sent.Should().BeEquivalentTo(ids);
        _labels.HasUnsynced().Should().BeFalse();
    }

    [Fact]
    public async Task Kapanis_gonderimi_kapidaki_turu_beklerken_butcesinde_biter_kapidan_vazgecer()
    {
        AddLabel();
        var hold = _server.Hold(FakeSalesServer.OrdersSync);
        var tour = Task.Run(() => _svc.SyncOnceAsync());
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(10));
        var lateLabel = AddLabel(150m, "B2");                                  // kapanışın göndereceği

        var flush = new SyncFlushService(new[]
        {
            new FlushStep("oturum ve etiket", SyncFlushService.Drain(async ct =>
            {
                var r = await _svc.SyncOnceAsync(ct);
                return r.SessionsPushed + r.OrdersPushed;
            })),
        });
        var sw = Stopwatch.StartNew();
        await flush.FlushAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(10));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "kapanış bütçesi kesin");

        hold.Release();
        await tour.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Local(lateLabel).SyncedAt.Should().BeNull(
            "kapanışın turu kapıyı beklerken belirteci dinler: bütçe dolunca vazgeçer, sonradan gönderim yapmaz");
        _server.Calls(FakeSalesServer.OrdersSync).Should().ContainSingle();
    }

    // ── canlı yol ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Sunucu_orders_synci_tutarken_canli_yol_beklemez_ucusta_yazilan_etiket_sonraki_turda_gider()
    {
        var queued = AddLabel();
        var toCancel = AddLabel(150m, "B2");
        var toPrint = AddLabel(175m, "C3");
        await _svc.SyncOnceAsync();
        var hold = _server.Hold(FakeSalesServer.OrdersSync);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        fallback.Token.Register(hold.Release);                     // sunucu en çok 10 sn tutar

        var flying = AddLabel(200m, "D4");                         // uçacak satır
        var tour = Task.Run(() => _svc.SyncOnceAsync());
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(10));

        var labelService = new LabelService(_labels,
            new CustomerService(new CustomerRepository(_db), _sessions, _labels, _clock), _db, _clock);
        var sw = Stopwatch.StartNew();
        var added = labelService.Add(_sessionId, new ChatMessage(Guid.NewGuid().ToString("N"), "tiktok", null,
            "ornek_alici", "Örnek Alıcı", null, "E5 alıyorum", 1_760_000_800L, Array.Empty<string>()), 120m, "E5");
        var addMs = sw.ElapsedMilliseconds;
        sw.Restart();
        labelService.MarkPrintedAndRecord(new[] { toPrint, flying });
        var printMs = sw.ElapsedMilliseconds;
        sw.Restart();
        labelService.Cancel(new[] { toCancel }, "vazgeçti");
        var cancelMs = sw.ElapsedMilliseconds;

        tour.IsCompleted.Should().BeFalse("ön koşul: canlı yazımlar sırasında orders/sync uçuştaydı");
        addMs.Should().BeLessThan(1000);
        printMs.Should().BeLessThan(1000);
        cancelMs.Should().BeLessThan(1000);

        hold.Release();
        await tour.WaitAsync(TimeSpan.FromSeconds(10));
        Local(flying).SyncedAt.Should().BeNull("uçuşta basılan satır onayla ezilmez (F05)");

        await _svc.SyncOnceAsync();

        _server.Order(Guid.Parse(added.Id)).Should().NotBeNull("uçuşta yazılan etiket sonraki turda gider");
        _server.Order(Guid.Parse(flying))!.Row.PrintedAt.Should().NotBeNull();
        _server.Order(Guid.Parse(toPrint))!.Row.PrintedAt.Should().NotBeNull();
        _server.Order(Guid.Parse(toCancel))!.Row.CancelledAt.Should().NotBeNull();
        _server.Order(Guid.Parse(queued))!.Row.CancelledAt.Should().BeNull();
        _labels.HasUnsynced().Should().BeFalse();
    }

    [Fact]
    public async Task Cevrimdisi_tur_hata_firlatmaz_etiketler_bekler_canli_yazim_etkilenmez()
    {
        var id = AddLabel();
        _server.Offline = true;

        for (var i = 0; i < 5; i++)
            (await _svc.SyncOnceAsync()).Should().Be(default(SessionOrderSyncService.SyncResult));

        Local(id).SyncedAt.Should().BeNull();
        var labelService = new LabelService(_labels,
            new CustomerService(new CustomerRepository(_db), _sessions, _labels, _clock), _db, _clock);
        var sw = Stopwatch.StartNew();
        labelService.Cancel(new[] { id }, "vazgeçti");
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);

        _server.Offline = false;
        await _svc.SyncOnceAsync();
        _server.Order(Guid.Parse(id))!.Row.CancelledAt.Should().NotBeNull("bağlantı gelince bekleyen etiket gider");
    }

    // ── zarf ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kopya_kimligi_bozuksa_gonderim_durmaz_v1_govdesi_gider_hata_gunluge_duser()
    {
        Sql("UPDATE _meta SET ReplicaId = 'bozuk' WHERE Id = 1");
        var id = AddLabel();

        await _svc.SyncOnceAsync();
        await _svc.SyncOnceAsync();

        _server.Faults.Should().BeEmpty();
        var root = _server.Calls(FakeSalesServer.OrdersSync).Single().Json;
        foreach (var field in new[] { "replicaId", "protocol", "seenMode", "format" })
            root.TryGetProperty(field, out _).Should().BeFalse($"v1 gövdesi {field} taşımaz");
        Local(id).SyncedAt.Should().NotBeNull("v1 yanıtı bugünkü gibi onaylanır");
        _envelopeLog.Entries.Where(e => e.Level == LogLevel.Error).Should().ContainSingle(
            "kimlik sorunu seri başına bir kez Error");
    }

    [Fact]
    public async Task Geri_yuklemeden_sonra_hello_gidene_kadar_tur_atlanir()
    {
        new ReplicaIdentityRepository(_db).SetPendingPreviousReplicaId(Guid.NewGuid());
        var id = AddLabel();

        var skipped = await _svc.SyncOnceAsync();

        skipped.Should().Be(default(SessionOrderSyncService.SyncResult));
        _server.PushCalls.Should().BeEmpty("§8.7: önceki kimlik sunucuya gitmeden hiçbir gönderim yok");
        Local(id).SyncedAt.Should().BeNull();

        _envelope.MarkHelloSucceeded(_server.Lisans, ReplicaId);
        await _svc.SyncOnceAsync();

        _server.Calls(FakeSalesServer.SessionsSync).Should().ContainSingle();
        _server.Calls(FakeSalesServer.OrdersSync).Single().Json.GetProperty("replicaId").GetGuid().Should().Be(ReplicaId);
        Local(id).SyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Oturumlar_v2_zarfi_ve_makine_adiyla_gider_bitmis_bos_kuyruklu_oturum_devredilir()
    {
        _sessions.End(_sessionId, 1_760_000_900L);

        await _svc.SyncOnceAsync();

        _server.Faults.Should().BeEmpty();
        var root = _server.Calls(FakeSalesServer.SessionsSync).Single().Json;
        root.GetProperty("replicaId").GetGuid().Should().Be(ReplicaId);
        root.GetProperty("protocol").GetInt32().Should().Be(2);
        root.GetProperty("seenMode").GetInt32().Should().Be(0);
        var item = root.GetProperty("sessions").EnumerateArray().Single();
        item.GetProperty("machineName").GetString().Should().Be(_envelope.MachineName);
        item.GetProperty("release").GetBoolean().Should().BeTrue("etiket kuyruğu boş (M2)");
        item.TryGetProperty("unsentLabelCount", out _).Should().BeFalse();
        var session = _sessions.GetById(_sessionId)!;
        session.SyncedAt.Should().NotBeNull();
        session.ReleasedAt.Should().NotBeNull();
        session.Authority.Should().Be(StreamSessionAuthority.Local, "mod Off: Authority yazılmaz");
    }

    [Fact]
    public async Task Kopya_kimligi_bozuksa_oturum_v1_govdesiyle_gider_devir_alanlari_yazilmaz()
    {
        Sql("UPDATE _meta SET ReplicaId = 'bozuk' WHERE Id = 1");
        _sessions.End(_sessionId, 1_760_000_900L);
        AddLabel();

        await _svc.SyncOnceAsync();

        _server.Faults.Should().BeEmpty("v1 gövdesi v2 öğe alanı taşımaz");
        var root = _server.Calls(FakeSalesServer.SessionsSync)[0].Json;
        foreach (var field in new[] { "replicaId", "protocol", "seenMode" })
            root.TryGetProperty(field, out _).Should().BeFalse();
        var item = root.GetProperty("sessions").EnumerateArray().Single();
        foreach (var field in new[] { "release", "machineName", "unsentLabelCount" })
            item.TryGetProperty(field, out _).Should().BeFalse($"v1 öğesi {field} taşımaz");
        _sessions.GetById(_sessionId)!.SyncedAt.Should().NotBeNull("sunucu v1 kapanışını bugünkü gibi devreder");
    }
}
