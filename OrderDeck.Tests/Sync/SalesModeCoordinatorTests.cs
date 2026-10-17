using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sales;
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
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Mod On'daki ilk tur (spec 2026-10-13 §8.4, §4.0, §5.3 ara durum, §5.4 catchup-overwrite, §6.1; W2-10) — satış akışı
/// turunun içinde, başarılı state'in hemen ardından (<see cref="SalesModeCoordinator"/>): 1. adım bütün giden kuyrukları
/// görülen kip 0 ile boşaltır (müşteri, oturum ve etiket, kargo, dekont); biri düşerse yerel kip 0 kalır. 2. adım kilit
/// altında yeniden denetler (1. ile 2. adım arasında kaydedilen dekontun kargo yazımı) ve tek işlemde kipi, görülen anı
/// ve devri bilinen kendi yayınlarının otoritesini yazar. 3. adım akışı 0'dan alır; 4. adım ufukta toplamları türetir,
/// <c>FirstCatchUpAt</c> yazar ve kendi satırında sunucunun değeri alındıysa bir kez bildirir. Canlı yayın yerel kalır.
/// Sunucu <see cref="FakeSalesServer"/>; gönderim servisleri gerçek.
/// </summary>
public sealed class SalesModeCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L);
    private static readonly DateTimeOffset ModeChangedAt = T0.AddDays(3);

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

    /// <summary>Bir bilgisayarın yığını (bir süreç): zarf, durum turu, gönderim servisleri, açılış ve satış akışı.</summary>
    private sealed class Pc
    {
        public License License { get; }
        public SalesSyncEnvelope Envelope { get; }
        public SalesSyncStateRepository State { get; }
        public SessionOrderSyncService Push { get; }
        public SalesModeCoordinator Coordinator { get; }
        public SalesChangesPullService Pull { get; }
        public CapturingLogger<SalesModeCoordinator> Log { get; } = new();
        public List<SalesFirstCatchUp> CatchUps { get; } = new();

        public Pc(FakeSalesServer server, IDbConnectionFactory db, string settingsPath, string fingerprint)
        {
            License = new License(server.Lisans);
            var identity = new ReplicaIdentityRepository(db);
            State = new SalesSyncStateRepository(db);
            Envelope = new SalesSyncEnvelope(identity, State, new Hardware(fingerprint), License,
                NullLogger<SalesSyncEnvelope>.Instance);
            var api = new LicenseApiClient(
                new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore());
            var clock = new Clock();
            var busy = new CustomerBusySet();
            var cursors = new SyncCursorRepository(db);
            var customerSync = new CustomerSyncRepository(db, busy);
            var state = new SalesSyncStateService(api, Envelope, identity, State, License, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance);
            Push = new SessionOrderSyncService(api, new SessionRepository(db), new LabelRepository(db), License, Envelope,
                new HandoverGate(), new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance);
            var store = new SettingsStore(settingsPath);
            var shipments = new ShipmentSyncService(api, new ShipmentRepository(db), store, store.Load(), License,
                Envelope, clock, NullLogger<ShipmentSyncService>.Instance, salesState: State);
            var payments = new PaymentSyncService(api, new PaymentRepository(db), cursors, License, clock,
                NullLogger<PaymentSyncService>.Instance);
            var customerPush = new WpfCustomerProjectionSyncService(api, customerSync, cursors, License, clock,
                NullLogger<WpfCustomerProjectionSyncService>.Instance);
            var customerPull = new CustomerChangesPullService(api, new CustomerRepository(db), customerSync, cursors,
                customerPush, License, clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance);
            Coordinator = new SalesModeCoordinator(state, State,
                new SalesOpeningFlush(customerPush, Push, shipments, payments, State), clock, Log);
            Pull = new SalesChangesPullService(api, state, State, Envelope, new SalesFeedRepository(db, busy),
                new SalesFeedFailureRepository(db), new RemoteLiveSessionRepository(db), cursors, new LabelRepository(db),
                customerPush, Push, customerPull, License, clock, NullLogger<SalesChangesPullService>.Instance,
                sessions: new SessionRepository(db), modeCoordinator: Coordinator);
            Pull.FirstCatchUpCompleted += (_, e) => { lock (CatchUps) CatchUps.Add(e); };
        }

        public Task<SalesPullOutcome> TourAsync() => Pull.TourOnceAsync(CancellationToken.None);
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"od-opening-{Guid.NewGuid():N}.json");
    private readonly string _fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly Guid _otherReplica = Guid.NewGuid();

    public SalesModeCoordinatorTests() => new MigrationRunner(_db).Run();

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_settingsPath); } catch { /* en iyi çaba */ }
    }

    private Pc NewPc() => new(_server, _db, _settingsPath, _fingerprint);

    private Guid ReplicaId => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

    private void ServerModeOn()
    {
        _server.Mode = 1;
        _server.ModeChangedAt = ModeChangedAt;
    }

    // ── kurulum: yerel ────────────────────────────────────────────────────

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

    private string LocalCustomer()
    {
        var id = Guid.NewGuid().ToString("N");
        new CustomerRepository(_db).Insert(new Customer(id, "tiktok", "alici_" + id[..8], "Takma Ad", null, 1, 1, false,
            null, null, 0, 0m, null, null, null));
        return id;
    }

    /// <summary>Bu bilgisayarın yayını; <paramref name="endedAt"/> boşsa canlı.</summary>
    private string LocalSession(DateTimeOffset? endedAt, bool synced = true, DateTimeOffset? releasedAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(id, "Kendi yayını", T0.ToUnixTimeSeconds(),
            endedAt?.ToUnixTimeSeconds(), ["tiktok"], null, SyncedAt: synced ? T0.ToUnixTimeSeconds() + 10 : null));
        if (releasedAt is { } r)
            Exec("UPDATE StreamSession SET ReleasedAt = @r WHERE Id = @id", new { r = r.ToUnixTimeSeconds(), id });
        return id;
    }

    private string LocalLabel(string sessionId, string customerId, decimal price = 40m, bool synced = true)
    {
        var id = Guid.NewGuid().ToString("N");
        new LabelRepository(_db).Insert(new Label(id, sessionId, customerId, "tiktok", "alici_yerel", "kendi yorumu",
            "B2", price, T0.ToUnixTimeSeconds() + 60, T0.ToUnixTimeSeconds() + 120, DisplayName: "Takma Ad",
            SyncedAt: synced ? T0.ToUnixTimeSeconds() + 200 : null));
        return id;
    }

    private void LocalShipment(string customerId, bool synced) =>
        new ShipmentRepository(_db).Insert(new Shipment(Guid.NewGuid().ToString("N"), customerId, ShipmentStatus.Pending,
            T0.ToUnixTimeSeconds(), null, null, 40m, SyncedAt: synced ? T0.ToUnixTimeSeconds() + 10 : null));

    private void LocalPayment() =>
        new PaymentRepository(_db).Insert(new Payment(Guid.NewGuid().ToString("N"), $"odeyen-{Guid.NewGuid():N}", 40m,
            T0.ToUnixTimeSeconds(), $"ref-{Guid.NewGuid():N}", null, PaymentStatus.Pending, T0.ToUnixTimeSeconds(),
            T0.ToUnixTimeSeconds(), null, null, null, null));

    private StreamSession Session(string id) => new SessionRepository(_db).GetById(id)!;

    private int ConflictCount(string kind) =>
        Scalar<int>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @kind", new { kind });

    private static Guid G(string id) => Guid.ParseExact(id, "N");

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    // ── kurulum: sunucu ───────────────────────────────────────────────────

    /// <summary>Yerel etiketin sunucudaki kopyası (aynı Id ve oluşum alanları); fiyat başka bilgisayarda değişmiş olabilir.</summary>
    private FakeSalesServer.OrderBody ServerCopy(string labelId, decimal? price = null)
    {
        var l = new LabelRepository(_db).GetById(labelId)!;
        return new FakeSalesServer.OrderBody(G(l.Id), G(l.SessionId), l.CustomerId, l.Platform, l.Username,
            l.DisplayName, l.MessageText, l.Code, price ?? l.Price, At(l.AddedAt), l.PrintedAt is { } p ? At(p) : null,
            l.CancelledAt is { } c ? At(c) : null, l.CancelReason, l.IsShippingFee, l.IsBackupPromoted,
            l.IsTentativeBackup, null, null, null, null, null, null, null, null, null);
    }

    /// <summary>Başka bilgisayarın devredilmiş yayını ve <paramref name="orders"/> siparişi (akışa girer).</summary>
    private void RemoteSales(int orders)
    {
        var session = Guid.NewGuid();
        _server.SeedSession(session, T0, T0.AddHours(1), owner: _otherReplica, title: "Akşam yayını",
            ownerMachineName: "PC-B");
        _server.ReleaseSession(session);
        var customer = Guid.NewGuid();
        _server.SeedCustomer(customer, "tiktok", "uzak_alici", "Takma Ad");
        for (var i = 0; i < orders; i++)
            _server.SeedOrder(new FakeSalesServer.OrderBody(Guid.NewGuid(), session, customer.ToString("N"), "tiktok",
                "uzak_alici", "Takma Ad", "aldım", "A1", 150m, T0.AddMinutes(1), T0.AddMinutes(2), null, null, false,
                false, false, null, null, null, null, null, null, null, null, null), version: 1, origin: _otherReplica);
    }

    private static int SeenModeOf(FakeSalesServer.PushCall call) => call.Json.GetProperty("seenMode").GetInt32();

    private static long AfterSeq(FakeSalesServer.StateCall call) =>
        long.Parse(Regex.Match(call.Query, @"[?&]afterSeq=(-?\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    // ── (a) 1. adım: kuyruklar görülen kip 0 ile ──────────────────────────

    [Fact]
    public async Task Acilis_turu_kuyruklari_gorulen_kip_0_ile_bosaltir_kilit_altinda_modu_acar_akisi_bastan_alir()
    {
        var customer = LocalCustomer();
        var session = LocalSession(endedAt: T0.AddHours(1), synced: false);
        var label = LocalLabel(session, customer, synced: false);
        LocalShipment(customer, synced: false);
        LocalPayment();
        ServerModeOn();
        var pc = NewPc();

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        foreach (var endpoint in new[] { FakeSalesServer.SessionsSync, FakeSalesServer.OrdersSync, FakeSalesServer.ShipmentsSync })
            _server.Calls(endpoint).Should().NotBeEmpty($"1. adım {endpoint} kuyruğunu boşaltır")
                .And.OnlyContain(c => SeenModeOf(c) == 0, "yerel kip açılana kadar her gönderim görülen kip 0 taşır (§8.4)");
        _server.Calls(FakeSalesServer.PaymentsSync).Should().ContainSingle("dekont kuyruğu da boşaltılır");
        _server.CustomerPushCalls.Should().NotBeEmpty("müşteri kuyruğu da");
        var state = pc.State.Get();
        state.Mode.Should().Be(1);
        state.ModeSeenAt.Should().Be(ModeChangedAt.ToUnixTimeSeconds(), "ModeSeenAt = sales/state'in modeChangedAt'i");
        var changes = _server.ChangesCalls.Should().NotBeEmpty().And.Subject.First();
        AfterSeq(changes).Should().Be(0, "akış 0'dan (§8.4-3)");
        changes.SeenMode.Should().Be("1");
        state.FirstCatchUpAt.Should().NotBeNull("tek sayfalık akış: ufka bu turda varıldı");
        Session(session).Authority.Should().Be(StreamSessionAuthority.Server,
            "kapanışın devri 1. adımda görüldü, 2. adım yayını sunucuya verdi");
        new LabelRepository(_db).GetById(label)!.SyncedAt.Should().NotBeNull();
        Scalar<int>("SELECT COUNT(*) FROM Shipment WHERE SyncedAt IS NULL").Should().Be(0);
        pc.Pull.Status.ModeOn.Should().BeTrue();
        pc.Pull.Status.OpeningPending.Should().BeFalse();
        _server.Hellos.Single().Json.GetProperty("featureLevel").GetInt32().Should().Be(2, "W2 derlemesi (§8.1-4)");
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Bir_gonderim_duserse_yerel_kip_0_kalir_durum_aciliyor_der_sonraki_tur_tamamlar()
    {
        var customer = LocalCustomer();
        var session = LocalSession(endedAt: T0.AddHours(1), synced: false);
        LocalLabel(session, customer, synced: false);
        LocalShipment(customer, synced: false);
        LocalPayment();
        ServerModeOn();
        var pc = NewPc();
        _server.EnqueueStatus(FakeSalesServer.ShipmentsSync, 500);

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.Opening);

        pc.State.GetMode().Should().Be(0, "kargo kuyruğu boşalmadı: yerel kip 0 kalır");
        pc.State.Get().ModeSeenAt.Should().BeNull();
        pc.Pull.Status.Should().Be(SalesFeedStatus.Opening, "durum satırı: Senkron açılıyor; bekleyen gönderim var");
        _server.ChangesCalls.Should().BeEmpty("açılmadan akış istenmez");
        _server.Calls(FakeSalesServer.PaymentsSync).Should().BeEmpty("boşalmayan ilk kuyrukta durulur");
        ConflictCount(SalesConflictLog.ModeOpened).Should().Be(0);
        pc.Log.Warnings.Should().ContainSingle().Which.Message.Should().Contain("kargo");

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        pc.State.GetMode().Should().Be(1, "sonraki tur 1. adımdan yeniden dener");
        _server.Calls(FakeSalesServer.ShipmentsSync).Should().HaveCount(2).And.OnlyContain(c => SeenModeOf(c) == 0);
        pc.Pull.Status.OpeningPending.Should().BeFalse();
        pc.Log.Warnings.Should().ContainSingle("bekleyen gönderim seride bir kez uyarılır");
    }

    // ── (b) 1. ile 2. adım arasında kaydedilen dekont ─────────────────────

    [Fact]
    public async Task Birinci_ile_ikinci_adim_arasinda_kaydedilen_dekont_ikinci_adimda_hicbir_sey_yazdirmaz_sonraki_tur_tamamlar()
    {
        var customer = LocalCustomer();
        LocalPayment();
        ServerModeOn();
        var pc = NewPc();
        var shipments = new ShipmentService(new ShipmentRepository(_db), new LabelRepository(_db), () => new AppSettings());
        var saved = 0;
        // Dekont gönderilirken operatör başka bir dekont kaydeder: kargo adımı (mod 0'da yerel) dosya yazar. Kargo
        // kuyruğu 1. adımda zaten boş bulunmuştu; yalnız 2. adımın kilit altındaki denetimi görür.
        _server.DuringPush = endpoint =>
        {
            if (endpoint == FakeSalesServer.PaymentsSync && Interlocked.Exchange(ref saved, 1) == 0)
                shipments.GetOrCreateOpenShipment(customer);
        };

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.Opening);

        saved.Should().Be(1, "ön koşul: dekont 1. adımın dekont gönderimi sırasında kaydedildi");
        pc.State.GetMode().Should().Be(0, "2. adım kilit altında gönderilmemiş dosyayı gördü: hiçbir şey yazılmadı");
        pc.State.Get().ModeSeenAt.Should().BeNull();
        Scalar<int>("SELECT COUNT(*) FROM Shipment WHERE SyncedAt IS NULL").Should().Be(1);
        ConflictCount(SalesConflictLog.ModeOpened).Should().Be(0);
        _server.ChangesCalls.Should().BeEmpty();
        pc.Log.Warnings.Should().ContainSingle().Which.Message.Should().Contain("kilit altında 1 kargo");

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        pc.State.GetMode().Should().Be(1, "sonraki tur dosyayı görülen kip 0 ile gönderir ve açılır");
        _server.Calls(FakeSalesServer.ShipmentsSync).Should().ContainSingle().Which.Should().Match<FakeSalesServer.PushCall>(
            c => SeenModeOf(c) == 0);
        Scalar<int>("SELECT COUNT(*) FROM Shipment WHERE SyncedAt IS NULL").Should().Be(0);
        _server.Faults.Should().BeEmpty();
    }

    // ── (d) devri bilinen yayın hemen server; bilinmeyen ilk yetişmeye kadar kapalı ─

    [Fact]
    public async Task Devri_bilinen_kendi_yayini_hemen_sunucuya_gecer_ilk_yetisme_surerken_devri_bilinmeyen_kapali()
    {
        var customer = LocalCustomer();
        var known = LocalSession(endedAt: T0.AddHours(1), releasedAt: T0.AddHours(2));     // devri mod Off'ta görüldü
        var knownLabel = LocalLabel(known, customer);
        var unknown = LocalSession(endedAt: T0.AddHours(3));                               // devri yerelde bilinmiyor
        var unknownLabel = LocalLabel(unknown, customer);
        var replica = ReplicaId;
        _server.SeedSession(G(known), T0, T0.AddHours(1), owner: replica, releasedAt: T0.AddHours(2));
        _server.SeedOrder(ServerCopy(knownLabel), version: 1, origin: replica);
        _server.SeedSession(G(unknown), T0, T0.AddHours(3), owner: replica);
        _server.SeedOrder(ServerCopy(unknownLabel), version: 1, origin: replica);
        ServerModeOn();
        var pc = NewPc();
        _server.EnqueueStatus(FakeSalesServer.Changes, 500);                                // ilk yetişme bu turda bitmez

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.Failed);

        pc.State.GetMode().Should().Be(1);
        pc.State.Get().FirstCatchUpAt.Should().BeNull();
        pc.Pull.IsFirstCatchUpPending().Should().BeTrue();
        Session(known).Authority.Should().Be(StreamSessionAuthority.Server, "2. adımda, akışı beklemeden");
        Session(unknown).Authority.Should().Be(StreamSessionAuthority.Local);
        using (var bed = new SalesEditTestBed(_db))
        {
            var decisions = bed.Router.Route([knownLabel, unknownLabel]);
            decisions.Single(d => d.LabelId == knownLabel).Route.Should().Be(SalesEditRoute.Command,
                "ilk yetişme sürerken devri bilinen yayında yerel yol seçilmez (K5 ile reddedilip kaybolurdu)");
            var blocked = decisions.Single(d => d.LabelId == unknownLabel);
            blocked.Route.Should().Be(SalesEditRoute.Blocked, "sunucuda onarım işiyle devredilmiş olabilir (§4.9-a)");
            blocked.Reason.Should().Be(SalesCommandMessages.NotReady);

            (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

            pc.State.Get().FirstCatchUpAt.Should().NotBeNull();
            pc.Pull.IsFirstCatchUpPending().Should().BeFalse();
            bed.Router.Route([unknownLabel]).Single().Route.Should().Be(SalesEditRoute.LocalGated,
                "ilk yetişmeden sonra sunucuda devredilmemiş kendi yayını kapılı yerel yoldan düzenlenir");
        }
        _server.Faults.Should().BeEmpty();
    }

    // ── (e) ilk yetişmede üstüne yazım: tek uyarı, ufukta türetme ────────

    [Fact]
    public async Task Farkli_kendi_satiri_tek_bildirim_ve_sayi_verir_FirstCatchUpAt_yalniz_ufukta_toplamlar_turetilir()
    {
        var customer = LocalCustomer();
        var own = LocalSession(endedAt: T0.AddHours(1), releasedAt: T0.AddHours(2));
        var label = LocalLabel(own, customer, price: 40m);
        var drifted = LocalCustomer();
        Exec("UPDATE Customer SET TotalAmount = 999, TotalLabelsPrinted = 9 WHERE Id = @drifted", new { drifted });
        var replica = ReplicaId;
        _server.SeedSession(G(own), T0, T0.AddHours(1), owner: replica, releasedAt: T0.AddHours(2));
        _server.SeedOrder(ServerCopy(label, price: 50m), version: 2, origin: replica);   // geri yüklenmiş kopyanın bayat yereli
        RemoteSales(orders: 11);
        _server.PageLimit = 1;                                                             // 14 öğe: ilk turda ufka varılmaz
        ServerModeOn();
        var pc = NewPc();

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.MorePending);

        pc.State.Get().FirstCatchUpAt.Should().BeNull("ufka varılmadı");
        pc.CatchUps.Should().BeEmpty();
        ConflictCount(SalesConflictLog.CatchUpOverwrite).Should().Be(1, "iz yazmadan önce düşer");
        Scalar<double>("SELECT TotalAmount FROM Customer WHERE Id = @drifted", new { drifted }).Should().Be(999d,
            "tam yeniden hesap yalnız ufukta");

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        pc.CatchUps.Should().ContainSingle().Which.CatchUpOverwrites.Should().Be(1);
        pc.State.Get().FirstCatchUpAt.Should().NotBeNull();
        Scalar<double>("SELECT TotalAmount FROM Customer WHERE Id = @drifted", new { drifted }).Should().Be(0d,
            "ilk tam yetişmeden sonra bütün toplamlar etiketlerden türetildi (§6.1)");
        Scalar<double>("SELECT Price FROM Label WHERE Id = @label", new { label }).Should().Be(50d, "sunucunun değeri alındı");

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        pc.CatchUps.Should().ContainSingle("açılış başına bir kez");
        _server.Faults.Should().BeEmpty();
    }

    // ── (f) canlı yayın yerel kalır, olağan kapanışla devreder ────────────

    [Fact]
    public async Task Acilista_acik_canli_yayin_yerel_kalir_sonra_olagan_kapanisla_devreder()
    {
        var customer = LocalCustomer();
        var live = LocalSession(endedAt: null);
        _server.SeedSession(G(live), T0, null, owner: ReplicaId);
        var liveLabel = LocalLabel(live, customer, synced: false);
        ServerModeOn();
        var pc = NewPc();
        _server.EnqueueStatus(FakeSalesServer.OrdersSync, 500);       // canlı yayının etiketi açılış anında gönderilemedi

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        pc.State.GetMode().Should().Be(1, "canlı yayının etiketleri açılışı bekletmez: iki kipte aynı yargılanır");
        var session = Session(live);
        session.Authority.Should().Be(StreamSessionAuthority.Local);
        session.EndedAt.Should().BeNull();
        new SessionRepository(_db).GetActive()!.Id.Should().Be(live, "canlı yayın kesilmez");
        new LabelRepository(_db).GetById(liveLabel)!.SyncedAt.Should().NotBeNull("turun ön gönderimi etiketi götürdü");

        new SessionRepository(_db).End(live, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await pc.Push.SyncOnceAsync();

        Session(live).Authority.Should().Be(StreamSessionAuthority.Server, "kapanış devri gördü; mod On'da sunucuya geçer");
        Session(live).ReleasedAt.Should().NotBeNull();
        _server.Session(G(live))!.ReleasedAt.Should().NotBeNull();
        _server.Faults.Should().BeEmpty();
    }

    // ── (i) sunucu mod 0, yerel 0 ─────────────────────────────────────────

    [Fact]
    public async Task Sunucu_mod_0_yerel_0_ise_degisiklik_ve_ek_istek_yok()
    {
        var customer = LocalCustomer();
        var session = LocalSession(endedAt: T0.AddHours(1), synced: false);
        LocalLabel(session, customer, synced: false);
        LocalShipment(customer, synced: false);
        LocalPayment();
        var pc = NewPc();

        for (var i = 0; i < 2; i++)
            (await pc.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);

        _server.Hellos.Should().ContainSingle();
        _server.States.Should().HaveCount(2);
        _server.PushCalls.Should().BeEmpty("mod Off'ta tur gönderim yapmaz (bugünkü zamanlayıcılar gönderir)");
        _server.CustomerPushCalls.Should().BeEmpty();
        _server.ChangesCalls.Should().BeEmpty();
        _server.RequestLog.Should().OnlyContain(r => r.EndsWith("/me/licenses", StringComparison.Ordinal)
            || r.EndsWith("/sales/hello", StringComparison.Ordinal) || r.EndsWith("/sales/state", StringComparison.Ordinal));
        var state = pc.State.Get();
        state.Mode.Should().Be(0);
        state.ModeSeenAt.Should().BeNull();
        state.FirstCatchUpAt.Should().BeNull();
        ConflictCount(SalesConflictLog.ModeOpened).Should().Be(0);
        new SyncCursorRepository(_db).Get(SalesChangesPullService.CursorName, _server.Lisans).Should().BeNull();
        pc.Pull.Status.Should().Be(SalesFeedStatus.Off);
        Session(session).Authority.Should().Be(StreamSessionAuthority.Local);
        _server.Faults.Should().BeEmpty();
    }
}
