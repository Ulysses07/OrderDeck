using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderDeck.Core.Chat;

/// <summary>
/// In-memory pub/sub for chat messages. Thread-safe: subscriptions and publishes can race.
/// Maintains a fixed-size ring buffer of recent messages for late subscribers (e.g., the
/// OBS overlay reconnecting mid-stream).
/// </summary>
public sealed class ChatBus : IChatBus
{
    /// <summary>
    /// Bir abone art arda patlarsa logun kaçta biri yazılır. Sohbet saniyede
    /// 6-10 mesaj akabiliyor; her başarısızlığı yazmak günlük Serilog
    /// dosyasını saatler içinde şişirirdi.
    /// </summary>
    private const int FailureLogEveryNth = 100;

    private readonly object _lock = new();
    private readonly List<HandlerEntry> _handlers = new();
    private readonly ChatMessage[] _ring;
    private readonly ILogger _log;
    private int _ringHead;
    private int _ringCount;

    /// <summary>Abone + o abonenin art arda kaç kez patladığı.</summary>
    private sealed class HandlerEntry(Action<ChatMessage> handler)
    {
        public Action<ChatMessage> Handler { get; } = handler;
        public long Failures;
    }

    public ChatBus(int ringBufferSize = 200, ILogger<ChatBus>? log = null)
    {
        _ring = new ChatMessage[ringBufferSize];
        _log = log ?? NullLogger<ChatBus>.Instance;
    }

    public IDisposable Subscribe(Action<ChatMessage> handler)
    {
        lock (_lock) _handlers.Add(new HandlerEntry(handler));
        return new Subscription(this, handler);
    }

    /// <summary>
    /// Mesajı halkaya yazar ve o anki abonelere dağıtır.
    ///
    /// <para><b>Her abone yalıtılmış.</b> Eskiden dağıtım düz bir döngüydü:
    /// ilk patlayan abone hem kendisinden SONRAKİ aboneleri atlatıyor hem de
    /// istisnayı yayıncıya geri fırlatıyordu. Sahadaki karşılığı şu: arayüz
    /// tarafındaki bir hata OBS overlay'ini yayın ortasında sessizce
    /// susturuyor, istisna da uzantı köprüsünün WebSocket döngüsüne gidip
    /// TikTok sohbetini büsbütün düşürüyordu. Yani tek bir abonenin hatası
    /// tüm sohbet hattını kapatabiliyordu.</para>
    ///
    /// <para><b>Patlayan abone çıkarılmıyor</b>, bilerek. Sessizce aboneliği
    /// düşürmek overlay'i kalıcı olarak karartır ve sebebi görünmez olurdu;
    /// log ise sorunu adıyla gösteriyor.</para>
    /// </summary>
    public void Publish(ChatMessage message)
    {
        HandlerEntry[] snapshot;
        lock (_lock)
        {
            _ring[_ringHead] = message;
            _ringHead = (_ringHead + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
            snapshot = _handlers.ToArray();
        }

        foreach (var entry in snapshot)
        {
            try
            {
                entry.Handler(message);
                Interlocked.Exchange(ref entry.Failures, 0);
            }
            catch (Exception ex)
            {
                var failures = Interlocked.Increment(ref entry.Failures);
                if (failures == 1 || failures % FailureLogEveryNth == 0)
                {
                    _log.LogError(ex,
                        "Sohbet abonesi {Handler} patladı ({Failures}. kez); diğer aboneler etkilenmedi",
                        DescribeHandler(entry.Handler), failures);
                }
            }
        }
    }

    /// <summary>Logda suçluyu adıyla göstermek için: <c>Tip.Metot</c>.</summary>
    private static string DescribeHandler(Action<ChatMessage> handler) =>
        $"{handler.Method.DeclaringType?.Name ?? "?"}.{handler.Method.Name}";

    public IReadOnlyList<ChatMessage> RecentMessages()
    {
        lock (_lock)
        {
            var result = new List<ChatMessage>(_ringCount);
            int start = (_ringHead - _ringCount + _ring.Length) % _ring.Length;
            for (int i = 0; i < _ringCount; i++)
            {
                result.Add(_ring[(start + i) % _ring.Length]);
            }
            return result;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ChatBus _bus;
        private Action<ChatMessage>? _handler;

        public Subscription(ChatBus bus, Action<ChatMessage> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is null) return;
            lock (_bus._lock)
            {
                // Yalnızca İLK eşleşen kayıt: aynı delege iki kez abone
                // olduysa bir Dispose ikisini birden düşürmemeli (eski
                // List.Remove davranışı).
                var i = _bus._handlers.FindIndex(e => e.Handler == h);
                if (i >= 0) _bus._handlers.RemoveAt(i);
            }
        }
    }
}
