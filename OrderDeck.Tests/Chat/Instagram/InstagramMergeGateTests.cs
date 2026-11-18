using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Birleştirme kapısının PR-A davranışı: etiket + ölçüm, düz geçiş (çoklu hesap tasarımı §8.3.5, §8.4).
///
/// NEDEN: iki ve üzeri Sayfada her Instagram yorumu sohbete bu kapıdan geçer. Kapı bir mesajı kaybeder
/// ya da bekletirse operatör yorumu görmez; etiketi yanlış basarsa yorum yanlış hesaba yazılır; özet
/// satırı yanlış sayarsa hesaplar arası sıralamanın gerekip gerekmediği yanlış rakamla kararlaştırılır.
/// Saat elle ilerletilir; ağ ve bekleme yoktur, kapının yöntemleri doğrudan çağrılır. Kimlik, hesap adı
/// ve izleyici adı literal yazılmaz, üretilir (depo herkese açık).
/// </summary>
public sealed class InstagramMergeGateTests
{
    private readonly ManualTime _time = new();
    private readonly ChatBus _inner = new(ringBufferSize: 50);
    private readonly List<ChatMessage> _received = new();
    private readonly CapturingLogger<InstagramMergeGate> _log = new();

    public InstagramMergeGateTests() => _inner.Subscribe(_received.Add);

    private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Dokuz argümanlı Instagram mesajı: kimlikler ve izleyici adı üretilir;
    /// <paramref name="receivedAt"/> Meta damgasıdır (sn).</summary>
    private static ChatMessage Msg(long receivedAt)
    {
        var viewer = New("izleyici");
        return new ChatMessage(
            New("m"), "instagram", New("c"), viewer, viewer, null, New("yorum"), receivedAt,
            Array.Empty<string>());
    }

    private InstagramMergeGate NewGate(IChatBus? inner = null, TimeProvider? time = null, TimeSpan? holdBudget = null)
        => new(inner ?? _inner, time ?? _time, _log, holdBudget);

    /// <summary>Kapının özet satırları (Information), yazıldıkları sırayla.</summary>
    private string[] Summaries() => _log.Entries
        .Where(e => e.Level == LogLevel.Information
            && e.Message.StartsWith("[InstagramMergeGate] summary", StringComparison.Ordinal))
        .Select(e => e.Message)
        .ToArray();

    /// <summary>Kulvarda verilen sürelerde birer tur koşar; saat her turda o kadar ilerler.</summary>
    private void RunRounds(InstagramMergeGate.Lane lane, IEnumerable<TimeSpan> durations)
    {
        foreach (var duration in durations)
        {
            lane.RoundStarting();
            _time.Advance(duration);
            lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        }
    }

    /// <summary><see cref="Broken"/> iken saat okunamaz: kapının ölçüm adımı fırlatır.</summary>
    private sealed class FaultyTime : TimeProvider
    {
        public bool Broken;

        public override long GetTimestamp()
            => Broken ? throw new InvalidOperationException("saat bozuk") : base.GetTimestamp();

        public override DateTimeOffset GetUtcNow()
            => Broken ? throw new InvalidOperationException("saat bozuk") : base.GetUtcNow();
    }

    /// <summary><see cref="Broken"/> iken yayım fırlatır; değilken mesajı <see cref="Published"/>'a yazar.</summary>
    private sealed class FlakyBus : IChatBus
    {
        public bool Broken;

        public List<ChatMessage> Published { get; } = new();

        public IDisposable Subscribe(Action<ChatMessage> handler) => throw new NotSupportedException();

        public void Publish(ChatMessage message)
        {
            if (Broken) throw new InvalidOperationException("bus bozuk");
            Published.Add(message);
        }

        public IReadOnlyList<ChatMessage> RecentMessages() => Published;
    }

    /// <summary>Her yazımda fırlatan günlük: kapının özet adımını düşürür.</summary>
    private sealed class ThrowingLogger : ILogger
    {
        /// <summary>Information düzeyindeki yazım denemeleri: özet satırı.</summary>
        public int SummaryAttempts { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) SummaryAttempts++;
            throw new InvalidOperationException("günlük bozuk");
        }
    }

    /// <summary>Yalnız tekdüze sayacı okutan saat: duvar saatini okumak ya da zamanlayıcı kurmak fırlatır.</summary>
    private sealed class TimestampOnlyTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("duvar saati okunmamalı");

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("zamanlayıcı kurulmamalı");

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Theory, InlineData(null), InlineData(400)] // PR-A'da tutma yok: holdBudget verilse de davranış aynı
    public void Publish_etiketi_basar_diger_alanlar_ayni_kalir_mesaj_hemen_gecer(int? holdMs)
    {
        using var gate = NewGate(holdBudget: holdMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
        var (handle, pageName, first, second) = (New("@u"), New("Sayfa"), Msg(1_000), Msg(1_001));

        gate.AddLane(New("p"), handle).Publish(first);
        gate.AddLane(New("p"), pageName).Publish(second); // etiket gösterime hazır: kapı "@" eklemez

        first.SourceAccount.Should().BeNull();
        _received.Should().Equal(first with { SourceAccount = handle }, second with { SourceAccount = pageName });
        InstagramMergeGate.HoldBudget.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Subscribe_ve_RecentMessages_ic_bus_a_devredilir()
    {
        using var gate = NewGate();
        var lane = gate.AddLane(New("p"), New("@u"));
        var seen = new List<ChatMessage>();
        using var sub = lane.Subscribe(seen.Add);
        var direct = Msg(1_000);

        _inner.Publish(direct); // kulvardan geçmeyen mesaj

        seen.Should().Equal(direct);
        lane.RecentMessages().Should().Equal(_inner.RecentMessages());
    }

    [Fact]
    public void Ters_sira_sayaci_yalniz_geri_giden_damgada_artar()
    {
        var gate = NewGate();
        var (a, b) = (gate.AddLane(New("p"), New("@u")), gate.AddLane(New("p"), New("@u")));

        a.Publish(Msg(100));
        b.Publish(Msg(100)); // eşit damga: ters sıra değil
        a.Publish(Msg(105));
        b.Publish(Msg(102)); // 3 sn geri
        b.Publish(Msg(104)); // 1 sn geri (en büyük damga 105)
        a.Publish(Msg(106)); // ileri
        gate.Dispose();

        Summaries().Should().ContainSingle().Which.Should().Contain("inversions=2 maxGapSeconds=3 ");
    }

    [Fact]
    public void Olcum_adimi_firlatsa_da_mesaj_ic_bus_a_ulasir()
    {
        var time = new FaultyTime();
        using var gate = NewGate(time: time); // Dispose da fırlatmamalı
        var lane = gate.AddLane(New("p"), New("@u"));
        var m = Msg(1_000);
        time.Broken = true;

        lane.Publish(m);
        FluentActions.Invoking(() =>
        {
            lane.RoundStarting();
            lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        }).Should().NotThrow();

        _received.Should().ContainSingle().Which.Id.Should().Be(m.Id);
    }

    [Fact]
    public void Ic_bus_firlatirsa_istisna_cagirana_cikar_sonraki_mesajlar_akar()
    {
        var inner = new FlakyBus { Broken = true };
        using var gate = NewGate(inner);
        var (a, b) = (gate.AddLane(New("p"), New("@u")), gate.AddLane(New("p"), New("@u")));

        FluentActions.Invoking(() => a.Publish(Msg(100))).Should().Throw<InvalidOperationException>();
        inner.Broken = false;
        b.Publish(Msg(101));
        a.Publish(Msg(102));

        inner.Published.Select(m => m.ReceivedAt).Should().Equal(101, 102);
    }

    [Fact]
    public void Ozet_dakikada_bir_olay_gelince_yazilir_Dispose_son_pencereyi_bir_kez_yazar()
    {
        var gate = NewGate();
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);

        foreach (var ms in new[] { 400, 100, 300, 200 })
        {
            lane.RoundStarting();
            _time.Advance(TimeSpan.FromMilliseconds(ms));
            lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        }

        Summaries().Should().BeEmpty(); // dakika dolmadı

        _time.Advance(TimeSpan.FromMinutes(1));
        lane.Publish(Msg(100)); // saat denetimi olay gelince yapılır

        Summaries().Should().Equal(
            $"[InstagramMergeGate] summary: inversions=0 maxGapSeconds=0 lanes: {label} rounds=4 p50Ms=200 p95Ms=400");

        gate.Dispose();
        gate.Dispose();

        Summaries().Should().HaveCount(2); // Dispose bir kez yazar
        Summaries()[1].Should().EndWith($"lanes: {label} rounds=0 p50Ms=0 p95Ms=0"); // pencere sıfırlandı

        lane.Publish(Msg(101));

        _received.Should().HaveCount(2);
        Summaries().Should().HaveCount(2); // Dispose'tan sonra da düz geçer, özet yazılmaz
    }

    [Fact]
    public void Complete_sonrasi_mesaj_yine_etiketlenir_kulvar_son_ozetten_sonra_duser()
    {
        var gate = NewGate();
        var (gone, stays) = (New("@u"), New("@u"));
        var closing = gate.AddLane(New("p"), gone);
        gate.AddLane(New("p"), stays);

        closing.Complete();
        closing.Complete();
        closing.Publish(Msg(100));

        _received.Should().ContainSingle().Which.SourceAccount.Should().Be(gone);

        _time.Advance(TimeSpan.FromMinutes(1));
        closing.Publish(Msg(101)); // 1. özet: kapanan kulvar son kez

        Summaries().Should().ContainSingle().Which.Should().Contain(gone).And.Contain(stays);

        gate.Dispose(); // 2. özet: yalnız açık kulvar

        Summaries()[1].Should().Contain(stays).And.NotContain(gone);
    }

    [Fact]
    public void Dispose_sonrasi_mesaj_yine_etiketlenir_olcum_ve_ozet_yoktur()
    {
        var gate = NewGate();
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);
        lane.Publish(Msg(105));
        gate.Dispose();
        var late = Msg(100);

        // Pencere her olaydan önce dolar: kapı açık olsaydı üç olay da özet yazdırırdı.
        _time.Advance(InstagramMergeGate.SummaryInterval);
        lane.Publish(late);
        _time.Advance(InstagramMergeGate.SummaryInterval);
        lane.RoundStarting();
        _time.Advance(InstagramMergeGate.SummaryInterval);
        lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);

        _received.Should().HaveCount(2);
        _received[1].Should().Be(late with { SourceAccount = label });
        Summaries().Should().ContainSingle(); // yalnız Dispose'un yazdığı satır
    }

    [Fact]
    public void Dispose_kulvar_hic_acilmamisken_de_ozeti_bir_kez_yazar()
    {
        var gate = NewGate();

        gate.Dispose();
        gate.Dispose();

        Summaries().Should().ContainSingle().Which.Should()
            .StartWith("[InstagramMergeGate] summary: inversions=0 maxGapSeconds=0 lanes:");
    }

    [Fact]
    public void Ozet_adimi_firlatsa_da_mesaj_etiketiyle_basilir_tur_bildirimi_ve_Dispose_firlatmaz()
    {
        var log = new ThrowingLogger();
        var gate = new InstagramMergeGate(_inner, _time, log);
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);
        var m = Msg(100);

        // Pencere her olaydan önce dolar: olay özeti yazmayı dener, günlük fırlatır.
        _time.Advance(InstagramMergeGate.SummaryInterval);
        FluentActions.Invoking(() => lane.Publish(m)).Should().NotThrow();
        _time.Advance(InstagramMergeGate.SummaryInterval);
        FluentActions.Invoking(lane.RoundStarting).Should().NotThrow();
        _time.Advance(InstagramMergeGate.SummaryInterval);
        FluentActions.Invoking(() => lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null))
            .Should().NotThrow();
        FluentActions.Invoking(gate.Dispose).Should().NotThrow();

        log.SummaryAttempts.Should().Be(4); // dört adım da özeti denedi
        _received.Should().Equal(m with { SourceAccount = label });
    }

    [Fact]
    public void Etiketleme_adimi_firlatsa_da_mesaj_etiketsiz_ic_bus_a_iletilir()
    {
        var inner = new FlakyBus();
        using var gate = NewGate(inner);
        var lane = gate.AddLane(New("p"), New("@u"));

        // Etiketlemeyi düşüren tek girdi null'dır: kapı onu da kendi fırlatmadan iç bus'a bırakır.
        FluentActions.Invoking(() => lane.Publish(null!)).Should().NotThrow();

        inner.Published.Should().HaveCount(1);
        inner.Published[0].Should().BeNull();
    }

    [Fact]
    public void Ic_bus_un_basamadigi_mesaj_ters_sira_olcumune_girmez()
    {
        var inner = new FlakyBus { Broken = true };
        var gate = NewGate(inner);
        var lane = gate.AddLane(New("p"), New("@u"));

        FluentActions.Invoking(() => lane.Publish(Msg(200))).Should().Throw<InvalidOperationException>();
        inner.Broken = false;
        lane.Publish(Msg(150)); // 200 basılamadı: kapının bastığı en büyük damga o değil
        gate.Dispose();

        Summaries().Should().ContainSingle().Which.Should().Contain("inversions=0 maxGapSeconds=0 ");
    }

    [Fact]
    public void Ozet_kulvarlari_AddLane_sirasiyla_ve_noktali_virgulle_yazar()
    {
        var gate = NewGate();
        var (first, second) = (New("@u"), New("Sayfa"));
        var a = gate.AddLane(New("p"), first);
        var b = gate.AddLane(New("p"), second);

        RunRounds(b, [TimeSpan.FromMilliseconds(120)]); // önce ikinci kulvar çalışır: sıra yine AddLane sırası
        RunRounds(a, [TimeSpan.FromMilliseconds(80)]);
        gate.Dispose();

        Summaries().Should().Equal(
            "[InstagramMergeGate] summary: inversions=0 maxGapSeconds=0 lanes: "
            + $"{first} rounds=1 p50Ms=80 p95Ms=80; {second} rounds=1 p50Ms=120 p95Ms=120");
    }

    [Fact]
    public void Ozeti_tur_bildirimleri_de_tetikler_olay_ozetten_once_islenir()
    {
        using var gate = NewGate();
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);

        _time.Advance(InstagramMergeGate.SummaryInterval);
        lane.RoundStarting(); // 1. özet: tur yeni başladı

        Summaries().Should().ContainSingle().Which.Should().EndWith($"lanes: {label} rounds=0 p50Ms=0 p95Ms=0");

        _time.Advance(InstagramMergeGate.SummaryInterval);
        lane.RoundCompleted(InstagramPollKind.NoBroadcast, null, TimeSpan.Zero, null); // 2. özet: biten tur bu satırda

        Summaries().Should().HaveCount(2);
        Summaries()[1].Should().EndWith($"lanes: {label} rounds=1 p50Ms=60000 p95Ms=60000");
    }

    [Fact]
    public void Pencere_tam_bir_dakikada_dolar_sayaclar_sifirlanir_en_buyuk_damga_kalir()
    {
        InstagramMergeGate.SummaryInterval.Should().Be(TimeSpan.FromMinutes(1));
        var gate = NewGate();
        var (a, b) = (gate.AddLane(New("p"), New("@u")), gate.AddLane(New("p"), New("@u")));
        a.Publish(Msg(105));
        b.Publish(Msg(101)); // 1. pencere: 4 sn geri

        _time.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        a.RoundStarting();

        Summaries().Should().BeEmpty(); // pencere bir tik eksik

        _time.Advance(TimeSpan.FromTicks(1));
        a.RoundStarting();

        Summaries().Should().ContainSingle().Which.Should().Contain("inversions=1 maxGapSeconds=4 ");

        b.Publish(Msg(103)); // 2. pencere: sayaçlar sıfırdan başlar, en büyük damga hâlâ 105 (2 sn geri)
        gate.Dispose();

        Summaries()[1].Should().Contain("inversions=1 maxGapSeconds=2 ");
    }

    [Fact]
    public void Her_sonuc_turu_tur_sayilir()
    {
        var gate = NewGate();
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);
        var kinds = Enum.GetValues<InstagramPollKind>();

        foreach (var kind in kinds)
        {
            lane.RoundStarting();
            _time.Advance(TimeSpan.FromMilliseconds(250));
            lane.RoundCompleted(kind, null, TimeSpan.Zero, null);
        }

        gate.Dispose();

        Summaries().Should().ContainSingle().Which.Should()
            .EndWith($"lanes: {label} rounds={kinds.Length} p50Ms=250 p95Ms=250");
    }

    [Theory]
    [InlineData(new[] { 70_000 }, 70, 70)] // tek tur: iki yüzdelik de o tur
    [InlineData(new[] { 50_000, 10_000, 40_000, 20_000, 30_000 }, 30, 50)] // ceil(0,50 × 5) = 3. ve ceil(0,95 × 5) = 5. tur
    [InlineData(new[] { 1_400, 1_600 }, 1, 2)] // tam milisaniyeye yuvarlanır: 1,4 → 1 ve 1,6 → 2
    public void Yuzdelik_en_yakin_siradaki_turdur_tam_milisaniyeye_yuvarlanir(int[] roundsMicroseconds, int p50, int p95)
    {
        var gate = NewGate();
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);

        RunRounds(lane, roundsMicroseconds.Select(us => TimeSpan.FromMicroseconds(us)));
        gate.Dispose();

        Summaries().Should().ContainSingle().Which.Should()
            .EndWith($"lanes: {label} rounds={roundsMicroseconds.Length} p50Ms={p50} p95Ms={p95}");
    }

    [Fact]
    public void Yirmi_turda_p50_onuncu_p95_on_dokuzuncu_degerdir()
    {
        var gate = NewGate();
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);

        // 200, 190, …, 10 ms: büyükten küçüğe koşulur, yüzdelik sıralanmış sürelerden okunur.
        RunRounds(lane, Enumerable.Range(1, 20).Reverse().Select(i => TimeSpan.FromMilliseconds(i * 10)));
        gate.Dispose();

        Summaries().Should().ContainSingle().Which.Should().EndWith($"lanes: {label} rounds=20 p50Ms=100 p95Ms=190");
    }

    [Fact]
    public void Sure_ve_pencere_tekduze_sayactan_olculur_duvar_saati_okunmaz_zamanlayici_kurulmaz()
    {
        var time = new TimestampOnlyTime();
        var gate = NewGate(time: time);
        var label = New("@u");
        var lane = gate.AddLane(New("p"), label);

        lane.RoundStarting();
        time.Advance(TimeSpan.FromMilliseconds(150));
        lane.RoundCompleted(InstagramPollKind.Ok, null, TimeSpan.Zero, null);
        time.Advance(InstagramMergeGate.SummaryInterval);
        lane.Publish(Msg(100));
        gate.Dispose();

        Summaries().Should().HaveCount(2);
        Summaries()[0].Should().EndWith($"lanes: {label} rounds=1 p50Ms=150 p95Ms=150");
    }

    [Fact]
    public async Task Eszamanli_yayimda_mesaj_kaybolmaz_olculen_sira_basilan_siradir()
    {
        const int perLane = 5_000;
        var published = new List<long>(); // damgalar, iç bus'a varış sırasıyla
        var inner = new ChatBus(ringBufferSize: 8);
        using var sub = inner.Subscribe(m =>
        {
            lock (published) published.Add(m.ReceivedAt);
        });
        var gate = NewGate(inner);
        var lanes = Enumerable.Range(0, 4).Select(_ => gate.AddLane(New("p"), New("@u"))).ToArray();

        // Damga yayımdan hemen önce ortak sayaçtan alınır: hesap içinde hep ileri gider, hesaplar arasında
        // da neredeyse hep. Ölçülen sıra basılan sıradan bir kez bile saparsa sayılar tutmaz.
        long clock = 0;
        using var start = new ManualResetEventSlim();
        var writers = lanes.Select(lane => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < perLane; i++)
                lane.Publish(Msg(Interlocked.Increment(ref clock)));
        })).ToArray();

        start.Set();
        await Task.WhenAll(writers).WaitAsync(TestPatience.Upper);
        gate.Dispose();

        // Beklenen sayılar iç bus'ın GÖRDÜĞÜ sıradan yeniden hesaplanır.
        var (newest, inversions, maxGap) = (long.MinValue, 0, 0L);
        foreach (var stamp in published)
        {
            if (stamp < newest)
            {
                inversions++;
                maxGap = Math.Max(maxGap, newest - stamp);
            }
            else
            {
                newest = stamp;
            }
        }

        published.Should().HaveCount(lanes.Length * perLane);
        Summaries().Should().ContainSingle().Which.Should()
            .StartWith($"[InstagramMergeGate] summary: inversions={inversions} maxGapSeconds={maxGap} lanes: ");
    }
}
