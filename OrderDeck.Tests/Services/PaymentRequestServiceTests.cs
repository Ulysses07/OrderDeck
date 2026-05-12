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
        _settingsPath = Path.Combine(Path.GetTempPath(), $"orderdeck-pr-{Guid.NewGuid():N}.json");
        _store = new SettingsStore(_settingsPath);
        _launcher = new FakeUrlLauncher();
    }

    public void Dispose()
    {
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    private static Customer MakeCustomer(string? phone, bool recipientPaysActive = false) =>
        new("c1", "twitch", "alice", "Alice", null, 100, 100,
            false, null, null, 0, 0m, null, null, phone,
            RecipientPaysActive: recipientPaysActive);

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

    // ── Kargo entegrasyon (2026-05-12) ──────────────────────────────────

    [Fact]
    public void ComputeShipping_recipient_pays_active_overrides_threshold()
    {
        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = 5000m;
        settings.Shipping.ShippingFee = 150m;

        var customer = MakeCustomer("+905551234567", recipientPaysActive: true);
        var (total, fee, note) = PaymentRequestService.ComputeShipping(customer, 3000m, settings);

        total.Should().Be(3000m, "alıcı ödemeli — total değişmez");
        fee.Should().BeNull();
        note.Should().Be("Kargo: alıcı ödemeli");
    }

    [Fact]
    public void ComputeShipping_feature_off_returns_empty_note()
    {
        var settings = new AppSettings();   // Shipping defaults null

        var customer = MakeCustomer("+905551234567");
        var (total, fee, note) = PaymentRequestService.ComputeShipping(customer, 3000m, settings);

        total.Should().Be(3000m);
        fee.Should().BeNull();
        note.Should().BeEmpty();
    }

    [Fact]
    public void ComputeShipping_above_threshold_is_free()
    {
        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = 5000m;
        settings.Shipping.ShippingFee = 150m;

        var customer = MakeCustomer("+905551234567");
        var (total, fee, note) = PaymentRequestService.ComputeShipping(customer, 6000m, settings);

        total.Should().Be(6000m);
        fee.Should().BeNull();
        note.Should().Be("Ücretsiz kargo");
    }

    [Fact]
    public void ComputeShipping_below_threshold_adds_fee()
    {
        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = 5000m;
        settings.Shipping.ShippingFee = 150m;

        var customer = MakeCustomer("+905551234567");
        var (total, fee, note) = PaymentRequestService.ComputeShipping(customer, 3000m, settings);

        total.Should().Be(3150m);
        fee.Should().Be(150m);
        note.Should().Be("Kargo: 150,00 TL");
    }

    [Fact]
    public void OpenWhatsApp_template_with_kargo_placeholders_renders_correctly()
    {
        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = 5000m;
        settings.Shipping.ShippingFee = 150m;
        settings.Payment.WhatsAppMessageTemplate =
            "Toplam: {tutar} Urun: {urun_toplami} {kargo}";
        _store.Save(settings);

        var sut = new PaymentRequestService(_store, new WhatsAppMessageBuilder(), _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("+905551234567"), 3000m, new DateTime(2026, 4, 30));

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().HaveCount(1);
        var url = _launcher.LaunchedUrls[0];
        url.Should().Contain("Toplam%3A%203.150%2C00");
        url.Should().Contain("Urun%3A%203.000%2C00");
        url.Should().Contain("Kargo%3A%20150%2C00%20TL");
    }
}
