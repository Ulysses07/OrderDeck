using System;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales.Pipeline;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class OrderService
{
    private readonly OrderRepository _orders;
    private readonly ActiveCodeRepository _codes;
    private readonly CustomerService _customers;
    private readonly OrderCaptureEngine _engine;
    private readonly IClock _clock;

    public OrderService(
        OrderRepository orders,
        ActiveCodeRepository codes,
        CustomerService customers,
        OrderCaptureEngine engine,
        IClock clock)
    {
        _orders = orders;
        _codes = codes;
        _customers = customers;
        _engine = engine;
        _clock = clock;
    }

    /// <summary>
    /// Runs the capture engine over a chat message and persists an OrderItem when the
    /// match is at least mid-confidence. Returns null when nothing actionable was captured.
    /// </summary>
    public OrderItem? Process(string sessionId, ChatMessage message)
    {
        var activeCodes = _codes.GetActiveBySession(sessionId);
        if (activeCodes.Count == 0) return null;

        var capture = _engine.Capture(message.Text, activeCodes);

        if (capture.MatchedCode is null) return null;
        if (capture.Confidence < _engine.LowConfidenceThreshold) return null;

        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl);

        var status = capture.Confidence >= _engine.HighConfidenceThreshold
            ? OrderStatus.New
            : OrderStatus.Pending;

        var size = capture.Size ?? "(belirsiz)";
        var qty = capture.Quantity;
        var unit = capture.MatchedCode.Price;
        var now = _clock.UnixNow();

        var order = new OrderItem(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            ActiveCodeId: capture.MatchedCode.Id,
            CustomerId: customer.Id,
            Code: capture.MatchedCode.Code,
            Size: size,
            Quantity: qty,
            UnitPrice: unit,
            TotalPrice: unit * qty,
            Confidence: capture.Confidence,
            Status: status,
            OriginalMessageText: message.Text,
            CapturedAt: now,
            StatusUpdatedAt: now,
            LabelPrintedAt: null,
            Notes: null);

        _orders.Insert(order);
        return order;
    }

    public void UpdateStatus(string orderId, string newStatus)
    {
        _orders.UpdateStatus(orderId, newStatus, _clock.UnixNow());
    }
}
