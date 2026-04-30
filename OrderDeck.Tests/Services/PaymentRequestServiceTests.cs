using System;
using System.IO;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Tests.Fakes;
using Xunit;

namespace OrderDeck.Tests.Services;

public class PaymentRequestServiceTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly SettingsStore _store;
    private readonly FakeUrlLauncher _launcher;

    public PaymentRequestServiceTests()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), $"livedeck-pr-{Guid.NewGuid():N}.json");
        _store = new SettingsStore(_settingsPath);
        _launcher = new FakeUrlLauncher();
    }

    public void Dispose()
    {
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    private static Customer MakeCustomer(string? phone) =>
        new("c1", "twitch", "alice", "Alice", null, 100, 100,
            false, null, null, 0, 0m, null, null, phone);

    [Fact]
    public void OpenWhatsApp_PhoneNull_ReturnsPhoneRequired()
    {
        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer(null), 100m, new DateTime(2026, 4, 30));
        result.Should().Be(PaymentRequestResult.PhoneRequired);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public void OpenWhatsApp_PhoneInvalid_ReturnsPhoneRequired()
    {
        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("not-a-phone"), 100m, new DateTime(2026, 4, 30));
        result.Should().Be(PaymentRequestResult.PhoneRequired);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public void OpenWhatsApp_ValidPhone_LaunchesWaMeUrl()
    {
        var settings = new AppSettings();
        settings.Payment.WhatsAppMessageTemplate = "Pay {tutar}";
        settings.Payment.Iban = "TR12";
        _store.Save(settings);

        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("+905551234567"), 100m, new DateTime(2026, 4, 30));

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().HaveCount(1);
        _launcher.LaunchedUrls[0].Should().StartWith("https://wa.me/905551234567?text=");
        _launcher.LaunchedUrls[0].Should().Contain("Pay%20100%2C00");
    }

    [Fact]
    public void OpenWhatsApp_LauncherThrows_ReturnsLaunchFailed()
    {
        _launcher.ThrowOnLaunch = new InvalidOperationException("no handler");
        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("+905551234567"), 100m, new DateTime(2026, 4, 30));
        result.Should().Be(PaymentRequestResult.LaunchFailed);
    }
}
