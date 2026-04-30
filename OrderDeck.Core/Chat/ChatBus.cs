using System;
using System.Collections.Generic;
using System.Threading;

namespace OrderDeck.Core.Chat;

/// <summary>
/// In-memory pub/sub for chat messages. Thread-safe: subscriptions and publishes can race.
/// Maintains a fixed-size ring buffer of recent messages for late subscribers (e.g., the
/// OBS overlay reconnecting mid-stream).
/// </summary>
public sealed class ChatBus : IChatBus
{
    private readonly object _lock = new();
    private readonly List<Action<ChatMessage>> _handlers = new();
    private readonly ChatMessage[] _ring;
    private int _ringHead;
    private int _ringCount;

    public ChatBus(int ringBufferSize = 200)
    {
        _ring = new ChatMessage[ringBufferSize];
    }

    public IDisposable Subscribe(Action<ChatMessage> handler)
    {
        lock (_lock) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    public void Publish(ChatMessage message)
    {
        Action<ChatMessage>[] snapshot;
        lock (_lock)
        {
            _ring[_ringHead] = message;
            _ringHead = (_ringHead + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
            snapshot = _handlers.ToArray();
        }
        foreach (var h in snapshot) h(message);
    }

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
            lock (_bus._lock) _bus._handlers.Remove(h);
        }
    }
}
