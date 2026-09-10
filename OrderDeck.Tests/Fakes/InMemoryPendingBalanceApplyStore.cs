using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Tests.Fakes;

/// <summary>SQLite deposunun bellek içi eşleniği (N02). Sözleşme:
/// müşteri başına en fazla bir çözülmemiş kayıt, en yenisi döner.</summary>
public sealed class InMemoryPendingBalanceApplyStore : IPendingBalanceApplyStore
{
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public required PendingBalanceApply Record { get; init; }
        public bool Resolved { get; set; }
    }

    public IReadOnlyList<PendingBalanceApply> All =>
        _entries.Select(e => e.Record).ToList();

    public IReadOnlyList<PendingBalanceApply> Unresolved =>
        _entries.Where(e => !e.Resolved).Select(e => e.Record).ToList();

    public PendingBalanceApply? GetUnresolved(string customerId) =>
        _entries.Where(e => !e.Resolved && e.Record.CustomerId == customerId)
            .OrderByDescending(e => e.Record.CreatedAt)
            .Select(e => e.Record)
            .FirstOrDefault();

    public void Create(string customerId, Guid idempotencyKey, decimal productTotal) =>
        _entries.Add(new Entry
        {
            Record = new PendingBalanceApply(
                idempotencyKey, customerId, productTotal,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        });

    public void MarkResolved(Guid idempotencyKey)
    {
        foreach (var e in _entries)
            if (e.Record.IdempotencyKey == idempotencyKey)
                e.Resolved = true;
    }
}
