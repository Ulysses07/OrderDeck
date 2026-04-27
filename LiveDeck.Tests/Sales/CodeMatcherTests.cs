using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class CodeMatcherTests
{
    private static ActiveCode Code(string code, params string[] sizes) =>
        new("id-" + code, "s1", code, sizes, 1m, null, System.Array.Empty<string>(), 0, null);

    private readonly CodeMatcher _matcher = new();

    [Fact]
    public void Exact_match_wins()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M"), Code("KIRMIZI", "M") };

        var match = _matcher.Match("MAVI M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Single_typo_within_distance_one_matches()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        var match = _matcher.Match("MAV1 M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Returns_null_when_no_active_code_matches()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        var match = _matcher.Match("MERHABA NASILSIN", codes);

        match.Should().BeNull();
    }

    [Fact]
    public void Alias_matches_when_main_code_does_not()
    {
        var codes = new List<ActiveCode>
        {
            new("id1", "s1", "MAVI", new[] { "M" }, 1m, null,
                new[] { "OCEAN", "DENIZ" }, 0, null)
        };

        var match = _matcher.Match("DENIZ M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Picks_best_match_when_multiple_codes_partially_match()
    {
        var codes = new List<ActiveCode>
        {
            Code("MAVI", "M"),
            Code("MAVIS", "M")
        };

        var match = _matcher.Match("MAVI M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("MAVI");
    }

    [Fact]
    public void Match_against_multi_word_code()
    {
        var codes = new List<ActiveCode> { Code("KIRMIZI ELBISE", "M") };

        var match = _matcher.Match("KIRMIZI ELBISE M ALDIM", codes);

        match.Should().NotBeNull();
        match!.Code.Should().Be("KIRMIZI ELBISE");
    }
}
