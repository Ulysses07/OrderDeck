using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
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
/// Satış akışı turu (spec 2026-10-13 §5.4 Servis, §4.4, §4.5, §3.2/M5, §5.7, §12 risk 9; W2-03): turda tam bir
/// <c>sales/state</c> (kalp atışı, her kipte); mod Off'ta başka hiçbir şey; mod On'da uzak canlı yayın aynası, ön
/// gönderim, sayfalar (ChangeSeq sırası, sayfa başına imleç, iptalde imleç, 10 sayfa sınırı, cursorReset), U5 durması
/// ve aynı turda gönderim, bekleyen müşteri ve ödeme akışı (sayılmaz), zehirli öğe (5 turda atlanır, dikkat sayacı),
/// ortam hatası (sayılmaz), lisans değişimi temizliği, <c>SalesChanged</c>, arka plan işi. Sunucu
/// <see cref="FakeSalesServer"/> (akış sayfası depodan bağımsız kurulur).
/// </summary>
public sealed class SalesChangesPullServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L);

    private sealed class License(string? key) : ICurrentLicenseProvider
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

    /// <summary>Kurulan hata akış deposunun SONRAKİ ilk <c>Open</c>'ında bir kez fırlatılır (öğenin uygulama işlemi).</summary>
    private sealed class FaultyFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        private Exception? _next;

        public void FailNextOpen(Exception ex) => _next = ex;

        public System.Data.IDbConnection Open()
        {
            var ex = Interlocked.Exchange(ref _next, null);
            if (ex is not null) throw ex;
            return inner.Open();
        }
    }

    /// <summary>Bir bilgisayarın yığını (bir süreç). Aynı veritabanıyla yeni örnek "yeniden başlatma"dır.</summary>
    private sealed class Pc
    {
        public License License { get; }
        public IDbConnectionFactory Db { get; }
        public FaultyFactory FeedDb { get; }
        public SalesSyncEnvelope Envelope { get; }
        public SalesChangesPullService Pull { get; }
        public CustomerBusySet Busy { get; } = new();
        public CapturingLogger<SalesChangesPullService> Log { get; } = new();
        public List<SalesChangedEventArgs> Changes { get; } = new();

        public Pc(FakeSalesServer server, IDbConnectionFactory db, string? licenseKey, string fingerprint)
        {
            Db = db;
            FeedDb = new FaultyFactory(db);
            License = new License(licenseKey);
            var identity = new ReplicaIdentityRepository(db);
            var stateRepo = new SalesSyncStateRepository(db);
            Envelope = new SalesSyncEnvelope(identity, stateRepo, new Hardware(fingerprint), License,
                NullLogger<SalesSyncEnvelope>.Instance);
            var api = new LicenseApiClient(
                new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore());
            var clock = new Clock();
            var cursors = new SyncCursorRepository(db);
            var customerSync = new CustomerSyncRepository(db, Busy);
            var state = new SalesSyncStateService(api, Envelope, identity, stateRepo, License, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance);
            var salesPush = new SessionOrderSyncService(api, new SessionRepository(db), new LabelRepository(db), License,
                Envelope, new HandoverGate(), new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance);
            var customerPush = new WpfCustomerProjectionSyncService(api, customerSync, cursors, License, clock,
                NullLogger<WpfCustomerProjectionSyncService>.Instance);
            var customerPull = new CustomerChangesPullService(api, new CustomerRepository(db), customerSync, cursors,
                customerPush, License, clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance);
            Pull = new SalesChangesPullService(api, state, stateRepo, Envelope, new SalesFeedRepository(FeedDb, Busy),
                new SalesFeedFailureRepository(db), new RemoteLiveSessionRepository(db), cursors, new LabelRepository(db),
                customerPush, salesPush, customerPull, License, clock, Log);
            Pull.SalesChanged += (_, e) => { lock (Changes) Changes.Add(e); };
        }

        public Guid ReplicaId => Envelope.ReplicaId!.Value;

        public Task<SalesPullOutcome> TourAsync(CancellationToken ct = default) => Pull.TourOnceAsync(ct);
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly string _fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly Guid _otherReplica = Guid.NewGuid();

    public SalesChangesPullServiceTests() => new MigrationRunner(_db).Run();

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private Pc NewPc(string? licenseKey = null) => new(_server, _db, licenseKey ?? _server.Lisans, _fingerprint);

    // ── kurulum: sunucu ───────────────────────────────────────────────────

    private Guid RemoteCustomer(string username, string? displayName = "Takma Ad")
    {
        var id = Guid.NewGuid();
        _server.SeedCustomer(id, "tiktok", username, displayName);
        return id;
    }

    /// <summary>Başka bilgisayarın yayını; <paramref name="released"/> ise devredilmiş (akışa girer).</summary>
    private Guid RemoteSession(bool released = true, Guid? licenseId = null)
    {
        var id = Guid.NewGuid();
        _server.SeedSession(id, T0, T0.AddHours(1), owner: _otherReplica, title: "Akşam yayını", ownerMachineName: "PC-B",
            licenseId: licenseId);
        if (released) _server.ReleaseSession(id);
        return id;
    }

    private static FakeSalesServer.OrderBody OrderRow(Guid id, Guid sessionId, string customerId, decimal price = 150m,
        string username = "alici_ornek", long? cancelledAt = null)
        => new(id, sessionId, customerId, "tiktok", username, "Takma Ad", "MAVI XL aldım", "A1", price,
            T0.AddMinutes(1), T0.AddMinutes(2),
            cancelledAt is { } c ? DateTimeOffset.FromUnixTimeSeconds(c) : null, cancelledAt is null ? null : "musteri",
            false, false, false, null, null, null, null, null, null, null, null, null);

    private Guid RemoteOrder(Guid sessionId, Guid customer, decimal price = 150m, string username = "alici_ornek",
        Guid? licenseId = null)
    {
        var id = Guid.NewGuid();
        _server.SeedOrder(OrderRow(id, sessionId, customer.ToString("N"), price, username), version: 1,
            origin: _otherReplica, licenseId: licenseId);
        return id;
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

    private void ModeOn() => Exec("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

    private string LocalCustomer(string username, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        new CustomerRepository(_db).Insert(new Customer(id, "tiktok", username, "Takma Ad", null, 1, 1, false, null, null,
            0, 0m, null, null, null));
        return id;
    }

    private string LocalSession(long? endedAt, bool synced = true)
    {
        var id = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(id, "Kendi yayını", T0.ToUnixTimeSeconds(), endedAt,
            ["tiktok"], null, SyncedAt: synced ? T0.ToUnixTimeSeconds() + 10 : null));
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

    private long? Cursor(string? licenseKey = null) =>
        new SyncCursorRepository(_db).Get(SalesChangesPullService.CursorName, licenseKey ?? _server.Lisans)?.Seq;

    private bool LabelExists(Guid id) =>
        Scalar<long>("SELECT EXISTS(SELECT 1 FROM Label WHERE Id = @id)", new { id = id.ToString("N") }) == 1;

    private decimal LabelPrice(Guid id) =>
        (decimal)Scalar<double>("SELECT Price FROM Label WHERE Id = @id", new { id = id.ToString("N") });

    private static long AfterSeq(FakeSalesServer.StateCall call) =>
        long.Parse(Regex.Match(call.Query, @"[?&]afterSeq=(-?\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    private void NoFaults() => _server.Faults.Should().BeEmpty();

    // ── mod Off: yalnız kalp atışı ────────────────────────────────────────

    [Fact]
    public async Task Mod_Off_turu_yalniz_kalp_atisi_akis_istenmez_gonderim_yok_canli_yayin_aynasi_yazilmaz()
    {
        var customer = RemoteCustomer("alici_ornek");
        RemoteOrder(RemoteSession(), customer);
        _server.LiveSessions.Add(new FakeSalesServer.LiveSessionBody(Guid.NewGuid(), "Canlı", T0, null, "PC-C", T0,
            "live", null));
        var own = LocalSession(endedAt: T0.ToUnixTimeSeconds() + 3600);
        LocalLabel(own, LocalCustomer("alici_yerel"), synced: false);       // mod Off'ta tur bunu göndermez
        var pc = NewPc();

        for (var i = 0; i < 3; i++)
            (await pc.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);

        _server.States.Should().HaveCount(3, "turda tam bir sales/state — kalp atışı mod Off'ta da sürer");
        _server.States.Should().OnlyContain(s => s.SeenMode == "0");
        _server.ChangesCalls.Should().BeEmpty("mod Off'ta akış istenmez");
        _server.CustomerPushCalls.Should().BeEmpty("mod Off'ta ön gönderim yok (bugünkü zamanlayıcılar gönderir)");
        _server.Calls(FakeSalesServer.OrdersSync).Should().BeEmpty();
        _server.Calls(FakeSalesServer.SessionsSync).Should().BeEmpty();
        Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote'").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM Label").Should().Be(1);
        Cursor().Should().BeNull();
        pc.Pull.Status.ModeOn.Should().BeFalse("durum satırı yalnız mod On'da");
        pc.Changes.Should().BeEmpty();
        NoFaults();
    }

    [Fact]
    public async Task Lisans_yoksa_hicbir_istek_gitmez()
    {
        var pc = NewPc();
        pc.License.CurrentLicenseKey = null;
        ModeOn();

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.NoLicense);

        _server.SalesRequestCount.Should().Be(0);
        _server.LicenseRequestCount.Should().Be(0);
        _server.CustomerPushCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mod_On_ama_state_basarisiz_akis_istenmez()
    {
        RemoteOrder(RemoteSession(), RemoteCustomer("alici_ornek"));
        var pc = NewPc();
        ModeOn();
        _server.EnqueueStatus(FakeSalesServer.State, 500);

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.Failed);

        _server.States.Should().ContainSingle();
        _server.ChangesCalls.Should().BeEmpty("kip ve başlıklar tazelenmeden akış uygulanmaz");
        Scalar<int>("SELECT COUNT(*) FROM Label").Should().Be(0);
    }

    // ── mod On: sayfalar ve imleç ─────────────────────────────────────────

    [Fact]
    public async Task Mod_On_sayfalari_ChangeSeq_sirasiyla_uygular_imleci_sayfa_basina_kaydeder_iptalden_sonra_tekrar_uygulamadan_surer()
    {
        _server.PageLimit = 2;
        var customer = RemoteCustomer("alici_ornek");
        var session = RemoteSession();
        var orders = Enumerable.Range(0, 5).Select(i => RemoteOrder(session, customer, price: 100m + i)).ToList();
        var pc = NewPc();
        ModeOn();

        // Üçüncü sayfa isteğinde iptal (kapanış): iki sayfa uygulandı, imleç ikinci sayfanın sonunda.
        using var cts = new CancellationTokenSource();
        var requests = 0;
        _server.OnChanges = _ => { if (++requests == 3) cts.Cancel(); };
        await FluentActions.Awaiting(() => pc.TourAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        var firstTour = _server.ChangesCalls;
        firstTour.Should().HaveCount(3);
        var savedAfterCancel = Cursor();
        savedAfterCancel.Should().Be(AfterSeq(firstTour[2]), "iptalde imleç son uygulanan sayfada kaydedildi");
        orders.Take(3).Should().OnlyContain(id => LabelExists(id), "ilk iki sayfa: oturum + üç sipariş");
        orders.Skip(3).Should().NotContain(id => LabelExists(id));

        // Arada başka bilgisayar ilk siparişin fiyatını değiştirdi: daha büyük seq'le yeniden gelir.
        _server.OnChanges = null;
        _server.MutateOrder(orders[0], o => o with { Price = 175m });

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        var secondTour = _server.ChangesCalls.Skip(3).ToList();
        AfterSeq(secondTour[0]).Should().Be(savedAfterCancel!.Value, "uygulanmış sayfalar yeniden istenmez");
        secondTour.Select(AfterSeq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems(
            "her sayfanın imleci bir öncekinin son öğesi");
        orders.Should().OnlyContain(id => LabelExists(id));
        LabelPrice(orders[0]).Should().Be(175m, "aynı satırın sonraki değişikliği sonra uygulandı");
        Cursor().Should().Be(_server.LastSeq);
        pc.Pull.Status.Should().Match<SalesFeedStatus>(s => s.ModeOn && !s.CatchingUp && s.WaitingItems == 0
                                                             && s.LastCaughtUpAt != null);
        _server.States.Should().HaveCount(2, "turda tam bir sales/state");
        NoFaults();
    }

    [Fact]
    public async Task Tur_basina_on_sayfa_siniri_turu_MorePending_bitirir_sonraki_tur_kaldigi_yerden()
    {
        _server.PageLimit = 1;
        var customer = RemoteCustomer("alici_ornek");
        var session = RemoteSession();
        var orders = Enumerable.Range(0, 11).Select(_ => RemoteOrder(session, customer)).ToList();   // 12 öğe
        var pc = NewPc();
        ModeOn();

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.MorePending);

        _server.ChangesCalls.Should().HaveCount(SalesChangesPullService.MaxPagesPerTour);
        orders.Count(LabelExists).Should().Be(9, "on sayfa: oturum + dokuz sipariş");
        pc.Pull.Status.CatchingUp.Should().BeTrue("durum satırı: yetişiyor");

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        _server.ChangesCalls.Should().HaveCount(SalesChangesPullService.MaxPagesPerTour + 3, "iki dolu ve bir boş sayfa");
        orders.Should().OnlyContain(id => LabelExists(id));
        pc.Pull.Status.CatchingUp.Should().BeFalse();
        NoFaults();
    }

    [Fact]
    public async Task CursorReset_imleci_sifira_ceker_akis_bastan_uygulanir_toplamlarda_fark_yok()
    {
        var customer = RemoteCustomer("alici_ornek");
        var session = RemoteSession();
        var a = RemoteOrder(session, customer, price: 120m);
        var b = RemoteOrder(session, customer, price: 80m);
        var pc = NewPc();
        ModeOn();
        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        var customerId = customer.ToString("N");
        var before = new CustomerRepository(_db).GetById(customerId)!;
        before.TotalAmount.Should().Be(200m);
        var labelsBefore = Scalar<string>("SELECT group_concat(Id || ':' || Price || ':' || COALESCE(ServerVersion, '-'), ',') FROM (SELECT * FROM Label ORDER BY Id)");
        var events = pc.Changes.Count;

        // Sunucu veritabanı yedekten döndü: istemcinin imleci ufkun üstünde.
        var stale = _server.LastSeq + 50;
        new SyncCursorRepository(_db).Upsert(SalesChangesPullService.CursorName, _server.Lisans, seq: stale);

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        AfterSeq(_server.ChangesCalls.Skip(2).First()).Should().Be(stale);
        Cursor().Should().Be(_server.LastSeq, "sayfa baştan geldi, imleç sıfırdan ilerledi");
        var after = new CustomerRepository(_db).GetById(customerId)!;
        (after.TotalAmount, after.TotalLabelsPrinted).Should().Be((before.TotalAmount, before.TotalLabelsPrinted),
            "aynı öğeleri yeniden uygulamak toplamda sıfır fark üretir");
        Scalar<string>("SELECT group_concat(Id || ':' || Price || ':' || COALESCE(ServerVersion, '-'), ',') FROM (SELECT * FROM Label ORDER BY Id)")
            .Should().Be(labelsBefore);
        pc.Changes.Should().HaveCount(events, "değişen satır yok: bildirim yok");
        pc.Log.Warnings.Should().Contain(e => e.Message.Contains("imleci sunucuda geçersiz"));
        LabelExists(a).Should().BeTrue();
        LabelExists(b).Should().BeTrue();
        NoFaults();
    }

    // ── zehirli öğe ve ortam hataları (U10) ───────────────────────────────

    [Fact]
    public async Task Uygulanamayan_oge_bes_turda_atlanir_SkippedAt_ve_dikkat_sayaci_arkasi_uygulanir()
    {
        var poison = Guid.NewGuid();
        _server.SeedShipment(new FakeSalesServer.ShipmentBody(poison, Guid.NewGuid().ToString("N"), "bilinmeyen-durum",
            100m, T0, null, null));
        var customer = RemoteCustomer("alici_ornek");
        var order = RemoteOrder(RemoteSession(), customer);
        var pc = NewPc();
        ModeOn();
        var failures = new SalesFeedFailureRepository(_db);

        for (var tour = 1; tour < SalesChangesPullService.MaxAttemptsBeforeSkip; tour++)
        {
            (await pc.TourAsync()).Should().Be(SalesPullOutcome.Failed);
            Scalar<int>("SELECT Attempts FROM SalesFeedFailure WHERE ItemId = @id", new { id = poison.ToString("N") })
                .Should().Be(tour);
            Cursor().Should().Be(0, "imleç uygulanamayan öğenin önünde");
            LabelExists(order).Should().BeFalse("arkasındaki öğe bekler");
        }

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        failures.CountSkipped().Should().Be(1);
        Scalar<long?>("SELECT SkippedAt FROM SalesFeedFailure WHERE ItemId = @id", new { id = poison.ToString("N") })
            .Should().NotBeNull();
        new SyncOutboxRepository(_db).CountAttention().SkippedSalesFeedItems.Should().Be(1, "dikkat sayacı +1 (§5.7)");
        LabelExists(order).Should().BeTrue();
        Cursor().Should().Be(_server.LastSeq);
        pc.Log.Entries.Should().NotContain(e => e.Full.Contains("MAVI XL") || e.Full.Contains("alici_ornek")
                                                || e.Full.Contains("Takma Ad"), "günlük yalnız kimlik ve sayı");
        NoFaults();
    }

    [Theory]
    [InlineData(5)]    // SQLITE_BUSY
    [InlineData(6)]    // SQLITE_LOCKED
    [InlineData(13)]   // SQLITE_FULL
    public async Task Kilit_cekismesi_ve_ortam_hatasi_deneme_sayilmaz_sonraki_tur_ayni_ogeden(int sqliteErrorCode)
    {
        var order = RemoteOrder(RemoteSession(), RemoteCustomer("alici_ornek"));
        var pc = NewPc();
        ModeOn();
        var contended = true;
        _server.OnChanges = _ => { if (contended) pc.FeedDb.FailNextOpen(new SqliteException("database is locked", sqliteErrorCode)); };

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.Failed);

        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0, "ortam hatası deneme sayılmaz");
        Cursor().Should().Be(0);
        LabelExists(order).Should().BeFalse();

        contended = false;
        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        LabelExists(order).Should().BeTrue();
        NoFaults();
    }

    // ── bekleyen öğe (U5, bekleyen müşteri, ödeme akışı) ──────────────────

    /// <summary>Bu bilgisayarın bitmiş, sunucuda devredilmiş yayını ve onun yerelde iptal edilmiş, henüz
    /// gönderilmemiş etiketi (sunucuda iptalsiz).</summary>
    private (string Session, Guid Label) OwnReleasedSessionWithUnsentCancel(Pc pc)
    {
        var customer = LocalCustomer("alici_yerel");
        var session = LocalSession(endedAt: T0.ToUnixTimeSeconds() + 3600);
        var label = Guid.NewGuid();
        new LabelRepository(_db).Insert(new Label(label.ToString("N"), session, customer, "tiktok", "alici_yerel",
            "kendi yorumu", "B2", 40m, T0.ToUnixTimeSeconds() + 60, T0.ToUnixTimeSeconds() + 120,
            SyncedAt: T0.ToUnixTimeSeconds() + 200));
        new LabelRepository(_db).MarkCancelled([label.ToString("N")], T0.ToUnixTimeSeconds() + 4000, "musteri");

        var sessionId = Guid.ParseExact(session, "N");
        _server.SeedSession(sessionId, T0, T0.AddHours(1), owner: pc.ReplicaId, releasedAt: T0.AddHours(1).AddMinutes(1));
        _server.SeedOrder(OrderRow(label, sessionId, customer, price: 40m, username: "alici_yerel"), version: 1,
            origin: pc.ReplicaId);
        return (session, label);
    }

    private long? LocalCancelledAt(Guid label) =>
        Scalar<long?>("SELECT CancelledAt FROM Label WHERE Id = @id", new { id = label.ToString("N") });

    [Fact]
    public async Task Gonderilmemis_yerel_etiket_turun_on_gonderimiyle_gider_akis_durmaz()
    {
        var pc = NewPc();
        ModeOn();
        var (session, label) = OwnReleasedSessionWithUnsentCancel(pc);

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        _server.Calls(FakeSalesServer.OrdersSync).Should().ContainSingle("ön gönderim akıştan önce");
        _server.Order(label)!.Row.CancelledAt.Should().NotBeNull("iptal sunucuya geçti");
        LocalCancelledAt(label).Should().NotBeNull("yerel iptal geri alınmadı (U5)");
        new SessionRepository(_db).GetById(session)!.Authority.Should().Be(StreamSessionAuthority.Server,
            "mod On'da devri akıştan görülen kendi oturumu");
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0);
        NoFaults();
    }

    [Fact]
    public async Task U5_durmasi_ayni_turdaki_gonderimle_kalkar_gonderilemezse_tur_durur_deneme_sayilmaz()
    {
        var pc = NewPc();
        ModeOn();
        var (_, label) = OwnReleasedSessionWithUnsentCancel(pc);

        // (a) Ön gönderim düşer; akış etikette durur; durma sonrası gönderim başarır → akış aynı turda sürer.
        _server.EnqueueStatus(FakeSalesServer.OrdersSync, 500);
        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        _server.Calls(FakeSalesServer.OrdersSync).Should().HaveCount(2, "ön gönderim + durma sonrası bir gönderim");
        LocalCancelledAt(label).Should().NotBeNull();
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0, "durma deneme sayılmaz");

        // (b) Yeni bir yerel değişiklik, iki gönderim de düşer → tur durur, imleç o öğede, deneme sayılmaz.
        Exec("UPDATE StreamSession SET Authority = 'local'");               // kurulum: yerel yazımı açar (ABORT olmasın)
        new LabelRepository(_db).Uncancel([label.ToString("N")]);
        _server.MutateOrder(label, o => o with { PrintSeq = 7 });          // akışa aynı satırın yeni bir sürümü
        _server.EnqueueStatus(FakeSalesServer.OrdersSync, 500);
        _server.EnqueueStatus(FakeSalesServer.OrdersSync, 500);
        var cursorBefore = Cursor();

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.Stalled);

        Cursor().Should().Be(cursorBefore, "imleç duran öğede");
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0);
        pc.Pull.Status.WaitingItems.Should().Be(1, "durum satırı: N öğe bekliyor");
        LocalCancelledAt(label).Should().BeNull("gönderilmemiş yerel değişiklik ezilmedi");

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        _server.Order(label)!.Row.CancelledAt.Should().BeNull();
        pc.Pull.Status.WaitingItems.Should().Be(0);
        NoFaults();
    }

    [Fact]
    public async Task Bekleyen_musteri_ogede_durur_deneme_sayilmaz()
    {
        LocalCustomer("ayni_kisi");                                         // aynı kimlik, başka Id
        var serverCustomer = RemoteCustomer("ayni_kisi");
        var session = RemoteSession();
        var order = RemoteOrder(session, serverCustomer, username: "ayni_kisi");
        var pc = NewPc();
        ModeOn();

        for (var i = 0; i < SalesChangesPullService.MaxAttemptsBeforeSkip + 1; i++)
            (await pc.TourAsync()).Should().Be(SalesPullOutcome.PendingCustomer);

        LabelExists(order).Should().BeFalse("yönlendirmeyi müşteri akışı getirecek");
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0, "bekleyen müşteri deneme sayılmaz, atlanmaz");
        new SessionRepository(_db).GetById(session.ToString("N")).Should().NotBeNull("öndeki oturum öğesi uygulandı");
        pc.Pull.Status.WaitingItems.Should().Be(1);
        NoFaults();
    }

    [Fact]
    public async Task Odeme_akisindaki_musteri_ogede_durur_kira_bitince_surer()
    {
        var customer = LocalCustomer("alici_ornek", Guid.NewGuid().ToString("N"));
        _server.SeedCustomer(Guid.ParseExact(customer, "N"), "tiktok", "alici_ornek", "Takma Ad");
        var order = RemoteOrder(RemoteSession(), Guid.ParseExact(customer, "N"));
        var pc = NewPc();
        ModeOn();
        var lease = await pc.Busy.EnterAsync(customer);

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.Busy);
        LabelExists(order).Should().BeFalse();
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0);

        lease.Dispose();
        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        LabelExists(order).Should().BeTrue();
        NoFaults();
    }

    // ── taslak ve KVKK (sahte sunucunun akışı) ────────────────────────────

    [Fact]
    public async Task Projeksiyonsuz_ham_kimlikli_musteri_taslaktan_acilir_silinmis_musterinin_metni_inmez()
    {
        var session = RemoteSession();
        var raw = $"ham-{Guid.NewGuid():N}";
        var rawOrder = Guid.NewGuid();
        _server.SeedOrder(OrderRow(rawOrder, session, raw, username: "ham_alici"), version: 1, origin: _otherReplica);
        var purged = RemoteCustomer("silinen_alici");
        var purgedOrder = RemoteOrder(session, purged, username: "silinen_alici");
        _server.PurgeCustomer(purged, T0.AddDays(1));
        var pc = NewPc();
        ModeOn();

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        Scalar<string>("SELECT CustomerId FROM Label WHERE Id = @id", new { id = rawOrder.ToString("N") }).Should().Be(raw);
        Scalar<string>("SELECT Username FROM Customer WHERE Id = @raw", new { raw }).Should().Be("ham_alici",
            "taslak siparişin kendi alanlarından, ham Id'yle");
        using var c = _db.Open();
        var masked = c.QuerySingle<(string? DisplayName, string MessageText)>(
            "SELECT DisplayName, MessageText FROM Label WHERE Id = @id", new { id = purgedOrder.ToString("N") });
        masked.Should().Be(((string?)null, ""), "silinmiş müşterinin metni okuma anında maskelenir (§9)");
        NoFaults();
    }

    // ── canlı yayın aynası (M5) ───────────────────────────────────────────

    [Fact]
    public async Task Canli_ve_devrediliyor_yayinlar_RemoteLiveSessiona_aynalanir_kendi_oturumu_alinmaz_StreamSessiona_yazilmaz()
    {
        var own = LocalSession(endedAt: null);                             // bu bilgisayarın canlı yayını
        var live = Guid.NewGuid();
        var handingOver = Guid.NewGuid();
        _server.LiveSessions.Add(new(live, "Canlı", T0, null, "PC-B", T0.AddMinutes(30), "live", null));
        _server.LiveSessions.Add(new(handingOver, "Bitti", T0, T0.AddHours(1), "PC-C", T0.AddMinutes(61), "handing-over", 4));
        _server.LiveSessions.Add(new(Guid.ParseExact(own, "N"), "Kendi", T0, null, "BU-PC", T0.AddMinutes(30), "live", null));
        var pc = NewPc();
        ModeOn();

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        var mirror = new RemoteLiveSessionRepository(_db).GetAll();
        mirror.Select(r => r.Id).Should().BeEquivalentTo(live.ToString("N"), handingOver.ToString("N"));
        mirror.Single(r => r.Id == handingOver.ToString("N")).Should().Be(new RemoteLiveSessionRow(
            handingOver.ToString("N"), "Bitti", T0.ToUnixTimeSeconds(), T0.AddHours(1).ToUnixTimeSeconds(), "PC-C",
            T0.AddMinutes(61).ToUnixTimeSeconds(), RemoteLiveSessionState.HandingOver, 4));
        Scalar<int>("SELECT COUNT(*) FROM StreamSession").Should().Be(1, "başka bilgisayarın yayını StreamSession'a girmez");
        new SessionRepository(_db).GetActive()!.Id.Should().Be(own);

        // Canlı yayın bitti (listeden çıktı), devrediliyorun son sinyali ilerledi.
        _server.LiveSessions.RemoveAll(s => s.Id == live);
        var i = _server.LiveSessions.FindIndex(s => s.Id == handingOver);
        _server.LiveSessions[i] = _server.LiveSessions[i] with { OwnerLastSeenAt = T0.AddMinutes(62) };

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        new RemoteLiveSessionRepository(_db).GetAll().Should().ContainSingle()
            .Which.OwnerLastSeenAt.Should().Be(T0.AddMinutes(62).ToUnixTimeSeconds());
        NoFaults();
    }

    // ── lisans değişimi ───────────────────────────────────────────────────

    [Fact]
    public async Task Lisans_degisince_uzak_satirlar_silinir_yerel_satirlar_kalir_toplamlar_turetilir_yeni_lisans_bastan()
    {
        var customerId = LocalCustomer("alici_ornek", Guid.NewGuid().ToString("N"));
        var customer = Guid.ParseExact(customerId, "N");
        _server.SeedCustomer(customer, "tiktok", "alici_ornek", "Takma Ad");
        var ownSession = LocalSession(endedAt: T0.ToUnixTimeSeconds() + 3600);
        var ownLabel = LocalLabel(ownSession, customerId, price: 40m);
        var remoteA = RemoteOrder(RemoteSession(), customer, price: 150m);
        _server.LiveSessions.Add(new(Guid.NewGuid(), "Canlı", T0, null, "PC-B", T0, "live", null));
        var pc = NewPc();
        ModeOn();
        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        new CustomerRepository(_db).GetById(customerId)!.TotalAmount.Should().Be(190m, "ön koşul");
        new SalesFeedFailureRepository(_db).Record("order", "eski-lisans-ogesi", 1, "x", 1);

        // İkinci lisansın bir devredilmiş yayını var; canlı yayın listesi boş.
        _server.LiveSessions.Clear();
        var remoteB = RemoteOrder(RemoteSession(licenseId: _server.SecondLicenseId), customer, price: 70m,
            licenseId: _server.SecondLicenseId);
        pc.License.CurrentLicenseKey = _server.SecondLisans;

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);

        LabelExists(remoteA).Should().BeFalse("önceki lisansın uzak etiketi silindi");
        LabelExists(remoteB).Should().BeTrue("yeni lisansın akışı temizlikten sonra uygulandı");
        Scalar<int>("SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote'").Should().Be(1);
        new SessionRepository(_db).GetById(ownSession).Should().NotBeNull("Origin='local' satırlara dokunulmaz");
        new LabelRepository(_db).GetById(ownLabel).Should().NotBeNull();
        Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0, "akış hata kayıtları silindi");
        new CustomerRepository(_db).GetById(customerId)!.TotalAmount.Should().Be(110m, "kendi 40 + yeni lisansın 70");
        Cursor(_server.Lisans).Should().BeNull("önceki lisansın imleci silindi: dönülürse akış baştan");
        Cursor(_server.SecondLisans).Should().Be(_server.LastSeq);

        // Yeniden başlatma, önceki lisansa dönüş: süreçler arası tanınır (başka lisansın imleci var).
        var restarted = NewPc(_server.Lisans);
        (await restarted.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        LabelExists(remoteB).Should().BeFalse();
        LabelExists(remoteA).Should().BeTrue("önceki lisansın akışı baştan alındı");
        Cursor(_server.SecondLisans).Should().BeNull();
        new CustomerRepository(_db).GetById(customerId)!.TotalAmount.Should().Be(190m);
        NoFaults();
    }

    // ── SalesChanged ve dispatcher ────────────────────────────────────────

    [Fact]
    public async Task SalesChanged_degisen_kimlikleri_tasir_tur_dispatchersiz_tamamlanir()
    {
        var customer = RemoteCustomer("alici_ornek");
        var session = RemoteSession();
        var order = RemoteOrder(session, customer);
        var shipment = Guid.NewGuid();
        _server.SeedShipment(new FakeSalesServer.ShipmentBody(shipment, customer.ToString("N"), "pending", 150m, T0, null,
            null), origin: _otherReplica);
        var pc = NewPc();
        ModeOn();

        // Dispatcher'sız iş parçacığından: tur kendisi hiçbir dispatcher'a ihtiyaç duymaz.
        (await Task.Run(() => pc.TourAsync())).Should().Be(SalesPullOutcome.CaughtUp);

        var args = pc.Changes.Should().ContainSingle().Subject;
        args.LabelIds.Should().Equal(order.ToString("N"));
        args.SessionIds.Should().Equal(session.ToString("N"));
        args.ShipmentIds.Should().Equal(shipment.ToString("N"));
        Scalar<long?>("SELECT ServerVersion FROM Shipment WHERE Id = @id", new { id = shipment.ToString("N") })
            .Should().NotBeNull("mod On'da dosya aynası");

        (await pc.TourAsync()).Should().Be(SalesPullOutcome.CaughtUp);
        pc.Changes.Should().ContainSingle("yeni değişiklik yok: bildirim yok");
        NoFaults();
    }

    // ── arka plan işi ─────────────────────────────────────────────────────

    [Fact]
    public async Task Arka_plan_isi_acilista_hemen_tur_atar_sonra_ritimle_surer_tek_state_surucusu()
    {
        var pc = NewPc();
        using (var once = new SalesChangesPullHostedService(pc.Pull,
                   NullLogger<SalesChangesPullHostedService>.Instance, TimeSpan.FromHours(1)))
        {
            await once.StartAsync(CancellationToken.None);
            await WaitUntil(() => _server.States.Count >= 1);
            await once.StopAsync(CancellationToken.None);
        }
        _server.Hellos.Should().ContainSingle("açılışta 30 sn beklenmez: hello + kalp atışı hemen");
        _server.States.Should().ContainSingle();

        ModeOn();
        using var cadence = new SalesChangesPullHostedService(pc.Pull,
            NullLogger<SalesChangesPullHostedService>.Instance, TimeSpan.FromMilliseconds(50));
        await cadence.StartAsync(CancellationToken.None);
        await WaitUntil(() => _server.States.Count >= 4);
        await cadence.StopAsync(CancellationToken.None);

        _server.Hellos.Should().ContainSingle("aynı servis örneği: süreç başına bir hello");
        _server.ChangesCalls.Should().NotBeEmpty("mod On'da tur akışı da çeker");
        _server.ChangesCalls.Count.Should().BeLessThan(_server.States.Count,
            "her tur tam bir state ile başlar; ayrı bir state zamanlayıcısı yok");
        SalesChangesPullHostedService.DefaultCadence.Should().Be(TimeSpan.FromSeconds(30));
        NoFaults();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestPatience.Upper;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"koşul {TestPatience.Upper.TotalSeconds} sn'de sağlanmadı");
            await Task.Delay(20);
        }
    }
}
