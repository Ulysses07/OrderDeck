using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
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

    /// <summary>Tüm balance HTTP istekleri 404 — testler eski sync OpenWhatsApp
    /// pattern'iyle çalışıyor, async path E3b'ye özel.</summary>
    private static PaymentRequestService MakeSut(SettingsStore store, FakeUrlLauncher launcher)
    {
        var http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        var licProv = new StubLicenseProvider();
        return new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
            api, licProv);
    }

    private sealed class StubLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => null;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            return System.Threading.Tasks.Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    /// <summary>Cloud API yolunu sürebilmek için iki uca cevap veren handler:
    /// /api/v1/me/licenses (lisans id çözümü) ve .../whatsapp/send (gönderim).
    /// Diğer her şey 404 — bakiye uçları dahil, o yol testte kullanılmıyor.</summary>
    private sealed class WhatsAppStubHandler : HttpMessageHandler
    {
        public static readonly Guid LicenseId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        public const string LicenseKey = "LDK-TEST-KEY";

        public List<string> SentBodies { get; } = new();

        /// <summary>Gönderim ucunun döneceği gövde. Varsayılan: başarılı.</summary>
        public string SendResponseJson { get; set; } =
            """{"ok":true,"errorCode":null,"errorMessage":null,"messageId":"66666666-7777-8888-9999-000000000000"}""";

        /// <summary>Gönderim ucunun HTTP durum kodu. Varsayılan: 200.</summary>
        public HttpStatusCode SendStatusCode { get; set; } = HttpStatusCode.OK;

        /// <summary>true ise gönderim ucu, iptal edilmemiş bir token'la gelen
        /// zaman aşımını taklit eder: OperationCanceledException fırlatır.
        /// (HttpClient kendi timeout'unda TaskCanceledException fırlatır, o da
        /// bu tipten türer — ama LicenseApiClient TaskCanceledException'ı
        /// LicenseApiNetworkException'a çeviriyor. Servis katmanındaki filtreyi
        /// sınamak için türetilmemiş temel tipi kullanıyoruz.)</summary>
        public bool ThrowTimeoutOnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/v1/me/licenses")
            {
                return Json($$"""
                    [{"licenseKey":"{{LicenseKey}}","skuCode":"STD",
                      "expiresAt":"2030-01-01T00:00:00+00:00","revokedAt":null,
                      "id":"{{LicenseId}}"}]
                    """);
            }

            if (path.EndsWith("/whatsapp/send", StringComparison.Ordinal))
            {
                if (ThrowTimeoutOnSend) throw new OperationCanceledException("timeout");
                SentBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(SendResponseJson, SendStatusCode);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class FixedLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => WhatsAppStubHandler.LicenseKey;
    }

    private static (PaymentRequestService Sut, WhatsAppStubHandler Handler) MakeCloudSut(
        SettingsStore store, FakeUrlLauncher launcher, ICurrentLicenseProvider? licenseProvider = null)
    {
        var handler = new WhatsAppStubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        var sut = new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
            api, licenseProvider ?? new FixedLicenseProvider());
        return (sut, handler);
    }

    /// <summary>Cloud API açık, sade şablonlu bir ayar dosyası yazar.
    /// (Dosya hiç yazılmazsa SettingsStore varsayılanı döner → UseCloudApi false.)</summary>
    private void EnableCloudApi()
    {
        var settings = new AppSettings();
        settings.Payment.WhatsAppMessageTemplate = "Ödeme: {tutar} TL";
        settings.Payment.UseCloudApi = true;
        _store.Save(settings);
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
        var sut = MakeSut(_store, _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer(null), 100m, new DateTime(2026, 4, 30));
        result.Should().Be(PaymentRequestResult.PhoneRequired);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public void OpenWhatsApp_PhoneInvalid_ReturnsPhoneRequired()
    {
        var sut = MakeSut(_store, _launcher);
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

        var sut = MakeSut(_store, _launcher);
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
        var sut = MakeSut(_store, _launcher);
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

        var sut = MakeSut(_store, _launcher);
        var result = sut.OpenWhatsApp(MakeCustomer("+905551234567"), 3000m, new DateTime(2026, 4, 30));

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().HaveCount(1);
        var url = _launcher.LaunchedUrls[0];
        url.Should().Contain("Toplam%3A%203.150%2C00");
        url.Should().Contain("Urun%3A%203.000%2C00");
        url.Should().Contain("Kargo%3A%20150%2C00%20TL");
    }

    // ── PR-E: ShippingWon kazandın WhatsApp ──────────────────────────────

    [Fact]
    public void OpenShippingWonWhatsApp_PhoneNull_ReturnsPhoneRequired()
    {
        var sut = MakeSut(_store, _launcher);
        var result = sut.OpenShippingWonWhatsApp(MakeCustomer(null), 5300m);
        result.Should().Be(PaymentRequestResult.PhoneRequired);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public void OpenShippingWonWhatsApp_EmptyTemplate_ReturnsOpenedSilently()
    {
        // Template boş bırakılırsa mesaj atılmaz — sessiz çıkış.
        var settings = new AppSettings();
        settings.Payment.ShippingWonTemplate = "";
        _store.Save(settings);

        var sut = MakeSut(_store, _launcher);
        var result = sut.OpenShippingWonWhatsApp(MakeCustomer("+905551234567"), 5300m);
        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public void OpenShippingWonWhatsApp_ValidPhone_LaunchesWaMeUrlWithCumulativeAmount()
    {
        var settings = new AppSettings();
        settings.Payment.ShippingWonTemplate = "Tebrikler {ad}, {kumulatif_tutar} TL aldın!";
        _store.Save(settings);

        var sut = MakeSut(_store, _launcher);
        var result = sut.OpenShippingWonWhatsApp(MakeCustomer("+905551234567"), 5300m);

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().HaveCount(1);
        _launcher.LaunchedUrls[0].Should().StartWith("https://wa.me/905551234567?text=");
        _launcher.LaunchedUrls[0].Should().Contain("Tebrikler%20Alice");
        _launcher.LaunchedUrls[0].Should().Contain("5.300%2C00%20TL");
    }

    // ── WhatsApp Cloud API ile doğrudan gönderim (2026-07-28) ────────────

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiEnabled_SendsWithoutLaunchingWaMe()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        result.Should().Be(PaymentRequestResult.Sent);
        _launcher.LaunchedUrls.Should().BeEmpty();
        handler.SentBodies.Should().ContainSingle();
        handler.SentBodies[0].Should().Contain("\"origin\":\"wpf-payment\"");
        // "Ödeme: {tutar} TL" → tr-TR "N2" ile "Ödeme: 250,00 TL". System.Text.Json
        // varsayılan encoder'ı ASCII dışını kaçırır: "Ö" → \u00D6.
        handler.SentBodies[0].Should().Contain(@"\u00D6deme: 250,00 TL");
    }

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiReportsWindowClosed_FallsBackToWaMe()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.SendResponseJson =
            """{"ok":false,"errorCode":"window_closed","errorMessage":"kapalı","messageId":null}""";

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().StartWith("https://wa.me/");
    }

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiReturnsServerError_FallsBackToWaMe()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.SendStatusCode = HttpStatusCode.InternalServerError;

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().StartWith("https://wa.me/");
    }

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiTimesOut_FallsBackToWaMe()
    {
        // Sözleşme testi (canlı bir hata düzeltmesi DEĞİL): HttpClient zaman
        // aşımında TaskCanceledException fırlatır ve o da
        // OperationCanceledException'dan TÜRER — yani "ex is not
        // OperationCanceledException" filtresi bu tipi yakalamaz. Pratikte
        // LicenseApiClient zaman aşımını bir katman altta
        // LicenseApiNetworkException'a çevirdiği için wa.me geri düşüşü zaten
        // bozuk değildi; buradaki catch derinlemesine savunma. Test, çıplak bir
        // OperationCanceledException fırlatarak (LicenseApiClient'ın soğurmadığı
        // tek şekil) filtrenin token'a bakmasını garantiye alır. ct verilmiyor
        // (default) — prod'daki iki çağrı yeri de böyle, yani
        // ct.IsCancellationRequested false. İleride biri catch'i sadeleştirirse
        // bu test patlamalı.
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.ThrowTimeoutOnSend = true;

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        result.Should().Be(PaymentRequestResult.Opened);
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().StartWith("https://wa.me/");
    }

    [Fact]
    public async Task OpenWhatsAppAsync_LicenseUnresolvable_FallsBackToWaMeWithoutSending()
    {
        EnableCloudApi();
        // CurrentLicenseKey null → ResolveLicenseIdAsync null döner, POST hiç atılmaz.
        var (sut, handler) = MakeCloudSut(_store, _launcher, new StubLicenseProvider());

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        result.Should().Be(PaymentRequestResult.Opened);
        handler.SentBodies.Should().BeEmpty();
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().StartWith("https://wa.me/");
    }

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiEnabled_SendsIdempotencyKey()
    {
        // Dayanıklılık katmanı POST'u yeniden denerse gövde aynen tekrar gider;
        // anahtar sunucunun tekrarı elemesi için gövdede olmak zorunda.
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);

        await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        handler.SentBodies.Should().ContainSingle();
        using var body = JsonDocument.Parse(handler.SentBodies[0]);
        var raw = body.RootElement.GetProperty("idempotencyKey").GetString();
        Guid.TryParse(raw, out var key).Should().BeTrue();
        key.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void InProgressErrorCode_LiteralIsPinned()
    {
        // Sunucudaki karşılığı LicensesWhatsAppSendController.ErrInProgress —
        // LicenseServer bu projeye referans vermediği için değer DUPLİKE duruyor.
        // İkisi sessizce ayrışırsa WPF in_progress'i tanımaz, wa.me açar ve
        // operatör uçuştaki gönderimin üstüne ikinci bir kopya yollar. Aynı
        // iddia sunucu tarafında da var (LicensesWhatsAppSendTests).
        WhatsAppSendErrorCodes.InProgress.Should().Be("in_progress");
    }

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiReportsInProgress_DoesNotLaunchWaMe()
    {
        // Gönderim gerçekten uçuşta: wa.me açmak operatörü ikinci bir kopya
        // yollamaya davet eder.
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.SendResponseJson =
            """{"ok":false,"errorCode":"in_progress","errorMessage":"işleniyor","messageId":null}""";

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        result.Should().Be(PaymentRequestResult.Sent);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiDisabled_LaunchesWaMeAsBefore()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);   // UseCloudApi varsayılan false

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28));

        result.Should().Be(PaymentRequestResult.Opened);
        handler.SentBodies.Should().BeEmpty();
        _launcher.LaunchedUrls.Should().ContainSingle();
    }
}
