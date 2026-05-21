using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Shortcuts;
using OrderDeck.Licensing.Api;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class SettingsViewModel_PaymentTests : IDisposable
{
    private readonly string _path;

    public SettingsViewModel_PaymentTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"orderdeck-svm-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>HttpMessageHandler that always returns 404 — keeps IntakeForm.LoadAsync fire-and-forget tame.</summary>
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}")
            });
    }

    private SettingsViewModel CreateVm(AppSettings settings, SettingsStore store)
    {
        var registry = new ShortcutRegistry(store);
        // ShortcutBinder is only invoked from ShortcutsTabViewModel.SaveCommand —
        // not used in payment tests, so null! is acceptable here.
        var shortcutsTab = new ShortcutsTabViewModel(registry, null!);

        var http = new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var intakeForm = new IntakeFormSettingsViewModel(api);
        var shopperApp = new ShopperAppSettingsViewModel(api, Microsoft.Extensions.Logging.Abstractions.NullLogger<ShopperAppSettingsViewModel>.Instance);

        return new SettingsViewModel(settings, store, shortcutsTab, intakeForm, shopperApp);
    }

    [Fact]
    public void Load_PopulatesPaymentFieldsFromSettings()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings();
        s.Payment.WhatsAppMessageTemplate = "Hi {ad}";
        s.Payment.Iban = "TR12";
        s.Payment.AccountHolder = "Burak";
        s.Payment.Papara = "1234567";

        var sut = CreateVm(s, store);

        sut.PaymentTemplate.Should().Be("Hi {ad}");
        sut.Iban.Should().Be("TR12");
        sut.AccountHolder.Should().Be("Burak");
        sut.Papara.Should().Be("1234567");
    }

    [Fact]
    public void Save_PersistsPaymentFields()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings(); // OverlayPort defaults to 4747 (valid)

        var sut = CreateVm(s, store);
        sut.PaymentTemplate = "New {tutar}";
        sut.Iban = "TR99";
        sut.AccountHolder = "X";
        sut.Papara = "9";

        sut.SaveCommand.Execute(null);

        sut.Saved.Should().BeTrue();

        var loaded = store.Load();
        loaded.Payment.WhatsAppMessageTemplate.Should().Be("New {tutar}");
        loaded.Payment.Iban.Should().Be("TR99");
        loaded.Payment.AccountHolder.Should().Be("X");
        loaded.Payment.Papara.Should().Be("9");
    }

    // ── Kargo PR A — Shipping settings ──────────────────────────────────

    [Fact]
    public void Load_displays_shipping_as_empty_when_settings_null()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings(); // Shipping defaults to null/null

        var sut = CreateVm(s, store);

        sut.FreeShippingThresholdText.Should().BeEmpty();
        sut.ShippingFeeText.Should().BeEmpty();
    }

    [Fact]
    public void Load_displays_shipping_values_when_set()
    {
        var store = new SettingsStore(_path);
        var s = new AppSettings();
        s.Shipping.FreeShippingThreshold = 5000m;
        s.Shipping.ShippingFee = 150m;

        var sut = CreateVm(s, store);

        sut.FreeShippingThresholdText.Should().Be("5000");
        sut.ShippingFeeText.Should().Be("150");
    }

    [Theory]
    [InlineData("5000", "150", 5000.00, 150.00)]
    [InlineData("4999,99", "149,50", 4999.99, 149.50)]   // Türkçe virgül
    [InlineData("4999.99", "149.50", 4999.99, 149.50)]   // Invariant nokta
    public void Save_persists_shipping_values_parsing_both_decimal_styles(
        string thresholdText, string feeText, decimal expectedThreshold, decimal expectedFee)
    {
        var store = new SettingsStore(_path);
        var sut = CreateVm(new AppSettings(), store);
        sut.FreeShippingThresholdText = thresholdText;
        sut.ShippingFeeText = feeText;

        sut.SaveCommand.Execute(null);
        sut.Saved.Should().BeTrue();

        var loaded = store.Load();
        loaded.Shipping.FreeShippingThreshold.Should().Be(expectedThreshold);
        loaded.Shipping.ShippingFee.Should().Be(expectedFee);
        loaded.Shipping.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("0", "0")]
    [InlineData("-100", "-50")]
    [InlineData("abc", "xyz")]
    public void Save_disables_shipping_when_invalid_or_zero_or_negative(
        string thresholdText, string feeText)
    {
        var store = new SettingsStore(_path);
        var sut = CreateVm(new AppSettings(), store);
        sut.FreeShippingThresholdText = thresholdText;
        sut.ShippingFeeText = feeText;

        sut.SaveCommand.Execute(null);
        sut.Saved.Should().BeTrue();

        var loaded = store.Load();
        loaded.Shipping.FreeShippingThreshold.Should().BeNull();
        loaded.Shipping.ShippingFee.Should().BeNull();
        loaded.Shipping.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Save_one_field_only_persists_partial_state()
    {
        // Operatör sadece threshold girip fee'yi unutursa: ikisi de
        // bağımsız parse; IsEnabled false kalır (ShippingSettings.IsEnabled
        // ikisinin de positive olmasını ister).
        var store = new SettingsStore(_path);
        var sut = CreateVm(new AppSettings(), store);
        sut.FreeShippingThresholdText = "5000";
        sut.ShippingFeeText = ""; // forgot

        sut.SaveCommand.Execute(null);

        var loaded = store.Load();
        loaded.Shipping.FreeShippingThreshold.Should().Be(5000m);
        loaded.Shipping.ShippingFee.Should().BeNull();
        loaded.Shipping.IsEnabled.Should().BeFalse();
    }
}
