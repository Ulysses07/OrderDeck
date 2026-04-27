using System;
using System.Collections.Generic;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class LabelService
{
    private readonly LabelRepository _labels;
    private readonly CustomerService _customers;
    private readonly IClock _clock;

    public LabelService(LabelRepository labels, CustomerService customers, IClock clock)
    {
        _labels = labels;
        _customers = customers;
        _clock = clock;
    }

    /// <summary>
    /// Queues a new (unprinted) label for the given chat message + price snapshot.
    /// Auto-creates the Customer on first encounter.
    /// </summary>
    public Label Add(string sessionId, ChatMessage message, decimal price, string? code)
    {
        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl);

        var label = new Label(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            CustomerId: customer.Id,
            Platform: message.Platform,
            Username: message.Username,
            MessageText: message.Text,
            Code: code,
            Price: price,
            AddedAt: _clock.UnixNow(),
            PrintedAt: null);

        _labels.Insert(label);
        return label;
    }

    public void Delete(string labelId) => _labels.Delete(labelId);

    public IReadOnlyList<Label> GetQueue(string sessionId) =>
        _labels.GetUnprintedBySession(sessionId);

    /// <summary>
    /// Marks the given labels as printed and increments customer aggregates per-customer.
    /// </summary>
    public void MarkPrintedAndRecord(IReadOnlyList<string> labelIds)
    {
        if (labelIds.Count == 0) return;

        // Per-customer rollup BEFORE marking, so we can group by CustomerId.
        var groupedAmounts = new Dictionary<string, (int Count, decimal Amount)>();

        foreach (var id in labelIds)
        {
            var lbl = _labels.GetById(id);
            if (lbl is null) continue;
            if (groupedAmounts.TryGetValue(lbl.CustomerId, out var agg))
                groupedAmounts[lbl.CustomerId] = (agg.Count + 1, agg.Amount + lbl.Price);
            else
                groupedAmounts[lbl.CustomerId] = (1, lbl.Price);
        }

        _labels.MarkPrinted(labelIds, _clock.UnixNow());

        foreach (var (customerId, agg) in groupedAmounts)
            _customers.RecordPrintedLabels(customerId, agg.Count, agg.Amount);
    }
}
