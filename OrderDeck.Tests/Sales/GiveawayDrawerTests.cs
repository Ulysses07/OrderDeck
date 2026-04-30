using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Sales;
using Xunit;

namespace OrderDeck.Tests.Sales;

public class GiveawayDrawerTests
{
    private static GiveawayParticipant Pp(string id, string username) =>
        new(id, "g1", "c-" + id, "instagram", username, EnteredAt: 100, IsWinner: false);

    private readonly GiveawayDrawer _drawer = new();

    [Fact]
    public void Pick_returns_empty_when_no_participants()
    {
        var winners = _drawer.Pick(System.Array.Empty<GiveawayParticipant>(),
                                    winnerCount: 3, randomSeed: "any");
        winners.Should().BeEmpty();
    }

    [Fact]
    public void Pick_returns_one_when_one_participant_three_winners()
    {
        var ps = new[] { Pp("p1", "@a") };
        var winners = _drawer.Pick(ps, winnerCount: 3, randomSeed: "seed");
        winners.Should().HaveCount(1);
        winners[0].Username.Should().Be("@a");
    }

    [Fact]
    public void Pick_returns_zero_when_winner_count_is_zero()
    {
        var ps = new[] { Pp("p1", "@a"), Pp("p2", "@b") };
        var winners = _drawer.Pick(ps, winnerCount: 0, randomSeed: "seed");
        winners.Should().BeEmpty();
    }

    [Fact]
    public void Pick_returns_distinct_winners_when_more_participants_than_winners()
    {
        var ps = Enumerable.Range(0, 10).Select(i => Pp($"p{i}", $"@u{i}")).ToList();
        var winners = _drawer.Pick(ps, winnerCount: 3, randomSeed: "seed");

        winners.Should().HaveCount(3);
        winners.Select(w => w.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Pick_is_deterministic_for_same_seed()
    {
        var ps = Enumerable.Range(0, 10).Select(i => Pp($"p{i}", $"@u{i}")).ToList();

        var run1 = _drawer.Pick(ps, winnerCount: 3, randomSeed: "fixed-seed");
        var run2 = _drawer.Pick(ps, winnerCount: 3, randomSeed: "fixed-seed");

        run1.Select(w => w.Id).Should().Equal(run2.Select(w => w.Id));
    }

    [Fact]
    public void Pick_produces_different_winners_for_different_seeds_on_average()
    {
        var ps = Enumerable.Range(0, 100).Select(i => Pp($"p{i}", $"@u{i}")).ToList();

        var seedA = _drawer.Pick(ps, winnerCount: 3, randomSeed: "alpha")
                            .Select(w => w.Id).ToList();
        var seedB = _drawer.Pick(ps, winnerCount: 3, randomSeed: "beta")
                            .Select(w => w.Id).ToList();

        // Probability of identical 3-of-100 picks for unrelated seeds is astronomically low.
        seedA.Should().NotEqual(seedB);
    }
}
