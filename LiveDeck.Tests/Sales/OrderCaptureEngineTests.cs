using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class OrderCaptureEngineTests
{
    private static ActiveCode Code(string code, params string[] sizes) =>
        new("id-" + code, "s1", code, sizes, 100m, null, System.Array.Empty<string>(), 0, null);

    private readonly OrderCaptureEngine _engine = new(
        new MessageNormalizer(),
        new CodeMatcher(),
        new VariantExtractor(),
        new QuantityExtractor(),
        new IntentScorer(),
        new ConfidenceScorer());

    [Fact]
    public void High_confidence_capture_with_explicit_intent()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "S", "M", "XL") };

        var r = _engine.Capture("MAVİ XL aldım", codes);

        r.IsCapture.Should().BeTrue();
        r.MatchedCode!.Code.Should().Be("MAVI");
        r.Size.Should().Be("XL");
        r.Quantity.Should().Be(1);
        r.Confidence.Should().BeGreaterOrEqualTo(80);
    }

    [Fact]
    public void Capture_with_quantity_and_typo()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        var r = _engine.Capture("Mavıı M 2 tane aldim", codes);

        r.IsCapture.Should().BeTrue();
        r.Size.Should().Be("M");
        r.Quantity.Should().Be(2);
    }

    [Fact]
    public void Question_message_does_not_capture()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M", "XL") };

        var r = _engine.Capture("Mavi M kaldı mı?", codes);

        r.Confidence.Should().BeLessThan(50);
        r.IsCapture.Should().BeFalse();
    }

    [Fact]
    public void Unknown_code_does_not_capture()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M") };

        var r = _engine.Capture("KIRMIZI M aldım", codes);

        r.MatchedCode.Should().BeNull();
        r.IsCapture.Should().BeFalse();
    }

    [Fact]
    public void Mid_confidence_returns_pending_capture()
    {
        var codes = new List<ActiveCode> { Code("MAVI", "M", "XL") };

        var r = _engine.Capture("MAVİ aldım", codes);

        r.MatchedCode.Should().NotBeNull();
        r.Size.Should().BeNull();
        r.Confidence.Should().BeInRange(40, 79);
    }
}
