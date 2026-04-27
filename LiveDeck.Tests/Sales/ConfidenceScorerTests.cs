using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class ConfidenceScorerTests
{
    private static ActiveCode Code() =>
        new("c1", "s1", "MAVI", new[] { "M", "XL" }, 199m, null, System.Array.Empty<string>(), 0, null);

    private readonly ConfidenceScorer _s = new();

    [Fact]
    public void High_intent_with_match_and_size_yields_score_eighty_or_more()
    {
        var r = _s.Score(matched: Code(), size: "XL", quantity: 1, intentScore: 90);
        r.Should().BeGreaterOrEqualTo(80);
    }

    [Fact]
    public void No_match_yields_zero()
    {
        var r = _s.Score(matched: null, size: null, quantity: 1, intentScore: 90);
        r.Should().Be(0);
    }

    [Fact]
    public void Match_without_size_yields_lower_than_with_size()
    {
        var withSize = _s.Score(Code(), "M", 1, intentScore: 70);
        var withoutSize = _s.Score(Code(), null, 1, intentScore: 70);

        withoutSize.Should().BeLessThan(withSize);
    }

    [Fact]
    public void Question_tone_low_intent_capped_at_low_confidence()
    {
        var r = _s.Score(Code(), "M", 1, intentScore: 20);
        r.Should().BeLessThan(50);
    }
}
