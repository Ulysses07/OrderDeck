using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sales;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Düzenleme yönlendiricisinin (spec 2026-10-13 §5.3; W2-05) test yatağı: <see cref="FakeSalesServer"/> (mod On, alt
/// sürüm tanımlı), testin veritabanı, gerçek <see cref="SalesCommandClient"/>, ayarlanabilir bağlantı
/// (<see cref="Online"/>) ve <see cref="SalesEditRouter"/>. Başka bilgisayarın devredilmiş yayını sunucuya konur ve
/// akışla yerele indirilir (<see cref="RemoteLabelAsync"/>); bu bilgisayarın kendi yayınları yerele yazılır
/// (<see cref="LocalSession"/>). Yerel kip testin kararıdır (<see cref="ModeOn"/>); varsayılan Off (bugün her lisans).
/// </summary>
internal sealed class SalesEditTestBed : IDisposable
{
    public static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L);

    private sealed class License(string? key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    /// <summary>Yatağın saati: <see cref="FixedUnixNow"/> verilmişse o (iki yatağın yazımları birebir karşılaştırılsın),
    /// yoksa gerçek saat.</summary>
    private sealed class Clock(SalesEditTestBed bed) : IClock
    {
        public long UnixNow() => bed.FixedUnixNow ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Verilirse yerel servislerin saati sabit.</summary>
    public long? FixedUnixNow { get; set; }

    private sealed class PromptSpy : IAppUpdatePrompt
    {
        public int Calls;
        public void CheckAndPrompt() => Interlocked.Increment(ref Calls);
    }

    private readonly PromptSpy _prompt = new();

    /// <summary>Komut istemcisinin açtığı güncelleme soruları (<c>client-update-required</c>).</summary>
    public int UpdatePrompts => Volatile.Read(ref _prompt.Calls);

    private readonly bool _ownsDb;
    private readonly Guid _otherReplica = Guid.NewGuid();
    private readonly License _license;

    public FakeSalesServer Server { get; } = new();
    public InMemorySqlite Db { get; }
    public LabelService Labels { get; }
    public LabelRepository LabelRepo { get; }
    public SessionRepository Sessions { get; }
    public CustomerRepository Customers { get; }
    public HandoverGate Gate { get; } = new();
    public LicenseApiClient Api { get; }
    public SalesSyncEnvelope Envelope { get; }
    public SalesCommandClient Commands { get; }
    public SalesConnectivity Connectivity { get; }
    public SalesEditRouter Router { get; }

    /// <summary>Bekleyen satış komutları (W2-07): kuyruk ve gönderici (yönlendiriciyle ve akış turuyla aynı örnek).</summary>
    public PendingSalesCommandRepository Pending { get; }
    public PendingSalesCommandSender Sender { get; }

    /// <summary>Gönderici sonuçları (<see cref="PendingSalesCommandSender.Settled"/>).</summary>
    public List<PendingSalesCommandsSettled> Settled { get; } = new();

    /// <summary>false: son başarılı <c>sales/state</c> yok sayılır (çevrimdışı; §5.7).</summary>
    public bool Online { get; set; } = true;

    /// <summary>Son başarılı <c>sales/state</c>'in alındığı an (kurulumda; tur yok).</summary>
    public DateTimeOffset StateReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Son satış akışı eşitlemesi (yeniden baskının önbellek notu).</summary>
    public DateTimeOffset? LastFeedSyncAt { get; set; }

    /// <param name="db">Verilirse (göç koşmuş) o veritabanı kullanılır ve kapatılmaz; yoksa yatak kendi kurar.</param>
    /// <param name="labels">Verilirse yerel yol bu servisi çağırır (kabuk harness'inin servisi).</param>
    public SalesEditTestBed(InMemorySqlite? db = null, LabelService? labels = null)
    {
        _ownsDb = db is null;
        Db = db ?? new InMemorySqlite();
        if (_ownsDb) new MigrationRunner(Db).Run();
        LabelRepo = new LabelRepository(Db);
        Sessions = new SessionRepository(Db);
        Customers = new CustomerRepository(Db);
        Labels = labels ?? new LabelService(LabelRepo,
            new CustomerService(Customers, Sessions, LabelRepo, new Clock(this)), Db, new Clock(this));

        Server.Mode = 1;
        Server.MinClientVersion = "0.9.11";
        _license = new License(Server.Lisans);
        var http = new HttpClient(Server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        Api = new LicenseApiClient(http, new LicenseTokenStore());
        Envelope = TestSalesEnvelope.For(Db, _license);
        var stateRepo = new SalesSyncStateRepository(Db);
        Commands = new SalesCommandClient(Api, Envelope, stateRepo, LabelRepo, new ShipmentRepository(Db),
            new SalesFeedRepository(Db), new SalesConflictLogRepository(Db), _prompt, _license, new Clock(this),
            NullLogger<SalesCommandClient>.Instance);
        Connectivity = new SalesConnectivity(
            () => Online
                ? new SalesStateSnapshot(Server.Lisans, 1, null, null, [], 15, [], StateReceivedAt)
                : null,
            () => _license.CurrentLicenseKey);
        Pending = new PendingSalesCommandRepository(Db);
        Sender = new PendingSalesCommandSender(Pending, Commands, new SalesConflictLogRepository(Db), stateRepo,
            Connectivity, new Clock(this), NullLogger<PendingSalesCommandSender>.Instance);
        Sender.Settled += (_, e) => { lock (Settled) Settled.Add(e); };
        ShipmentRepo = new ShipmentRepository(Db);
        Shipments = new ShipmentService(ShipmentRepo, LabelRepo, () => Settings, () => new Clock(this).UnixNow());
        Router = new SalesEditRouter(Labels, LabelRepo, Sessions, stateRepo, Gate, Commands, Sender, Connectivity,
            Shipments, ShipmentRepo, Customers, () => LastFeedSyncAt, NullLogger<SalesEditRouter>.Instance);
    }

    /// <summary>Kargo (W2-08): yerel ayar (eşik ve ücret; lisans geneli ayar W3), dosya deposu ve yerel servis — mod Off
    /// yolu ve mod On'un eşik değerlendirmesi bunu kullanır.</summary>
    public AppSettings Settings { get; } = new();
    public ShipmentRepository ShipmentRepo { get; }
    public ShipmentService Shipments { get; }

    public void Dispose()
    {
        if (_ownsDb) Db.Dispose();
        Server.Dispose();
        if (_ownsDb) SqliteConnection.ClearAllPools();
    }

    // ── kip ───────────────────────────────────────────────────────────────

    /// <summary>Yerel kip On; <paramref name="caughtUp"/> ise ilk yetişme bitmiş (<c>FirstCatchUpAt</c> dolu).</summary>
    public void ModeOn(bool caughtUp = true) =>
        Exec("UPDATE SalesSyncState SET Mode = 1, FirstCatchUpAt = @at WHERE Id = 1",
            new { at = caughtUp ? T0.ToUnixTimeSeconds() : (long?)null });

    /// <summary>Yerel kip Off (bugün her lisans); <paramref name="caughtUp"/> yalnız tablo kombinasyonu için.</summary>
    public void ModeOff(bool caughtUp = false) =>
        Exec("UPDATE SalesSyncState SET Mode = 0, FirstCatchUpAt = @at WHERE Id = 1",
            new { at = caughtUp ? T0.ToUnixTimeSeconds() : (long?)null });

    /// <summary>Sunucuya giden bütün istekler (satış uçları ve lisans çözümü).</summary>
    public int HttpCount => Server.SalesRequestCount + Server.LicenseRequestCount;

    // ── başka bilgisayarın devredilmiş yayını ─────────────────────────────

    public Guid RemoteCustomer(string username = "alici_ornek")
    {
        var id = Guid.NewGuid();
        Server.SeedCustomer(id, "tiktok", username, "Takma Ad");
        return id;
    }

    public Guid RemoteSession(bool released = true, string machine = "PC-B")
    {
        var id = Guid.NewGuid();
        Server.SeedSession(id, T0, released ? T0.AddHours(1) : null, owner: _otherReplica, title: "Akşam yayını",
            ownerMachineName: machine);
        if (released) Server.ReleaseSession(id);
        return id;
    }

    public Guid RemoteOrder(Guid sessionId, Guid customer, decimal price = 150m, bool printed = true,
        bool tentative = false, bool cancelled = false, Guid? parent = null, string username = "alici_ornek")
    {
        var id = Guid.NewGuid();
        Server.SeedOrder(new FakeSalesServer.OrderBody(id, sessionId, customer.ToString("N"), "tiktok", username,
                "Takma Ad", "MAVI XL aldım", "A1", price, T0.AddMinutes(1), printed ? T0.AddMinutes(2) : null,
                cancelled ? T0.AddMinutes(3) : null, cancelled ? "musteri" : null, false, tentative, tentative, null, null,
                null, parent, null, null, null, null, null),
            version: 1, origin: _otherReplica);
        return id;
    }

    /// <summary>Devredilmiş bir yayının etiketi: sunucuda ve (akışla) yerelde, sürüm 1, yayını <c>server</c>.</summary>
    public async Task<(Guid Session, Guid Customer, Guid Order)> RemoteLabelAsync(decimal price = 150m,
        bool printed = true)
    {
        var session = RemoteSession();
        var customer = RemoteCustomer();
        var order = RemoteOrder(session, customer, price, printed);
        await PullAsync();
        return (session, customer, order);
    }

    /// <summary>Sunucunun akışını yerele uygular (W2-03'ün akış yolu, dosyalar dahil).</summary>
    public async Task PullAsync()
    {
        var feed = new SalesFeedRepository(Db);
        long after = 0;
        while (true)
        {
            var page = await Api.GetSalesChangesAsync(Server.LicenseId, after, 500, Envelope.ReplicaId!.Value, 1);
            if (page.Items!.Count == 0) return;
            var side = SalesFeedMapper.SideSessions(page);
            var drafts = SalesFeedMapper.Drafts(page);
            foreach (var item in page.Items)
            {
                var status = feed.ApplyItem(SalesFeedMapper.ToCore(item), side, drafts,
                    new SalesFeedApplyOptions(true, false), DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Status;
                if (status is not (SalesFeedApplyStatus.Applied or SalesFeedApplyStatus.Unchanged))
                    throw new InvalidOperationException($"Akış öğesi uygulanamadı: {status}");
            }
            after = page.NextAfterSeq;
        }
    }

    // ── bu bilgisayarın yayınları ─────────────────────────────────────────

    /// <summary>Bu bilgisayarın yayını (<c>Origin = 'local'</c>); <paramref name="authority"/> ile otorite.</summary>
    public string LocalSession(bool ended, long? releasedAt = null, string authority = StreamSessionAuthority.Local,
        string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        Sessions.Insert(new StreamSession(id, "Yerel yayın", 500, ended ? 900 : null, ["tiktok"], null));
        Exec("UPDATE StreamSession SET ReleasedAt = @releasedAt, Authority = @authority, SyncedAt = 900 WHERE Id = @id",
            new { id, releasedAt, authority });
        return id;
    }

    /// <summary>Yerel müşteri (yoksa) ve bu yayına onun basılmış etiketi; onaylı (<c>SyncedAt</c> dolu).</summary>
    public string LocalLabel(string sessionId, string customerId, decimal price = 100m, bool printed = true,
        string? parentLabelId = null, bool tentative = false, string? id = null)
    {
        if (Customers.GetById(customerId) is null)
            Customers.Insert(new Customer(customerId, "tiktok", $"yerel_{customerId[..6]}", null, null, 100, 200,
                false, null, null, 0, 0m, null, null, null));
        var authority = Scalar<string>("SELECT Authority FROM StreamSession WHERE Id = @sessionId", new { sessionId });
        id ??= Guid.NewGuid().ToString("N");
        // Sunucu otoritesindeki yayına yerel ekleme ABORT eder (049): yalnız kurulum için otorite bir an yerele alınır.
        if (authority == StreamSessionAuthority.Server)
            Exec("UPDATE StreamSession SET Authority = 'local' WHERE Id = @sessionId", new { sessionId });
        LabelRepo.Insert(new Label(id, sessionId, customerId, "tiktok", "yerel_alici", "aldım", "A1", price, 600,
            printed ? 650 : null, IsBackupPromoted: tentative, ParentLabelId: parentLabelId,
            IsTentativeBackup: tentative));
        Exec("UPDATE Label SET SyncedAt = 700 WHERE Id = @id", new { id });
        if (authority == StreamSessionAuthority.Server)
            Exec("UPDATE StreamSession SET Authority = 'server' WHERE Id = @sessionId", new { sessionId });
        return id;
    }

    /// <summary>
    /// Bu bilgisayarın DEVREDİLMİŞ yayını ve basılmamış kalıntı etiketleri (§5.5): yerelde <c>Origin = 'local'</c>,
    /// <c>Authority = 'server'</c>, etiketler onaylı ve sürüm 1; sunucuda aynı yayın (sahip bu kopya, devredilmiş) ve
    /// siparişler (sürüm 1, kaynak bu kopya).
    /// </summary>
    public (string Session, string Customer, IReadOnlyList<string> Labels) OwnResidual(int count = 1, decimal price = 100m)
    {
        var session = Guid.NewGuid();
        var customer = Guid.NewGuid().ToString("N");
        LocalSession(ended: true, releasedAt: 950, authority: StreamSessionAuthority.Server, id: N(session));
        Server.SeedSession(session, T0, T0.AddHours(1), owner: Envelope.ReplicaId, title: "Yerel yayın");
        Server.ReleaseSession(session);
        var labels = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            LocalLabel(N(session), customer, price, printed: false, id: N(id));
            Exec("UPDATE Label SET ServerVersion = 1 WHERE Id = @id", new { id = N(id) });
            var label = LabelRepo.GetById(N(id))!;
            Server.SeedOrder(new FakeSalesServer.OrderBody(id, session, customer, label.Platform, label.Username,
                    label.DisplayName, label.MessageText, label.Code, price, DateTimeOffset.FromUnixTimeSeconds(label.AddedAt),
                    null, null, null, false, false, false, null, null, null, null, null, null, null, null, null),
                version: 1, origin: Envelope.ReplicaId);
            labels.Add(N(id));
        }
        return (N(session), customer, labels);
    }

    /// <summary>Bu yatağın veritabanı, sunucusu, zarfı ve göndericisiyle satış akışı turu (W2-03'ün servisi).</summary>
    public SalesChangesPullService CreatePull()
    {
        var identity = new ReplicaIdentityRepository(Db);
        var stateRepo = new SalesSyncStateRepository(Db);
        var clock = new Clock(this);
        var busy = new CustomerBusySet();
        var cursors = new SyncCursorRepository(Db);
        var customerSync = new CustomerSyncRepository(Db, busy);
        var customerPush = new WpfCustomerProjectionSyncService(Api, customerSync, cursors, _license, clock,
            NullLogger<WpfCustomerProjectionSyncService>.Instance);
        return new SalesChangesPullService(Api,
            new SalesSyncStateService(Api, Envelope, identity, stateRepo, _license, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance),
            stateRepo, Envelope, new SalesFeedRepository(Db, busy), new SalesFeedFailureRepository(Db),
            new RemoteLiveSessionRepository(Db), cursors, LabelRepo, customerPush,
            new SessionOrderSyncService(Api, Sessions, LabelRepo, _license, Envelope, Gate, new SalesPushApply(Db), clock,
                NullLogger<SessionOrderSyncService>.Instance),
            new CustomerChangesPullService(Api, Customers, customerSync, cursors, customerPush, _license, clock,
                new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance),
            _license, clock, NullLogger<SalesChangesPullService>.Instance, Sender, Sessions);
    }

    // ── okuma ─────────────────────────────────────────────────────────────

    public sealed class LabelState
    {
        public decimal Price { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public long IsTentativeBackup { get; init; }
        public long? ServerVersion { get; init; }
        public long? SyncedAt { get; init; }
        public long Revision { get; init; }
    }

    public LabelState Local(string id)
    {
        using var c = Db.Open();
        return c.QuerySingle<LabelState>(
            "SELECT Price, CancelledAt, CancelReason, IsTentativeBackup, ServerVersion, SyncedAt, Revision FROM Label WHERE Id = @id",
            new { id });
    }

    public LabelState Local(Guid id) => Local(id.ToString("N"));

    /// <summary>Etiket, müşteri, kargo dosyası ve oturum tablolarının bütün kolonları (iki yolun yazımı birebir aynı
    /// mı).</summary>
    public string Snapshot()
    {
        using var c = Db.Open();
        var sb = new System.Text.StringBuilder();
        foreach (var table in new[] { "Label", "Customer", "Shipment", "StreamSession" })
            foreach (IDictionary<string, object?> row in c.Query($"SELECT * FROM {table} ORDER BY Id"))
                sb.AppendLine(table + ": " + string.Join("|", row.Select(kv => $"{kv.Key}={kv.Value}")));
        return sb.ToString();
    }

    public void Exec(string sql, object? param = null)
    {
        using var c = Db.Open();
        c.Execute(sql, param);
    }

    public T Scalar<T>(string sql, object? param = null)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<T>(sql, param)!;
    }

    public static string N(Guid id) => id.ToString("N");
}
