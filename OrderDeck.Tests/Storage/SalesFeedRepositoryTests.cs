using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// <see cref="SalesFeedRepository.ApplyItem"/> (spec 2026-10-13 §5.4, §3.2, §4.4, §6.1, §9; DM4, DM8, U5, U17; W2-01):
/// uzak oturum <c>remote/server</c> olarak iner ve açık kalmaz; kendi oturumu devri kipe göre alır, aktif kendi yayını
/// force-end'de yerel kalır; eksik müşteri taslaktan açılır ya da kimlik başka Id'deyse beklenir; gönderilmemiş yerel
/// satır durur; var olan satıra yalnız birimler ve sürüm yazılır; yeni satır onaylı ve stokta sayılmış doğar; ilk
/// yetişmede kendi satırının üzerine yazım izlenir; KVKK okuma maskesi; dosya aynası yalnız istendiğinde.
/// Gerçek kurguda (dosya, WAL, havuz), 049'a kadar göç edilmiş veritabanında.
/// </summary>
public sealed class SalesFeedRepositoryTests : IDisposable
{
    private const long Now = 1_791_000_000L;
    private const long ReleasedAt = 1_790_990_000L;
    private const long StartedAt = 1_790_980_000L;
    private const long LocalEndedAt = 1_790_985_000L;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"od-salesfeed-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _db;
    private readonly SalesFeedRepository _feed;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly CustomerRepository _customers;

    public SalesFeedRepositoryTests()
    {
        _db = new SqliteConnectionFactory(_path);
        new MigrationRunner(_db).Run();
        _feed = new SalesFeedRepository(_db);
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        _customers = new CustomerRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    // ── kurulum ───────────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static readonly SalesFeedApplyOptions Plain = new(ApplyShipments: false, CountCatchUpOverwrite: false);
    private static readonly SalesFeedApplyOptions CatchUp = new(ApplyShipments: false, CountCatchUpOverwrite: true);
    private static readonly SalesFeedApplyOptions WithShipments = new(ApplyShipments: true, CountCatchUpOverwrite: false);

    private SalesFeedApplyResult Apply(
        SalesFeedItem item,
        IEnumerable<SalesFeedSession>? sessions = null,
        IEnumerable<SalesFeedCustomerDraft>? drafts = null,
        SalesFeedApplyOptions? options = null,
        SalesFeedRepository? feed = null)
        => (feed ?? _feed).ApplyItem(item, (sessions ?? []).ToList(), (drafts ?? []).ToList(), options ?? Plain, Now);

    private static SalesFeedItem SessionItem(SalesFeedSession s) => new(SalesFeedItemKind.Session, 101, s, null, null);
    private static SalesFeedItem OrderItem(SalesFeedOrder o) => new(SalesFeedItemKind.Order, 102, null, o, null);
    private static SalesFeedItem ShipmentItem(SalesFeedShipment s) => new(SalesFeedItemKind.Shipment, 103, null, null, s);

    /// <summary>Başka bilgisayarın devredilmiş yayını, sunucunun gördüğü hâliyle.</summary>
    private static SalesFeedSession ServerSession(string? id = null, long? endedAt = LocalEndedAt,
        long? releasedAt = ReleasedAt, string? machine = "PC-B")
        => new(id ?? NewId(), "Akşam yayını", StartedAt, endedAt, ["tiktok", "instagram"], null, machine, releasedAt);

    private static SalesFeedOrder Order(string sessionId, string customerId, string? id = null,
        string username = "alici_ornek", decimal price = 150m, int version = 3, string? productId = null)
        => new(
            Id: id ?? NewId(), SessionId: sessionId, CustomerId: customerId, Platform: "tiktok", Username: username,
            DisplayName: "Takma Ad", MessageText: "MAVI XL aldım", Code: "A1", Price: price,
            AddedAt: StartedAt + 60, PrintedAt: StartedAt + 120, CancelledAt: null, CancelReason: null,
            IsShippingFee: false, IsBackupPromoted: false, IsTentativeBackup: false,
            ProductId: productId, ProductVariantId: null, Version: version, ParentLabelId: null, ShipmentId: null,
            PrintBatchId: null, PrintSeq: null, PriceCorrectedAt: null, PaidPrice: null);

    private static SalesFeedCustomerDraft Draft(string id, string username = "alici_ornek",
        string? displayName = "Takma Ad", long? purgedAt = null)
        => new(id, "tiktok", username, displayName, purgedAt);

    private string LocalSession(long? endedAt, bool synced = false)
    {
        var id = NewId();
        _sessions.Insert(new StreamSession(id, "Kendi yayını", StartedAt, endedAt, ["tiktok"], null,
            SyncedAt: synced ? StartedAt + 10 : null));
        return id;
    }

    private string InsertCustomer(string username, int labels = 0, decimal amount = 0m, string? id = null)
    {
        id ??= NewId();
        _customers.Insert(new Customer(id, "tiktok", username, "Takma Ad", null, FirstSeenAt: 100, LastSeenAt: 200,
            IsBlacklisted: false, BlacklistReason: null, Notes: null, TotalLabelsPrinted: labels, TotalAmount: amount,
            BlacklistedAt: null, Address: null, Phone: null));
        return id;
    }

    /// <summary>Bu bilgisayarın etiketi; <paramref name="synced"/> ise sunucuya gitmiş (SyncedAt = StockSyncedAt).</summary>
    private string InsertLocalLabel(string sessionId, string customerId, bool synced, decimal price = 150m,
        string? productId = null)
    {
        var id = NewId();
        _labels.Insert(new Label(id, sessionId, customerId, "tiktok", "alici_yerel", "MAVI XL aldım", "A1", price,
            AddedAt: StartedAt + 60, PrintedAt: StartedAt + 120, DisplayName: "Takma Ad",
            SyncedAt: synced ? StartedAt + 200 : null, ProductId: productId));
        return id;
    }

    private void SetMode(int mode) => Exec("UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });

    private void SetFirstCatchUp(long? at) => Exec("UPDATE SalesSyncState SET FirstCatchUpAt = @at WHERE Id = 1", new { at });

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

    private IDictionary<string, object?> LabelRow(string id)
    {
        using var c = _db.Open();
        var row = (IDictionary<string, object?>)c.QuerySingle("SELECT * FROM Label WHERE Id = @id", new { id });
        return new Dictionary<string, object?>(row);
    }

    private int ConflictRows(string kind) =>
        Scalar<int>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @kind", new { kind });

    private static LabelService LabelServiceFor(IDbConnectionFactory db)
    {
        var clock = Mock.Of<IClock>(c => c.UnixNow() == Now);
        var labels = new LabelRepository(db);
        var customers = new CustomerService(new CustomerRepository(db), new SessionRepository(db), labels, clock);
        return new LabelService(labels, customers, db, clock);
    }

    private static ChatMessage Msg(string username) =>
        new(NewId(), "tiktok", null, username, "Örnek Müşteri", null, "MAVI XL aldım", Now, Array.Empty<string>());

    // ── (a) uzak oturum ───────────────────────────────────────────────────

    [Fact]
    public void Uzak_oturum_remote_ve_server_olarak_iner_acik_kalmaz_GetActive_etkilenmez_canli_basligi_silinir()
    {
        var own = LocalSession(endedAt: null);                                   // bu bilgisayarın canlı yayını
        var remote = ServerSession(endedAt: null);                               // onarım öncesi boş EndedAt
        Exec(@"INSERT INTO RemoteLiveSession (Id, Title, StartedAt, OwnerMachineName, State)
               VALUES (@id, 'Akşam yayını', @at, 'PC-B', 'handing-over')", new { id = remote.Id, at = StartedAt });

        var result = Apply(SessionItem(remote));

        result.Status.Should().Be(SalesFeedApplyStatus.Applied);
        result.SessionIds.Should().Equal(remote.Id);
        var row = _sessions.GetById(remote.Id)!;
        row.Origin.Should().Be(StreamSessionOrigin.Remote);
        row.Authority.Should().Be(StreamSessionAuthority.Server);
        row.OwnerMachineName.Should().Be("PC-B");
        row.ReleasedAt.Should().Be(ReleasedAt);
        row.EndedAt.Should().Be(ReleasedAt, "uzak oturum yerelde hiçbir zaman açık inmez (§3.2)");
        row.Platforms.Should().Equal("tiktok", "instagram");
        row.SyncedAt.Should().Be(Now, "onaylı doğar: gönderim kuyruğuna girmez");
        _sessions.GetUnsynced().Select(s => s.Id).Should().NotContain(remote.Id);
        _sessions.GetActive()!.Id.Should().Be(own, "başka bilgisayarın yayını GetActive'i yanıltmaz (§5.9)");
        Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession WHERE Id = @id", new { id = remote.Id }).Should().Be(0);
    }

    [Fact]
    public void Uzak_oturumun_guncel_hali_yeniden_inince_yazilir_ikinci_kez_degisiklik_yok()
    {
        var remote = ServerSession();
        Apply(SessionItem(remote));

        Apply(SessionItem(remote with { Title = "Yeni başlık" })).Status.Should().Be(SalesFeedApplyStatus.Applied);
        _sessions.GetById(remote.Id)!.Title.Should().Be("Yeni başlık");

        var again = Apply(SessionItem(remote with { Title = "Yeni başlık" }));
        again.Status.Should().Be(SalesFeedApplyStatus.Unchanged);
        again.SessionIds.Should().BeEmpty();
    }

    // ── (b) kendi bitmiş oturumu ──────────────────────────────────────────

    [Theory]
    [InlineData(0, StreamSessionAuthority.Local)]
    [InlineData(1, StreamSessionAuthority.Server)]
    public void Kendi_bitmis_oturumu_devir_anini_alir_Authority_yalniz_mod_Onda_server(int mode, string authority)
    {
        var id = LocalSession(endedAt: LocalEndedAt, synced: true);
        SetMode(mode);
        var before = _sessions.GetById(id)!;

        var result = Apply(SessionItem(ServerSession(id, endedAt: LocalEndedAt + 5) with { Title = "Sunucudaki başlık" }));

        result.Status.Should().Be(SalesFeedApplyStatus.Applied);
        result.SessionEndedRemotely.Should().BeFalse();
        var after = _sessions.GetById(id)!;
        after.ReleasedAt.Should().Be(ReleasedAt);
        after.Authority.Should().Be(authority);
        after.Origin.Should().Be(StreamSessionOrigin.Local);
        after.EndedAt.Should().Be(LocalEndedAt, "kendi oturumunun kapanışı yereldir");
        after.Title.Should().Be(before.Title, "kendi oturumuna sunucunun başlığı yazılmaz");
        after.SyncedAt.Should().Be(before.SyncedAt);
        after.Revision.Should().Be(before.Revision, "akış uygulaması kuyruğa almaz (DM8)");
    }

    // ── (c) force-end: aktif kendi yayını yerel kalır ─────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Force_end_aktif_kendi_yayinini_kesmez_yalniz_devir_ani_saklanir_ve_satis_surer(int mode)
    {
        var id = LocalSession(endedAt: null);
        SetMode(mode);

        var result = Apply(SessionItem(ServerSession(id, endedAt: ReleasedAt)));

        result.SessionEndedRemotely.Should().BeTrue("MainShell bandı açılır (§5.7)");
        var row = _sessions.GetById(id)!;
        row.EndedAt.Should().BeNull("force-end aktif yayını kesmez (M18)");
        row.Authority.Should().Be(StreamSessionAuthority.Local);
        row.ReleasedAt.Should().Be(ReleasedAt);
        _sessions.GetActive()!.Id.Should().Be(id);

        var label = LabelServiceFor(_db).Add(id, Msg("alici_canli"), 80m, "A2");   // ABORT yok
        _labels.GetById(label.Id)!.SessionId.Should().Be(id);
    }

    [Fact]
    public void Yan_listedeki_oturum_aktif_kendi_yayininsa_siparis_de_bandi_acar_ve_etiket_o_yayina_iner()
    {
        var id = LocalSession(endedAt: null);
        SetMode(1);
        var customerId = NewId();
        var order = Order(id, customerId, username: "alici_yedek");

        var result = Apply(OrderItem(order), [ServerSession(id, endedAt: ReleasedAt)], [Draft(customerId, "alici_yedek")]);

        result.Status.Should().Be(SalesFeedApplyStatus.Applied);
        result.SessionEndedRemotely.Should().BeTrue();
        _labels.GetById(order.Id)!.SessionId.Should().Be(id);
        _sessions.GetById(id)!.EndedAt.Should().BeNull();
    }

    // ── (d) müşteri taslağı ───────────────────────────────────────────────

    [Fact]
    public void Eksik_musteri_taslaktan_acilir_ve_toplamlari_etiketlerden_turetilir()
    {
        var session = ServerSession();
        var customerId = NewId();
        var order = Order(session.Id, customerId, username: "alici_taslak", price: 150m);

        var result = Apply(OrderItem(order), [session], [Draft(customerId, "alici_taslak", "Takma Ad")]);

        result.Status.Should().Be(SalesFeedApplyStatus.Applied);
        result.LabelIds.Should().Equal(order.Id);
        var customer = _customers.GetById(customerId)!;
        customer.Username.Should().Be("alici_taslak");
        customer.DisplayName.Should().Be("Takma Ad");
        customer.TotalLabelsPrinted.Should().Be(1, "taslak 0/0 açılır, toplam aynı işlemde etiketten türetilir");
        customer.TotalAmount.Should().Be(150m);
        result.Aggregates.ChangedRows.Should().Be(1);
        _labels.GetById(order.Id)!.CustomerId.Should().Be(customerId);
    }

    [Fact]
    public void GUID_olmayan_ham_Id_ile_taslak_acilir()
    {
        var session = ServerSession();
        var rawId = $"yerel-{Guid.NewGuid():N}";

        Apply(OrderItem(Order(session.Id, rawId, username: "alici_ham")), [session], [Draft(rawId, "alici_ham")])
            .Status.Should().Be(SalesFeedApplyStatus.Applied);

        _customers.GetById(rawId)!.Username.Should().Be("alici_ham");
    }

    [Fact]
    public void Mezar_tasi_olan_kimlik_bosaltilmis_taslakla_acilir_dirilmez_ve_etiket_metni_inmez()
    {
        _customers.RecordPurge("tiktok", "alici_silinmis", Now - 100);
        var session = ServerSession();
        var customerId = NewId();
        var order = Order(session.Id, customerId, username: "alici_silinmis");

        Apply(OrderItem(order), [session], [Draft(customerId, "alici_silinmis", "Takma Ad")])
            .Status.Should().Be(SalesFeedApplyStatus.Applied);

        Scalar<long?>("SELECT PurgedAt FROM Customer WHERE Id = @customerId", new { customerId })
            .Should().Be(Now - 100, "etiket inişi silinmiş kimliği diriltmez");
        _customers.GetById(customerId)!.DisplayName.Should().Be("[Silindi]");
        var label = _labels.GetById(order.Id)!;
        label.DisplayName.Should().BeNull();
        label.MessageText.Should().BeEmpty();
    }

    [Fact]
    public void Sunucuda_silinmis_diyen_taslak_bosaltilmis_acilir_etiket_metni_inmez()
    {
        var session = ServerSession();
        var customerId = NewId();
        var order = Order(session.Id, customerId, username: "alici_sunucuda_silinmis");

        Apply(OrderItem(order), [session], [Draft(customerId, "alici_sunucuda_silinmis", null, purgedAt: Now - 50)]);

        Scalar<long?>("SELECT PurgedAt FROM Customer WHERE Id = @customerId", new { customerId }).Should().Be(Now - 50);
        var label = _labels.GetById(order.Id)!;
        label.DisplayName.Should().BeNull();
        label.MessageText.Should().BeEmpty();
    }

    [Theory]
    [InlineData("alici_kopya")]
    [InlineData("ALICI_KOPYA")]
    public void Ayni_kimlik_baska_Idde_ise_musteri_beklenir_hicbir_sey_yazilmaz_deneme_sayilmaz(string serverSpelling)
    {
        InsertCustomer("alici_kopya");
        var session = ServerSession();
        var canonical = NewId();
        var order = Order(session.Id, canonical, username: serverSpelling);

        var result = Apply(OrderItem(order), [session], [Draft(canonical, serverSpelling)]);

        result.Status.Should().Be(SalesFeedApplyStatus.PendingCustomer);
        _customers.GetById(canonical).Should().BeNull();
        _labels.GetById(order.Id).Should().BeNull();
        _sessions.GetById(session.Id).Should().BeNull("öğenin hiçbir yazımı kalmaz (oturum dahil)");
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0);
    }

    // ── (e) U5 ────────────────────────────────────────────────────────────

    [Fact]
    public void Gonderilmemis_yerel_satirda_uygulama_durur_satir_ve_oturum_birebir_kalir()
    {
        var sessionId = LocalSession(endedAt: LocalEndedAt, synced: true);
        var customerId = InsertCustomer("alici_yerel");
        var labelId = InsertLocalLabel(sessionId, customerId, synced: false);
        var before = LabelRow(labelId);
        var server = Order(sessionId, customerId, id: labelId, price: 999m, version: 5)
            with { CancelledAt = Now - 10, CancelReason = "customer" };

        var result = Apply(OrderItem(server), [ServerSession(sessionId)], options: CatchUp);

        result.Status.Should().Be(SalesFeedApplyStatus.Stalled);
        LabelRow(labelId).Should().BeEquivalentTo(before);
        _sessions.GetById(sessionId)!.ReleasedAt.Should().BeNull("hiçbir şey yazılmaz");
        ConflictRows(SalesConflictLog.CatchUpOverwrite).Should().Be(0);
    }

    // ── (f) var olan onaylı satır ─────────────────────────────────────────

    [Fact]
    public void Sunucu_otoritesindeki_onayli_satira_birimler_bag_ve_surum_yazilir_olusum_ve_kuyruk_degismez()
    {
        SetMode(1);
        var sessionId = LocalSession(endedAt: LocalEndedAt, synced: true);
        var customerId = InsertCustomer("alici_yerel", labels: 1, amount: 150m);
        var labelId = InsertLocalLabel(sessionId, customerId, synced: true, productId: NewId());
        Exec("UPDATE StreamSession SET Authority = 'server', ReleasedAt = @r WHERE Id = @sessionId",
            new { sessionId, r = ReleasedAt });                                  // devir görüldü (mod On)
        var before = LabelRow(labelId);
        var shipmentId = NewId();
        var server = Order(sessionId, customerId: NewId(), id: labelId, price: 175m, version: 9) with
        {
            // oluşum alanları sunucuda farklı görünse de yerelde değişmez (DM4)
            Username = "baska_ad", DisplayName = "Başka", MessageText = "başka metin", Code = "B9", AddedAt = 1,
            PriceCorrectedAt = Now - 30, PaidPrice = 150m,
            CancelledAt = Now - 20, CancelReason = "customer",
            ShipmentId = shipmentId,
            PrintedAt = StartedAt + 120, PrintBatchId = "b-0002", PrintSeq = 2,
        };

        var result = Apply(OrderItem(server), [ServerSession(sessionId)]);

        result.Status.Should().Be(SalesFeedApplyStatus.Applied);
        result.LabelIds.Should().Equal(labelId);
        var after = LabelRow(labelId);
        Convert.ToDecimal(after["Price"]).Should().Be(175m);
        after["PriceCorrectedAt"].Should().Be(Now - 30);
        Convert.ToDecimal(after["PaidPrice"]).Should().Be(150m);
        after["CancelledAt"].Should().Be(Now - 20);
        after["CancelReason"].Should().Be("customer");
        after["ShipmentId"].Should().Be(shipmentId);
        after["PrintBatchId"].Should().Be("b-0002");
        after["PrintSeq"].Should().Be(2L);
        after["ServerVersion"].Should().Be(9L);
        foreach (var unchanged in new[]
                 {
                     "SyncedAt", "StockSyncedAt", "Revision", "CustomerId", "SessionId", "Platform", "Username",
                     "DisplayName", "MessageText", "Code", "AddedAt", "IsShippingFee", "IsBackupPromoted", "ProductId",
                     "ProductVariantId", "ParentLabelId",
                 })
            after[unchanged].Should().Be(before[unchanged], $"{unchanged} akış uygulamasında yazılmaz");
        _labels.HasUnsynced().Should().BeFalse("hiçbir şey yeniden kuyruğa alınmaz");
        var customer = _customers.GetById(customerId)!;
        (customer.TotalLabelsPrinted, customer.TotalAmount).Should().Be((0, 0m), "iptal edilen etiket toplamdan düşer");
    }

    // ── (g) yeni satır ────────────────────────────────────────────────────

    [Fact]
    public void Yeni_satir_onayli_ve_stokta_sayilmis_dogar()
    {
        var session = ServerSession();
        var customerId = NewId();
        var productId = NewId();
        var order = Order(session.Id, customerId, productId: productId, version: 4);

        Apply(OrderItem(order), [session], [Draft(customerId)]);

        var row = LabelRow(order.Id);
        row["SyncedAt"].Should().Be(Now);
        row["StockSyncedAt"].Should().Be(Now, "boş kalsaydı aynı etiket stoktan ikinci kez düşerdi");
        row["Revision"].Should().Be(0L);
        row["ServerVersion"].Should().Be(4L);
        row["ProductId"].Should().Be(productId);
        _labels.GetPendingStockDeltas(productId).Should().BeEmpty();
        _labels.HasUnsynced().Should().BeFalse();
    }

    // ── (h) idempotent ────────────────────────────────────────────────────

    [Fact]
    public void Ayni_oge_iki_kez_uygulaninca_toplam_farki_sifir_stok_bir_kez()
    {
        var session = ServerSession();
        var customerId = NewId();
        var productId = NewId();
        var order = Order(session.Id, customerId, productId: productId, price: 120m);
        var first = Apply(OrderItem(order), [session], [Draft(customerId)]);

        var second = Apply(OrderItem(order), [session], [Draft(customerId)]);

        first.Aggregates.ChangedRows.Should().Be(1);
        second.Status.Should().Be(SalesFeedApplyStatus.Unchanged);
        second.Aggregates.Should().Be(AggregateRecomputeResult.None);
        second.LabelIds.Should().BeEmpty();
        Scalar<int>("SELECT COUNT(*) FROM Label").Should().Be(1);
        _labels.GetPendingStockDeltas(productId).Should().BeEmpty();
        var customer = _customers.GetById(customerId)!;
        (customer.TotalLabelsPrinted, customer.TotalAmount).Should().Be((1, 120m));
    }

    [Fact]
    public void Yereldeki_surumden_eski_oge_yazilmaz()
    {
        var session = ServerSession();
        var customerId = NewId();
        var order = Order(session.Id, customerId, version: 9);
        Apply(OrderItem(order), [session], [Draft(customerId)]);

        var stale = Apply(OrderItem(order with { Version = 8, Price = 1m }), [session], [Draft(customerId)]);

        stale.Status.Should().Be(SalesFeedApplyStatus.Unchanged);
        _labels.GetById(order.Id)!.Price.Should().Be(150m);
        Scalar<long>("SELECT ServerVersion FROM Label WHERE Id = @id", new { id = order.Id }).Should().Be(9);
    }

    // ── (i) ilk yetişme ───────────────────────────────────────────────────

    private (string SessionId, string CustomerId, string LabelId) OwnSyncedLabel()
    {
        var sessionId = LocalSession(endedAt: LocalEndedAt, synced: true);
        var customerId = InsertCustomer($"alici_{NewId()[..8]}");
        return (sessionId, customerId, InsertLocalLabel(sessionId, customerId, synced: true));
    }

    private static SalesFeedOrder CancelledOnServer(string sessionId, string customerId, string labelId) =>
        Order(sessionId, customerId, id: labelId, version: 6) with
        {
            Username = "alici_yerel", CancelledAt = Now - 20, CancelReason = "customer",
        };

    [Fact]
    public void Ilk_yetismede_kendi_satirinda_farkli_birim_yazilmadan_once_izlenir_metin_tasimaz()
    {
        var (sessionId, customerId, labelId) = OwnSyncedLabel();

        var result = Apply(OrderItem(CancelledOnServer(sessionId, customerId, labelId)), [ServerSession(sessionId)],
            options: CatchUp);

        result.CatchUpOverwrites.Should().Be(1);
        using var c = _db.Open();
        var log = c.QuerySingle<(string? LabelId, string Kind, string? Reason)>(
            "SELECT LabelId, Kind, Reason FROM SalesConflictLog");
        log.Should().Be((labelId, SalesConflictLog.CatchUpOverwrite, "units=cancel"));
        log.Reason.Should().NotContainAny("alici", "Takma", "MAVI");
        _labels.GetById(labelId)!.CancelledAt.Should().Be(Now - 20, "sunucunun değeri yine yazılır");
    }

    [Fact]
    public void Ilk_yetisme_bittiyse_iz_dusmez()
    {
        var (sessionId, customerId, labelId) = OwnSyncedLabel();
        SetFirstCatchUp(Now - 1000);

        Apply(OrderItem(CancelledOnServer(sessionId, customerId, labelId)), [ServerSession(sessionId)], options: CatchUp)
            .CatchUpOverwrites.Should().Be(0);

        ConflictRows(SalesConflictLog.CatchUpOverwrite).Should().Be(0);
        _labels.GetById(labelId)!.CancelledAt.Should().Be(Now - 20);
    }

    [Fact]
    public void Bayrak_yoksa_ya_da_yalniz_surum_farkliysa_iz_dusmez()
    {
        var (sessionId, customerId, labelId) = OwnSyncedLabel();

        Apply(OrderItem(CancelledOnServer(sessionId, customerId, labelId)), [ServerSession(sessionId)], options: Plain)
            .CatchUpOverwrites.Should().Be(0);
        var (sessionId2, customerId2, labelId2) = OwnSyncedLabel();
        var sameUnits = Order(sessionId2, customerId2, id: labelId2, version: 7);   // birimler yereldekiyle aynı
        Apply(OrderItem(sameUnits), [ServerSession(sessionId2)], options: CatchUp).CatchUpOverwrites.Should().Be(0);

        ConflictRows(SalesConflictLog.CatchUpOverwrite).Should().Be(0);
        Scalar<long>("SELECT ServerVersion FROM Label WHERE Id = @labelId2", new { labelId2 }).Should().Be(7);
    }

    [Fact]
    public void Uzak_yayinin_satirinda_iz_dusmez()
    {
        var session = ServerSession();
        var customerId = NewId();
        var order = Order(session.Id, customerId);
        Apply(OrderItem(order), [session], [Draft(customerId)], CatchUp);

        Apply(OrderItem(order with { Version = 4, CancelledAt = Now - 5, CancelReason = "customer" }), [session],
            [Draft(customerId)], CatchUp).CatchUpOverwrites.Should().Be(0);

        ConflictRows(SalesConflictLog.CatchUpOverwrite).Should().Be(0);
    }

    // ── (j) KVKK ──────────────────────────────────────────────────────────

    [Fact]
    public void Yerel_musteri_silinmisse_inen_satirin_takma_adi_bos_mesaji_bostur()
    {
        var customerId = InsertCustomer("alici_kvkk");
        _customers.ScrubPersonalData(customerId);
        var session = ServerSession();
        var order = Order(session.Id, customerId, username: "alici_kvkk");

        Apply(OrderItem(order), [session]).Status.Should().Be(SalesFeedApplyStatus.Applied);

        var label = _labels.GetById(order.Id)!;
        label.DisplayName.Should().BeNull();
        label.MessageText.Should().Be("");
        label.Code.Should().Be("A1", "kod ve fiyat mali kayıt olarak kalır");
    }

    // ── (k) dosya aynası ──────────────────────────────────────────────────

    private static SalesFeedShipment Shipment(string customerId, string? id = null, int version = 4) => new(
        Id: id ?? NewId(), CustomerId: customerId, Status: ShipmentStatus.Held, CumulativeAmount: 550m,
        CreatedAt: StartedAt, HeldAt: StartedAt + 300, ShippedAt: null, Version: version, OriginReplicaId: NewId());

    [Fact]
    public void Dosya_ogesi_bayrak_yoksa_yok_sayilir()
    {
        var shipment = Shipment(InsertCustomer("alici_kargo"));

        Apply(ShipmentItem(shipment)).Status.Should().Be(SalesFeedApplyStatus.Ignored);

        Scalar<int>("SELECT COUNT(*) FROM Shipment").Should().Be(0);
    }

    [Fact]
    public void Dosya_ogesi_bayrakla_aynaya_yazilir_mod_1de_ABORT_yok_eski_surum_yazilmaz()
    {
        SetMode(1);
        var customerId = InsertCustomer("alici_kargo");
        var shipment = Shipment(customerId);

        var inserted = Apply(ShipmentItem(shipment), options: WithShipments);

        inserted.Status.Should().Be(SalesFeedApplyStatus.Applied);
        inserted.ShipmentIds.Should().Equal(shipment.Id);
        using (var c = _db.Open())
        {
            var row = c.QuerySingle<(string CustomerId, string Status, double CumulativeAmount, long? HeldAt,
                string? OriginReplicaId, long ServerVersion, long SyncedAt, long Revision)>(
                @"SELECT CustomerId, Status, CumulativeAmount, HeldAt, OriginReplicaId, ServerVersion, SyncedAt, Revision
                  FROM Shipment WHERE Id = @id", new { id = shipment.Id });
            row.CustomerId.Should().Be(customerId);
            row.Status.Should().Be("Held");
            row.CumulativeAmount.Should().Be(550d);
            row.HeldAt.Should().Be(StartedAt + 300);
            row.OriginReplicaId.Should().Be(shipment.OriginReplicaId);
            row.ServerVersion.Should().Be(4);
            row.SyncedAt.Should().Be(Now, "ayna onaylı: gönderilmez");
            row.Revision.Should().Be(0, "akış uygulaması kuyruğa almaz (DM8)");
        }

        var shipped = shipment with { Status = ShipmentStatus.Shipped, ShippedAt = Now - 60, Version = 5 };
        Apply(ShipmentItem(shipped), options: WithShipments).Status.Should().Be(SalesFeedApplyStatus.Applied);
        Apply(ShipmentItem(shipment with { Version = 3 }), options: WithShipments).Status
            .Should().Be(SalesFeedApplyStatus.Unchanged, "eski sürüm aynayı geri sarmaz");

        using var check = _db.Open();
        var final = check.QuerySingle<(string Status, long? ShippedAt, long ServerVersion, long Revision)>(
            "SELECT Status, ShippedAt, ServerVersion, Revision FROM Shipment WHERE Id = @id", new { id = shipment.Id });
        final.Status.Should().Be("Shipped");
        final.ShippedAt.Should().Be(Now - 60);
        final.ServerVersion.Should().Be(5);
        final.Revision.Should().Be(0);
    }

    // ── kilit sırası ve sözleşme ──────────────────────────────────────────

    [Fact]
    public async Task Musterisi_odeme_akisinda_olan_oge_Busy_doner_hicbir_sey_yazmaz()
    {
        var busy = new CustomerBusySet();
        var feed = new SalesFeedRepository(_db, busy);
        var (sessionId, customerId, labelId) = OwnSyncedLabel();
        var before = LabelRow(labelId);
        using var lease = await busy.EnterAsync(customerId);

        Apply(OrderItem(CancelledOnServer(sessionId, customerId, labelId)), [ServerSession(sessionId)], feed: feed)
            .Status.Should().Be(SalesFeedApplyStatus.Busy);

        LabelRow(labelId).Should().BeEquivalentTo(before);
        _sessions.GetById(sessionId)!.ReleasedAt.Should().BeNull();
    }

    [Fact]
    public void Sozlesmeye_uymayan_oge_ogeye_ozgu_hata_atar_hicbir_sey_yazmaz()
    {
        var session = ServerSession();
        var customerId = NewId();
        var order = Order(session.Id, customerId);

        FluentActions.Invoking(() => Apply(OrderItem(order with { SessionId = null }), [session], [Draft(customerId)]))
            .Should().Throw<SalesFeedContractException>();
        FluentActions.Invoking(() => Apply(OrderItem(order), [], [Draft(customerId)]))
            .Should().Throw<SalesFeedContractException>("oturum ne yan listede ne yerelde");
        FluentActions.Invoking(() => Apply(OrderItem(order), [session], []))
            .Should().Throw<SalesFeedContractException>("müşteri ne yerelde ne taslakta");
        FluentActions.Invoking(() => Apply(SessionItem(session with { ReleasedAt = null })))
            .Should().Throw<SalesFeedContractException>("devredilmemiş uzak oturum StreamSession'a girmez");

        Scalar<int>("SELECT COUNT(*) FROM Label").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM StreamSession").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM Customer").Should().Be(0);
    }
}
