using System;
using System.Collections.Generic;
using System.Linq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;

namespace OrderDeck.Core.Sales;

public sealed class GiveawayService
{
    /// <summary>SQLite extended error code for UNIQUE constraint violations.</summary>
    private const int SqliteUniqueConstraintCode = 2067;

    /// <summary>
    /// tr-TR CompareInfo for keyword matching (handles dotted/dotless i correctly).
    /// Pinned to "tr-TR" explicitly because Core does not depend on App's startup
    /// culture lock — unit tests run under whatever culture the test host picks.
    /// </summary>
    private static readonly System.Globalization.CompareInfo TrCompare =
        new System.Globalization.CultureInfo("tr-TR").CompareInfo;

    private readonly GiveawayRepository _giveaways;
    private readonly CustomerService _customers;
    private readonly GiveawayDrawer _drawer;
    private readonly IClock _clock;

    /// <summary>The most recently started giveaway that has not yet been drawn or cancelled. Null if none.</summary>
    public Giveaway? Active { get; private set; }

    /// <summary>
    /// Cached set of customer ids that won previous (non-cancelled) giveaways in this
    /// session, populated at <see cref="Start"/> when PreventRewinning is true. Cleared
    /// in <see cref="Draw"/> and <see cref="Cancel"/>. Null when no active giveaway or
    /// when PreventRewinning is off.
    /// </summary>
    private HashSet<string>? _activePreviousWinners;

    /// <summary>Live participant count for the active giveaway, or 0 when none active.</summary>
    public int GetActiveParticipantCount() =>
        Active is null ? 0 : _giveaways.GetParticipantCount(Active.Id);

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
        int winnerCount, IReadOnlyList<string>? platformFilter, bool preventRewinning,
        string? animationId = null)
    {
        // Resolve + validate. Unknown id falls back; empty/null falls back.
        var resolvedAnimationId = AnimationCatalog.IsKnown(animationId ?? "")
            ? animationId!
            : AnimationCatalog.DefaultId;

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
            CancelledAt: null,
            AnimationId: resolvedAnimationId);
        _giveaways.Insert(g);
        Active = g;

        // Cache previous winners ONCE so per-message lookups stay O(1) instead of hitting the DB.
        _activePreviousWinners = preventRewinning
            ? new HashSet<string>(_giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id))
            : null;

        Started?.Invoke(new GiveawayStartedEvent(
            g.Id, g.Keyword, g.WinnerCount, g.DurationSeconds, g.StartedAt, g.AnimationId));
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

        // (d) PreventRewinning check — cache populated by Start; defensive fallback to DB if null.
        if (g.PreventRewinning)
        {
            var prevWinners = _activePreviousWinners
                ?? new HashSet<string>(_giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id));
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
        if (Active?.Id == g.Id)
        {
            Active = null;
            _activePreviousWinners = null;
        }

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
        if (Active?.Id == giveawayId)
        {
            Active = null;
            _activePreviousWinners = null;
        }
        Cancelled?.Invoke(new GiveawayCancelledEvent(giveawayId));
    }

    /// <summary>
    /// Builds the OBS roulette animation pool: a shuffled prefix of participants ending
    /// with the actual winners. Capped at 50 entries to keep the overlay snappy.
    /// DisplayName is looked up per participant from the Customer table so the
    /// overlay shows the human-readable name rather than the channel ID
    /// (avatar URL is intentionally not propagated — the animations don't
    /// render avatars and we want to keep the WS payload small).
    /// </summary>
    private IReadOnlyList<GiveawayWinnerDto> BuildAnimationPool(
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

    /// <summary>Resolves DisplayName from the Customer table so the overlay
    /// shows e.g. "Ali Yıldız" instead of the YouTube channel ID
    /// (UCxxxxxx...). Customer is guaranteed to exist at this point because
    /// AddParticipantFromChat creates one before storing the participant.
    /// AvatarUrl is intentionally not propagated to the WS payload.</summary>
    private GiveawayWinnerDto ToWinnerDto(GiveawayParticipant p)
    {
        var customer = _customers.Find(p.Platform, p.Username);
        return new GiveawayWinnerDto(
            Username: p.Username,
            DisplayName: customer?.DisplayName,
            AvatarUrl: null,
            Platform: p.Platform);
    }
}
