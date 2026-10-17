using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// <c>disable</c> turu uçtan uca (spec 2026-10-13 §8.5, §4.4, §4.10, §5.3 mode-off, §6.2, §6.5-1, M15, M19, §10.3 On →
/// Off senaryoları; W2-11). İki bilgisayar aynı <see cref="FakeSalesServer"/>'a konuşur (her biri kendi veritabanıyla);
/// mod On gerçek açılış turuyla açılır, sunucu <see cref="FakeSalesServer.Disable"/> ile kapatır (dosyaların
/// <c>OriginReplicaId</c>'si bağlı etiketlerin sahibine yeniden yazılır), her bilgisayarın satış akışı turu Off turunu
/// koşar: son akış dosyalarla, tek işlemde yerel kip 0 ve temizlik, kargo ayarı, bekleyen komutların yerel yazıma
/// çevrilmesi. Görülen kip ancak tur bitince 0.
/// </summary>
public sealed class DisableRoundTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L);
    private static readonly DateTimeOffset EnabledAt = T0.AddDays(2);
    private static readonly DateTimeOffset DisabledAt = T0.AddDays(5);

    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Hardware(string id) : IHardwareIdProvider
    {
        public string GetHardwareId() => id;
        public string? GetLegacyHardwareId() => null;
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private sealed class NoPrompt : IAppUpdatePrompt
    {
        public void CheckAndPrompt() { }
    }

    /// <summary>Bir bilgisayarın yığını (bir süreç). Veritabanını test tutar: aynı veritabanında yeni bir yığın "yeniden
    /// açılış"tır.</summary>
    private sealed class Pc
    {
        public InMemorySqlite Db { get; }
        public SalesSyncEnvelope Envelope { get; }
        public SalesSyncStateRepository State { get; }
        public SessionOrderSyncService Push { get; }
        public ShipmentSyncService ShipmentPush { get; }
        public SalesCommandClient Commands { get; }
        public SalesModeOffRound Round { get; }
        public SalesChangesPullService Pull { get; }
        public LabelService Labels { get; }
        public AppSettings Settings { get; }
        public string SettingsPath { get; }
        public Guid Replica { get; }

        public Pc(FakeSalesServer server, InMemorySqlite db, string settingsPath)
        {
            Db = db;
            SettingsPath = settingsPath;
            var license = new License(server.Lisans);
            var identity = new ReplicaIdentityRepository(db);
            Replica = identity.GetReplicaId()!.Value;
            State = new SalesSyncStateRepository(db);
            Envelope = new SalesSyncEnvelope(identity, State, new Hardware(Guid.NewGuid().ToString("N")), license,
                NullLogger<SalesSyncEnvelope>.Instance);
            var http = new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
            var api = new LicenseApiClient(http, new LicenseTokenStore());
            var clock = new Clock();
            var busy = new CustomerBusySet();
            var cursors = new SyncCursorRepository(db);
            var customerSync = new CustomerSyncRepository(db, busy);
            var store = new SettingsStore(settingsPath);
            Settings = store.Load();
            var feed = new SalesFeedRepository(db, busy);
            var labelRepo = new LabelRepository(db);
            Labels = new LabelService(labelRepo,
                new CustomerService(new CustomerRepository(db), new SessionRepository(db), labelRepo, clock), db, clock);
            var state = new SalesSyncStateService(api, Envelope, identity, State, license, () => Settings,
                NullLogger<SalesSyncStateService>.Instance);
            Push = new SessionOrderSyncService(api, new SessionRepository(db), labelRepo, license, Envelope,
                new HandoverGate(), new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance);
            var shipments = ShipmentPush = new ShipmentSyncService(api, new ShipmentRepository(db), store, Settings,
                license, Envelope, clock, NullLogger<ShipmentSyncService>.Instance, salesState: State);
            var payments = new PaymentSyncService(api, new PaymentRepository(db), cursors, license, clock,
                NullLogger<PaymentSyncService>.Instance);
            var customerPush = new WpfCustomerProjectionSyncService(api, customerSync, cursors, license, clock,
                NullLogger<WpfCustomerProjectionSyncService>.Instance);
            var customerPull = new CustomerChangesPullService(api, new CustomerRepository(db), customerSync, cursors,
                customerPush, license, clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance);
            Commands = new SalesCommandClient(api, Envelope, State, labelRepo, new ShipmentRepository(db), feed,
                new SalesConflictLogRepository(db), new NoPrompt(), license, clock, NullLogger<SalesCommandClient>.Instance);
            Round = new SalesModeOffRound(feed, State, new PendingSalesCommandRepository(db), Labels, labelRepo,
                new SalesConflictLogRepository(db), store, Settings, clock, NullLogger<SalesModeOffRound>.Instance);
            var coordinator = new SalesModeCoordinator(state, State, Envelope,
                new SalesOpeningFlush(customerPush, Push, shipments, payments, State), clock,
                NullLogger<SalesModeCoordinator>.Instance, Round);
            Pull = new SalesChangesPullService(api, state, State, Envelope, feed, new SalesFeedFailureRepository(db),
                new RemoteLiveSessionRepository(db), cursors, labelRepo, customerPush, Push, customerPull, license, clock,
                NullLogger<SalesChangesPullService>.Instance, sessions: new SessionRepository(db),
                modeCoordinator: coordinator, commands: Commands, shipmentPush: shipments,
                shipments: new ShipmentRepository(db));
        }

        public Task<SalesPullOutcome> TourAsync() => Pull.TourOnceAsync(CancellationToken.None);

        public T Scalar<T>(string sql, object? param = null)
        {
            using var c = Db.Open();
            return c.ExecuteScalar<T>(sql, param)!;
        }

        public void Exec(string sql, object? param = null)
        {
            using var c = Db.Open();
            c.Execute(sql, param);
        }

        public string Customer()
        {
            var id = Guid.NewGuid().ToString("N");
            new CustomerRepository(Db).Insert(new Customer(id, "tiktok", "alici_" + id[..8], "Takma Ad", null, 1, 1,
                false, null, null, 0, 0m, null, null, null));
            return id;
        }

        /// <summary>Bu bilgisayarın bitmiş, henüz gönderilmemiş yayını (açılışın gönderimi götürür ve devreder).</summary>
        public string EndedSession()
        {
            var id = Guid.NewGuid().ToString("N");
            new SessionRepository(Db).Insert(new StreamSession(id, "Kendi yayını", T0.ToUnixTimeSeconds(),
                T0.AddHours(1).ToUnixTimeSeconds(), ["tiktok"], null));
            return id;
        }

        public string Label(string sessionId, string customerId, decimal price, bool printed = true)
        {
            var id = Guid.NewGuid().ToString("N");
            new LabelRepository(Db).Insert(new Label(id, sessionId, customerId, "tiktok", "alici", "yorum", "A1", price,
                T0.AddMinutes(1).ToUnixTimeSeconds(), printed ? T0.AddMinutes(2).ToUnixTimeSeconds() : null,
                DisplayName: "Takma Ad"));
            return id;
        }

        public void Enqueue(SalesCommandPayload payload, IReadOnlyCollection<string> labelIds)
        {
            var intent = new SalesCommandIntent(Guid.NewGuid(), payload.Kind, payload.ToWireJson(), []);
            new PendingSalesCommandRepository(Db).Enqueue(intent.CommandId.ToString("N"), intent.Kind,
                PendingSalesCommandCodec.Encode(intent), labelIds, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .Should().BeTrue("ön koşul: yerel kip 1");
        }

        public int PendingCount => Scalar<int>("SELECT COUNT(*) FROM PendingSalesCommand");
        public int RemoteRows => Scalar<int>(
            "SELECT (SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote') + (SELECT COUNT(*) FROM Label l "
          + "JOIN StreamSession s ON s.Id = l.SessionId WHERE s.Origin = 'remote')");
    }

    private readonly FakeSalesServer _server = new();
    private readonly List<InMemorySqlite> _dbs = new();
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        _server.Dispose();
        foreach (var db in _dbs) db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var p in _paths)
            foreach (var f in new[] { p, p + ".bak", p + ".tmp" })
                try { File.Delete(f); } catch { /* en iyi çaba */ }
    }

    /// <summary>Yeni bir bilgisayar (yeni veritabanı); yerel kargo ayarı 500/50 (sunucununkinden farklı).</summary>
    private Pc NewPc()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        _dbs.Add(db);
        var path = Path.Combine(Path.GetTempPath(), $"od-disable-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        new SettingsStore(path).Update(s => s.Shipping = new ShippingSettings { FreeShippingThreshold = 500m, ShippingFee = 50m });
        return new Pc(_server, db, path);
    }

    /// <summary>Aynı veritabanında yeni süreç (yeniden açılış).</summary>
    private Pc Restart(Pc pc) => new(_server, pc.Db, pc.SettingsPath);

    private void ServerModeOn()
    {
        _server.Mode = 1;
        _server.ModeChangedAt = EnabledAt;
        _server.Shipping = new FakeSalesServer.ShippingBody(Version: 2, FreeShippingThreshold: 750m, Fee: 60m);
    }

    private static async Task OpenAsync(Pc pc)
    {
        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        pc.State.GetMode().Should().Be(1, "ön koşul: mod On açıldı");
        pc.State.Get().FirstCatchUpAt.Should().NotBeNull();
    }

    private static Guid G(string id) => Guid.ParseExact(id, "N");

    private void ServerShipment(Guid id, string customerId, Guid origin, decimal amount, string status = "pending") =>
        _server.SeedShipment(new FakeSalesServer.ShipmentBody(id, customerId, status, amount, T0.AddHours(2), null,
            status == "shipped" ? T0.AddHours(3) : null), origin: origin);

    private void Attach(string labelId, Guid shipmentId) =>
        _server.MutateOrder(G(labelId), o => o with { ShipmentId = shipmentId });

    /// <summary>Yerel kargo aynasındaki dosyanın kaynak kopyası.</summary>
    private static string? Origin(Pc pc, Guid file) =>
        pc.Scalar<string?>("SELECT OriginReplicaId FROM Shipment WHERE Id = @id", new { id = file.ToString("N") });

    private static AggregateRecomputeResult RecomputeAll(Pc pc)
    {
        using var write = DbWrite.Begin(pc.Db);
        return CustomerAggregateSql.RecomputeAll(write);
    }

    // ── tam tur, iki bilgisayar ───────────────────────────────────────────

    [Fact]
    public async Task Off_turu_uzak_satirlari_siler_toplamlari_yerelden_turetir_dosyayi_etiketlerin_sahibine_birakir_kargo_ayarini_yazar()
    {
        ServerModeOn();
        var a = NewPc();
        var customer = a.Customer();
        var sessionA = a.EndedSession();
        var labelA1 = a.Label(sessionA, customer, 40m);
        var labelA2 = a.Label(sessionA, customer, 30m);
        await OpenAsync(a);
        var b = NewPc();
        var customerB = b.Customer();
        var sessionB = b.EndedSession();
        var labelB = b.Label(sessionB, customerB, 25m);
        await OpenAsync(b);

        // Mod On döneminde PC-B, PC-A'nın etiketleri için dosya açtı (Y2: yayın sonrası işler başka PC'de) ve kendi
        // etiketine de bir dosya. PC-A'nın canlı yayın başlığı PC-B'de aynalı.
        var fileForA = Guid.NewGuid();
        ServerShipment(fileForA, customer, origin: b.Replica, amount: 70m);
        Attach(labelA1, fileForA);
        Attach(labelA2, fileForA);
        var fileForB = Guid.NewGuid();
        ServerShipment(fileForB, customerB, origin: b.Replica, amount: 25m);
        Attach(labelB, fileForB);
        _server.LiveSessions.Add(new FakeSalesServer.LiveSessionBody(Guid.NewGuid(), "Canlı", DateTimeOffset.UtcNow, null,
            "PC-A", DateTimeOffset.UtcNow, "live", null));
        (await a.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        (await b.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        b.RemoteRows.Should().Be(3, "ön koşul: PC-A'nın yayını ve iki etiketi PC-B'de");
        b.Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(1);
        new CustomerRepository(b.Db).GetById(customer)!.TotalAmount.Should().Be(70m, "ön koşul: uzak etiketler toplamda");
        Origin(a, fileForA).Should().Be(b.Replica.ToString("N"));
        new LabelRepository(a.Db).GetById(labelA1)!.ShipmentId.Should().Be(fileForA.ToString("N"));
        _server.LiveSessions.Clear();

        _server.Disable(DisabledAt);
        var statesBefore = _server.States.Count;

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);
        (await b.TourAsync()).Should().Be(SalesPullOutcome.Disabled);

        foreach (var pc in new[] { a, b })
        {
            var state = pc.State.Get();
            state.Mode.Should().Be(0);
            state.ModeSeenAt.Should().Be(DisabledAt.ToUnixTimeSeconds());
            pc.RemoteRows.Should().Be(0, "Off'ta uzak satır kalmaz (§6.5-1)");
            pc.Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(0);
            pc.Scalar<int>("SELECT COUNT(*) FROM StreamSession WHERE Authority = 'server'").Should().Be(0,
                "kendi yayınları yerel otoriteye döndü");
            RecomputeAll(pc).ChangedRows.Should().Be(0, "toplamlar yalnız yerel etiketlerden türetilmiş");
            pc.Settings.Shipping.FreeShippingThreshold.Should().Be(750m, "lisans geneli kargo ayarı yerele yazıldı (§6.2)");
            pc.Settings.Shipping.ShippingFee.Should().Be(60m);
            new SettingsStore(pc.SettingsPath).Load().Shipping.ShippingFee.Should().Be(60m);
            pc.Envelope.SeenMode.Should().Be(0, "tur bitti: görülen kip 0");
        }
        _server.States.Skip(statesBefore).Select(s => s.SeenMode).Should().Equal(["1", "1"],
            "turun başındaki state görülen kip 1 taşır: tur bitmedi");
        new CustomerRepository(b.Db).GetById(customer)!.TotalAmount.Should().Be(0m, "PC-B'de PC-A'nın etiketi yok");
        Origin(a, fileForA).Should().Be(a.Replica.ToString("N"),
            "dosya etiketlerin durduğu PC-A'ya geçti (M19)");
        new ShipmentRepository(b.Db).GetById(fileForA.ToString("N")).Should().BeNull("PC-B dosyayı tutmaz");
        new ShipmentRepository(b.Db).GetById(fileForB.ToString("N")).Should().NotBeNull("PC-B'nin kendi dosyası kalır");
        new ShipmentRepository(a.Db).GetById(fileForB.ToString("N")).Should().BeNull("PC-A'da başkasının açık dosyası kalmaz");
        new ShipmentService(new ShipmentRepository(a.Db), new LabelRepository(a.Db), () => a.Settings)
            .GetOrCreateOpenShipment(customer).Id.Should().Be(fileForA.ToString("N"),
                "PC-A'nın sonraki dekontu dosyayı bulur; eşik doğru havuzdan");

        (await a.TourAsync()).Should().Be(SalesPullOutcome.ModeOff, "tur bir kez");
        _server.States[^1].SeenMode.Should().Be("0");
        _server.ReplicaRow(_server.LicenseId, a.Replica)!.SeenMode.Should().Be(0, "sunucu turun bittiğini görür (§8.6)");
        _server.Faults.Should().BeEmpty();
    }

    // ── PC-A kapalıyken PC-B dosyayı kargoladı ─────────────────────────────

    [Fact]
    public async Task Pc_A_kapaliyken_kargolanan_dosya_son_akista_Shipped_iner_sonraki_dekont_yeni_dosya_acar()
    {
        ServerModeOn();
        var a = NewPc();
        var customer = a.Customer();
        var session = a.EndedSession();
        var label = a.Label(session, customer, 40m);
        await OpenAsync(a);
        var file = Guid.NewGuid();
        ServerShipment(file, customer, origin: a.Replica, amount: 40m);
        Attach(label, file);
        (await a.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        new ShipmentRepository(a.Db).GetById(file.ToString("N"))!.Status.Should().Be(ShipmentStatus.Pending);

        // PC-A kapalı: PC-B dosyayı kargoya verir, ardından mod kapanır.
        _server.MutateShipment(file, s => s with { Status = "shipped", ShippedAt = DateTimeOffset.UtcNow });
        _server.Disable(DisabledAt);

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);

        var local = new ShipmentRepository(a.Db).GetById(file.ToString("N"))!;
        local.Status.Should().Be(ShipmentStatus.Shipped, "son akış dosya öğelerini de uygular (§8.5-1)");
        var next = new ShipmentService(new ShipmentRepository(a.Db), new LabelRepository(a.Db), () => a.Settings)
            .GetOrCreateOpenShipment(customer);
        next.Id.Should().NotBe(file.ToString("N"), "kargolanmış dosyaya yeni etiket bağlanmaz: yeni dosya");
        _server.Faults.Should().BeEmpty();
    }

    // ── bekleyen komutlar ──────────────────────────────────────────────────

    [Fact]
    public async Task Bekleyen_iptal_ve_baski_onayi_yerel_yazima_cevrilir_push_ile_gider_kagit_yeniden_teklif_edilmez()
    {
        ServerModeOn();
        var a = NewPc();
        var customer = a.Customer();
        var session = a.EndedSession();
        var cancel = a.Label(session, customer, 40m, printed: false);
        var print = a.Label(session, customer, 30m, printed: false);
        await OpenAsync(a);
        new SessionRepository(a.Db).GetById(session)!.Authority.Should().Be(StreamSessionAuthority.Server);
        // Çevrimdışıyken kalıntı kuyruğun iptali ve kâğıdı basılmış etiketin onayı (§5.5).
        a.Enqueue(new SalesCancelPayload([G(cancel)], "musteri-vazgecti"), [cancel]);
        var batch = LabelService.NewPrintBatchId();
        a.Enqueue(new SalesMarkPrintedPayload(batch, [new SalesMarkPrintedItem(G(print), 1)]), [print]);
        _server.Disable(DisabledAt);

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);

        _server.CommandCalls.Should().BeEmpty("Off turunda bekleyen komut gönderilmez (komut ucu mode-off)");
        a.PendingCount.Should().Be(0);
        a.Envelope.SeenMode.Should().Be(0);
        var labels = new LabelRepository(a.Db);
        labels.GetById(cancel)!.CancelledAt.Should().NotBeNull();
        labels.GetById(cancel)!.SyncedAt.Should().BeNull("kuyruğa girdi");
        labels.GetById(print)!.PrintedAt.Should().NotBeNull();
        labels.GetById(print)!.SyncedAt.Should().BeNull();
        labels.GetUnprintedOutsideSession(Guid.NewGuid().ToString("N")).Should().BeEmpty(
            "basılmış kâğıt ve iptal edilen kalıntı yeniden teklif edilmez");

        await a.Push.SyncOnceAsync();

        _server.Order(G(cancel))!.Row.CancelledAt.Should().NotBeNull("yerel yazım push'la gitti");
        _server.Order(G(print))!.Row.PrintBatchId.Should().Be(batch, "aynı basış kimliği");
        _server.Order(G(print))!.Row.PrintSeq.Should().Be(1);
        labels.GetById(print)!.SyncedAt.Should().NotBeNull();
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Ikinci_ile_ucuncu_adim_arasinda_kesilirse_acilista_ucuncu_adim_kosar_o_ana_kadar_gorulen_kip_1()
    {
        ServerModeOn();
        var a = NewPc();
        var customer = a.Customer();
        var session = a.EndedSession();
        var cancel = a.Label(session, customer, 40m, printed: false);
        await OpenAsync(a);
        a.Enqueue(new SalesCancelPayload([G(cancel)], "musteri-vazgecti"), [cancel]);
        // 3. adımın yerel yazımı düşer (süreç 2. ile 3. adım arasında ölmüş gibi).
        a.Exec("CREATE TRIGGER TestFailCancel BEFORE UPDATE OF CancelledAt ON Label BEGIN SELECT RAISE(ABORT, 'test'); END");
        _server.Disable(DisabledAt);

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);

        a.State.GetMode().Should().Be(0, "2. adım işlendi");
        a.PendingCount.Should().Be(1, "3. adım yarıda kaldı");
        a.Envelope.SeenMode.Should().Be(1);
        (await a.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);
        _server.States[^1].SeenMode.Should().Be("1", "tur bitene kadar state görülen kip 1 taşır");
        _server.ReplicaRow(_server.LicenseId, a.Replica)!.SeenMode.Should().Be(1);

        a.Exec("DROP TRIGGER TestFailCancel");
        var restarted = Restart(a);
        restarted.Envelope.SeenMode.Should().Be(1, "ön koşul: açılışta kuyruk dolu");
        restarted.Round.Resume().Converted.Should().Be(1, "AppHost açılışta, gönderimden önce 3. adımı yeniden koşar");
        restarted.Envelope.SeenMode.Should().Be(0);
        var hellos = _server.Hellos.Count;

        (await restarted.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);

        _server.Hellos.Skip(hellos).Should().ContainSingle().Which.Json.GetProperty("seenMode").GetInt32().Should().Be(0);
        _server.States[^1].SeenMode.Should().Be("0");
        _server.ReplicaRow(_server.LicenseId, a.Replica)!.SeenMode.Should().Be(0);
        new LabelRepository(a.Db).GetById(cancel)!.CancelledAt.Should().NotBeNull();
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Ucuncu_adimin_yazimi_surec_icinde_duserse_sonraki_turun_basinda_cevrilir_gorulen_kip_0()
    {
        ServerModeOn();
        var a = NewPc();
        var customer = a.Customer();
        var session = a.EndedSession();
        var cancel = a.Label(session, customer, 40m, printed: false);
        await OpenAsync(a);
        a.Enqueue(new SalesCancelPayload([G(cancel)], "musteri-vazgecti"), [cancel]);
        a.Exec("CREATE TRIGGER TestFailCancel BEFORE UPDATE OF CancelledAt ON Label BEGIN SELECT RAISE(ABORT, 'test'); END");
        _server.Disable(DisabledAt);
        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);
        a.PendingCount.Should().Be(1, "ön koşul: 3. adım yarıda kaldı");

        a.Exec("DROP TRIGGER TestFailCancel");                // yazımı düşüren geçici sebep geçti; süreç aynı

        (await a.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);

        a.PendingCount.Should().Be(0, "turun başı 3. adımı yeniden koştu — yeniden başlatma beklenmez");
        _server.States[^1].SeenMode.Should().Be("0", "aynı turun state'i görülen kip 0 taşır");
        new LabelRepository(a.Db).GetById(cancel)!.CancelledAt.Should().NotBeNull();
        _server.Faults.Should().BeEmpty();
    }

    // ── komut ucunun mode-off'u ───────────────────────────────────────────

    [Fact]
    public async Task Komutun_409_mode_offu_state_okutur_ve_Off_turunu_hemen_baslatir()
    {
        ServerModeOn();
        var a = NewPc();
        var customer = a.Customer();
        var session = a.EndedSession();
        var label = a.Label(session, customer, 40m);
        await OpenAsync(a);
        _server.Disable(DisabledAt);
        var started = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Pull.ModeOffStarted += (_, _) => Interlocked.Increment(ref started);
        a.Pull.ModeOffFinished += (_, _) => finished.TrySetResult();
        var statesBefore = _server.States.Count;

        var outcome = await a.Commands.SendAsync(a.Commands.Cancel([label], "musteri-vazgecti"));

        outcome.Status.Should().Be(SalesCommandStatus.ModeOff);
        outcome.Message.Should().Be("Senkron kapatıldı; ekran yenileniyor.");
        started.Should().Be(1, "kabuk şeridi açar");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        a.State.GetMode().Should().Be(0, "zamanlayıcıyı beklemeden Off turu koştu");
        _server.States.Count.Should().BeGreaterThan(statesBefore, "sales/state hemen okundu");
        new SessionRepository(a.Db).GetById(session)!.Authority.Should().Be(StreamSessionAuthority.Local);
        new LabelRepository(a.Db).GetById(label)!.CancelledAt.Should().BeNull("komut uygulanmadı; operatör yeniden dener");
        _server.Faults.Should().BeEmpty();
    }

    // ── mod On dönemini hiç görmemiş kopya ────────────────────────────────

    [Fact]
    public async Task Mod_On_donemini_gormemis_kopya_yeni_modeChangedAti_gorunce_birinci_ve_ikinci_adimi_bir_kez_kosar()
    {
        var a = NewPc();
        var customer = a.Customer();
        var session = a.EndedSession();
        var label = a.Label(session, customer, 40m);
        await a.Push.SyncOnceAsync();                        // mod Off'ta bugünkü gönderim: yayın ve etiket sunucuda
        new SessionRepository(a.Db).GetById(session)!.SyncedAt.Should().NotBeNull("ön koşul");
        // Mod On dönemi PC-A kapalıyken yaşandı: PC-B kendi yayınını yaptı, PC-A'nın etiketini iptal etti ve onun için
        // dosya açtı; ardından mod kapandı.
        var other = Guid.NewGuid();
        var remoteSession = Guid.NewGuid();
        _server.SeedSession(remoteSession, T0, T0.AddHours(1), owner: other, ownerMachineName: "PC-B");
        _server.ReleaseSession(remoteSession);
        _server.SeedOrder(new FakeSalesServer.OrderBody(Guid.NewGuid(), remoteSession, customer, "tiktok", "alici",
            "Takma Ad", "aldım", "B1", 150m, T0.AddMinutes(1), T0.AddMinutes(2), null, null, false, false, false, null,
            null, null, null, null, null, null, null, null), version: 1, origin: other);
        _server.MutateOrder(G(label), o => o with { CancelledAt = DisabledAt.AddHours(-1), CancelReason = "stok-yok" });
        var file = Guid.NewGuid();
        ServerShipment(file, customer, origin: other, amount: 40m);
        Attach(label, file);
        _server.Shipping = new FakeSalesServer.ShippingBody(Version: 3, FreeShippingThreshold: 800m, Fee: 70m);
        _server.Disable(DisabledAt);

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);

        var state = a.State.Get();
        state.Mode.Should().Be(0);
        state.ModeSeenAt.Should().Be(DisabledAt.ToUnixTimeSeconds());
        a.RemoteRows.Should().Be(0, "yerel kip 0'da uzak etiket hiç inmez (§6.5-1)");
        a.Scalar<int>("SELECT COUNT(*) FROM Label").Should().Be(1);
        var row = new LabelRepository(a.Db).GetById(label)!;
        row.CancelledAt.Should().NotBeNull("dönemde başka bilgisayarın iptali kendi etiketine indi");
        row.ShipmentId.Should().Be(file.ToString("N"));
        Origin(a, file).Should().Be(a.Replica.ToString("N"),
            "dönemde başka bilgisayarın onun etiketleriyle açtığı dosya ona geçti");
        a.Settings.Shipping.FreeShippingThreshold.Should().Be(800m);
        _server.ChangesCalls.Should().OnlyContain(c => c.SeenMode == "0", "yerel kip 0: görülen kip 0");
        var changes = _server.ChangesCalls.Count;

        (await a.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);

        _server.ChangesCalls.Count.Should().Be(changes, "görülen an yazıldı: tur bir kez");
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Gorulen_an_sunucununkiyle_ayniysa_mod_Offta_tur_yok()
    {
        var a = NewPc();
        a.Exec("UPDATE SalesSyncState SET ModeSeenAt = @at WHERE Id = 1", new { at = DisabledAt.ToUnixTimeSeconds() });
        _server.Disable(DisabledAt);

        (await a.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);

        _server.ChangesCalls.Should().BeEmpty("mod Off'ta akış uygulanmaz (bugünkü gibi)");
        _server.PushCalls.Should().BeEmpty();
    }

    /// <summary>Mod On dönemini görmemiş kopyanın bu kopyada açtığı, gönderilmiş (sürümü bilinen) dosyası; ardından yerel,
    /// gönderilmemiş "Beklet" kararı ve ikinci dekontun tutarı (Held/140).</summary>
    private static async Task<(string Id, string Customer)> UnsentLocalDecisionAsync(Pc pc)
    {
        var customer = pc.Customer();
        var id = Guid.NewGuid().ToString("N");
        var repo = new ShipmentRepository(pc.Db);
        repo.Insert(new Shipment(id, customer, ShipmentStatus.Pending, T0.ToUnixTimeSeconds(), null, null, 60m));
        await pc.ShipmentPush.SyncOnceAsync();                 // mod Off'ta bugünkü gönderim
        repo.GetById(id)!.SyncedAt.Should().NotBeNull("ön koşul: dosya sunucuda");
        repo.Update(new Shipment(id, customer, ShipmentStatus.Held, T0.ToUnixTimeSeconds(),
            DisabledAt.AddMinutes(1).ToUnixTimeSeconds(), null, 140m));
        return (id, customer);
    }

    [Fact]
    public async Task Mod_On_donemini_gormemis_kopyanin_gonderilmemis_dosya_karari_son_akista_ezilmez_gonderilir()
    {
        var a = NewPc();
        var (file, _) = await UnsentLocalDecisionAsync(a);
        _server.Disable(DisabledAt);

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);

        var local = new ShipmentRepository(a.Db).GetById(file)!;
        local.Status.Should().Be(ShipmentStatus.Held, "gönderilmemiş karar sunucunun eski hâliyle ezilmez");
        local.CumulativeAmount.Should().Be(140m);
        local.SyncedAt.Should().NotBeNull("Off turunun ön gönderimi götürdü");
        var server = _server.Shipment(G(file))!.Row;
        server.Status.Should().Be("held");
        server.CumulativeAmount.Should().Be(140m);
        _server.ChangesCalls.Should().HaveCount(2, "ön gönderim kargoyu götürdü: son akış hiç durmadı");
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Kargo_on_gonderimi_dusse_de_duran_dosya_kargo_gonderimiyle_ayni_turda_biter()
    {
        var a = NewPc();
        var (file, _) = await UnsentLocalDecisionAsync(a);
        _server.Disable(DisabledAt);
        _server.EnqueueStatus(FakeSalesServer.ShipmentsSync, 500);      // turun ön gönderimi düşer

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled, "durmadan sonraki gönderim dosyanın kuyruğundan");

        new ShipmentRepository(a.Db).GetById(file)!.CumulativeAmount.Should().Be(140m);
        _server.Shipment(G(file))!.Row.CumulativeAmount.Should().Be(140m);
    }

    // ── turdan sonra W1 yolu ──────────────────────────────────────────────

    [Fact]
    public async Task Turdan_sonra_yok_sayilan_push_yaniti_currenti_W1_yolundan_uygular()
    {
        ServerModeOn();
        var a = NewPc();
        var customer = a.Customer();
        var session = a.EndedSession();
        var label = a.Label(session, customer, 40m);
        await OpenAsync(a);
        _server.Disable(DisabledAt);
        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);

        a.Labels.Cancel([label], "musteri-vazgecti");        // Off'ta yerel yol: yayını artık yerel
        new LabelRepository(a.Db).GetById(label)!.SyncedAt.Should().BeNull("ön koşul: kuyrukta");
        // Sunucu: damgalı iptal birimi bayat gönderimi yok sayar, güncel satırı döner (K2).
        _server.OrderVerdicts[G(label)] = new FakeSalesServer.OrderVerdict("ignored", "unit-stamped", ["cancel"],
            Current: item => item with { CancelledAt = null, CancelReason = null, Price = 55m });

        await a.Push.SyncOnceAsync();

        var row = new LabelRepository(a.Db).GetById(label)!;
        row.CancelledAt.Should().BeNull("ignored'daki current uygulandı (§8.5-4, §5.2)");
        row.SyncedAt.Should().NotBeNull();
        _server.Faults.Should().BeEmpty();
    }
}
