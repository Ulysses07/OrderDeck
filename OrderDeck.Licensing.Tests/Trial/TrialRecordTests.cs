using System.Text.Json;
using FluentAssertions;
using LiveDeck.Licensing.Trial;
using Xunit;

namespace LiveDeck.Licensing.Tests.Trial;

public class TrialRecordTests
{
    [Fact]
    public void Records_with_same_values_are_equal()
    {
        var a = new TrialRecord(
            StartedAt: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            ExpiresAt: new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
            HardwareFingerprint: "abc",
            Version: 1);
        var b = new TrialRecord(
            StartedAt: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            ExpiresAt: new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
            HardwareFingerprint: "abc",
            Version: 1);
        a.Should().Be(b);
    }

    [Fact]
    public void Json_roundtrip_preserves_all_fields()
    {
        var original = new TrialRecord(
            StartedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(14),
            HardwareFingerprint: "fp-test",
            Version: 1);
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(original, opts);
        var parsed = JsonSerializer.Deserialize<TrialRecord>(json, opts);

        parsed.Should().NotBeNull();
        parsed!.HardwareFingerprint.Should().Be("fp-test");
        parsed.Version.Should().Be(1);
        parsed.StartedAt.Should().BeCloseTo(original.StartedAt, TimeSpan.FromSeconds(1));
        parsed.ExpiresAt.Should().BeCloseTo(original.ExpiresAt, TimeSpan.FromSeconds(1));
    }
}
