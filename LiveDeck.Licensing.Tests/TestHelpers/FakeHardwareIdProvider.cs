using LiveDeck.Licensing;

namespace LiveDeck.Licensing.Tests.TestHelpers;

public sealed class FakeHardwareIdProvider : IHardwareIdProvider
{
    public string Id { get; set; } = "test-hw-fingerprint-deadbeef";
    public string GetHardwareId() => Id;
}
