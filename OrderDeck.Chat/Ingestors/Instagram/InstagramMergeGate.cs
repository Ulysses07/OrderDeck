using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Chat;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// İki ve üzeri Sayfa bağlıyken hesap poller'ları ile sohbet bus'ının arasında durur. Her hesabın
/// poller'ı kendi kulvarına (<see cref="Lane"/>) yazar; kulvar mesajı hangi hesaptan geldiğiyle
/// etiketler (<see cref="ChatMessage.SourceAccount"/>) ve HEMEN iç bus'a basar. Kapı mesaj tutmaz,
/// bekletmez: hesaplar arası sıra varış sırasıdır.
///
/// <para><b>Ölçüm.</b> Kapı iki şeyi sayar ve tek Information satırında özetler: hesap başına
/// yoklama turlarının süresi (<see cref="Lane.RoundStarting"/> → <see cref="Lane.RoundCompleted"/>)
/// ve <b>ters sıra</b>: basılan mesajın Meta damgası, kapının o ana dek bastığı en büyük damgadan
/// küçükse. Hesap içi damga geri gitmediği için her ters sıra hesaplar arasıdır. Hesaplar arası
/// sıralamanın gerekip gerekmediği bu rakamlarla kararlaştırılır.</para>
///
/// <para><b>Özet, zamanlayıcısız.</b> Bir olay (yayım ya da tur bildirimi) işlendikten sonra pencere
/// <see cref="SummaryInterval"/> kadar dolmuşsa satır yazılır ve pencere sayaçları sıfırlanır;
/// <see cref="Dispose"/> son pencereyi bir kez yazar. Olay gelmeyen kapı satır yazmaz. Satırda
/// yalnız hesap etiketleri ve sayılar bulunur; izleyici adı ve yorum metni girmez.</para>
///
/// <para><b>Mesaj kaybı yok.</b> Kapının kendi adımı (etiketleme, ölçüm, özet) fırlatırsa istisna
/// yutulur ve mesaj yine iç bus'a basılır (etiketsiz de olsa); tur bildirimleri ve
/// <see cref="Dispose"/> da bu yüzden fırlatmaz. İç bus'ın fırlattığı istisna ise yutulmaz, çağırana
/// çıkar; basılamayan mesaj ölçüme girmez.</para>
///
/// <para><b>Tek kilit.</b> Kulvar listesi, pencere sayaçları ve iç bus'a yayım aynı kilit
/// altındadır: ölçülen sıra basılan sıradır. Poller'lar ayrı iş parçacıklarından yazar.</para>
/// </summary>
public sealed class InstagramMergeGate : IDisposable
{
    /// <summary>Mesaj tutma bütçesi. Sıfır: kapı hiçbir mesajı tutmaz.</summary>
    internal static readonly TimeSpan HoldBudget = TimeSpan.Zero;

    /// <summary>Özet penceresi: olay geldiğinde pencere bu kadar dolmuşsa satır yazılır.</summary>
    internal static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _sync = new();
    private readonly IChatBus _inner;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <summary>Özete giren kulvarlar, <see cref="AddLane"/> sırasıyla.</summary>
    private readonly List<Lane> _lanes = new();

    /// <summary>Pencerenin başladığı an (<see cref="TimeProvider.GetTimestamp"/>).</summary>
    private long _windowStartedAt;

    /// <summary>Kapının bastığı en büyük Meta damgası (sn). Pencereyle sıfırlanmaz: ters sıra,
    /// önceki pencerede basılmış mesaja göre de ters sıradır.</summary>
    private long _newestStamp = long.MinValue;

    private int _inversions;
    private long _maxGapSeconds;
    private bool _disposed;

    /// <param name="inner">Etiketlenen mesajların basıldığı bus.</param>
    /// <param name="time">Tur süresinin ve özet penceresinin saati.</param>
    /// <param name="log">Özet satırının yazıldığı günlük; kategorisini çağıran seçer.</param>
    /// <param name="holdBudget">Okunmaz: kapı mesaj tutmaz (<see cref="HoldBudget"/>). Verilse de
    /// davranış aynıdır.</param>
    public InstagramMergeGate(IChatBus inner, TimeProvider time, ILogger log, TimeSpan? holdBudget = null)
    {
        _inner = inner;
        _time = time;
        _log = log;
        _windowStartedAt = time.GetTimestamp();
    }

    /// <summary>Bir hesap için kulvar açar. Kulvar o hesabın poller'ına hem bus hem tur gözlemcisi
    /// olarak verilir.</summary>
    /// <param name="accountKey">Kulvarın hesabı (PageId). Özet satırına girmez.</param>
    /// <param name="label">Gösterime hazır hesap etiketi: mesaja ve özet satırına olduğu gibi
    /// yazılır, kapı "@" eklemez.</param>
    public Lane AddLane(string accountKey, string label)
    {
        var lane = new Lane(this, label);
        lock (_sync) _lanes.Add(lane);
        return lane;
    }

    /// <summary>
    /// Son pencerenin özetini yazar (pencere boş ya da kulvar hiç açılmamış olsa da) ve ölçümü
    /// kapatır. İkinci çağrı bir şey yazmaz. Sonrasında gelen mesaj yine etiketlenip basılır; ölçüm
    /// ve özet yoktur.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                WriteSummary();
            }
            catch (Exception ex)
            {
                LogSwallowed(ex);
            }
        }
    }

    /// <summary>Basılan mesajın damgasını ters sıra ölçümüne işler. Eşit damga ters sıra değildir:
    /// Meta damgası saniye çözünürlüklüdür, aynı saniyedeki iki yorumun sırası bilinemez.</summary>
    private void NotePublished(long receivedAt)
    {
        if (receivedAt < _newestStamp)
        {
            _inversions++;
            _maxGapSeconds = Math.Max(_maxGapSeconds, _newestStamp - receivedAt);
        }
        else
        {
            _newestStamp = receivedAt;
        }
    }

    /// <summary>Pencere dolduysa özeti yazar ve yeni pencereyi <paramref name="now"/>'dan başlatır.</summary>
    private void SummarizeIfDue(long now)
    {
        if (_time.GetElapsedTime(_windowStartedAt, now) < SummaryInterval) return;

        // Pencere yazımdan ÖNCE ilerler: günlük fırlatırsa satır her olayda değil, bir sonraki
        // pencere dolunca yeniden denenir.
        _windowStartedAt = now;
        WriteSummary();
    }

    /// <summary>
    /// Pencerenin özet satırını yazar, sonra pencere sayaçlarını sıfırlar ve kapanmış kulvarları
    /// listeden düşürür (kapanan kulvar bu satırda son kez yer aldı). Günlük fırlatırsa hiçbiri
    /// yapılmaz: yazılamayan pencerenin sayıları bir sonraki satıra kalır.
    /// </summary>
    private void WriteSummary()
    {
        var lanes = new List<string>(_lanes.Count);
        foreach (var lane in _lanes)
            lanes.Add(lane.DescribeWindow());

        _log.LogInformation(
            "[InstagramMergeGate] summary: inversions={Inversions} maxGapSeconds={MaxGapSeconds} lanes: {Lanes}",
            _inversions, _maxGapSeconds, string.Join("; ", lanes));

        _inversions = 0;
        _maxGapSeconds = 0;
        foreach (var lane in _lanes)
            lane.ResetWindow();
        _lanes.RemoveAll(static lane => lane.IsCompleted);
    }

    /// <summary>
    /// Kapının kendi adımı fırlattı: istisna yutulur. Debug düzeyinde yazılır (yorum başına bir
    /// satır günlüğü doldururdu); günlüğün kendisi fırlatıyorsa o da yutulur.
    /// </summary>
    private void LogSwallowed(Exception ex)
    {
        try
        {
            _log.LogDebug(ex, "[InstagramMergeGate] gate step threw; swallowed");
        }
        catch
        {
            // Fırlatan adım günlüğün kendisi olabilir; yapılacak başka bir şey yok.
        }
    }

    /// <summary>
    /// En yakın sıra (nearest-rank) yüzdeliği, tam milisaniyeye yuvarlanmış:
    /// <c>sıralı[ceil(q × n) − 1]</c>; süre yoksa 0.
    /// </summary>
    /// <param name="ascending">Süreler, küçükten büyüğe.</param>
    /// <param name="percent">Yüzdelik, 1–100.</param>
    private static long PercentileMs(List<TimeSpan> ascending, int percent)
    {
        if (ascending.Count == 0) return 0;

        // ceil(percent / 100 × n), tam sayı aritmetiğiyle: sıra kayan nokta yuvarlamasına yaslanmaz.
        var rank = (ascending.Count * percent + 99) / 100;
        return (long)Math.Round(ascending[rank - 1].TotalMilliseconds, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Bir hesabın kulvarı. Poller'a <see cref="IChatBus"/> olarak verilir (poller kapıyı bilmez) ve
    /// aynı poller'ın turlarını <see cref="IInstagramPollObserver"/> olarak dinler.
    /// </summary>
    public sealed class Lane : IChatBus, IInstagramPollObserver
    {
        private readonly InstagramMergeGate _gate;
        private readonly string _label;

        /// <summary>Penceredeki tamamlanmış turların süreleri.</summary>
        private readonly List<TimeSpan> _rounds = new();

        /// <summary>Süren turun başladığı an; tur sürmüyorsa null.</summary>
        private long? _roundStartedAt;

        private bool _completed;

        internal Lane(InstagramMergeGate gate, string label)
        {
            _gate = gate;
            _label = label;
        }

        /// <summary>Kulvar kapandı mı (<see cref="Complete"/>). Kapının kilidi altında okunur.</summary>
        internal bool IsCompleted => _completed;

        /// <summary>İç bus'a devredilir: abone kulvardan geçmeyen mesajları da görür.</summary>
        public IDisposable Subscribe(Action<ChatMessage> handler) => _gate._inner.Subscribe(handler);

        /// <summary>İç bus'a devredilir.</summary>
        public IReadOnlyList<ChatMessage> RecentMessages() => _gate._inner.RecentMessages();

        /// <summary>
        /// Mesajı bu kulvarın etiketiyle hemen iç bus'a basar; verilen mesaj değişmez (kopyası
        /// etiketlenir). Kulvar kapandıktan ve kapı kapandıktan sonra da etiketleyip basar.
        /// </summary>
        public void Publish(ChatMessage message)
        {
            var gate = _gate;
            lock (gate._sync)
            {
                var outgoing = message;
                try
                {
                    outgoing = message with { SourceAccount = _label };
                }
                catch (Exception ex)
                {
                    gate.LogSwallowed(ex);
                }

                // try/catch'lerin DIŞINDA: iç bus fırlatırsa istisna çağırana (poller'a) çıkar.
                // Sayaçlara henüz dokunulmadı ve kilit bırakılır: sonraki mesajlar akar.
                gate._inner.Publish(outgoing);

                if (gate._disposed) return;

                try
                {
                    gate.NotePublished(message.ReceivedAt);
                    gate.SummarizeIfDue(gate._time.GetTimestamp());
                }
                catch (Exception ex)
                {
                    gate.LogSwallowed(ex);
                }
            }
        }

        /// <inheritdoc />
        public void RoundStarting()
        {
            var gate = _gate;
            lock (gate._sync)
            {
                if (gate._disposed) return;

                try
                {
                    var now = gate._time.GetTimestamp();
                    if (!_completed) _roundStartedAt = now;
                    gate.SummarizeIfDue(now);
                }
                catch (Exception ex)
                {
                    gate.LogSwallowed(ex);
                }
            }
        }

        /// <summary>Turu sayar. Her sonuç türü bir turdur: süre, isteğin ne döndüğünü değil ne
        /// kadar sürdüğünü anlatır.</summary>
        public void RoundCompleted(
            InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader)
        {
            var gate = _gate;
            lock (gate._sync)
            {
                if (gate._disposed) return;

                try
                {
                    var now = gate._time.GetTimestamp();
                    if (_roundStartedAt is { } startedAt)
                    {
                        _roundStartedAt = null;
                        _rounds.Add(gate._time.GetElapsedTime(startedAt, now));
                    }

                    gate.SummarizeIfDue(now);
                }
                catch (Exception ex)
                {
                    gate.LogSwallowed(ex);
                }
            }
        }

        /// <summary>
        /// Kulvarı kapatır (hesap döngüsü çıkarken). Kulvar bir sonraki özette son kez yer alır,
        /// sonra listeden düşer; artık tur ölçmez. Yinelenen çağrı bir şey değiştirmez.
        /// </summary>
        public void Complete()
        {
            lock (_gate._sync)
            {
                _completed = true;
                _roundStartedAt = null;
            }
        }

        /// <summary>Bu kulvarın özet parçası: etiket, penceredeki tur sayısı ve süre yüzdelikleri.</summary>
        internal string DescribeWindow()
        {
            _rounds.Sort();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{_label} rounds={_rounds.Count} p50Ms={PercentileMs(_rounds, 50)} p95Ms={PercentileMs(_rounds, 95)}");
        }

        /// <summary>Pencere sayaçlarını sıfırlar. Süren turun başlangıcı kalır: tur bittiği
        /// pencerede sayılır.</summary>
        internal void ResetWindow() => _rounds.Clear();
    }
}
