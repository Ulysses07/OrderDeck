using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Şablon seçicinin hata yolları. Buradaki asıl mesele mesaj metni değil,
/// <b>hangi exception tipinin</b> yakalandığı: LicenseApiClient başarısız
/// yanıtı ThrowMappedAsync ile sarmaladığı için 5xx dışarı
/// <c>LicenseApiUnknownException</c> olarak çıkıyor. ViewModel bir dönem
/// <c>HttpRequestException</c> yakalamaya çalışıyordu; o dal hiç çalışmıyor,
/// WhatsApp'ı henüz bağlamamış yayıncı "panelden bağla" yönlendirmesi yerine
/// genel "tekrar dene" mesajını görüyordu — yani özellik tam da ilk kullanım
/// anında yanlış teşhis koyuyordu. Derleyici ölü catch dalını uyarmadığı için
/// bunu ancak test yakalar.
/// </summary>
public class WhatsAppCloudSettingsViewModelTests
{
    private const string LicenseKey = "OD-TEST-KEY";
    private static readonly Guid LicenseId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class StubLicenseProvider : ICurrentLicenseProvider
    {
        public StubLicenseProvider(string? key) => CurrentLicenseKey = key;
        public string? CurrentLicenseKey { get; }
    }

    /// <summary>/me/licenses'a tek lisans, şablon ucuna testin verdiği yanıt.</summary>
    private static WhatsAppCloudSettingsViewModel Build(
        Func<HttpRequestMessage, HttpResponseMessage> templatesResponder,
        string? licenseKey = LicenseKey)
    {
        var handler = new FakeHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/v1/me/licenses"
                ? FakeHttpMessageHandler.Json(200, $$"""
                    [{"licenseKey":"{{LicenseKey}}","skuCode":"PRO",
                      "expiresAt":"2030-01-01T00:00:00Z","revokedAt":null,
                      "id":"{{LicenseId}}"}]
                    """)
                : templatesResponder(req));

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());

        return new WhatsAppCloudSettingsViewModel(
            api,
            new StubLicenseProvider(licenseKey),
            NullLogger<WhatsAppCloudSettingsViewModel>.Instance);
    }

    [Fact]
    public async Task LoadTemplates_shows_link_whatsapp_hint_when_server_returns_503()
    {
        var vm = Build(_ => FakeHttpMessageHandler.Problem(
            503, "no-whatsapp-account", "WhatsApp hesabı bağlı değil"));

        await vm.LoadTemplatesCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Contain("panelden WhatsApp'ı bağla");
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadTemplates_shows_generic_error_on_other_failures()
    {
        var vm = Build(_ => FakeHttpMessageHandler.Problem(500, "boom"));

        await vm.LoadTemplatesCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Contain("Şablonlar okunamadı");
    }

    [Fact]
    public async Task LoadTemplates_populates_list_on_success()
    {
        var vm = Build(_ => FakeHttpMessageHandler.Json(200, """
            [{"name":"siparis_onay","language":"tr","category":"UTILITY","headerText":null,
              "bodyText":"Merhaba {{1}}","footerText":null,"buttons":[],
              "parameterCount":1,"parameterExamples":["Ayse"],"unsupportedReason":null}]
            """));

        await vm.LoadTemplatesCommand.ExecuteAsync(null);

        vm.Templates.Should().HaveCount(1);
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LoadTemplates_reports_missing_activation_when_no_license_key()
    {
        var vm = Build(_ => FakeHttpMessageHandler.Empty(200), licenseKey: null);

        await vm.LoadTemplatesCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Contain("aktivasyon");
    }
}
