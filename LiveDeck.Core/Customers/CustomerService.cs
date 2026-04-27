using System;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Customers;

public sealed class CustomerService
{
    private readonly CustomerRepository _repo;
    private readonly IClock _clock;

    public CustomerService(CustomerRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public Customer GetOrCreate(string platform, string username,
        string? displayName, string? avatarUrl)
    {
        var existing = _repo.FindByPlatformAndUsername(platform, username);
        if (existing is not null) return existing;

        var now = _clock.UnixNow();
        var customer = new Customer(
            Id: Guid.NewGuid().ToString("N"),
            Platform: platform,
            Username: username,
            DisplayName: displayName,
            AvatarUrl: avatarUrl,
            FirstSeenAt: now,
            LastSeenAt: now,
            TotalOrders: 0,
            CompletedOrders: 0,
            CancelledOrders: 0,
            TrustScore: 100,
            IsBlacklisted: false,
            BlacklistReason: null,
            Notes: null,
            TotalLabelsPrinted: 0,
            TotalAmount: 0m);
        _repo.Insert(customer);
        return customer;
    }

    /// <summary>
    /// Increments label aggregate counters when one or more labels are printed.
    /// </summary>
    public void RecordPrintedLabels(string customerId, int labelCount, decimal amount)
    {
        _repo.IncrementLabelStats(customerId, labelCount, amount, _clock.UnixNow());
    }
}
