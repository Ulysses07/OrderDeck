using FluentAssertions;
using Xunit;

namespace OrderDeck.Licensing.Tests;

public class HardwareIdProviderTests
{
    [Fact]
    public void ComputeHash_is_deterministic_for_same_inputs()
    {
        var a = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "alice");
        var b = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "alice");
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeHash_differs_for_different_inputs()
    {
        var a = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "alice");
        var b = HardwareIdProvider.ComputeHash("guid-2", "cpu-A", "alice");
        var c = HardwareIdProvider.ComputeHash("guid-1", "cpu-B", "alice");
        var d = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "bob");

        a.Should().NotBe(b);
        a.Should().NotBe(c);
        a.Should().NotBe(d);
    }

    [Fact]
    public void ComputeHash_returns_64_char_lowercase_hex()
    {
        var hash = HardwareIdProvider.ComputeHash("g", "c", "u");
        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeHash_username_is_case_insensitive()
    {
        var lower = HardwareIdProvider.ComputeHash("g", "c", "alice");
        var upper = HardwareIdProvider.ComputeHash("g", "c", "ALICE");
        var mixed = HardwareIdProvider.ComputeHash("g", "c", "Alice");
        lower.Should().Be(upper);
        lower.Should().Be(mixed);
    }

    [Fact]
    public void GetHardwareId_returns_non_empty_string_on_real_machine()
    {
        // Integration test — runs WMI/Registry; only validates that it doesn't throw.
        var provider = new HardwareIdProvider();
        var id = provider.GetHardwareId();
        id.Should().NotBeNullOrWhiteSpace();
        id.Should().HaveLength(64);
    }
}
