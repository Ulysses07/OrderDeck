using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sales;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Chat;
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
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// İki bilgisayarlı satış senkronu testlerinin (spec 2026-10-13 §10.3, W2-12) bir bilgisayarı: kendi SQLite'ı, kendi
/// ayar dosyası, kendi donanım parmak izi ve kendi ağ bağlantısı (<see cref="Offline"/>) ile, üretimdeki gibi kurulmuş
/// GERÇEK servisler — <see cref="SessionOrderSyncService"/>, <see cref="SalesChangesPullService"/>,
/// <see cref="SalesSyncStateService"/> + <see cref="SalesModeCoordinator"/> + <see cref="SalesModeOffRound"/>,
/// <see cref="SalesCommandClient"/>, <see cref="PendingSalesCommandSender"/>, <see cref="SalesEditRouter"/>
/// (<see cref="ILabelEditor"/>), <see cref="ShipmentSyncService"/>, <see cref="SalesBackfillService"/> ve
/// <see cref="RemoteSessionsViewModel"/>. Kurulum <c>AppHost</c>'un kayıtlarıyla aynı örnek paylaşımını izler (tek zarf,
/// tek devir kapısı, tek gönderici, tek CustomerBusySet). Sunucu <see cref="FakeSalesServer"/>.
///
/// <para>Bir nesne bir süreçtir: <see cref="Restart"/> aynı veritabanında yeni süreç açar (AppHost'un açılış adımlarıyla:
/// geri yükleme işareti, toplam ve dosya türetmesi, yarım kalmış Off turunun 3. adımı); <see cref="RestoreFrom"/>
/// yedeği veritabanına geri yükler, kopya kimliğini döndürür (RestoreService gibi) ve yeni süreç açar.</para>
///
/// <para>Kâğıt sayacı (<see cref="Papers"/>): <see cref="PrintAsync"/> MainShell'in baskı sırasını izler — satırlar
/// kimlikle yeniden okunur ve örtü uygulanır (<see cref="LabelRepository.GetPrintableByIds"/>, §5.6-1), kâğıda kalanlar
/// basılır, sonra yönlendiriciyle işaretlenir (§5.6-4).</para>
/// </summary>
internal sealed class SalesPc
{
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

    /// <summary>Güncelleme sorusu (Velopack) — açılmaz, sayılır (<see cref="UpdatePrompts"/>).</summary>
    private sealed class PromptSpy : IAppUpdatePrompt
    {
        public int Calls;
        public void CheckAndPrompt() => Interlocked.Increment(ref Calls);
    }

    private readonly PromptSpy _prompt = new();

    /// <summary>Bu süreçte açılan güncelleme soruları (komut istemcisi ve ödeme kirası paylaşır, AppHost gibi).</summary>
    public int UpdatePrompts => Volatile.Read(ref _prompt.Calls);

    /// <summary>Bu bilgisayarın ağ bağlantısı: kapalıyken her istek (lisans çözümü dahil) ağ hatası verir; öbür
    /// bilgisayar etkilenmez.</summary>
    internal sealed class Link(HttpMessageHandler server) : DelegatingHandler(server)
    {
        private int _offline;

        public bool Offline
        {
            get => Volatile.Read(ref _offline) == 1;
            set => Volatile.Write(ref _offline, value ? 1 : 0);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Offline ? throw new HttpRequestException("fake offline (bu bilgisayar)") : base.SendAsync(request, ct);
    }

    private readonly Link _link;

    public string Name { get; }
    public FakeSalesServer Server { get; }
    public InMemorySqlite Db { get; }
    public string SettingsPath { get; }
    public string Fingerprint { get; }
    public AppSettings Settings { get; }

    public SessionRepository Sessions { get; }
    public LabelRepository LabelRepo { get; }
    public CustomerRepository Customers { get; }
    public ShipmentRepository ShipmentRepo { get; }
    public SalesSyncStateRepository State { get; }
    public ReplicaIdentityRepository Identity { get; }
    public PendingSalesCommandRepository Pending { get; }

    public LabelService Labels { get; }
    public StreamSessionService Streams { get; }

    /// <summary>Gerçek kargo ayarı sağlayıcısı: mod Off'ta <see cref="Settings"/>, mod On'da sunucunun lisans geneli
    /// ayarı (state turunun önbelleği; spec 2026-10-13 §6.2).</summary>
    public ShippingSettingsProvider Shipping { get; }
    public ShipmentService Shipments { get; }
    public SalesSyncEnvelope Envelope { get; }
    public SalesSyncStateService StateService { get; }
    public SessionOrderSyncService Push { get; }
    public ShipmentSyncService ShipmentSync { get; }
    public SalesCommandClient Commands { get; }
    public PendingSalesCommandSender Sender { get; }
    public SalesConnectivity Connectivity { get; }
    public SalesEditRouter Editor { get; }
    public SalesModeOffRound OffRound { get; }
    public SalesChangesPullService Pull { get; }
    public SalesBackfillService Backfill { get; }

    /// <summary>Bekleyen ödeme kayıtlarının göndericisi (W3-05): açılışın 1. adımı ve geri doldurma turu paylaşır.</summary>
    public PaymentRecordSender PaymentRecords { get; }
    public RemoteSessionsViewModel RemoteSessions { get; }

    // ── ödeme isteği (W3-07): AppHost gibi tek ödeme servisi, kira sarmalı ve mod Off kaydı ──

    public LicenseApiClient Api { get; }
    public ICurrentLicenseProvider LicenseProvider { get; }
    public CustomerBusySet Busy { get; }
    public WpfCustomerProjectionSyncService CustomerPush { get; }
    public SettingsStore Store { get; }
    public PaymentJobRepository Jobs { get; }
    public PendingPaymentRecordRepository PendingPaymentRecords { get; }

    /// <summary>wa.me penceresi (açılan bağlantılar; <see cref="FakeUrlLauncher.ThrowOnLaunch"/> açılamamayı taklit
    /// eder).</summary>
    public FakeUrlLauncher Launcher { get; } = new();
    public PaymentOffRecordWriter OffRecords { get; }
    public PaymentLeaseCoordinator Lease { get; }
    public PaymentRequestService Payments { get; }

    /// <summary>Bu süreçte basılan kâğıt sayısı, etiket başına (<see cref="PrintAsync"/>).</summary>
    public Dictionary<string, int> Papers { get; } = new(StringComparer.Ordinal);

    /// <summary>Bu süreçte "kâğıdı imha et" denen etiketler (bekleyen komut sonucu, baskı işaretlemesi, push).</summary>
    public List<string> Discarded { get; } = new();

    /// <summary>Bu süreçteki push sonuçları (<see cref="SessionOrderSyncService.PushOutcome"/>).</summary>
    public List<SalesPushOutcome> PushOutcomes { get; } = new();

    /// <summary>Mod On'un ilk yetişmesinin sonuçları (<see cref="SalesChangesPullService.FirstCatchUpCompleted"/>).</summary>
    public List<SalesFirstCatchUp> FirstCatchUps { get; } = new();

    public SalesPc(FakeSalesServer server, string name, InMemorySqlite db, string settingsPath, string fingerprint,
        bool offline = false)
    {
        Name = name;
        Server = server;
        Db = db;
        SettingsPath = settingsPath;
        Fingerprint = fingerprint;
        _link = new Link(server) { Offline = offline };

        var license = new License(server.Lisans);
        LicenseProvider = license;
        var clock = new Clock();
        var store = new SettingsStore(settingsPath);
        Store = store;
        Settings = store.Load();
        var http = new HttpClient(_link, disposeHandler: false) { BaseAddress = new Uri("https://test.local") };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        Api = api;

        Sessions = new SessionRepository(db);
        LabelRepo = new LabelRepository(db);
        Customers = new CustomerRepository(db);
        ShipmentRepo = new ShipmentRepository(db);
        State = new SalesSyncStateRepository(db);
        Identity = new ReplicaIdentityRepository(db);
        Pending = new PendingSalesCommandRepository(db);
        var busy = new CustomerBusySet();
        Busy = busy;
        var cursors = new SyncCursorRepository(db);
        var customerSync = new CustomerSyncRepository(db, busy);
        var feed = new SalesFeedRepository(db, busy);
        var conflicts = new SalesConflictLogRepository(db);
        var gate = new HandoverGate();
        var liveSessions = new RemoteLiveSessionRepository(db);

        Labels = new LabelService(LabelRepo, new CustomerService(Customers, Sessions, LabelRepo, clock), db, clock);
        Streams = new StreamSessionService(Sessions, clock);
        Shipping = new ShippingSettingsProvider(State, Settings);
        Shipments = new ShipmentService(ShipmentRepo, LabelRepo, Shipping, clock.UnixNow);
        Envelope = new SalesSyncEnvelope(Identity, State, new Hardware(fingerprint), license,
            NullLogger<SalesSyncEnvelope>.Instance);
        StateService = new SalesSyncStateService(api, Envelope, Identity, State, license, () => Settings,
            NullLogger<SalesSyncStateService>.Instance);
        Push = new SessionOrderSyncService(api, Sessions, LabelRepo, license, Envelope, gate, new SalesPushApply(db), clock,
            NullLogger<SessionOrderSyncService>.Instance);
        Push.PushOutcome += (_, e) =>
        {
            lock (PushOutcomes) PushOutcomes.Add(e);
        };
        ShipmentSync = new ShipmentSyncService(api, ShipmentRepo, store, Settings, license, Envelope, clock,
            NullLogger<ShipmentSyncService>.Instance, salesState: State);
        var payments = new PaymentSyncService(api, new PaymentRepository(db), cursors, license, clock,
            NullLogger<PaymentSyncService>.Instance);
        var customerPush = new WpfCustomerProjectionSyncService(api, customerSync, cursors, license, clock,
            NullLogger<WpfCustomerProjectionSyncService>.Instance);
        CustomerPush = customerPush;
        var customerPull = new CustomerChangesPullService(api, Customers, customerSync, cursors, customerPush, license,
            clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance);
        Commands = new SalesCommandClient(api, Envelope, State, LabelRepo, ShipmentRepo, feed, conflicts, _prompt,
            license, clock, NullLogger<SalesCommandClient>.Instance);
        Connectivity = new SalesConnectivity(() => StateService.LastState, () => license.CurrentLicenseKey);
        Sender = new PendingSalesCommandSender(Pending, Commands, conflicts, State, Connectivity, clock,
            NullLogger<PendingSalesCommandSender>.Instance);
        Sender.Settled += (_, e) =>
        {
            lock (Discarded) Discarded.AddRange(e.DiscardLabelIds);
        };
        OffRound = new SalesModeOffRound(feed, State, Pending, Labels, LabelRepo, conflicts, store, Settings, clock,
            NullLogger<SalesModeOffRound>.Instance);
        // AppHost gibi tek ödeme kaydı göndericisi: açılışın 1. adımı ve geri doldurma turu aynı kapıdan.
        PendingPaymentRecords = new PendingPaymentRecordRepository(db);
        PaymentRecords = new PaymentRecordSender(api, PendingPaymentRecords, Envelope, license, clock,
            NullLogger<PaymentRecordSender>.Instance);
        var coordinator = new SalesModeCoordinator(StateService, State, Envelope,
            new SalesOpeningFlush(PaymentRecords, customerPush, Push, ShipmentSync, payments, State), clock,
            NullLogger<SalesModeCoordinator>.Instance, OffRound);
        Pull = new SalesChangesPullService(api, StateService, State, Envelope, feed, new SalesFeedFailureRepository(db),
            liveSessions, cursors, LabelRepo, customerPush, Push, customerPull, license, clock,
            NullLogger<SalesChangesPullService>.Instance, Sender, Sessions, coordinator, Commands);
        Pull.FirstCatchUpCompleted += (_, e) =>
        {
            lock (FirstCatchUps) FirstCatchUps.Add(e);
        };
        Editor = new SalesEditRouter(Labels, LabelRepo, Sessions, State, gate, Commands, Sender, Connectivity, Shipments,
            ShipmentRepo, Customers, () => Pull.Status.LastCaughtUpAt, NullLogger<SalesEditRouter>.Instance);
        Backfill = new SalesBackfillService(api, Envelope, StateService, Push, ShipmentSync, PaymentRecords,
            new SalesBackfillRepository(db), cursors, gate, license, clock, NullLogger<SalesBackfillService>.Instance);
        RemoteSessions = new RemoteSessionsViewModel(liveSessions, State, Sessions, Commands, Connectivity,
            () => StateService.LastState, () => license.CurrentLicenseKey, ct => Pull.TourOnceAsync(ct), clock,
            NullLogger<RemoteSessionsViewModel>.Instance);

        // AppHost gibi ödeme isteği: tek servis, kira sarmalı (ön gönderim aynı örneklerden), mod Off kaydı aynı
        // göndericinin kuyruğuna, aynı CustomerBusySet.
        Jobs = new PaymentJobRepository(db);
        OffRecords = new PaymentOffRecordWriter(PaymentRecords, State, LabelRepo, license, clock,
            NullLogger<PaymentOffRecordWriter>.Instance);
        Lease = new PaymentLeaseCoordinator(State, Pull, PaymentRecords, customerPush, Push, Pending, LabelRepo,
            NullLogger<PaymentLeaseCoordinator>.Instance);
        Payments = new PaymentRequestService(store, new WhatsAppMessageBuilder(), Launcher, api, license, Jobs, Shipping,
            NullLogger<PaymentRequestService>.Instance, Customers, busy, OffRecords, Lease);

        // AppHost.Initialize'ın açılış adımları (göçten sonra, arka plan işlerinden önce).
        Envelope.CaptureRestoreAtBoot(markerApplied: false);
        CustomerAggregatesBoot.Run(db, clock.UnixNow(), NullLogger.Instance);
        ShipmentAmountsBoot.Run(db, clock.UnixNow(), NullLogger.Instance);
        OffRound.Resume();
    }

    /// <summary>Yeni bilgisayar: göç koşmuş yeni veritabanı, yerel kargo ayarı 500/50 (lisans genelinden farklı).</summary>
    public static SalesPc New(FakeSalesServer server, string name, List<IDisposable> owned, List<string> files)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        owned.Add(db);
        var path = Path.Combine(Path.GetTempPath(), $"od-twopc-{Guid.NewGuid():N}.json");
        files.Add(path);
        new SettingsStore(path).Update(s => s.Shipping = new ShippingSettings { FreeShippingThreshold = 500m, ShippingFee = 50m });
        return new SalesPc(server, name, db, path, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
    }

    /// <summary>Aynı veritabanında yeni süreç (yeniden açılış); ağ durumu korunur.</summary>
    public SalesPc Restart() => new(Server, Name, Db, SettingsPath, Fingerprint, Offline);

    /// <summary>Veritabanının yedeği (yedek dosyası gibi; testin sahipliğinde).</summary>
    public InMemorySqlite Backup(List<IDisposable> owned)
    {
        var copy = new InMemorySqlite();
        owned.Add(copy);
        using var source = (SqliteConnection)Db.Open();
        using var target = (SqliteConnection)copy.Open();
        source.BackupDatabase(target);
        return copy;
    }

    /// <summary>
    /// Yedekten geri yükleme (§8.7, <c>RestoreService</c>): yedek veritabanının üzerine yazılır, kopya kimliği döner
    /// (yedeğin kimliği önceki kimlik olur), uygulama yeniden başlar — aynı makine (aynı parmak izi).
    /// </summary>
    public SalesPc RestoreFrom(InMemorySqlite backup)
    {
        using (var source = (SqliteConnection)backup.Open())
        using (var target = (SqliteConnection)Db.Open())
            source.BackupDatabase(target);
        Identity.Rotate();
        return Restart();
    }

    public bool Offline
    {
        get => _link.Offline;
        set => _link.Offline = value;
    }

    public Guid Replica => Identity.GetReplicaId()!.Value;

    // ── turlar ─────────────────────────────────────────────────────────────

    /// <summary>Satış akışı servisinin bir turu (state → kip adımı → ön gönderim → bekleyen komutlar → akış). Her turdan
    /// sonra sahte sunucunun işleyemediği istek olmamalı.</summary>
    public async Task<SalesPullOutcome> TourAsync()
    {
        var outcome = await Pull.TourOnceAsync(CancellationToken.None);
        Server.Faults.Should().BeEmpty($"{Name}: sahte sunucu her isteği işleyebilmeli");
        return outcome;
    }

    /// <summary>Tur sayfa sınırına takıldıkça (<see cref="SalesPullOutcome.MorePending"/>) yeniden; son sonucu döner.</summary>
    public async Task<SalesPullOutcome> SettleAsync()
    {
        SalesPullOutcome outcome;
        var tours = 0;
        do
        {
            outcome = await TourAsync();
        } while (outcome == SalesPullOutcome.MorePending && ++tours < 50);
        return outcome;
    }

    /// <summary>Gönderim servisinin bir turu (60 sn'lik sürücü ya da kapanış); sunucu hatasız olmalı.</summary>
    public async Task PushAsync()
    {
        await Push.SyncOnceAsync();
        Server.Faults.Should().BeEmpty($"{Name}: sahte sunucu her isteği işleyebilmeli");
    }

    /// <summary>
    /// Bir bilgisayarın bütün arka plan sürücülerinin birer turu, uygulamadaki gibi: satış akışı turu (30 sn; sayfa
    /// sınırı bitene kadar), oturum ve etiket gönderimi (60 sn), kargo gönderimi (30 sn; mod On'da istek yok) ve geri
    /// doldurma (60 sn; tamamlandıysa istek yok). Mod Off'ta satış akışı turu yalnız kalp atışıdır — gönderim 60 sn'lik
    /// sürücünündür.
    /// </summary>
    public async Task<SalesPullOutcome> BackgroundRoundAsync()
    {
        var outcome = await SettleAsync();
        await Push.SyncOnceAsync();
        await ShipmentSync.SyncOnceAsync();
        await Backfill.TourOnceAsync(CancellationToken.None);
        Server.Faults.Should().BeEmpty($"{Name}: sahte sunucu her isteği işleyebilmeli");
        return outcome;
    }

    // ── canlı yol ─────────────────────────────────────────────────────────

    public string StartStream() => Streams.Start("Akşam yayını", ["tiktok"]).Id;

    public void EndStream(string sessionId) => Streams.End(sessionId);

    /// <summary>Sohbetten etiket (canlı yol, <see cref="LabelService.Add"/>).</summary>
    public Label Sell(string sessionId, string username, string code = "A1", decimal price = 100m) =>
        Labels.Add(sessionId,
            new ChatMessage(Guid.NewGuid().ToString("N"), "tiktok", null, username, "Takma Ad", null, $"{code} aldım",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Array.Empty<string>()),
            price, code);

    /// <summary>
    /// MainShell'in baskı sırası (§5.6): satırlar kimlikle yeniden okunur, örtü uygulanır, iptal edilmiş ya da basılmış
    /// satır düşer; kalanlar kâğıda basılır (<see cref="Papers"/>) ve kâğıt sırasıyla yönlendiriciden işaretlenir.
    /// </summary>
    public async Task<SalesPrintMarkResult> PrintAsync(params string[] labelIds)
    {
        var printable = LabelRepo.GetPrintableByIds(labelIds).Select(l => l.Id).ToList();
        foreach (var id in printable) Papers[id] = Papers.GetValueOrDefault(id) + 1;
        if (printable.Count == 0) return SalesPrintMarkResult.Empty;
        var result = await Editor.MarkPrintedAsync(printable);
        lock (Discarded) Discarded.AddRange(result.DiscardLabelIds);
        return result;
    }

    /// <summary>"Fiyatı düzelt"in onayı, PriceCorrectionViewModel'in kurduğu istekle: yayın×kod planı, düzeltilebilir
    /// etiketler seçili; yerel yazım bugünkü <see cref="LabelService.CorrectPrices"/>.</summary>
    public Task<SalesCodePriceResult> CorrectCodePriceAsync(string sessionId, string code, decimal newPrice)
    {
        var plans = new[] { Labels.PlanCodeCorrection(sessionId, code) };
        var correctable = plans.SelectMany(p => p.Plan.Correctable).ToList();
        var ids = correctable.Concat(plans.SelectMany(p => p.Plan.Shipped.Concat(p.Plan.Elsewhere)))
            .Select(l => l.Id).Distinct(StringComparer.Ordinal).ToList();
        var selection = correctable.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        return Editor.CorrectCodePricesAsync(
            new SalesCodePriceRequest(sessionId, plans, selection, ids, newPrice, null),
            local => Labels.CorrectPrices(local, newPrice));
    }

    /// <summary>Dekont kaydedildikten sonraki kargo adımı (DekontEkleViewModel'in çağrısı).</summary>
    public Task<SalesShipmentStepResult> DekontAsync(string customerId, ShipmentDirective directive = ShipmentDirective.Normal)
        => Editor.RunDekontShipmentStepAsync(customerId, directive);

    /// <summary>Üst çubuktaki çipten "Yayını bitir…" ve onay (§5.7, RemoteSessionsViewModel); başarılıysa başlattığı akış
    /// turu beklenir. Çipin sessizlik dakikası döner (eylem sunulmadıysa null — hiçbir şey gönderilmez).</summary>
    public async Task<RemoteSessionChipViewModel?> ForceEndFromChipAsync(string sessionId)
    {
        RemoteSessions.Refresh();
        var chip = RemoteSessions.Chips.Single(c => c.SessionId == sessionId);
        if (!chip.CanForceEnd) return chip;
        RemoteSessions.BeginForceEndCommand.Execute(chip);
        RemoteSessions.ForceEndPrompt.Should().Be(
            SalesCommandMessages.ForceEndConfirm(chip.MachineName, chip.SilentMinutes!.Value), "§5.7 onay metni");
        await RemoteSessions.ConfirmForceEndCommand.ExecuteAsync(null);
        RemoteSessions.ForceEndProblem.Should().BeNull();
        if (RemoteSessions.LastFeedTour is { } tour) await tour;
        Server.Faults.Should().BeEmpty($"{Name}: sahte sunucu her isteği işleyebilmeli");
        return chip;
    }

    /// <summary>Önceki yayınlardan basılmamış etiketler sorusunun teklifi (yerel oturumlar, örtü dışında).</summary>
    public IReadOnlyList<string> ResidualOffer() =>
        LabelRepo.GetUnprintedOutsideSession(Guid.NewGuid().ToString("N")).Select(l => l.Id).ToList();

    // ── okuma ─────────────────────────────────────────────────────────────

    /// <summary>Etiketin sınanan kolonları (birimler, sürüm, kuyruk).</summary>
    internal sealed record LabelRow
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string? Code { get; init; }
        public decimal Price { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public long? PrintedAt { get; init; }
        public string? PrintBatchId { get; init; }
        public long? PrintSeq { get; init; }
        public long IsTentativeBackup { get; init; }
        public string? ShipmentId { get; init; }
        public decimal? PaidPrice { get; init; }
        public long? PriceCorrectedAt { get; init; }
        public long? ServerVersion { get; init; }
        public long? SyncedAt { get; init; }
    }

    public LabelRow? Row(string labelId)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<LabelRow>(
            @"SELECT Id, SessionId, CustomerId, Code, Price, CancelledAt, CancelReason, PrintedAt, PrintBatchId, PrintSeq,
                     IsTentativeBackup, ShipmentId, PaidPrice, PriceCorrectedAt, ServerVersion, SyncedAt
              FROM Label WHERE Id = @labelId", new { labelId });
    }

    /// <summary>Bütün etiketler (Id sırasıyla).</summary>
    public List<LabelRow> Rows()
    {
        using var c = Db.Open();
        return c.Query<LabelRow>(
            @"SELECT Id, SessionId, CustomerId, Code, Price, CancelledAt, CancelReason, PrintedAt, PrintBatchId, PrintSeq,
                     IsTentativeBackup, ShipmentId, PaidPrice, PriceCorrectedAt, ServerVersion, SyncedAt
              FROM Label ORDER BY Id").ToList();
    }

    internal sealed record SessionRow
    {
        public string Id { get; init; } = "";
        public long? EndedAt { get; init; }
        public long? ReleasedAt { get; init; }
        public string Origin { get; init; } = "";
        public string Authority { get; init; } = "";
        public long? SyncedAt { get; init; }
    }

    public SessionRow? SessionOf(string sessionId)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<SessionRow>(
            "SELECT Id, EndedAt, ReleasedAt, Origin, Authority, SyncedAt FROM StreamSession WHERE Id = @sessionId",
            new { sessionId });
    }

    internal sealed record ShipmentRow
    {
        public string Id { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Status { get; init; } = "";
        public decimal CumulativeAmount { get; init; }
        public long? ServerVersion { get; init; }
        public string? OriginReplicaId { get; init; }
    }

    public List<ShipmentRow> ShipmentRows()
    {
        using var c = Db.Open();
        return c.Query<ShipmentRow>(
            "SELECT Id, CustomerId, Status, CumulativeAmount, ServerVersion, OriginReplicaId FROM Shipment ORDER BY Id")
            .ToList();
    }

    /// <summary>Etiket, oturum, dosya ve müşteri toplamı tablolarının bütün kolonları — fazladan bir turun hiçbir şey
    /// değiştirmediğini (aynı öğe iki kez → sıfır fark) sınamak için.</summary>
    public string Snapshot()
    {
        using var c = Db.Open();
        var sb = new System.Text.StringBuilder();
        foreach (var table in new[] { "Label", "StreamSession", "Shipment", "Customer", "PendingSalesCommand" })
            foreach (IDictionary<string, object?> row in c.Query($"SELECT * FROM {table} ORDER BY 1"))
                sb.AppendLine(table + ": " + string.Join("|", row.Select(kv => $"{kv.Key}={kv.Value}")));
        return sb.ToString();
    }

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

    public int PendingCount => Scalar<int>("SELECT COUNT(*) FROM PendingSalesCommand");

    public int RemoteRows => Scalar<int>(
        "SELECT (SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote') + (SELECT COUNT(*) FROM Label l "
      + "JOIN StreamSession s ON s.Id = l.SessionId WHERE s.Origin = 'remote')");

    public int ConflictCount(string kind) =>
        Scalar<int>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @kind", new { kind });
}
