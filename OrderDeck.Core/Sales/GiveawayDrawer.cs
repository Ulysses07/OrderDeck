using System;
using System.Collections.Generic;

namespace OrderDeck.Core.Sales;

/// <summary>
/// Selects up to <c>winnerCount</c> winners from a participant list using a deterministic
/// shuffle keyed by <c>randomSeed</c>. Pure function; no DB or wall-clock dependency.
///
/// The seed → 32-bit int conversion uses FNV-1a (deterministic across runtimes, unlike
/// <see cref="string.GetHashCode"/> which is randomized in .NET).
/// </summary>
public sealed class GiveawayDrawer
{
    public IReadOnlyList<GiveawayParticipant> Pick(
        IReadOnlyList<GiveawayParticipant> participants,
        int winnerCount,
        string randomSeed)
    {
        if (winnerCount <= 0 || participants.Count == 0)
            return System.Array.Empty<GiveawayParticipant>();

        // Copy + shuffle (Fisher-Yates) — leaves caller's list untouched.
        var pool = new GiveawayParticipant[participants.Count];
        for (int i = 0; i < participants.Count; i++) pool[i] = participants[i];

        var rng = new Random(unchecked((int)Fnv1a32(randomSeed)));
        for (int i = pool.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        int take = Math.Min(winnerCount, pool.Length);
        var winners = new GiveawayParticipant[take];
        Array.Copy(pool, 0, winners, 0, take);
        return winners;
    }

    /// <summary>
    /// FNV-1a 32-bit hash. Stable across processes and platforms.
    /// </summary>
    private static uint Fnv1a32(string s)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
