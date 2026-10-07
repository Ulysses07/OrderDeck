using FluentAssertions;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WhatsAppServiceWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsOpen_false_when_never_received_inbound()
    {
        WhatsAppServiceWindow.IsOpen(null, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]      // az önce yazdı
    [InlineData(1)]
    [InlineData(23)]
    [InlineData(23.9)]
    public void IsOpen_true_within_24h(double hoursAgo)
    {
        WhatsAppServiceWindow.IsOpen(Now.AddHours(-hoursAgo), Now).Should().BeTrue();
    }

    [Theory]
    [InlineData(24)]     // tam sınır → kapalı
    [InlineData(24.1)]
    [InlineData(72)]
    public void IsOpen_false_at_or_after_24h(double hoursAgo)
    {
        WhatsAppServiceWindow.IsOpen(Now.AddHours(-hoursAgo), Now).Should().BeFalse();
    }

    [Fact]
    public void ExpiresAt_is_inbound_plus_24h()
    {
        var inbound = Now.AddHours(-5);
        WhatsAppServiceWindow.ExpiresAt(inbound).Should().Be(inbound.AddHours(24));
        WhatsAppServiceWindow.ExpiresAt(null).Should().BeNull();
    }

    [Fact]
    public void Remaining_counts_down_and_floors_at_zero()
    {
        WhatsAppServiceWindow.Remaining(Now.AddHours(-20), Now).Should().Be(TimeSpan.FromHours(4));
        WhatsAppServiceWindow.Remaining(Now.AddHours(-30), Now).Should().Be(TimeSpan.Zero);
        WhatsAppServiceWindow.Remaining(null, Now).Should().Be(TimeSpan.Zero);
    }
}

public sealed class WaPhoneTests
{
    // Numara her koşuda üretilir; InlineData yalnız yazılış biçimini ve beklenen
    // öneki taşır. {0} operatör kodu (3 hane), {1} 3 hane, {2} ve {3} ikişer hane.
    [Theory]
    [InlineData("+90 {0} {1} {2} {3}", "90")]
    [InlineData("90{0}{1}{2}{3}", "90")]
    [InlineData("+90-{0}-{1}-{2}-{3}", "90")]
    [InlineData("(0{0}) {1} {2} {3}", "0")]
    public void Canonical_strips_everything_but_digits(string format, string expectedPrefix)
    {
        var n = TestPhone.NewNational();
        var input = string.Format(format, n[..3], n[3..6], n[6..8], n[8..]);

        WaPhone.Canonical(input).Should().Be(expectedPrefix + n);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Canonical_returns_empty_for_blank(string? input)
    {
        WaPhone.Canonical(input).Should().BeEmpty();
    }
}
