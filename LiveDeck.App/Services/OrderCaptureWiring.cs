using System;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services;

/// <summary>
/// Subscribes to <see cref="IChatBus"/> and feeds incoming messages into
/// <see cref="OrderService"/> when a stream session is active.
/// Owned by AppHost for the app lifetime.
/// </summary>
public sealed class OrderCaptureWiring : IDisposable
{
    private readonly OrderService _orders;
    private readonly StreamSessionService _sessions;
    private readonly ILogger<OrderCaptureWiring> _log;
    private readonly IDisposable _sub;

    public OrderCaptureWiring(
        IChatBus bus,
        OrderService orders,
        StreamSessionService sessions,
        ILogger<OrderCaptureWiring> log)
    {
        _orders = orders;
        _sessions = sessions;
        _log = log;
        _sub = bus.Subscribe(OnMessage);
    }

    private void OnMessage(ChatMessage m)
    {
        try
        {
            var session = _sessions.GetActive();
            if (session is null) return;

            var order = _orders.Process(session.Id, m);
            if (order is not null)
                _log.LogInformation("Captured order {Code} {Size} ×{Qty} from @{User} (conf={Conf})",
                    order.Code, order.Size, order.Quantity, m.Username, order.Confidence);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OrderCaptureWiring failed for message from @{User}", m.Username);
        }
    }

    public void Dispose() => _sub.Dispose();
}
