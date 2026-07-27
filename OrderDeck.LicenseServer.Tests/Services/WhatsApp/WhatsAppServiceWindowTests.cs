using FluentAssertions;
using OrderDeck.LicenseServer.Services.WhatsApp;
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
    [Theory]
    [InlineData("+90 532 123 45 67", "905321234567")]
    [InlineData("905321234567", "905321234567")]
    [InlineData("+90-532-123-45-67", "905321234567")]
    [InlineData("(0532) 123 45 67", "05321234567")]
    public void Canonical_strips_everything_but_digits(string input, string expected)
    {
        WaPhone.Canonical(input).Should().Be(expected);
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
