using OrderDeck.Licensing;

namespace OrderDeck.Licensing.Tests.TestHelpers;

public sealed class FakeHardwareIdProvider : IHardwareIdProvider
{
    public string Id { get; set; } = "test-hw-fingerprint-deadbeef";
    public string? LegacyId { get; set; }  // null when no migration scenario is being tested
    public string GetHardwareId() => Id;
    public string? GetLegacyHardwareId() => LegacyId;
}
