using System;
using System.Collections.Generic;
using System.Linq;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class GiveawayService
{
    private readonly GiveawayRepository _giveaways;
    private readonly CustomerService _customers;
    private readonly CustomerRepository _customerRepo;
    private readonly GiveawayDrawer _drawer;
    private readonly IClock _clock;

    public GiveawayService(
        GiveawayRepository giveaways,
        CustomerService customers,
        CustomerRepository customerRepo,
        GiveawayDrawer drawer,
        IClock clock)
    {
        _giveaways = giveaways;
        _customers = customers;
        _customerRepo = customerRepo;
        _drawer = drawer;
        _clock = clock;
    }

    public Giveaway Start(string sessionId, string keyword, int durationSeconds,
        int winnerCount, IReadOnlyList<string>? platformFilter, bool preventRewinning)
    {
        var g = new Giveaway(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            Keyword: keyword,
            DurationSeconds: durationSeconds,
            WinnerCount: winnerCount,
            PlatformFilter: platformFilter,
            PreventRewinning: preventRewinning,
            RandomSeed: Guid.NewGuid().ToString("N"),
            StartedAt: _clock.UnixNow(),
            EndedAt: null,
            CancelledAt: null);
        _giveaways.Insert(g);
        return g;
    }

    /// <summary>
    /// Adds the chat message author as a participant if (a) the message contains the
    /// giveaway keyword (case-insensitive substring), (b) the platform passes the filter,
    /// (c) the customer is not blacklisted, (d) PreventRewinning + previous winner check
    /// passes, (e) this username hasn't already entered (UNIQUE constraint).
    /// All filters fail silently — there is no surface to report errors to.
    /// </summary>
    public void AddParticipantFromChat(string giveawayId, ChatMessage message)
    {
        var g = _giveaways.GetById(giveawayId);
        if (g is null || g.EndedAt is not null || g.CancelledAt is not null) return;

        // (a) Keyword match — case-insensitive substring
        if (!message.Text.Contains(g.Keyword, StringComparison.OrdinalIgnoreCase))
            return;

        // (b) Platform filter
        if (g.PlatformFilter is { Count: > 0 } filter
            && !filter.Contains(message.Platform, StringComparer.OrdinalIgnoreCase))
            return;

        // Resolve customer (creates if missing)
        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl);

        // (c) Blacklist check
        if (customer.IsBlacklisted) return;

        // (d) PreventRewinning check
        if (g.PreventRewinning)
        {
            var prevWinners = _giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id);
            if (prevWinners.Contains(customer.Id)) return;
        }

        // (e) UNIQUE INDEX guard — wrap insert in try/catch to swallow duplicate
        try
        {
            _giveaways.AddParticipant(new GiveawayParticipant(
                Id: Guid.NewGuid().ToString("N"),
                GiveawayId: g.Id,
                CustomerId: customer.Id,
                Platform: message.Platform,
                Username: message.Username,
                EnteredAt: _clock.UnixNow(),
                IsWinner: false));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // 19 = SQLITE_CONSTRAINT — duplicate (already entered). Silently ignore.
        }
    }

    /// <summary>
    /// Picks winners using <see cref="GiveawayDrawer"/> with the giveaway's stored seed,
    /// marks them in DB, and sets <c>EndedAt = now</c>. Returns the chosen winners
    /// (empty list if no participants).
    /// </summary>
    public IReadOnlyList<GiveawayParticipant> Draw(string giveawayId)
    {
        var g = _giveaways.GetById(giveawayId)
                ?? throw new InvalidOperationException($"Giveaway {giveawayId} not found");

        var participants = _giveaways.GetParticipants(g.Id);
        var winners = _drawer.Pick(participants, g.WinnerCount, g.RandomSeed);

        if (winners.Count > 0)
            _giveaways.MarkWinners(winners.Select(w => w.Id));

        _giveaways.MarkEnded(g.Id, _clock.UnixNow());
        return winners;
    }

    /// <summary>Aborts the giveaway. Sets CancelledAt; participants kept for audit.</summary>
    public void Cancel(string giveawayId)
    {
        _giveaways.MarkCancelled(giveawayId, _clock.UnixNow());
    }
}
