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

        return new SettingsViewModel(settings, store, shortcutsTab, intakeForm);
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
}
