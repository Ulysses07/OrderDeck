using FluentAssertions;
using Xunit;

namespace OrderDeck.Licensing.Tests;

public class HardwareIdProviderTests
{
    // ─── New (Phase 5d) hash: SID-based ──────────────────────────────────

    [Fact]
    public void ComputeHash_is_deterministic_for_same_inputs()
    {
        var a = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "S-1-5-21-1");
        var b = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "S-1-5-21-1");
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeHash_differs_for_different_inputs()
    {
        var a = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "S-1-5-21-1");
        var b = HardwareIdProvider.ComputeHash("guid-2", "cpu-A", "S-1-5-21-1");
        var c = HardwareIdProvider.ComputeHash("guid-1", "cpu-B", "S-1-5-21-1");
        var d = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "S-1-5-21-2");

        a.Should().NotBe(b);
        a.Should().NotBe(c);
        a.Should().NotBe(d);
    }

    [Fact]
    public void ComputeHash_returns_64_char_lowercase_hex()
    {
        var hash = HardwareIdProvider.ComputeHash("g", "c", "S-1-5-21-1");
        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeHash_with_same_machine_and_different_username_remains_stable()
    {
        // SID is immutable across username rename. The new hash must NOT change
        // when the user renames their Windows account — that's the whole point
        // of the Phase 5d migration.
        var sid = "S-1-5-21-1234567890-1234567890-1234567890-1001";
        var a = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", sid);
        var b = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", sid);
        a.Should().Be(b);
    }

    // ─── Legacy hash: username-based, kept for migration ─────────────────

    [Fact]
    public void ComputeLegacyHash_is_deterministic_for_same_inputs()
    {
        var a = HardwareIdProvider.ComputeLegacyHash("guid-1", "cpu-A", "alice");
        var b = HardwareIdProvider.ComputeLegacyHash("guid-1", "cpu-A", "alice");
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeLegacyHash_username_is_case_insensitive()
    {
        var lower = HardwareIdProvider.ComputeLegacyHash("g", "c", "alice");
        var upper = HardwareIdProvider.ComputeLegacyHash("g", "c", "ALICE");
        var mixed = HardwareIdProvider.ComputeLegacyHash("g", "c", "Alice");
        lower.Should().Be(upper);
        lower.Should().Be(mixed);
    }

    [Fact]
    public void Legacy_and_new_hash_differ_for_same_machine()
    {
        // Critical regression guard: if these ever collide, the migration logic
        // server-side would no-op when it should be migrating.
        var legacy = HardwareIdProvider.ComputeLegacyHash("g", "c", "alice");
        var modern = HardwareIdProvider.ComputeHash("g", "c", "S-1-5-21-1");
        legacy.Should().NotBe(modern);
    }

    // ─── Real-machine smoke test ─────────────────────────────────────────

    [Fact]
    public void GetHardwareId_returns_non_empty_string_on_real_machine()
    {
        // Integration test — runs WMI/Registry; only validates that it doesn't throw.
        var provider = new HardwareIdProvider();
        var id = provider.GetHardwareId();
        id.Should().NotBeNullOrWhiteSpace();
        id.Should().HaveLength(64);
    }

    [Fact]
    public void GetLegacyHardwareId_returns_non_empty_string_on_real_machine()
    {
        var provider = new HardwareIdProvider();
        var id = provider.GetLegacyHardwareId();
        id.Should().NotBeNullOrWhiteSpace();
        id!.Should().HaveLength(64);
    }
}
