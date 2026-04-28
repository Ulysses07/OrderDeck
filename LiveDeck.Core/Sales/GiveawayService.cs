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
    /// <summary>SQLite extended error code for UNIQUE constraint violations.</summary>
    private const int SqliteUniqueConstraintCode = 2067;

    /// <summary>tr-TR CompareInfo for keyword matching (handles dotted/dotless i correctly).</summary>
    private static readonly System.Globalization.CompareInfo TrCompare =
        new System.Globalization.CultureInfo("tr-TR").CompareInfo;

    private readonly GiveawayRepository _giveaways;
    private readonly CustomerService _customers;
    private readonly GiveawayDrawer _drawer;
    private readonly IClock _clock;

    /// <summary>The most recently started giveaway that has not yet been drawn or cancelled. Null if none.</summary>
    public Giveaway? Active { get; private set; }

    public event Action<GiveawayStartedEvent>? Started;
    public event Action<GiveawayParticipantEvent>? ParticipantAdded;
    public event Action<GiveawayWinnersDrawnEvent>? WinnersDrawn;
    public event Action<GiveawayCancelledEvent>? Cancelled;

    public GiveawayService(
        GiveawayRepository giveaways,
        CustomerService customers,
        GiveawayDrawer drawer,
        IClock clock)
    {
        _giveaways = giveaways;
        _customers = customers;
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
        Active = g;
        Started?.Invoke(new GiveawayStartedEvent(
            g.Id, g.Keyword, g.WinnerCount, g.DurationSeconds, g.StartedAt));
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

        // (a) Keyword match — Turkish-aware case-insensitive substring
        if (TrCompare.IndexOf(message.Text, g.Keyword, System.Globalization.CompareOptions.IgnoreCase) < 0)
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
        var participant = new GiveawayParticipant(
            Id: Guid.NewGuid().ToString("N"),
            GiveawayId: g.Id,
            CustomerId: customer.Id,
            Platform: message.Platform,
            Username: message.Username,
            EnteredAt: _clock.UnixNow(),
            IsWinner: false);
        try
        {
            _giveaways.AddParticipant(participant);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
            when (ex.SqliteExtendedErrorCode == SqliteUniqueConstraintCode)
        {
            // Duplicate participant for this giveaway. Silently ignore.
            return;
        }

        // Successful add → broadcast count
        var total = _giveaways.GetParticipants(g.Id).Count;
        ParticipantAdded?.Invoke(new GiveawayParticipantEvent(
            g.Id, message.Username, message.DisplayName, message.AvatarUrl,
            message.Platform, total));
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
        if (Active?.Id == g.Id) Active = null;

        var animationPool = BuildAnimationPool(participants, winners);
        WinnersDrawn?.Invoke(new GiveawayWinnersDrawnEvent(
            g.Id,
            winners.Select(ToWinnerDto).ToList(),
            animationPool,
            participants.Count));

        return winners;
    }

    /// <summary>Aborts the giveaway. Sets CancelledAt; participants kept for audit.</summary>
    public void Cancel(string giveawayId)
    {
        _giveaways.MarkCancelled(giveawayId, _clock.UnixNow());
        if (Active?.Id == giveawayId) Active = null;
        Cancelled?.Invoke(new GiveawayCancelledEvent(giveawayId));
    }

    /// <summary>
    /// Builds the OBS roulette animation pool: a shuffled prefix of participants ending
    /// with the actual winners. Capped at 50 entries to keep the overlay snappy.
    /// </summary>
    private static IReadOnlyList<GiveawayWinnerDto> BuildAnimationPool(
        IReadOnlyList<GiveawayParticipant> participants,
        IReadOnlyList<GiveawayParticipant> winners)
    {
        const int MaxPoolSize = 50;
        if (participants.Count == 0) return Array.Empty<GiveawayWinnerDto>();

        var winnerIds = new HashSet<string>(winners.Select(w => w.Id));
        var nonWinners = participants.Where(p => !winnerIds.Contains(p.Id)).ToList();

        // Shuffle non-winners for visual variety; deterministic shuffle is unnecessary here.
        var rng = new Random();
        for (int i = nonWinners.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (nonWinners[i], nonWinners[j]) = (nonWinners[j], nonWinners[i]);
        }

        // Reserve last N slots for the actual winners (in order). Fill earlier slots from
        // the shuffled non-winner pool.
        int total = Math.Min(MaxPoolSize, participants.Count);
        int filler = Math.Max(0, total - winners.Count);
        var pool = new List<GiveawayWinnerDto>(total);
        for (int i = 0; i < filler && i < nonWinners.Count; i++)
            pool.Add(ToWinnerDto(nonWinners[i]));
        foreach (var w in winners) pool.Add(ToWinnerDto(w));
        return pool;
    }

    private static GiveawayWinnerDto ToWinnerDto(GiveawayParticipant p) =>
        new(p.Username, DisplayName: null, AvatarUrl: null, p.Platform);
}
