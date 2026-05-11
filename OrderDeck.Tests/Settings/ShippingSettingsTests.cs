using FluentAssertions;
using OrderDeck.Core.Settings;
using Xunit;

namespace OrderDeck.Tests.Settings;

public sealed class ShippingSettingsTests
{
    [Fact]
    public void Default_state_is_disabled()
    {
        var s = new ShippingSettings();
        s.FreeShippingThreshold.Should().BeNull();
        s.ShippingFee.Should().BeNull();
        s.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_true_when_both_positive()
    {
        var s = new ShippingSettings { FreeShippingThreshold = 5000m, ShippingFee = 150m };
        s.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_false_when_threshold_null() =>
        new ShippingSettings { FreeShippingThreshold = null, ShippingFee = 150m }.IsEnabled.Should().BeFalse();

    [Fact]
    public void IsEnabled_false_when_fee_null() =>
        new ShippingSettings { FreeShippingThreshold = 5000m, ShippingFee = null }.IsEnabled.Should().BeFalse();

    [Fact]
    public void IsEnabled_false_when_threshold_zero() =>
        new ShippingSettings { FreeShippingThreshold = 0m, ShippingFee = 150m }.IsEnabled.Should().BeFalse();

    [Fact]
    public void IsEnabled_false_when_fee_zero() =>
        new ShippingSettings { FreeShippingThreshold = 5000m, ShippingFee = 0m }.IsEnabled.Should().BeFalse();

    [Fact]
    public void IsEnabled_false_when_threshold_negative() =>
        new ShippingSettings { FreeShippingThreshold = -100m, ShippingFee = 150m }.IsEnabled.Should().BeFalse();

    [Fact]
    public void IsEnabled_false_when_fee_negative() =>
        new ShippingSettings { FreeShippingThreshold = 5000m, ShippingFee = -50m }.IsEnabled.Should().BeFalse();

    [Fact]
    public void AppSettings_default_has_shipping_disabled()
    {
        var app = new AppSettings();
        app.Shipping.Should().NotBeNull();
        app.Shipping.IsEnabled.Should().BeFalse();
    }
}
