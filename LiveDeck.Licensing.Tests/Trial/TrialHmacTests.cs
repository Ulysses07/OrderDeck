using FluentAssertions;
using LiveDeck.Licensing.Trial;
using Xunit;

namespace LiveDeck.Licensing.Tests.Trial;

public class TrialHmacTests
{
    private static TrialRecord SampleRecord() => new(
        StartedAt: new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero),
        ExpiresAt: new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
        HardwareFingerprint: "fp",
        Version: 1);

    [Fact]
    public void Compute_is_deterministic()
    {
        var a = TrialHmac.Compute(SampleRecord());
        var b = TrialHmac.Compute(SampleRecord());
        a.Should().Be(b);
    }

    [Fact]
    public void Compute_produces_64_char_hex_lowercase()
    {
        var mac = TrialHmac.Compute(SampleRecord());
        mac.Should().HaveLength(64);
        mac.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Verify_returns_true_for_matching_record()
    {
        var record = SampleRecord();
        var mac = TrialHmac.Compute(record);
        TrialHmac.Verify(record, mac).Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_when_record_tampered()
    {
        var record = SampleRecord();
        var mac = TrialHmac.Compute(record);
        var tampered = record with { ExpiresAt = record.ExpiresAt.AddDays(30) };
        TrialHmac.Verify(tampered, mac).Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_false_for_garbage_mac()
    {
        TrialHmac.Verify(SampleRecord(), "deadbeef").Should().BeFalse();
        TrialHmac.Verify(SampleRecord(), "").Should().BeFalse();
    }
}
