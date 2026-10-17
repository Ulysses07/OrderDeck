using System.Diagnostics;
using System.Reflection;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Gönderim ritmi (sahip kararı Y9, spec 2026-10-13 §11; W1-06): canlı yayında ≈5 sn, aktif yayın yokken en çok
/// 60 sn'de bir tur; gönderilecek bir şey yoksa hiç istek; ritim yayını başlatma ve bitirmeden sonra bir tıkta
/// değişir; çevrimdışı turlar dakikada en çok bir uyarı yazar ve canlı yazımı etkilemez. Tıklar elle ilerletilen bir
/// zamanla <c>TickAsync</c>'ten sürülür; zamanlayıcı döngüsü ayrıca gerçek zamanla sınanır. Sunucu
/// <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class SessionOrderSyncCadenceTests : IDisposable
{
    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    /// <summary>Gönderim servisinin saati (uyarı seyreltme); tıklarla birlikte ilerletilir.</summary>
    private sealed class Clock : IClock
    {
        public long Now = 1_760_000_000L;
        public long UnixNow() => Now;
    }

    /// <summary>Elle ilerletilen zaman: zaman damgası tik cinsinden.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.FromDays(1).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static readonly TimeSpan Tick = SessionOrderSyncHostedService.LiveInterval;

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly License _license;
    private readonly Clock _clock = new();
    private readonly ManualTime _time = new();
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly CapturingLogger<SessionOrderSyncService> _log = new();
    private readonly CapturingLogger<SessionOrderSyncHostedService> _hostedLog = new();
    private readonly SessionOrderSyncService _svc;
    private readonly SessionOrderSyncHostedService _hosted;
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private long _addedAt = 1_760_000_100L;

    public SessionOrderSyncCadenceTests()
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
            new HandoverGate(), new SalesPushApply(_db), _clock, _log);
        _hosted = new SessionOrderSyncHostedService(_svc, _sessions, _hostedLog,
            Tick, SessionOrderSyncHostedService.IdleInterval, _time);
    }

    public void Dispose()
    {
        _hosted.Dispose();
        _db.Dispose();
        _server.Dispose();
    }

    private string StartStream()
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Örnek yayın", _clock.Now, null, new[] { "tiktok" }, null));
        return id;
    }

    private string AddLabel(string sessionId)
    {
        var id = Guid.NewGuid().ToString("N");
        _labels.Insert(new Label(id, sessionId, _customerId, "tiktok", "ornek_alici", "alıyorum", "A1", 100m,
            _addedAt++, null, DisplayName: "Örnek Alıcı"));
        return id;
    }

    /// <summary>Zamanı bir tık ilerletir ve tıkı sürer.</summary>
    private Task<bool> NextTickAsync()
    {
        _time.Advance(Tick);
        _clock.Now += (long)Tick.TotalSeconds;
        return _hosted.TickAsync(CancellationToken.None);
    }

    private int AllRequests => _server.PushCalls.Count + _server.LicenseRequestCount + _server.SalesRequestCount;

    [Fact]
    public void Varsayilan_ritim_canli_yayinda_5_sn_bosta_60_sn()
    {
        SessionOrderSyncHostedService.LiveInterval.Should().Be(TimeSpan.FromSeconds(5));
        SessionOrderSyncHostedService.IdleInterval.Should().Be(TimeSpan.FromSeconds(60));

        using var hosted = new SessionOrderSyncHostedService(_svc, _sessions,
            NullLogger<SessionOrderSyncHostedService>.Instance);
        Field<TimeSpan>(hosted, "_tick").Should().Be(TimeSpan.FromSeconds(5));
        Field<TimeSpan>(hosted, "_idleInterval").Should().Be(TimeSpan.FromSeconds(60));
        Field<TimeProvider>(hosted, "_time").Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public async Task Canli_yayinda_bekleyen_etiket_bir_tikta_gider()
    {
        var session = StartStream();
        (await _hosted.TickAsync(CancellationToken.None)).Should().BeTrue("açılıştaki ilk tık");

        for (var i = 0; i < 3; i++)
        {
            var label = AddLabel(session);
            (await NextTickAsync()).Should().BeTrue("canlı yayında her tık tur");
            _server.Order(Guid.Parse(label)).Should().NotBeNull($"{i + 1}. etiket 5 sn içinde gitmeli");
        }
    }

    [Fact]
    public async Task Canli_yayinda_bos_kuyruk_on_tikta_hic_istek_atmaz_lisans_cozumu_dahil()
    {
        // Canlı yayın zaten gönderilmiş, etiket yok: servis lisansı hiç çözmemiş (önbellek boş).
        _sessions.Insert(new StreamSession(Guid.NewGuid().ToString("N"), "Örnek yayın", _clock.Now, null,
            new[] { "tiktok" }, null, SyncedAt: _clock.Now));
        _sessions.GetActive().Should().NotBeNull("ön koşul: canlı yayın");

        for (var i = 0; i < 10; i++)
            (await NextTickAsync()).Should().BeTrue("canlı yayında her tık tur — ama gönderecek bir şey yok");

        AllRequests.Should().Be(0, "gönderilecek bir şey yoksa hiçbir istek yok, lisans çözümü dahil (Y9)");
        _log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Canli_yayinda_kuyruk_bosalinca_sonraki_tiklar_istek_atmaz()
    {
        var session = StartStream();
        AddLabel(session);
        await _hosted.TickAsync(CancellationToken.None);
        _server.Calls(FakeSalesServer.OrdersSync).Should().ContainSingle("ön koşul: kuyruk boşaldı");
        var before = AllRequests;

        for (var i = 0; i < 10; i++)
            (await NextTickAsync()).Should().BeTrue();

        AllRequests.Should().Be(before);
    }

    [Fact]
    public async Task Aktif_yayin_yokken_60_snde_en_cok_bir_tur()
    {
        var ended = StartStream();
        _sessions.End(ended, _clock.Now);
        (await _hosted.TickAsync(CancellationToken.None)).Should().BeTrue("açılıştaki ilk tık");
        var label = AddLabel(ended);

        var tours = new List<bool>();
        for (var i = 1; i <= 11; i++) tours.Add(await NextTickAsync());       // +5 … +55 sn

        tours.Should().AllSatisfy(t => t.Should().BeFalse("60 sn dolmadı"));
        _server.Order(Guid.Parse(label)).Should().BeNull();

        (await NextTickAsync()).Should().BeTrue("+60 sn");
        _server.Order(Guid.Parse(label)).Should().NotBeNull();
        (await NextTickAsync()).Should().BeFalse("sayaç sıfırlandı");
    }

    [Fact]
    public async Task Ritim_yayin_baslayinca_ve_bitince_bir_tikta_degisir()
    {
        (await _hosted.TickAsync(CancellationToken.None)).Should().BeTrue("açılıştaki ilk tık (t0)");
        (await NextTickAsync()).Should().BeFalse("t0+5: boşta");

        var session = StartStream();                                            // t0+5 ile t0+10 arası
        var first = AddLabel(session);
        (await NextTickAsync()).Should().BeTrue("t0+10: yayın başladı, 60 sn beklenmez");
        _server.Order(Guid.Parse(first)).Should().NotBeNull();
        (await NextTickAsync()).Should().BeTrue("t0+15: canlı");

        var last = AddLabel(session);
        _sessions.End(session, _clock.Now);                                     // yayın bitti
        (await NextTickAsync()).Should().BeTrue("t0+20: yayının bittiği görülen tık — kapanış ve son etiket");
        _server.Order(Guid.Parse(last)).Should().NotBeNull();
        _sessions.GetById(session)!.SyncedAt.Should().NotBeNull("kapanış da gitti");

        var idle = new List<bool>();
        for (var i = 0; i < 11; i++) idle.Add(await NextTickAsync());          // t0+25 … t0+75
        idle.Should().AllSatisfy(t => t.Should().BeFalse("yayın bitti: boşta ritim"));
        (await NextTickAsync()).Should().BeTrue("t0+80: son turdan 60 sn sonra");
    }

    [Fact]
    public async Task Yayin_bitince_devir_bekleyen_kuyruk_gonderildikce_canli_ritim_surer_devirden_sonra_bosta()
    {
        var session = StartStream();
        (await _hosted.TickAsync(CancellationToken.None)).Should().BeTrue("açılıştaki ilk tık");
        // "Hepsini bas ve yayını bitir": kuyrukta 250 gönderilmemiş etiket varken yayın biter.
        var labels = Enumerable.Range(0, 250).Select(_ => AddLabel(session)).ToList();
        _sessions.End(session, _clock.Now);

        (await NextTickAsync()).Should().BeTrue("yayının bittiği görülen tık");
        _sessions.GetById(session)!.ReleasedAt.Should().BeNull("turda 100 etiket: 150 kaldı, devir bekliyor");
        (await NextTickAsync()).Should().BeTrue("kendi oturumu devir beklerken ve kuyruk gittikçe ritim canlı kalır");
        (await NextTickAsync()).Should().BeTrue();

        labels.Should().OnlyContain(id => _server.Order(Guid.Parse(id)) != null);
        _sessions.GetById(session)!.ReleasedAt.Should().NotBeNull("devir ≈15 sn'de; dakikalar sonra değil");
        _server.Session(Guid.Parse(session))!.ReleasedAt.Should().NotBeNull();
        (await NextTickAsync()).Should().BeFalse("devir bitti: boşta ritim");
    }

    [Fact]
    public async Task Devir_bekleyen_oturumun_etiketleri_gidemiyorsa_ritim_bosta_kalir()
    {
        var session = StartStream();
        (await _hosted.TickAsync(CancellationToken.None)).Should().BeTrue("açılıştaki ilk tık");
        AddLabel(session);
        _sessions.End(session, _clock.Now);
        _server.Offline = true;                                                // etiket kalıcı olarak gidemiyor

        (await NextTickAsync()).Should().BeTrue("yayının bittiği görülen tık");
        var tours = 0;
        for (var i = 0; i < 12; i++)                                           // +10 … +65 sn
            if (await NextTickAsync()) tours++;
        tours.Should().Be(1, "ilerleme yokken devir beklemesi ritmi canlı tutmaz: son turdan 60 sn sonra bir tur");
    }

    [Fact]
    public async Task Cevrimdisi_turlar_disari_sizmaz_dakikada_en_cok_bir_uyari_yazar_canli_yazim_etkilenmez()
    {
        var session = StartStream();
        var pending = AddLabel(session);
        _server.Offline = true;

        var tours = 0;
        for (var i = 0; i < 12; i++)                                           // +5 … +60 sn
            if (await NextTickAsync()) tours++;

        tours.Should().Be(12, "canlı yayında her tık tur");
        _log.Warnings.Should().ContainSingle("çevrimdışı: bir dakikalık pencerede en çok bir uyarı");
        _hostedLog.Warnings.Should().BeEmpty("HTTP hatasını servis yutar; tur hata vermez");
        _labels.HasUnsynced().Should().BeTrue();
        new LabelRepository(_db).GetById(pending)!.SyncedAt.Should().BeNull();

        var labelService = new LabelService(_labels,
            new CustomerService(new CustomerRepository(_db), _sessions, _labels, _clock), _db, _clock);
        var sw = Stopwatch.StartNew();
        labelService.MarkPrintedAndRecord(new[] { pending });
        sw.ElapsedMilliseconds.Should().BeLessThan(1000, "canlı yol ağı beklemez");

        await NextTickAsync();                                                  // ilk uyarıdan 60 sn sonra
        _log.Warnings.Should().HaveCount(2, "bir dakika dolunca yeni uyarı");

        _server.Offline = false;
        await NextTickAsync();
        _server.Order(Guid.Parse(pending))!.Row.PrintedAt.Should().NotBeNull("bağlantı gelince bekleyen gider");
    }

    [Fact]
    public async Task Tur_hatasi_zamanlayiciyi_durdurmaz_uyari_dakikada_en_cok_bir()
    {
        StartStream();
        using (var c = _db.Open()) c.Execute("ALTER TABLE Label RENAME TO LabelGone");

        for (var i = 0; i < 12; i++)                                           // +5 … +60 sn
            (await NextTickAsync()).Should().BeTrue("hata tıkı sızdırmaz, ritim sürer");

        _hostedLog.Warnings.Should().ContainSingle("yerel hata: bir dakikalık pencerede en çok bir uyarı");
        await NextTickAsync();                                                  // ilk uyarıdan 60 sn sonra
        _hostedLog.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public async Task Zamanlayici_dongusu_canli_yayinda_kisa_aralikla_bosta_uzun_aralikla_tur_atar()
    {
        var session = StartStream();
        using var hosted = new SessionOrderSyncHostedService(_svc, _sessions,
            NullLogger<SessionOrderSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1), TimeProvider.System);
        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        try
        {
            var live = AddLabel(session);
            await WaitUntilAsync(() => _server.Order(Guid.Parse(live)) is not null);

            _sessions.End(session, _clock.Now);
            // Kapanışı götüren tur oturumu onayladıktan sonra etiketleri okur: boşta etiket o turun okumasından önce
            // yazılırsa aynı turda giderdi. Döngü boşta ritme oturana (yayının bittiği tık geçti) ve süren tur bitene
            // kadar beklenir.
            await WaitUntilAsync(() => _sessions.GetById(session)!.SyncedAt is not null
                && !Field<bool>(hosted, "_wasLive") && !Field<bool>(hosted, "_handingOver"));
            await Task.Delay(100);
            await _svc.SyncOnceAsync();

            var idle = AddLabel(session);
            await Task.Delay(400);
            _server.Order(Guid.Parse(idle)).Should().BeNull("aktif yayın yok: 1 saatlik boşta aralık");
        }
        finally
        {
            cts.Cancel();
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            sw.Elapsed.Should().BeLessThan(TestPatience.Upper, "koşul süresinde gerçekleşmedi");
            await Task.Delay(20);
        }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
