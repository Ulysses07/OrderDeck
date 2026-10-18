using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
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
/// Ödeme geçmişi geri doldurması (spec 2026-10-13 §6.4 "Ödeme geçmişi (mod Off'ta da)", §8.2-6, M17, §8.7; W3-06):
/// yerel <c>PaymentJob</c>'lardan teslim edilmiş ve belirsiz her iş (miras dahil) belirlenimli Id'li kirasız kayıt olarak
/// kuyruğa, oradan <c>payment-requests/backfill</c>'e gider; kapsam işin asıl kapsamı, kapsanan siparişler
/// <c>PrintedAt ≤ PromptAt</c>; imleç sayfalarda ve yeniden başlatmada sürer; kuyruk boşalınca
/// <c>hello {paymentHistoryCompleted: true}</c>; tamamlandıktan sonra istek yok; geri yükleme (yeni kopya) baştan, aynı
/// Id'lerle. Sunucu <see cref="FakeSalesServer"/>, mod Off (bugün her lisans).
/// </summary>
public sealed class PaymentHistoryBackfillTests : IDisposable
{
    private const long T0 = 1_755_000_000L;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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

    /// <summary>Sunucu modu 0: kip adımı açılış yapmaz.</summary>
    private sealed class NoOpening : ISalesOpeningFlush
    {
        public Task<SalesOpeningFlushResult> FlushAsync(string licenseKey, CancellationToken ct) =>
            throw new InvalidOperationException("ödeme geçmişi testlerinde sunucu modu 0: açılış koşmaz");
    }

    /// <summary>Bir bilgisayarın yığını (bir süreç). Aynı veritabanıyla yeni örnek "yeniden başlatma"dır.</summary>
    private sealed class Pc
    {
        public License License { get; }
        public SalesSyncEnvelope Envelope { get; }
        public SalesSyncStateService State { get; }
        public SalesModeCoordinator Coordinator { get; }
        public PaymentRecordSender PaymentRecords { get; }
        public PaymentHistoryBackfill History { get; }
        public CapturingLogger<PaymentHistoryBackfill> Log { get; } = new();

        public Pc(FakeSalesServer server, IDbConnectionFactory db, string fingerprint, int pageSize, int maxPages)
        {
            License = new License(server.Lisans);
            var identity = new ReplicaIdentityRepository(db);
            var state = new SalesSyncStateRepository(db);
            Envelope = new SalesSyncEnvelope(identity, state, new Hardware(fingerprint), License,
                NullLogger<SalesSyncEnvelope>.Instance);
            var api = new LicenseApiClient(
                new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore());
            var clock = new Clock();
            State = new SalesSyncStateService(api, Envelope, identity, state, License, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance);
            var pending = new PendingPaymentRecordRepository(db);
            PaymentRecords = new PaymentRecordSender(api, pending, Envelope, License, clock,
                NullLogger<PaymentRecordSender>.Instance);
            History = new PaymentHistoryBackfill(api, Envelope, State, state, new PaymentJobRepository(db),
                new CustomerRepository(db), new LabelRepository(db), PaymentRecords, pending, new SyncCursorRepository(db),
                License, clock, Log)
            {
                PageSize = pageSize,
                MaxPagesPerTour = maxPages,
            };
            Coordinator = new SalesModeCoordinator(State, state, Envelope, new NoOpening(), clock,
                NullLogger<SalesModeCoordinator>.Instance);
        }

        /// <summary>Üretimdeki satış akışı turu gibi: hello + state, ardından kip adımı.</summary>
        public async Task HelloAsync()
        {
            (await State.TourOnceAsync(CancellationToken.None)).Should().Be(SalesStateTourOutcome.Ok);
            (await Coordinator.RunAsync(License.CurrentLicenseKey!, CancellationToken.None)).Should().Be(SalesModeStep.None);
        }

        /// <summary><see cref="SalesBackfillHostedService"/>'in turu: önce bekleyen kayıtların gönderimi
        /// (<see cref="SalesBackfillService"/>'in ilk adımı), sonra ödeme geçmişi.</summary>
        public async Task<PaymentHistoryTourOutcome> TickAsync()
        {
            await PaymentRecords.FlushAsync(CancellationToken.None);
            return await History.TourOnceAsync(CancellationToken.None);
        }
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly string _fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly CustomerRepository _customers;
    private readonly SessionRepository _sessions;
    private readonly PaymentJobRepository _jobs;
    private readonly PendingPaymentRecordRepository _pending;
    private readonly SyncCursorRepository _cursors;
    private long _addedAt = T0 + 100;

    public PaymentHistoryBackfillTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        _sessions = new SessionRepository(_db);
        _jobs = new PaymentJobRepository(_db);
        _pending = new PendingPaymentRecordRepository(_db);
        _cursors = new SyncCursorRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private Pc NewPc(int pageSize = PaymentHistoryBackfill.DefaultPageSize,
        int maxPages = PaymentHistoryBackfill.DefaultMaxPagesPerTour)
        => new(_server, _db, _fingerprint, pageSize, maxPages);

    private Guid ReplicaId => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

    // ── kurulum ────────────────────────────────────────────────────────────

    private string AddCustomer(string username)
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, "tiktok", username, "Örnek Alıcı", null, 1, 1, false, null, null, 0, 0m,
            null, null, null));
        return id;
    }

    private string AddSession(long startedAt)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Örnek yayın", startedAt, startedAt + 3600, new[] { "tiktok" }, null,
            SyncedAt: startedAt + 3700));
        return id;
    }

    /// <summary>Basılmış (ya da değil) yerel etiket; ham INSERT.</summary>
    private string AddLabel(string sessionId, string customerId, decimal price, long? printedAt,
        long? cancelledAt = null, bool tentative = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var added = _addedAt++;
        Exec(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price,
                                  AddedAt, PrintedAt, CancelledAt, CancelReason, IsTentativeBackup, SyncedAt, StockSyncedAt)
               VALUES (@id, @sessionId, @customerId, 'tiktok', 'ornek_alici', 'Örnek Alıcı', 'alıyorum', 'A1', @price,
                       @added, @printedAt, @cancelledAt, @reason, @tentative, @added, @added)",
            new
            {
                id, sessionId, customerId, price, added, printedAt, cancelledAt,
                reason = cancelledAt is null ? null : "müşteri vazgeçti", tentative = tentative ? 1 : 0,
            });
        return id;
    }

    /// <summary>
    /// Ödeme işi, depo API'siyle: isteğe bağlı düşüm, <paramref name="deliveries"/> teslim ve sonucu bilinmeyen son
    /// deneme. Anlar sabitlenir: teslim (ve kapanış) <paramref name="deliveredAt"/>, deneme <paramref name="attemptAt"/>.
    /// </summary>
    private PaymentJob AddJob(string ownerId, string scopeKey, decimal total, int deliveries = 1,
        decimal? applied = null, decimal? unconfirmedNet = null, long deliveredAt = T0 + 5000, long attemptAt = T0 + 6000)
    {
        var job = _jobs.FindOrCreate(ownerId, scopeKey, total);
        if (applied is { } amount)
        {
            var key = Guid.NewGuid();
            _jobs.BeginApply(job.Id, key).Should().BeTrue();
            _jobs.MarkApplied(job.Id, key, 0, amount).Should().BeTrue();
        }
        var net = total - (applied ?? 0m);
        for (var i = 0; i < deliveries; i++)
        {
            _jobs.BeginSend(job.Id, net);
            _jobs.MarkDelivered(job.Id, net);
        }
        if (unconfirmedNet is { } attempted) _jobs.BeginSend(job.Id, attempted);
        Exec(@"UPDATE PaymentJob
               SET DeliveredAt = CASE WHEN DeliveredAt IS NULL THEN NULL ELSE @deliveredAt END,
                   ClosedAt = CASE WHEN ClosedAt IS NULL THEN NULL ELSE @deliveredAt END,
                   AttemptAt = CASE WHEN AttemptAt IS NULL THEN NULL
                                    WHEN SendAttempts > Deliveries THEN @attemptAt ELSE @deliveredAt END,
                   PriorAttemptAt = NULL
               WHERE Id = @id",
            new { id = job.Id, deliveredAt, attemptAt });
        return _jobs.Get(job.Id)!;
    }

    /// <summary>Kişi birleştirmesinin indirmesi gibi: asıl kapsamı bilinen miras iş.</summary>
    private PaymentJob DemoteToLegacy(PaymentJob job)
    {
        Exec("UPDATE PaymentJob SET PriorScopeKey = ScopeKey, ScopeKey = 'legacy:' || Id || ':' || Id WHERE Id = @id",
            new { id = job.Id });
        return _jobs.Get(job.Id)!;
    }

    private void Exec(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    private IReadOnlyList<PaymentRequestBackfillItem> Queued() =>
        _pending.List().Where(r => r.Kind == PendingPaymentRecordKinds.Record)
            .Select(PendingPaymentRecordCodec.DecodeRecord).ToList();

    /// <summary>Sunucuya giden kirasız kayıtlar, gönderim sırasıyla (yinelemeler dahil).</summary>
    private IReadOnlyList<PaymentRequestBackfillItem> Sent() =>
        _server.Calls(FakeSalesServer.PaymentRequestsBackfill)
            .SelectMany(c => JsonSerializer.Deserialize<PaymentRequestBackfillRequest>(c.RawJson, Web)!.Records)
            .ToList();

    private IReadOnlyList<FakeSalesServer.HelloCall> CompletionHellos =>
        _server.Hellos.Where(h => h.Body.PaymentHistoryCompleted == true).ToList();

    private SyncCursor? Cursor() => _cursors.Get(PaymentHistoryCursor.Name, _server.Lisans);

    private static Guid G(string id) => Guid.ParseExact(id, "N");

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    // ── kayıtlar ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Teslim_edilmis_ve_belirsiz_isler_belirlenimli_Id_ve_alanlarla_gider_teslim_edilmemis_is_gitmez()
    {
        var owner = AddCustomer("ornek_sahip");
        var second = AddCustomer("ornek_ikinci");
        _customers.MergeIntoGroup(new[] { owner, second });
        var s1 = AddSession(T0);
        var s2 = AddSession(T0 + 10_000);
        var ownLabel = AddLabel(s1, owner, 150m, printedAt: T0 + 1000);
        var secondLabel = AddLabel(s1, second, 100m, printedAt: T0 + 1100);
        var laterSession = AddLabel(s2, owner, 80m, printedAt: T0 + 5500);
        AddLabel(s1, owner, 60m, printedAt: T0 + 1200, cancelledAt: T0 + 1300);   // iptal: katkı yok
        AddLabel(s1, owner, 40m, printedAt: null);                                 // basılmamış
        AddLabel(s1, owner, 30m, printedAt: T0 + 1250, tentative: true);           // geçici yedek

        // Kümülatif, düşümlü, iki kez teslim edilmiş (son teslim T0+5000; s2'nin etiketi T0+5500'de basıldı).
        var delivered = AddJob(owner, "cumulative", 250m, deliveries: 2, applied: 50m);
        // Yayın kapsamı: bir teslim, sonra sonucu bilinmeyen deneme (T0+6000, deneme neti 270).
        var unconfirmed = AddJob(owner, $"session:{s2}", 260m, deliveries: 1, unconfirmedNet: 270m);
        // Tamamı bakiyeden: net 0.
        var fully = AddJob(second, $"session:{s1}", 100m, applied: 100m);
        // Hiç gönderilmemiş iş: soruya girmez, kayıt yok.
        AddJob(owner, $"session:{AddSession(T0 + 20_000)}", 90m, deliveries: 0);
        delivered.HasUnconfirmedSend.Should().BeFalse();
        unconfirmed.HasUnconfirmedSend.Should().BeTrue();

        var pc = NewPc();
        await pc.HelloAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.AwaitingQueue, "kayıtlar kuyrukta, gönderilmedi");

        var queued = Queued().ToDictionary(r => r.RecordId);
        queued.Should().HaveCount(3);

        var a = queued[PaymentRecordIds.Delivered(delivered.Id, 2)];
        a.OwnerId.Should().Be(owner);
        a.MemberIds.Should().Equal(new[] { owner, second }, "sahip önce, sonra kişinin öbür üyesi");
        a.ScopeKey.Should().Be("cumulative");
        a.State.Should().Be(PaymentRequestStates.Delivered);
        a.NetAmount.Should().Be(200m);
        a.AppliedAmount.Should().Be(50m);
        a.FullyFromBalance.Should().BeFalse();
        a.At.Should().Be(At(T0 + 5000));
        a.OrderIds.Should().BeEquivalentTo(new[] { G(ownLabel), G(secondLabel) },
            "kişinin bütün üyelerinin katkı veren etiketleri, istekten önce basılmış olanlar");

        var b = queued[PaymentRecordIds.Unconfirmed(unconfirmed.Id, 2)];
        b.State.Should().Be(PaymentRequestStates.Unconfirmed);
        b.ScopeKey.Should().Be($"session:{s2}");
        b.NetAmount.Should().Be(270m, "sorunun gösterdiği net: belirsiz denemenin neti");
        b.AppliedAmount.Should().BeNull();
        b.At.Should().Be(At(T0 + 6000));
        b.OrderIds.Should().Equal(new[] { G(laterSession) }, "yalnız o yayının etiketleri");
        b.RecordId.Should().NotBe(PaymentRecordIds.Delivered(unconfirmed.Id, 2),
            "iki ad uzayı: sonraki teslim belirsiz denemenin Id'sini yeniden kullanmaz");

        var c = queued[PaymentRecordIds.Delivered(fully.Id, 1)];
        c.OwnerId.Should().Be(second);
        c.NetAmount.Should().Be(0m);
        c.AppliedAmount.Should().Be(100m);
        c.FullyFromBalance.Should().BeTrue();
        c.OrderIds.Should().BeEquivalentTo(new[] { G(ownLabel), G(secondLabel) });

        CompletionHellos.Should().BeEmpty("kuyruk boşalmadan paymentHistoryCompleted gitmez");

        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);

        Sent().Select(r => r.RecordId).Should().BeEquivalentTo(queued.Keys);
        _server.Calls(FakeSalesServer.PaymentRequestsBackfill).Should().OnlyContain(call =>
            call.Json.GetProperty("replicaId").GetGuid() == ReplicaId
            && call.Json.GetProperty("protocol").GetInt32() == 2
            && call.Json.GetProperty("seenMode").GetInt32() == 0);
        _pending.Count().Should().Be(0);
        CompletionHellos.Should().ContainSingle().Which.Body.ReplicaId.Should().Be(ReplicaId);
        _server.ReplicaRow(_server.LicenseId, ReplicaId)!.PaymentHistoryCompleted.Should().BeTrue();
        Cursor()!.Seq.Should().Be(PaymentHistoryCursor.Completed.Encode());
        Cursor()!.LastId.Should().Be(ReplicaId);
        Scalar<long?>("SELECT PaymentHistorySentAt FROM SalesSyncState WHERE Id = 1").Should().NotBeNull();
    }

    [Fact]
    public async Task PromptAttan_sonra_basilan_etiket_orderIdse_girmez()
    {
        var owner = AddCustomer("ornek_sahip");
        var session = AddSession(T0);
        var before = AddLabel(session, owner, 100m, printedAt: T0 + 4000);
        var atPrompt = AddLabel(session, owner, 100m, printedAt: T0 + 5000);
        AddLabel(session, owner, 100m, printedAt: T0 + 5001);                     // istekten sonra basıldı
        var job = AddJob(owner, $"session:{session}", 200m, deliveredAt: T0 + 5000);

        var pc = NewPc();
        await pc.HelloAsync();
        await pc.TickAsync();

        Queued().Should().ContainSingle().Which.OrderIds.Should().BeEquivalentTo(new[] { G(before), G(atPrompt) },
            "Covers: PrintedAt ≤ PromptAt (job={0})", job.Id);
    }

    [Fact]
    public async Task Asil_kapsami_bilinen_miras_is_asil_kapsamla_bilinmeyen_legacy_bicimiyle_gider()
    {
        var owner = AddCustomer("ornek_sahip");
        var session = AddSession(T0);
        var label = AddLabel(session, owner, 120m, printedAt: T0 + 1000);
        var known = DemoteToLegacy(AddJob(owner, $"session:{session}", 120m));
        var legacyKey = $"legacy:{Guid.NewGuid():N}";
        var unknown = AddJob(owner, legacyKey, 75m);
        known.IsLegacyScope.Should().BeTrue();
        known.PriorScopeKey.Should().Be($"session:{session}");
        unknown.PriorScopeKey.Should().BeNull();

        var pc = NewPc();
        await pc.HelloAsync();
        await pc.TickAsync();

        var queued = Queued().ToDictionary(r => r.RecordId);
        var k = queued[PaymentRecordIds.Delivered(known.Id, 1)];
        k.ScopeKey.Should().Be($"session:{session}", "miras iş ASIL kapsamıyla: begin'in (e) adımı onu orada bulur");
        k.OrderIds.Should().Equal(G(label));
        var u = queued[PaymentRecordIds.Delivered(unknown.Id, 1)];
        u.ScopeKey.Should().Be(legacyKey, "legacy biçimi yalnız asıl kapsamı bilinmeyen iş için");
        u.OrderIds.Should().BeEmpty("kapsamı bilinmeyen işin kapsadığı siparişler kurulamaz");
        u.NetAmount.Should().Be(75m);
    }

    [Fact]
    public async Task Kayit_bicimine_uymayan_is_atlanir_tamamlanmayi_bekletmez()
    {
        var owner = AddCustomer("ornek_sahip");
        var bad = AddJob(owner, "session:yerel-olmayan-bicim", 50m);               // sunucu 400 invalid-scope verirdi
        var good = AddJob(owner, "cumulative", 60m);

        var pc = NewPc();
        await pc.HelloAsync();
        await pc.TickAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);

        Sent().Select(r => r.RecordId).Should().Equal(PaymentRecordIds.Delivered(good.Id, 1));
        pc.Log.Warnings.Should().Contain(e => e.Message.Contains(bad.Id) && e.Message.Contains("kapsam"));
    }

    // ── imleç ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Imlec_sayfalarda_ve_yeniden_baslatmada_kaldigi_yerden_surer_her_kayit_bir_kez_gider()
    {
        var owner = AddCustomer("ornek_sahip");
        var jobs = Enumerable.Range(0, 5).Select(i => AddJob(owner, $"session:{AddSession(T0 + i * 10_000)}", 100m + i))
            .ToList();

        var pc = NewPc(pageSize: 2, maxPages: 1);
        await pc.HelloAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.InProgress);
        Queued().Select(r => r.RecordId).Should().BeEquivalentTo(jobs.Take(2).Select(j => PaymentRecordIds.Delivered(j.Id, 1)));
        var firstSeq = Cursor()!.Seq;
        PaymentHistoryCursor.Decode(firstSeq!.Value)!.Value.Phase.Should().Be(PaymentHistoryPhase.Jobs);

        pc = NewPc(pageSize: 2, maxPages: 1);                                     // yeniden başlatma
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.WaitingForHello, "hello'suz hiçbir şey yapılmaz");
        await pc.HelloAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.InProgress);
        Cursor()!.Seq.Should().BeGreaterThan(firstSeq.Value);
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.InProgress);
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);

        Sent().Select(r => r.RecordId).Should().Equal(jobs.Select(j => PaymentRecordIds.Delivered(j.Id, 1)),
            "imleç baştan başlamadı: her kayıt bir kez, sırayla");
        CompletionHellos.Should().ContainSingle();
    }

    [Fact]
    public async Task Tamamlandiktan_sonra_istek_yok_yeniden_baslatmada_da()
    {
        var owner = AddCustomer("ornek_sahip");
        AddJob(owner, "cumulative", 100m);
        var pc = NewPc();
        await pc.HelloAsync();
        await pc.TickAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);
        var sent = _server.Calls(FakeSalesServer.PaymentRequestsBackfill).Count;
        var hellos = _server.Hellos.Count;

        AddJob(owner, $"session:{AddSession(T0 + 50_000)}", 70m);                  // sonraki isteği mod Off kaydı taşır
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.AlreadyCompleted);
        pc = NewPc();
        await pc.HelloAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.AlreadyCompleted);

        _server.Calls(FakeSalesServer.PaymentRequestsBackfill).Count.Should().Be(sent);
        _pending.Count().Should().Be(0);
        _server.Hellos.Count.Should().Be(hellos + 1, "yalnız yeniden başlatmanın olağan hello'su");
        CompletionHellos.Should().ContainSingle();
    }

    [Fact]
    public async Task Geri_yukleme_yeni_kopya_icin_bastan_baslar_ayni_Idler_sunucuda_unchanged()
    {
        var owner = AddCustomer("ornek_sahip");
        var first = AddJob(owner, "cumulative", 100m);
        var second = AddJob(owner, $"session:{AddSession(T0)}", 80m, unconfirmedNet: 85m);
        var pc = NewPc();
        await pc.HelloAsync();
        await pc.TickAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);
        var original = ReplicaId;
        var firstRound = Sent().ToList();

        new ReplicaIdentityRepository(_db).Rotate();                               // geri yükleme: kimlik döner
        pc = NewPc();
        await pc.HelloAsync();
        ReplicaId.Should().NotBe(original);
        await pc.TickAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);

        var secondRound = Sent().Skip(firstRound.Count).ToList();
        secondRound.Select(r => r.RecordId).Should().BeEquivalentTo(new[]
        {
            PaymentRecordIds.Delivered(first.Id, 1), PaymentRecordIds.Unconfirmed(second.Id, 2),
        });
        secondRound.Select(r => JsonSerializer.Serialize(r, Web)).Should()
            .BeEquivalentTo(firstRound.Select(r => JsonSerializer.Serialize(r, Web)), "aynı içerik: sunucu unchanged");
        _server.ReplicaRow(_server.LicenseId, ReplicaId)!.PaymentHistoryCompleted.Should().BeTrue(
            "tamamlanma kopya başına: yeni kopya kendi bayrağını gönderir");
        CompletionHellos.Select(h => h.Body.ReplicaId).Should().Equal(original, ReplicaId);
        Cursor()!.LastId.Should().Be(ReplicaId);
    }

    [Fact]
    public async Task PaymentHistoryCompleted_ancak_kuyruk_bosalinca_gider()
    {
        var owner = AddCustomer("ornek_sahip");
        AddJob(owner, "cumulative", 100m);
        var pc = NewPc();
        await pc.HelloAsync();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.AwaitingQueue);

        _server.EnqueueStatus(FakeSalesServer.PaymentRequestsBackfill, 503);
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.AwaitingQueue, "gönderim düştü, kayıt kuyrukta");
        _pending.Count().Should().Be(1);
        CompletionHellos.Should().BeEmpty();
        Cursor()!.Seq.Should().Be(PaymentHistoryCursor.Done.Encode());

        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);
        CompletionHellos.Should().ContainSingle();
    }

    [Fact]
    public async Task Hello_basarisiz_ya_da_tamamlanma_hellosu_duserse_imlec_tamamlanmaz_sonraki_turda_yeniden()
    {
        var owner = AddCustomer("ornek_sahip");
        AddJob(owner, "cumulative", 100m);
        var pc = NewPc();
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.WaitingForHello);
        _pending.Count().Should().Be(0, "hello başarılı olmadan kuyruğa da yazılmaz");

        await pc.HelloAsync();
        await pc.TickAsync();
        _server.EnqueueStatus(FakeSalesServer.Hello, 503);
        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Failed);
        Cursor()!.Seq.Should().Be(PaymentHistoryCursor.Done.Encode());
        _server.ReplicaRow(_server.LicenseId, ReplicaId)!.PaymentHistoryCompleted.Should().BeFalse();
        Scalar<long?>("SELECT PaymentHistorySentAt FROM SalesSyncState WHERE Id = 1").Should().BeNull();

        (await pc.TickAsync()).Should().Be(PaymentHistoryTourOutcome.Completed);
        CompletionHellos.Should().HaveCount(2, "düşen bildirim sonraki turda yeniden");
        _server.ReplicaRow(_server.LicenseId, ReplicaId)!.PaymentHistoryCompleted.Should().BeTrue();
        Cursor()!.Seq.Should().Be(PaymentHistoryCursor.Completed.Encode());
    }
}
