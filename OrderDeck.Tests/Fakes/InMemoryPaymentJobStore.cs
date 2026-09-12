using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Tests.Fakes;

public sealed class InMemoryPaymentJobStore : IPaymentJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PaymentJob> _jobs = new();

    /// <summary>Testlerin durum iddiaları için anlık kopya.</summary>
    public IReadOnlyList<PaymentJob> Snapshot
    {
        get { lock (_gate) return _jobs.Values.ToList(); }
    }

    /// <summary>Test tohumu (ör. 034'ten taşınmış legacy iş).</summary>
    public PaymentJob Seed(PaymentJob job)
    {
        lock (_gate) { _jobs[job.Id] = job; return job; }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public PaymentJob FindOrCreate(string customerId, string scopeKey, decimal productTotal)
    {
        lock (_gate)
        {
            var existing = _jobs.Values.FirstOrDefault(
                j => j.CustomerId == customerId && j.ScopeKey == scopeKey);
            if (existing is not null) return existing;
            var now = Now();
            var job = new PaymentJob(
                Guid.NewGuid().ToString("N"), customerId, scopeKey, productTotal,
                0, null, null, PaymentJobState.Created, now, now, null);
            _jobs[job.Id] = job;
            return job;
        }
    }

    public PaymentJob? Get(string id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    public IReadOnlyList<PaymentJob> GetOpenLegacies(string customerId)
    {
        lock (_gate)
            return _jobs.Values
                .Where(j => j.CustomerId == customerId && j.ClosedAt is null
                            && (j.ScopeKey == "legacy" || j.ScopeKey.StartsWith("legacy:", StringComparison.Ordinal)))
                .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id, StringComparer.Ordinal)
                .ToList();
    }

    public bool BeginApply(string id, Guid applyKey)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (j.ApplyKey is not null) return false;
            _jobs[id] = j with
            {
                ApplyKey = applyKey,
                State = PaymentJobState.ApplyUncertain,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    /// <summary>R4-01 koşulu: cevap, gönderildiği denemeye ait olmalı.</summary>
    private bool Bayat(PaymentJob j, Guid? expectedKey, int expectedRevision)
        => j.Revision != expectedRevision || j.ApplyKey != expectedKey;

    public bool MarkApplied(string id, Guid expectedKey, int expectedRevision, decimal appliedAmount)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (Bayat(j, expectedKey, expectedRevision)) return false;
            _jobs[id] = j with
            {
                State = PaymentJobState.Applied,
                AppliedAmount = appliedAmount,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    public bool MarkNoBalance(string id, Guid? expectedKey, int expectedRevision)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (Bayat(j, expectedKey, expectedRevision)) return false;
            _jobs[id] = j with
            {
                State = PaymentJobState.NoBalance,
                AppliedAmount = 0m,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    public bool MarkUncertain(string id, Guid? expectedKey, int expectedRevision)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (Bayat(j, expectedKey, expectedRevision)) return false;
            _jobs[id] = j with
            {
                State = PaymentJobState.ApplyUncertain,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    public void Close(string id)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            _jobs[id] = j with { ClosedAt = j.ClosedAt ?? Now(), UpdatedAt = Now() };
        }
    }

    public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (j.Revision != expectedRevision) return false;
            _jobs[id] = j with
            {
                ProductTotal = newProductTotal,
                Revision = j.Revision + 1,
                ApplyKey = newApplyKey,
                AppliedAmount = null,
                State = PaymentJobState.ApplyUncertain,
                UpdatedAt = Now(),
                ClosedAt = null, // revizyon kapanmış işi yeniden açar (R2-02)
            };
            return true;
        }
    }

    public bool BeginReversal(string id, int expectedRevision, Guid expectedKey, decimal targetTotal)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (Bayat(j, expectedKey, expectedRevision)) return false;
            _jobs[id] = j with
            {
                State = PaymentJobState.ReversePending,
                PendingTotal = targetTotal,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    public bool CompleteReversal(string id, int expectedRevision, Guid expectedKey)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (Bayat(j, expectedKey, expectedRevision)) return false;
            if (j.State != PaymentJobState.ReversePending) return false;
            _jobs[id] = j with
            {
                ProductTotal = j.PendingTotal ?? j.ProductTotal,
                Revision = j.Revision + 1,
                ApplyKey = null,
                AppliedAmount = null,
                State = PaymentJobState.Created,
                PendingTotal = null,
                ClosedAt = null,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    public void AdoptLegacyResult(string targetId, string legacyId)
    {
        lock (_gate)
        {
            var target = _jobs[targetId];
            var legacy = _jobs[legacyId];
            if (target.ApplyKey is null && target.State == PaymentJobState.Created)
                _jobs[targetId] = target with
                {
                    ProductTotal = legacy.ProductTotal,
                    ApplyKey = legacy.ApplyKey,
                    AppliedAmount = legacy.AppliedAmount,
                    State = legacy.State,
                    UpdatedAt = Now(),
                };
            _jobs[legacyId] = legacy with { ClosedAt = legacy.ClosedAt ?? Now(), UpdatedAt = Now() };
        }
    }
}
