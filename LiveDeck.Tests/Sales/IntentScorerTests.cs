using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class IntentScorerTests
{
    private readonly IntentScorer _s = new();

    [Theory]
    [InlineData("MAVI XL ALDIM", true)]
    [InlineData("MAVI XL ALIYORUM", true)]
    [InlineData("MAVI XL ISTIYORUM", true)]
    [InlineData("MAVI XL OLSUN", true)]
    [InlineData("MAVI XL LUTFEN", true)]
    public void Buying_intent_words_yield_high_score(string msg, bool _)
    {
        _s.Score(msg, originalText: msg).Should().BeGreaterOrEqualTo(70);
    }

    [Theory]
    [InlineData("MAVI XL VAR MI")]
    [InlineData("MAVI XL KALDI MI")]
    [InlineData("MAVI XL NE KADAR")]
    public void Question_tone_lowers_score(string msg)
    {
        _s.Score(msg, originalText: msg).Should().BeLessThan(50);
    }

    [Fact]
    public void Bare_code_only_yields_medium_score()
    {
        _s.Score("MAVI XL", originalText: "MAVI XL")
          .Should().BeInRange(40, 70);
    }

    [Fact]
    public void Heart_or_cart_emoji_in_original_text_boosts_score()
    {
        _s.Score("MAVI XL", originalText: "MAVI XL ❤️").Should().BeGreaterOrEqualTo(60);
        _s.Score("MAVI XL", originalText: "MAVI XL 🛒").Should().BeGreaterOrEqualTo(60);
    }
}
