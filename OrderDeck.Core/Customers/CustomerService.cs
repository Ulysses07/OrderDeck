using System;
using System.Collections.Generic;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Customers;

public sealed class CustomerService
{
    private readonly CustomerRepository _repo;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly IClock _clock;

    public CustomerService(
        CustomerRepository repo,
        SessionRepository sessions,
        LabelRepository labels,
        IClock clock)
    {
        _repo = repo;
        _sessions = sessions;
        _labels = labels;
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
            IsBlacklisted: false,
            BlacklistReason: null,
            Notes: null,
            TotalLabelsPrinted: 0,
            TotalAmount: 0m,
            BlacklistedAt: null,
            Address: null,
            Phone: null);
        _repo.Insert(customer);
        return customer;
    }

    public void RecordPrintedLabels(string customerId, int labelCount, decimal amount)
    {
        _repo.IncrementLabelStats(customerId, labelCount, amount, _clock.UnixNow());
    }

    /// <summary>Marks the customer as blacklisted with optional reason.</summary>
    public void AddToBlacklist(string customerId, string? reason)
    {
        _repo.UpdateBlacklist(customerId, isBlacklisted: true, reason, blacklistedAt: _clock.UnixNow());
    }

    /// <summary>Clears the blacklist flag, reason, and timestamp.</summary>
    public void RemoveFromBlacklist(string customerId)
    {
        _repo.UpdateBlacklist(customerId, isBlacklisted: false, reason: null, blacklistedAt: null);
    }

    /// <summary>
    /// Creates the customer if missing, then blacklists. Returns the post-blacklist record.
    /// Used by the "+ Manuel Ekle" flow in the Blacklist dialog.
    /// </summary>
    public Customer EnsureBlacklistedManual(string platform, string username, string? reason)
    {
        var c = GetOrCreate(platform, username, displayName: null, avatarUrl: null);
        AddToBlacklist(c.Id, reason);
        return _repo.GetById(c.Id)!;
    }

    /// <summary>
    /// Phase 4g: en son tamamlanmış yayında alışveriş yapan müşteriler
    /// (printed label'ları olanlar), tutar DESC sıralı. Yayın yoksa empty.
    /// </summary>
    public IReadOnlyList<Customer> GetLastStreamShoppers()
    {
        var session = _sessions.GetLatestEnded();
        if (session is null) return Array.Empty<Customer>();

        var top = _labels.GetTopCustomersBySession(session.Id, int.MaxValue);
        var result = new List<Customer>(top.Count);
        foreach (var t in top)
        {
            var c = _repo.FindByPlatformAndUsername(t.Platform, t.Username);
            if (c is not null) result.Add(c);
        }
        return result;
    }
}
