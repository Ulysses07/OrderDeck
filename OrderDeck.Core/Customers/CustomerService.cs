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
        if (existing is not null)
            return MaybeAdoptYouTube(existing, platform, displayName) ?? existing;

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
        return MaybeAdoptYouTube(customer, platform, displayName) ?? customer;
    }

    /// <summary>
    /// YouTube channelId satırını, intake formda @handle ile bildirilen kişinin
    /// grubuna adopte eder (chat DisplayName = @handle eşleşmesi, case-insensitive).
    /// Zaten gruplu ya da YouTube olmayan satırlarda no-op. Grup kara listedeyse
    /// satır da kara listeye alınır. Adopte edilirse güncel kaydı döner, yoksa null.
    /// </summary>
    private Customer? MaybeAdoptYouTube(Customer row, string platform, string? displayName)
    {
        if (!string.Equals(platform, "youtube", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(row.GroupId)) return null;
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var handle = displayName.Trim().TrimStart('@').Trim();
        if (handle.Length == 0) return null;

        var declared = _repo.FindGroupedYouTubeByHandle(handle);
        if (declared?.GroupId is not { Length: > 0 } groupId) return null;
        if (string.Equals(declared.Id, row.Id, StringComparison.Ordinal)) return null;

        _repo.SetGroupId(row.Id, groupId);
        if (_repo.IsGroupBlacklisted(groupId))
            _repo.UpdateBlacklist(row.Id, isBlacklisted: true,
                declared.BlacklistReason, declared.BlacklistedAt ?? _clock.UnixNow());

        return _repo.GetById(row.Id);
    }

    /// <param name="write">
    /// Doluysa güncelleme çağıranın işlemine katılır — etiket durumuyla
    /// müşteri toplamının aynı pakette yazılmasını sağlar
    /// (bkz. <see cref="Storage.DbWrite"/>).
    /// </param>
    public void RecordPrintedLabels(
        string customerId, int labelCount, decimal amount, Storage.DbWrite? write = null)
    {
        _repo.IncrementLabelStats(customerId, labelCount, amount, _clock.UnixNow(), write);
    }

    /// <summary>Marks the customer as blacklisted with optional reason. If the
    /// customer belongs to a person-group (linked platform identities), the whole
    /// group is blacklisted so they cannot slip through on another platform.</summary>
    public void AddToBlacklist(string customerId, string? reason)
    {
        var at = _clock.UnixNow();
        _repo.UpdateBlacklist(customerId, isBlacklisted: true, reason, blacklistedAt: at);
        var c = _repo.GetById(customerId);
        if (c?.GroupId is { Length: > 0 } groupId)
            _repo.SetGroupBlacklist(groupId, isBlacklisted: true, reason, at);
    }

    /// <summary>Clears the blacklist flag, reason, and timestamp. Propagates to
    /// the whole person-group if the customer is linked.</summary>
    public void RemoveFromBlacklist(string customerId)
    {
        _repo.UpdateBlacklist(customerId, isBlacklisted: false, reason: null, blacklistedAt: null);
        var c = _repo.GetById(customerId);
        if (c?.GroupId is { Length: > 0 } groupId)
            _repo.SetGroupBlacklist(groupId, isBlacklisted: false, reason: null, blacklistedAt: null);
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
