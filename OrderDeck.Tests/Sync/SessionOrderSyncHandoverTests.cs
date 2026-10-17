using System.Diagnostics;
using Dapper;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
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
/// Kapanış sırası ve devir (spec 2026-10-13 §5.2, M2, §4.1, §8.7; W1-07): bitmiş oturum gönderilmemiş etiket
/// sayısıyla bekletilir, etiketler gider, kuyruk boşalınca aynı turda <c>Release = true</c>; devir kapısı sayım ile
/// yanıt arasında tutulur; geri alma penceresinde oturum kuyrukta kalır; eski sunucu bugünkü gibi onaylanır; mod
/// Off'ta <c>Authority</c> yazılmaz; force-end aktif yayını kesmez; sahip olmayanın kapanışı son durumdur; canlı
/// ritimde bekletilen oturum seyreltilir; çevrimdışı düzenleme sürer, devir bağlantı gelince olur. Sunucu
/// <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class SessionOrderSyncHandoverTests : IDisposable
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

    /// <summary>Elle ilerletilen zaman (zamanlayıcının ritmi): zaman damgası tik cinsinden.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.FromDays(1).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly License _license;
    private readonly Clock _clock = new();
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly HandoverGate _gate = new();
    private readonly CapturingLogger<SessionOrderSyncService> _log = new();
    private readonly SessionOrderSyncService _svc;
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private long _addedAt = 1_760_000_100L;

    /// <summary><c>Release = true</c> taşıyan oturum gönderimi sunucuya vardığında o oturumun yerelde gönderilmemiş
    /// etiketi varsa buraya yazılır (M2 değişmezi; her testte boş kalmalı).</summary>
    private readonly List<string> _releaseViolations = new();

    public SessionOrderSyncHandoverTests()
    {
        new MigrationRunner(_db).Run();
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        _license = new License(_server.Lisans);
        new CustomerRepository(_db).Insert(new Customer(_customerId, "tiktok", "ornek_alici", "Örnek Alıcı",
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
        var api = new LicenseApiClient(
            new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
            new LicenseTokenStore());
        _svc = new SessionOrderSyncService(api, _sessions, _labels, _license, TestSalesEnvelope.For(_db, _license),
            _gate, new SalesPushApply(_db), _clock, _log);
        _server.DuringPush = endpoint =>
        {
            if (endpoint == FakeSalesServer.SessionsSync) CheckReleaseInvariant();
            DuringPush?.Invoke(endpoint);
        };
    }

    public void Dispose()
    {
        _releaseViolations.Should().BeEmpty("Release = true, oturumun gönderilmemiş etiketi varken gönderilmemeli (M2)");
        _db.Dispose();
        _server.Dispose();
    }

    /// <summary>Testin kendi uçuş geri çağrısı (değişmez denetiminden sonra çalışır).</summary>
    private Action<string>? DuringPush { get; set; }

    private void CheckReleaseInvariant()
    {
        foreach (var item in _server.Calls(FakeSalesServer.SessionsSync)[^1].Sessions.Sessions!)
        {
            if (!item.Release) continue;
            var unsent = _labels.CountUnsyncedBySession(item.Id.ToString("N"));
            if (unsent != 0) _releaseViolations.Add($"{item.Id}: {unsent}");
        }
    }

    private string NewSession(long? endedAt = null, bool synced = false)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Örnek yayın", 1_760_000_000L, endedAt, new[] { "tiktok" }, null,
            SyncedAt: synced ? _clock.Now : null));
        return id;
    }

    private string AddLabel(string sessionId, decimal price = 100m)
    {
        var id = Guid.NewGuid().ToString("N");
        _labels.Insert(new Label(id, sessionId, _customerId, "tiktok", "ornek_alici", "alıyorum", "A1", price,
            _addedAt++, null, DisplayName: "Örnek Alıcı"));
        return id;
    }

    private LabelService LabelService() => new(_labels,
        new CustomerService(new CustomerRepository(_db), _sessions, _labels, _clock), _db, _clock);

    private IReadOnlyList<FakeSalesServer.SessionBody> SessionItems(FakeSalesServer.PushCall call) =>
        call.Sessions.Sessions!;

    private List<FakeSalesServer.SessionBody> SentFor(string sessionId) =>
        _server.Calls(FakeSalesServer.SessionsSync)
            .SelectMany(SessionItems)
            .Where(s => s.Id == Guid.Parse(sessionId))
            .ToList();

    private int CountPending() => new SyncOutboxRepository(_db).CountPending(long.MaxValue);

    private void Sql(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    private static Func<CancellationToken, Task> DrainOf(SessionOrderSyncService svc, List<int> rounds) =>
        SyncFlushService.Drain(async ct =>
        {
            var r = await svc.SyncOnceAsync(ct);
            rounds.Add(r.SessionsPushed + r.OrdersPushed);
            return r.SessionsPushed + r.OrdersPushed;
        });

    // ── 1. kapanış sırası ─────────────────────────────────────────────────

    [Fact]
    public async Task Bitmis_oturum_uc_etiketle_tek_turda_once_bekletilir_etiketler_gider_sonra_devredilir()
    {
        var session = NewSession();
        var labels = Enumerable.Range(0, 3).Select(_ => AddLabel(session)).ToList();
        _sessions.End(session, 1_760_000_900L);

        var rounds = new List<int>();
        await DrainOf(_svc, rounds)(CancellationToken.None);

        _server.Faults.Should().BeEmpty();
        _server.PushCalls.Select(c => c.Endpoint).Should().Equal(
            new[] { FakeSalesServer.SessionsSync, FakeSalesServer.OrdersSync, FakeSalesServer.SessionsSync },
            "kapanış sırası: oturum (bekletilir) → etiketler → oturum yeniden (M2)");

        var sent = SentFor(session);
        sent[0].Release.Should().BeFalse();
        sent[0].UnsentLabelCount.Should().Be(3);
        sent[0].EndedAt.Should().NotBeNull();
        sent[1].Release.Should().BeTrue("etiketler bu turda gitti, kuyruk boş");
        sent[1].UnsentLabelCount.Should().BeNull();
        _server.Calls(FakeSalesServer.OrdersSync).Single().Orders.Orders!.Select(o => o.Id.ToString("N"))
            .Should().BeEquivalentTo(labels);

        var server = _server.Session(Guid.Parse(session))!;
        server.ReleasedAt.Should().NotBeNull("sahibin Release = true'su devreder");
        server.OwnerReplicaId.Should().Be(new ReplicaIdentityRepository(_db).GetReplicaId());

        var local = _sessions.GetById(session)!;
        local.ReleasedAt.Should().Be(server.ReleasedAt!.Value.ToUnixTimeSeconds());
        local.SyncedAt.Should().Be(_clock.Now);
        local.Authority.Should().Be(StreamSessionAuthority.Local, "mod Off: devir görülse de Authority yazılmaz");
        local.EndedAt.Should().Be(1_760_000_900L);

        rounds.Should().Equal(new[] { 4, 0 }, "1 oturum + 3 etiket; ikinci tur gönderecek bir şey bulmaz, döngü biter");
        CountPending().Should().Be(0);
    }

    [Fact]
    public async Task Ilk_gecis_bekletilen_oturumun_gonderimini_saymaz_devir_ikinci_geciste_sayilir()
    {
        var session = NewSession();
        AddLabel(session);
        _sessions.End(session, 1_760_000_900L);

        var result = await _svc.SyncOnceAsync();

        result.Should().Be(new SessionOrderSyncService.SyncResult(1, 1),
            "bekletilen ilk gönderim sayılmaz; devirle onaylanan ikinci gönderim sayılır");
    }

    // ── 2. Release yalnız kuyruk boşken ───────────────────────────────────

    [Fact]
    public async Task Etiketler_gonderilirken_degisen_etiket_kuyrukta_kalirsa_Release_gitmez_sonraki_turda_devredilir()
    {
        var session = NewSession();
        var labels = Enumerable.Range(0, 3).Select(_ => AddLabel(session)).ToList();
        _sessions.End(session, 1_760_000_900L);
        var edited = false;
        DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.OrdersSync || edited) return;
            edited = true;
            _labels.MarkCancelled(new[] { labels[1] }, 1_760_001_000L, "uçuşta");     // F05: onay tutmaz
        };

        await _svc.SyncOnceAsync();

        SentFor(session).Should().ContainSingle().Which.Release.Should().BeFalse(
            "uçuşta değişen etiket kuyrukta kaldı: aynı turda Release gönderilmez");
        _server.Session(Guid.Parse(session))!.ReleasedAt.Should().BeNull();
        _sessions.GetById(session)!.SyncedAt.Should().BeNull("devir görülmedi: oturum kuyrukta");

        await _svc.SyncOnceAsync();

        var sent = SentFor(session);
        sent.Should().HaveCount(3);
        sent[1].Release.Should().BeFalse();
        sent[1].UnsentLabelCount.Should().Be(1, "kalan tek etiket");
        sent[2].Release.Should().BeTrue();
        _server.Order(Guid.Parse(labels[1]))!.Row.CancelledAt.Should().NotBeNull("iptal devirden önce gitti");
        _sessions.GetById(session)!.ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Etiketler_gonderilirken_bitmis_yayina_eklenen_etiket_Release_i_engeller()
    {
        var session = NewSession();
        var first = AddLabel(session);
        _sessions.End(session, 1_760_000_900L);
        string? late = null;
        DuringPush = endpoint =>
        {
            if (endpoint == FakeSalesServer.OrdersSync && late is null) late = AddLabel(session);   // ör. yedek
        };

        await _svc.SyncOnceAsync();

        SentFor(session).Should().ContainSingle().Which.UnsentLabelCount.Should().Be(1);
        _server.Order(Guid.Parse(first)).Should().NotBeNull();
        _server.Order(Guid.Parse(late!)).Should().BeNull("sayımdan sonra eklendi, bu turun partisinde yoktu");
        _sessions.GetById(session)!.ReleasedAt.Should().BeNull();

        await _svc.SyncOnceAsync();

        _server.Order(Guid.Parse(late!)).Should().NotBeNull();
        SentFor(session)[^1].Release.Should().BeTrue();
        _sessions.GetById(session)!.ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Release_gonderimi_ucustayken_kapiya_uyan_yazici_giremez_yanittan_sonra_girer()
    {
        var session = NewSession(endedAt: 1_760_000_900L);
        bool? enteredDuringFlight = null;
        DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.SessionsSync) return;
            // W2'nin yönlendiricisi gibi: önce kapı, sonra yerel yazım.
            if (_gate.TryEnter(session, out var lease))
            {
                using (lease) AddLabel(session);
                enteredDuringFlight = true;
            }
            else enteredDuringFlight = false;
        };

        await _svc.SyncOnceAsync();

        enteredDuringFlight.Should().BeFalse("sayımdan yanıt uygulanana kadar kilit tutulur");
        SentFor(session).Should().ContainSingle().Which.Release.Should().BeTrue();
        _labels.CountUnsyncedBySession(session).Should().Be(0, "kapıya uyan yazım devre karışmadı");
        _gate.TryEnter(session, out var after).Should().BeTrue("yanıt uygulandı: kilit açık");
        after!.Dispose();
        _gate.EntryCount.Should().Be(0);
    }

    // ── 3. devir kapısı ───────────────────────────────────────────────────

    [Fact]
    public async Task Kapi_sunucu_oturumu_tutarken_kapali_yanittan_sonra_acik_baska_oturumlar_etkilenmez()
    {
        var ended = NewSession(endedAt: 1_760_000_900L);
        var live = NewSession();                                     // aynı partide, aktif
        var other = NewSession(endedAt: 1_760_000_800L, synced: true);  // kuyrukta değil
        var hold = _server.Hold(FakeSalesServer.SessionsSync);
        using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        fallback.Token.Register(hold.Release);

        var tour = Task.Run(() => _svc.SyncOnceAsync());
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(10));

        SessionItems(_server.Calls(FakeSalesServer.SessionsSync).Single()).Select(s => s.Id.ToString("N"))
            .Should().BeEquivalentTo(new[] { ended, live }, "ön koşul: istek iki oturumu taşıyor");
        _gate.TryEnter(ended, out var blocked).Should().BeFalse("sunucu oturumu tutarken devir kapısı kapalı");
        blocked.Should().BeNull();
        _gate.TryEnter(live, out var liveLease).Should().BeTrue("aktif oturum devir adayı değil");
        liveLease!.Dispose();
        _gate.TryEnter(other, out var otherLease).Should().BeTrue("başka oturum etkilenmez");
        otherLease!.Dispose();

        hold.Release();
        await tour.WaitAsync(TimeSpan.FromSeconds(10));

        _sessions.GetById(ended)!.ReleasedAt.Should().NotBeNull("ön koşul: yanıt uygulandı");
        _gate.TryEnter(ended, out var opened).Should().BeTrue("yanıt uygulandıktan sonra kapı açık");
        opened!.Dispose();
        _gate.EntryCount.Should().Be(0, "bırakılan kilit bellekte kalmaz");
    }

    [Fact]
    public async Task Gonderim_hatasinda_da_kapi_birakilir()
    {
        var session = NewSession(endedAt: 1_760_000_900L);
        _server.EnqueueStatus(FakeSalesServer.SessionsSync, 500);

        (await _svc.SyncOnceAsync()).Should().Be(default(SessionOrderSyncService.SyncResult));

        _gate.EntryCount.Should().Be(0);
        _sessions.GetById(session)!.SyncedAt.Should().BeNull();
        await _svc.SyncOnceAsync();
        _sessions.GetById(session)!.ReleasedAt.Should().NotBeNull();
    }

    // ── 4. geri alma penceresi ────────────────────────────────────────────

    [Fact]
    public async Task Release_e_ragmen_ReleasedAt_gelmezse_oturum_kuyrukta_kalir_sayilir_sonraki_turda_yeniden_gider()
    {
        var session = NewSession(endedAt: 1_760_000_900L);
        _server.WithheldReleases[Guid.Parse(session)] = 0;

        var first = await _svc.SyncOnceAsync();

        first.SessionsPushed.Should().Be(0, "bekletilen gönderim sayılmaz");
        SentFor(session).Should().ContainSingle().Which.Release.Should().BeTrue();
        var local = _sessions.GetById(session)!;
        local.SyncedAt.Should().BeNull("devir görülmedi: kapanış kaybolmasın diye kuyrukta (M2)");
        local.ReleasedAt.Should().BeNull();
        CountPending().Should().Be(1, "bekleyen sayı (kapanış uyarısı, durum satırı) oturumu görür");

        var rounds = new List<int>();
        await DrainOf(_svc, rounds)(CancellationToken.None);
        rounds.Should().Equal(new[] { 0 }, "kapanıştaki boşaltma bekletilen oturum yüzünden dönmez");
        SentFor(session).Should().HaveCount(2, "sonraki tur yeniden gönderir");

        _server.WithheldReleases.Clear();
        (await _svc.SyncOnceAsync()).SessionsPushed.Should().Be(1);

        SentFor(session).Should().HaveCount(3);
        _sessions.GetById(session)!.ReleasedAt.Should().NotBeNull();
        CountPending().Should().Be(0);
    }

    // ── 5. eski sunucu ────────────────────────────────────────────────────

    [Theory]
    [InlineData("v1-listesi")]
    [InlineData("bos-liste")]
    public async Task Eski_sunucunun_yaniti_bugunku_gibi_onaylanir_oturum_bir_kez_gider(string response)
    {
        _server.SessionsResponse = response == "v1-listesi"
            ? FakeSalesServer.PushResponseKind.V1List
            : FakeSalesServer.PushResponseKind.EmptyList;
        var session = NewSession();
        AddLabel(session);
        _sessions.End(session, 1_760_000_900L);

        var result = await _svc.SyncOnceAsync();

        result.Should().Be(new SessionOrderSyncService.SyncResult(1, 1));
        SentFor(session).Should().ContainSingle("Protocol yok: bugünkü gibi onay, ikinci geçiş yok");
        var local = _sessions.GetById(session)!;
        local.SyncedAt.Should().NotBeNull();
        local.ReleasedAt.Should().BeNull("eski sunucu devir anı bildirmez");
        CountPending().Should().Be(0);
    }

    // ── 6. Authority yalnız mod On'da ─────────────────────────────────────

    [Theory]
    [InlineData(0, StreamSessionAuthority.Local)]
    [InlineData(1, StreamSessionAuthority.Server)]
    public async Task Devir_gorulunce_Authority_yalniz_mod_Onda_server_olur(int mode, string expected)
    {
        Sql("UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });
        var session = NewSession();
        AddLabel(session);
        _sessions.End(session, 1_760_000_900L);

        await _svc.SyncOnceAsync();

        var local = _sessions.GetById(session)!;
        local.ReleasedAt.Should().NotBeNull();
        local.Authority.Should().Be(expected);
        local.SyncedAt.Should().NotBeNull();
    }

    // ── 7. force-end, yayın sürüyor ───────────────────────────────────────

    [Fact]
    public async Task Aktif_yayina_devir_ani_gelirse_yalniz_saklanir_yayin_ve_canli_yol_surer()
    {
        var session = NewSession();
        await _svc.SyncOnceAsync();
        _server.ForceEnd(Guid.Parse(session));                       // başka bilgisayardan force-end
        // Oturum satırı yeniden kuyruğa girer (ör. sonraki bir başlık düzenlemesi); yanıt devir anını taşır.
        Sql("UPDATE StreamSession SET SyncedAt = NULL, Revision = Revision + 1 WHERE Id = @session", new { session });

        await _svc.SyncOnceAsync();

        var local = _sessions.GetById(session)!;
        local.ReleasedAt.Should().NotBeNull("devir anı saklanır");
        local.EndedAt.Should().BeNull("operatör bitirene kadar yayın açık (M18)");
        local.Authority.Should().Be(StreamSessionAuthority.Local);
        local.SyncedAt.Should().NotBeNull("normal onay");
        _sessions.GetActive()!.Id.Should().Be(session);

        var sw = Stopwatch.StartNew();
        var label = AddLabel(session);
        LabelService().MarkPrintedAndRecord(new[] { label });
        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "canlı yol kesilmez");
        await _svc.SyncOnceAsync();
        _server.Order(Guid.Parse(label))!.Row.PrintedAt.Should().NotBeNull();

        _sessions.End(session, 1_760_002_000L);
        await _svc.SyncOnceAsync();
        var ended = _sessions.GetById(session)!;
        ended.SyncedAt.Should().NotBeNull("sunucuda zaten devredilmiş: yanıt devir anını döner");
        ended.Authority.Should().Be(StreamSessionAuthority.Local, "mod Off");
    }

    // ── 8. sahip olmayanın kapanışı ───────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Baska_kopyanin_oturumunda_kapanis_son_durumdur_tek_iz_Authority_dokunulmaz(int mode)
    {
        var session = NewSession();
        await _svc.SyncOnceAsync();
        _server.SetSessionOwner(Guid.Parse(session), Guid.NewGuid());
        Sql("UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });
        _sessions.End(session, 1_760_000_900L);

        var result = await _svc.SyncOnceAsync();

        result.SessionsPushed.Should().Be(1, "son durum: onaylandı");
        var local = _sessions.GetById(session)!;
        local.SyncedAt.Should().NotBeNull();
        local.ReleasedAt.Should().BeNull();
        local.Authority.Should().Be(StreamSessionAuthority.Local, "devralamayan kopya 'devredildi' sanmaz");
        _server.Session(Guid.Parse(session))!.EndedAt.Should().BeNull("sahip olmayanın kapanışı yazılmaz");

        using (var c = _db.Open())
        {
            var rows = c.Query<(string? LabelId, string Kind, string? Reason)>(
                "SELECT LabelId, Kind, Reason FROM SalesConflictLog").ToList();
            rows.Should().ContainSingle().Which.Should().Be(
                ((string?)null, SalesConflictLog.SessionOwnedElsewhere, (string?)SalesConflictLog.SessionReason(session)));
        }

        await _svc.SyncOnceAsync();
        SentFor(session).Should().HaveCount(2, "ilk ekleme + kapanış; son durumdan sonra yeniden gönderim yok");
        _log.Warnings.Should().ContainSingle().Which.Message.Should().Contain(session).And.NotContain("Örnek");
    }

    // ── 9. canlı ritimde seyreltme ────────────────────────────────────────

    [Fact]
    public async Task Canli_ritimde_yalniz_bekletilen_oturum_dakikada_en_cok_bir_kez_gider_sayisi_degismedikce()
    {
        var time = new ManualTime();
        using var hosted = new SessionOrderSyncHostedService(_svc, _sessions,
            new CapturingLogger<SessionOrderSyncHostedService>(),
            SessionOrderSyncHostedService.LiveInterval, SessionOrderSyncHostedService.IdleInterval, time);
        NewSession(synced: true);                                     // canlı yayın: 5 sn ritmi
        var held = NewSession(endedAt: 1_760_000_900L);
        _server.WithheldReleases[Guid.Parse(held)] = 0;
        async Task Tick()
        {
            time.Advance(SessionOrderSyncHostedService.LiveInterval);
            _clock.Now += (long)SessionOrderSyncHostedService.LiveInterval.TotalSeconds;
            (await hosted.TickAsync(CancellationToken.None)).Should().BeTrue("canlı yayında her tık tur");
        }

        await hosted.TickAsync(CancellationToken.None);                         // t = 0
        SentFor(held).Should().ContainSingle();
        var requests = _server.PushCalls.Count + _server.LicenseRequestCount;

        for (var t = 5; t <= 55; t += 5) await Tick();
        SentFor(held).Should().ContainSingle("aynı sayı, 60 sn dolmadı");
        (_server.PushCalls.Count + _server.LicenseRequestCount).Should().Be(requests,
            "kuyrukta yalnız bekletilen oturum var: hiç istek yok");

        await Tick();                                                           // t = 60
        SentFor(held).Should().HaveCount(2, "60 sn doldu");

        await Tick();                                                           // t = 65
        SentFor(held).Should().HaveCount(2);
        var late = AddLabel(held);
        await Tick();                                                           // t = 70: sayı 0 → 1
        SentFor(held).Should().HaveCount(4, "sayı değişti: hemen gider (Release = false), etiket gider, yeniden");
        SentFor(held)[2].UnsentLabelCount.Should().Be(1);
        _server.Order(Guid.Parse(late)).Should().NotBeNull();

        for (var t = 75; t <= 125; t += 5) await Tick();
        SentFor(held).Should().HaveCount(4);
        await Tick();                                                           // t = 130
        SentFor(held).Should().HaveCount(5);
        _sessions.GetById(held)!.SyncedAt.Should().BeNull("sunucu devri hâlâ yazmıyor: kuyrukta");
    }

    [Fact]
    public async Task Canli_ritimde_seyreltilen_oturumun_etiketleri_bu_turda_giderse_ayni_turda_devredilir()
    {
        var time = new ManualTime();
        using var hosted = new SessionOrderSyncHostedService(_svc, _sessions,
            new CapturingLogger<SessionOrderSyncHostedService>(),
            SessionOrderSyncHostedService.LiveInterval, SessionOrderSyncHostedService.IdleInterval, time);
        NewSession(synced: true);                                     // canlı yayın: 5 sn ritmi
        var held = NewSession();
        var label = AddLabel(held);
        _sessions.End(held, 1_760_000_900L);
        _server.EnqueueStatus(FakeSalesServer.OrdersSync, 500);       // ilk turda etiket gidemez

        await hosted.TickAsync(CancellationToken.None);
        SentFor(held).Should().ContainSingle().Which.UnsentLabelCount.Should().Be(1);
        _server.Order(Guid.Parse(label)).Should().BeNull();

        time.Advance(SessionOrderSyncHostedService.LiveInterval);
        _clock.Now += (long)SessionOrderSyncHostedService.LiveInterval.TotalSeconds;
        await hosted.TickAsync(CancellationToken.None);

        _server.Order(Guid.Parse(label)).Should().NotBeNull();
        var sent = SentFor(held);
        sent.Should().HaveCount(2, "ilk geçişte seyreltildi (sayı aynı), etiket gidince son geçişte devredildi");
        sent[1].Release.Should().BeTrue();
        _sessions.GetById(held)!.ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Biriken_bekletilen_kapanislar_canli_yayinin_oturumunu_partiden_disarida_birakmaz()
    {
        for (var i = 0; i < 60; i++)
        {
            var held = NewSession(endedAt: 1_760_000_900L);
            _server.WithheldReleases[Guid.Parse(held)] = 0;
        }
        await _svc.SyncOnceAsync();
        _sessions.GetUnsynced(100).Should().HaveCount(60, "ön koşul: kapanışlar devir bekliyor");

        var live = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(live, "Örnek yayın", 1_760_009_000L, null, new[] { "tiktok" }, null));
        var label = AddLabel(live);

        await _svc.SyncOnceAsync();

        _server.Session(Guid.Parse(live)).Should().NotBeNull("aktif oturum partide önce gelir");
        _server.Order(Guid.Parse(label)).Should().NotBeNull();
        _sessions.GetById(live)!.SyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Bosta_ritimde_ve_dogrudan_turda_bekletilen_oturum_seyreltilmez()
    {
        var held = NewSession(endedAt: 1_760_000_900L);
        _server.WithheldReleases[Guid.Parse(held)] = 0;

        await _svc.SyncOnceAsync();
        await _svc.SyncOnceAsync();
        await _svc.SyncOnceAsync(liveCadence: true);

        SentFor(held).Should().HaveCount(2, "doğrudan tur seyreltmez; canlı ritimdeki tur aynı sayıyı 60 sn içinde atlar");
    }

    // ── 10. çevrimdışı ────────────────────────────────────────────────────

    [Fact]
    public async Task Cevrimdisi_kendi_bitmis_yayininda_duzenleme_calisir_devir_baglanti_gelince_olur()
    {
        var session = NewSession();
        var labels = Enumerable.Range(0, 3).Select(i => AddLabel(session, 100m + i)).ToList();
        _server.Offline = true;
        _sessions.End(session, 1_760_000_900L);

        await _svc.SyncOnceAsync();
        var service = LabelService();
        var sw = Stopwatch.StartNew();
        service.MarkPrintedAndRecord(labels);
        service.Cancel(new[] { labels[0] }, "vazgeçti");
        service.CorrectPrices(new[] { labels[1] }, 90m);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "çevrimdışı yerel düzenleme ağı beklemez (§5.8)");
        (await _svc.SyncOnceAsync()).Should().Be(default(SessionOrderSyncService.SyncResult));
        _sessions.GetById(session)!.Authority.Should().Be(StreamSessionAuthority.Local);
        CountPending().Should().Be(4, "oturum + üç etiket bekliyor");

        _server.Offline = false;
        await _svc.SyncOnceAsync();

        var local = _sessions.GetById(session)!;
        local.ReleasedAt.Should().NotBeNull("bağlantı gelince devir");
        local.SyncedAt.Should().NotBeNull();
        _server.Order(Guid.Parse(labels[0]))!.Row.CancelledAt.Should().NotBeNull();
        _server.Order(Guid.Parse(labels[1]))!.Row.Price.Should().Be(90m);
        labels.Should().AllSatisfy(id => _server.Order(Guid.Parse(id))!.Row.PrintedAt.Should().NotBeNull());
        CountPending().Should().Be(0);
    }
}
