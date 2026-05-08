using System;
using System.Collections.Generic;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;

namespace OrderDeck.Core.Customers;

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

    /// <summary>Read-only lookup. Returns null if no customer matches —
    /// caller decides whether to create-or-skip. Used by paths that already
    /// know the customer should exist (e.g. GiveawayService building the
    /// animation pool from existing participants) and don't want the
    /// side-effect of creating a new row.</summary>
    public Customer? Find(string platform, string username) =>
        _repo.FindByPlatformAndUsername(platform, username);

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
    /// <summary>Cap on shoppers returned. Bigger than any realistic
    /// single-stream customer count (was uncapped via int.MaxValue, which
    /// trivially scales — but limits the JOIN buffer + caller list size
    /// at a known number).</summary>
    private const int MaxLastStreamShoppers = 500;

    public IReadOnlyList<Customer> GetLastStreamShoppers()
    {
        var session = _sessions.GetLatestEnded();
        if (session is null) return Array.Empty<Customer>();

        // Was N+1: a TopCustomer-by-session query, then per-row
        // FindByPlatformAndUsername — ~1000 round-trips on busy sessions.
        // Now a single JOIN through CustomerRepository.GetTopShoppersForSession.
        return _repo.GetTopShoppersForSession(session.Id, MaxLastStreamShoppers);
    }
}
