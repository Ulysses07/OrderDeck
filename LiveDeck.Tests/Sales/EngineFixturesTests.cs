using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace LiveDeck.Tests.Sales;

public class EngineFixturesTests
{
    private readonly ITestOutputHelper _out;
    public EngineFixturesTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void All_fixtures_have_expected_outcome()
    {
        var path = Path.Combine(System.AppContext.BaseDirectory, "Sales", "Fixtures",
                                "tr_chat_samples.json");
        var json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<Fixtures>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        var codes = new List<ActiveCode>();
        foreach (var c in data.ActiveCodes)
            codes.Add(new ActiveCode("id-" + c.Code, "s1", c.Code, c.Sizes, c.Price, null,
                System.Array.Empty<string>(), 0, null));

        var engine = new OrderCaptureEngine(
            new MessageNormalizer(), new CodeMatcher(), new VariantExtractor(),
            new QuantityExtractor(), new IntentScorer(), new ConfidenceScorer());

        var failures = new List<string>();
        foreach (var s in data.Samples)
        {
            var r = engine.Capture(s.Msg, codes);

            if (r.IsCapture != s.ExpectCapture)
                failures.Add($"[{s.Msg}] expected capture={s.ExpectCapture}, got={r.IsCapture} (conf={r.Confidence})");
            else if (s.ExpectCapture)
            {
                if (r.MatchedCode?.Code != s.ExpectCode)
                    failures.Add($"[{s.Msg}] expected code={s.ExpectCode}, got={r.MatchedCode?.Code}");
                if (r.Size != s.ExpectSize)
                    failures.Add($"[{s.Msg}] expected size={s.ExpectSize}, got={r.Size}");
                if (r.Quantity != s.ExpectQty)
                    failures.Add($"[{s.Msg}] expected qty={s.ExpectQty}, got={r.Quantity}");
            }
        }

        foreach (var f in failures) _out.WriteLine(f);
        failures.Should().BeEmpty($"Expected all {data.Samples.Count} fixtures to pass");
    }

    private sealed record Fixtures(List<CodeFixture> ActiveCodes, List<Sample> Samples);
    private sealed record CodeFixture(string Code, List<string> Sizes, decimal Price);
    private sealed record Sample(
        string Msg,
        bool ExpectCapture,
        string? ExpectCode,
        string? ExpectSize,
        int ExpectQty);
}
