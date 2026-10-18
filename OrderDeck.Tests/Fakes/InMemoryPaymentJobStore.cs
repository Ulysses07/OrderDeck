using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Customers;
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

    /// <summary>İşlem içi üye okumasının karşılığı (gerçek depo Customer tablosunu okur): kiralanan
    /// satırın güncel üyeleri. Null = akışın gördüğü küme doğru kabul edilir (üye tablosu olmayan testler).</summary>
    public Func<string, IReadOnlyCollection<string>>? MembersOf { get; set; }

    public PersonJob FindOrCreateForPerson(PersonJobRequest request)
    {
        lock (_gate)
        {
            // Gerçek depo gibi: satırın üyesi okunamıyorsa kişi yalnız kiralanan satırdır.
            var members = MembersOf is null
                ? request.MemberIds
                : MembersOf(request.LeasedId) is { Count: > 0 } read ? read : new[] { request.LeasedId };
            if (PaymentPerson.KeyOf(members) != request.MemberKey) return PersonJob.MembershipChanged;

            var memberSet = members.ToHashSet(StringComparer.Ordinal);
            var jobs = _jobs.Values
                .Where(j => memberSet.Contains(j.CustomerId) && j.ScopeKey == request.ScopeKey)
                .ToList();
            // Gerçek deponun ScopeLegacyCondition'ı: asıl kapsamı bu olan açık ya da teslim edilmiş (gitmiş
            // olabilecek) miras — miras kapsamında IsDelivered ClosedAt'a bakmaz.
            var legacies = _jobs.Values
                .Where(j => memberSet.Contains(j.CustomerId) && j.PriorScopeKey == request.ScopeKey
                            && IsLegacyScope(j.ScopeKey) && (j.ClosedAt is null || j.IsDelivered))
                .ToList();
            var now = Now();

            var plan = jobs.Count + legacies.Count == 0 ? null : PersonScopePlan.For(jobs, legacies, request.OwnerId);
            if (plan is { NeedsConfirmation: true } && request.ConfirmedTokens?.Contains(plan.Token) != true)
                return new PersonJob(PersonJobStatus.ConfirmationRequired, plan.Survivor,
                    Array.Empty<DemotedJob>(), plan);

            if (jobs.Count == 0)
            {
                var job = new PaymentJob(
                    Guid.NewGuid().ToString("N"), request.OwnerId, request.ScopeKey, request.ProductTotal,
                    0, null, null, PaymentJobState.Created, now, now, null) { ResumeCovers = plan?.Covers };
                _jobs[job.Id] = job;
                return PersonJob.Ready(job, Array.Empty<DemotedJob>());
            }

            var demoted = new List<DemotedJob>();
            foreach (var l in plan!.Losers)
            {
                // Gerçek deponun PaymentJobSql.DemoteToLegacy metniyle aynı karar — teslim kaydı
                // dondurulur (önceki sürümün teslimi netiyle teslim kaydına geçer).
                _jobs[l.Id] = l.FreezeDelivery() with
                {
                    PriorScopeKey = l.PriorScopeKey ?? l.ScopeKey,
                    ScopeKey = $"legacy:{l.ApplyKey?.ToString("N") ?? l.Id}:{l.Id}",
                    ClosedAt = l.CarriesMoney ? null : l.ClosedAt ?? now,
                    UpdatedAt = now,
                };
                demoted.Add(PersonScopePlan.Demotion(l));
            }
            // Gerçek depo gibi: onay damgası kalan işe yazılır.
            var survivor = _jobs[plan.Survivor!.Id] with { ResumeCovers = plan.Covers };
            _jobs[survivor.Id] = survivor;
            return PersonJob.Ready(survivor, demoted);
        }
    }

    private static bool IsLegacyScope(string scopeKey)
        => scopeKey == "legacy" || scopeKey.StartsWith("legacy:", StringComparison.Ordinal);

    public (IReadOnlyList<PaymentJob> Jobs, IReadOnlyList<PaymentJob> Legacies) GetPersonScopeJobs(
        IReadOnlyCollection<string> memberIds, string scopeKey)
    {
        lock (_gate)
        {
            var memberSet = memberIds.ToHashSet(StringComparer.Ordinal);
            var jobs = _jobs.Values
                .Where(j => memberSet.Contains(j.CustomerId) && j.ScopeKey == scopeKey).ToList();
            var legacies = _jobs.Values
                .Where(j => memberSet.Contains(j.CustomerId) && j.PriorScopeKey == scopeKey
                            && IsLegacyScope(j.ScopeKey) && (j.ClosedAt is null || j.IsDelivered))
                .ToList();
            return (jobs, legacies);
        }
    }

    public PaymentJob? Get(string id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    public IReadOnlyList<PaymentJob> GetOpenLegacies(string customerId)
        => GetOpenLegacies(new[] { customerId });

    public IReadOnlyList<PaymentJob> GetOpenLegacies(IReadOnlyCollection<string> customerIds)
    {
        lock (_gate)
            return _jobs.Values
                .Where(j => customerIds.Contains(j.CustomerId) && j.ClosedAt is null && IsLegacyScope(j.ScopeKey))
                .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id, StringComparer.Ordinal)
                .ToList();
    }

    public bool ReassignOwner(string id, string newCustomerId)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (j.State != PaymentJobState.Created || j.ApplyKey is not null || j.ClosedAt is not null) return false;
            if (_jobs.Values.Any(t => t.CustomerId == newCustomerId && t.ScopeKey == j.ScopeKey)) return false;
            _jobs[id] = j with { CustomerId = newCustomerId, UpdatedAt = Now() };
            return true;
        }
    }

    public bool BeginApply(string id, Guid applyKey)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            // U8 (Bölüm C): kapalı işe anahtar yazılmaz — gerçek deponun koşuluyla aynı sözleşme.
            if (j.ApplyKey is not null || j.ClosedAt is not null) return false;
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

    /// <summary>R8-D01: gerçek deponun ResultGuard'ındaki
    /// <c>State&lt;&gt;reverse_pending</c> koşulu. Geri alma niyeti üç sonuç
    /// yazıcısı tarafından ezilemez (R6-03) — sözleşme paritesi
    /// <see cref="Storage.PaymentJobStoreContractTests"/> ile doğrulanır.</summary>
    private static bool GeriAlmaBekliyor(PaymentJob j)
        => j.State == PaymentJobState.ReversePending;

    public bool MarkApplied(string id, Guid expectedKey, int expectedRevision, decimal appliedAmount)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (Bayat(j, expectedKey, expectedRevision) || GeriAlmaBekliyor(j)) return false;
            _jobs[id] = j.FreezeDelivery() with
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
            if (Bayat(j, expectedKey, expectedRevision) || GeriAlmaBekliyor(j)) return false;
            _jobs[id] = j.FreezeDelivery() with
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
            if (Bayat(j, expectedKey, expectedRevision) || GeriAlmaBekliyor(j)) return false;
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

    public void MarkDelivered(string id, decimal requestedNet)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            var now = Now();
            var settled = j.State is PaymentJobState.Applied or PaymentJobState.NoBalance;
            _jobs[id] = j with
            {
                DeliveredAt = now,
                DeliveredNet = requestedNet,
                Deliveries = j.Deliveries + 1,
                SendAttempts = j.Deliveries + 1,      // teslim bütün denemeleri karşılar (gerçek depo gibi)
                ClosedAt = settled ? j.ClosedAt ?? now : j.ClosedAt,
                UpdatedAt = now,
            };
        }
    }

    public void BeginSend(string id, decimal requestedNet)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var j)) return;
            var now = Now();
            _jobs[id] = j with
            {
                SendAttempts = j.SendAttempts + 1,
                PriorAttemptNet = j.AttemptNet,
                AttemptNet = requestedNet,
                PriorAttemptAt = j.AttemptAt,
                AttemptAt = now,
                UpdatedAt = now,
            };
        }
    }

    public void AbandonSend(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var j) || j.SendAttempts <= j.Deliveries) return;
            _jobs[id] = j with
            {
                SendAttempts = j.SendAttempts - 1,
                AttemptNet = j.PriorAttemptNet,
                AttemptAt = j.PriorAttemptAt,
                UpdatedAt = Now(),
            };
        }
    }

    public bool BeginRevision(string id, decimal newProductTotal, Guid newApplyKey, int expectedRevision)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (j.Revision != expectedRevision) return false;
            _jobs[id] = j.FreezeDelivery() with
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
            _jobs[id] = j.FreezeDelivery() with
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
            // Gerçek depoyla aynı ikinci kat koruma: başka sahip satırın ya da başka kapsamın düşümü
            // benimsenmez, legacy kapanmaz.
            if (target.CustomerId != legacy.CustomerId) return;
            if (legacy.PriorScopeKey is not null && legacy.PriorScopeKey != target.ScopeKey) return;
            if (target.ApplyKey is null && target.State == PaymentJobState.Created)
                _jobs[targetId] = target.FreezeDelivery() with
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

    public bool AdoptExternalReversal(string id, Guid expectedKey, int expectedRevision)
    {
        lock (_gate)
        {
            var j = _jobs[id];
            if (Bayat(j, expectedKey, expectedRevision)) return false;
            if (j.State != PaymentJobState.Applied) return false;
            _jobs[id] = j.FreezeDelivery() with
            {
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

    /// <summary>İki <c>AdoptRemoteResult</c> biçiminin çağrı sayısı (W3-11: miras işte benimseme olmadığı).</summary>
    public int AdoptRemoteCalls
    {
        get { lock (_gate) return _adoptRemoteCalls; }
    }
    private int _adoptRemoteCalls;

    public bool AdoptRemoteResult(
        string id, Guid transactionId, decimal appliedAmount, decimal productTotal,
        Guid expectedKey, int expectedRevision)
    {
        lock (_gate)
        {
            _adoptRemoteCalls++;
            var j = _jobs[id];
            // Gerçek deponun ResultGuard'ı: denemenin kimliği tutmalı, geri alma niyeti ezilmez.
            if (Bayat(j, expectedKey, expectedRevision) || GeriAlmaBekliyor(j)) return false;
            _jobs[id] = j.FreezeDelivery() with
            {
                ApplyKey = transactionId,
                AppliedAmount = appliedAmount,
                ProductTotal = productTotal,
                State = PaymentJobState.Applied,
                UpdatedAt = Now(),
            };
            return true;
        }
    }

    public bool AdoptRemoteResult(
        string id, Guid transactionId, decimal appliedAmount, decimal productTotal)
    {
        lock (_gate)
        {
            _adoptRemoteCalls++;
            var j = _jobs[id];
            if (j.ApplyKey is not null || j.State != PaymentJobState.Created) return false;
            _jobs[id] = j.FreezeDelivery() with
            {
                ApplyKey = transactionId,
                AppliedAmount = appliedAmount,
                ProductTotal = productTotal,
                State = PaymentJobState.Applied,
                UpdatedAt = Now(),
            };
            return true;
        }
    }
}
