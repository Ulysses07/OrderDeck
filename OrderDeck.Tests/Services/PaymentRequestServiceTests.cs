using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
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
    private readonly InMemoryPaymentJobStore _jobs = new();

    public PaymentRequestServiceTests()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), $"orderdeck-pr-{Guid.NewGuid():N}.json");
        _store = new SettingsStore(_settingsPath);
        _launcher = new FakeUrlLauncher();
    }

    /// <summary>Tüm balance HTTP istekleri 404 — testler eski sync OpenWhatsApp
    /// pattern'iyle çalışıyor, async path E3b'ye özel.</summary>
    private PaymentRequestService MakeSut(SettingsStore store, FakeUrlLauncher launcher)
    {
        var http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        var licProv = new StubLicenseProvider();
        return new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
            api, licProv, _jobs);
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

    /// <summary>Cloud API yolunu sürebilmek için uçlara cevap veren handler:
    /// /api/v1/me/licenses (lisans id çözümü), .../whatsapp/send (gönderim) ve
    /// .../customer-balance/{preview,apply,transactions/{id}/reverse} (bakiye işlemleri).
    /// Diğer her şey 404.</summary>
    private sealed class WhatsAppStubHandler : HttpMessageHandler
    {
        public static readonly Guid LicenseId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        public const string LicenseKey = "LDK-TEST-KEY";

        // A9 paralel testi SentBodies/AppliedBalanceBodies/ReverseCalls'u eşzamanlı
        // yazar; ham List<T> thread-safe değil — tüm yazımlar bu kilit altında.
        private readonly object _sync = new();

        public List<string> SentBodies { get; } = new();

        /// <summary>Bakiye düşüm ucuna gelen gövdeler (idempotency anahtarı burada).</summary>
        public List<string> AppliedBalanceBodies { get; } = new();

        /// <summary>Preview ucunun döneceği bakiye. 0 → apply hiç çağrılmaz.</summary>
        public decimal PreviewBalance { get; set; }

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

        /// <summary>Apply çağrısı bu problem title'ı ile 409 dönsün (null = normal).</summary>
        public string? ApplyProblemTitle { get; set; }
        /// <summary>Apply çağrısı ağ hatası fırlatsın (gövde YİNE kaydedilir — istek tele çıktı).</summary>
        public bool ThrowTimeoutOnApply { get; set; }
        /// <summary>Reverse çağrılarında yakalanan transactionId'ler.</summary>
        public List<Guid> ReverseCalls { get; } = new();
        public string? ReverseProblemTitle { get; set; }
        public bool ThrowTimeoutOnReverse { get; set; }
        /// <summary>A9: preview cevabından önce beklenir (yarış rendezvous'u).</summary>
        public Func<Task>? OnPreviewAsync { get; set; }

        /// <summary>R4-01: apply cevabından önce beklenir. Parametre kaçıncı
        /// apply çağrısı olduğudur (1'den başlar) — tek bir cevabı yolda
        /// bekletip gecikmiş cevap senaryosunu kurmak için.</summary>
        public Func<int, Task>? OnApplyAsync { get; set; }
        private int _applyCount;

        /// <summary>true ise apply cevabındaki appliedAmount = min(istenen
        /// tutar, PreviewBalance) — gerçek sunucu gibi. Varsayılan false:
        /// eski testler appliedAmount olarak doğrudan PreviewBalance bekliyor
        /// (hepsinde bakiye satış tutarından küçük, yani fark etmiyor).</summary>
        public bool CapAppliedToRequest { get; set; }

        /// <summary>true ise lisans listesi ucu ağ hatası fırlatır: anahtar VAR
        /// ama sunucuya ulaşılamıyor (VPS kapalı / internet yok). Anahtarın hiç
        /// olmadığı durumdan farklıdır — ayrımı A10 testleri sınar.</summary>
        public bool ThrowTimeoutOnLicenses { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/v1/me/licenses")
            {
                if (ThrowTimeoutOnLicenses) throw new TaskCanceledException("stub timeout");
                return Json($$"""
                    [{"licenseKey":"{{LicenseKey}}","skuCode":"STD",
                      "expiresAt":"2030-01-01T00:00:00+00:00","revokedAt":null,
                      "id":"{{LicenseId}}"}]
                    """);
            }

            if (path.EndsWith("/whatsapp/send", StringComparison.Ordinal))
            {
                if (ThrowTimeoutOnSend) throw new OperationCanceledException("timeout");
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                lock (_sync) SentBodies.Add(body);
                return Json(SendResponseJson, SendStatusCode);
            }

            if (path.EndsWith("/customer-balance/preview", StringComparison.Ordinal))
            {
                if (OnPreviewAsync is not null) await OnPreviewAsync();
                return Json($$"""
                    {"wpfCustomerId":"{{Guid.Empty}}","balance":{{PreviewBalance.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                     "updatedAt":"2030-01-01T00:00:00+00:00"}
                    """);
            }

            if (path.EndsWith("/customer-balance/apply", StringComparison.Ordinal))
            {
                // Gövde ÖNCE kaydedilir — zaman aşımı simülasyonunda bile istek tele çıktı sayılır.
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                int sira;
                lock (_sync)
                {
                    AppliedBalanceBodies.Add(body);
                    sira = ++_applyCount;
                }
                if (OnApplyAsync is not null) await OnApplyAsync(sira);
                if (ThrowTimeoutOnApply) throw new TaskCanceledException("stub timeout");
                if (ApplyProblemTitle is not null) return Problem(ApplyProblemTitle);
                var applied = CapAppliedToRequest
                    ? Math.Min(PreviewBalance, AppliedAmountRequested(body))
                    : PreviewBalance;
                return Json($$"""
                    {"transactionId":"{{Guid.NewGuid()}}","appliedAmount":{{applied.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                     "remainingBalance":0}
                    """);
            }

            // /customer-balance/transactions/{id}/reverse
            if (path.Contains("/customer-balance/transactions/", StringComparison.Ordinal)
                && path.EndsWith("/reverse", StringComparison.Ordinal))
            {
                // URL'den Guid'i ayıkla: .../transactions/{guid}/reverse
                var segments = path.Split('/');
                var guidSegment = segments[^2]; // "reverse"'den önceki segment
                var transactionId = Guid.Parse(guidSegment);
                lock (_sync) ReverseCalls.Add(transactionId);
                if (ThrowTimeoutOnReverse) throw new TaskCanceledException("stub timeout");
                if (ReverseProblemTitle is not null) return Problem(ReverseProblemTitle);
                return Json("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static decimal AppliedAmountRequested(string body)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("amount").GetDecimal();
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage Problem(string title) => new((HttpStatusCode)409)
        {
            Content = new StringContent(
                $"{{\"title\":\"{title}\",\"status\":409}}",
                Encoding.UTF8, "application/problem+json"),
        };
    }

    private sealed class FixedLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => WhatsAppStubHandler.LicenseKey;
    }

    /// <param name="jobs">Varsayılan fake depo yerine başka bir depo — A9
    /// yarışın hakemini GERÇEK <see cref="PaymentJobRepository"/> yapıyor.</param>
    private (PaymentRequestService Sut, WhatsAppStubHandler Handler) MakeCloudSut(
        SettingsStore store, FakeUrlLauncher launcher,
        ICurrentLicenseProvider? licenseProvider = null, IPaymentJobStore? jobs = null)
    {
        var handler = new WhatsAppStubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        var sut = new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
            api, licenseProvider ?? new FixedLicenseProvider(), jobs ?? _jobs);
        return (sut, handler);
    }

    /// <summary>Cloud API açık, sade şablonlu bir ayar dosyası yazar.
    /// (Dosya hiç yazılmazsa SettingsStore varsayılanı döner → UseCloudApi false.)</summary>
    /// <param name="configure">Şablon yolunu (IBAN/hesap sahibi/şablon adı)
    /// değiştiren testler için. Varsayılan ayarlarda IBAN boş olduğundan
    /// şablon gövdeye HİÇ eklenmez — eski testlerin beklentisi budur.</param>
    private void EnableCloudApi(Action<AppSettings>? configure = null)
    {
        var settings = new AppSettings();
        settings.Payment.WhatsAppMessageTemplate = "Ödeme: {tutar} TL";
        settings.Payment.UseCloudApi = true;
        configure?.Invoke(settings);
        _store.Save(settings);
    }

    /// <summary>Şablon yolunun açılması için gereken asgari ayar: ödeme
    /// bilgileri + yayıncının Ayarlar ekranında seçtiği şablon ve yuva
    /// eşlemesi. Eşleme artık kodda sabit değil, bu yüzden test de kurmak
    /// zorunda.</summary>
    private static void WithPaymentDetails(AppSettings s)
    {
        s.Payment.Iban = "TR12 0006 4000 0011";
        s.Payment.AccountHolder = "Burak S";
        s.Payment.CloudTemplateName = "odeme_hatirlatma";
        s.Payment.CloudTemplateLanguage = "tr";
        s.Payment.CloudTemplateParams = new List<string>
            { "ad", "tarih", "urun_toplami", "kargo", "tutar", "iban", "hesap_sahibi" };
    }

    public void Dispose()
    {
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    /// <summary><paramref name="id"/> yalnızca bakiye yolu için anlamlı: servis
    /// Customer.Id'yi "N" formatında Guid olarak ayrıştırabilirse bakiye uçlarını
    /// çağırıyor, aksi halde (varsayılan "c1") o yolu tümden atlıyor.</summary>
    private static Customer MakeCustomer(
        string? phone, bool recipientPaysActive = false, string id = "c1") =>
        new(id, "twitch", "alice", "Alice", null, 100, 100,
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
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

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
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

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
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

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
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

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
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

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
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

        handler.SentBodies.Should().ContainSingle();
        using var body = JsonDocument.Parse(handler.SentBodies[0]);
        var raw = body.RootElement.GetProperty("idempotencyKey").GetString();
        Guid.TryParse(raw, out var key).Should().BeTrue();
        key.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task OpenWhatsAppAsync_BalanceApplied_SendsIdempotencyKey()
    {
        // Bakiye düşümü GERÇEK para: dayanıklılık katmanı bu POST'u da 5xx/ağ
        // hatasında yeniden deniyor. Anahtar gövdede olmazsa aynı tık müşterinin
        // bakiyesini iki kez düşürür ve ledger'a iki satır yazar.
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;

        await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N")),
            250m, new DateTime(2026, 7, 28), "cumulative");

        handler.AppliedBalanceBodies.Should().ContainSingle();
        using var body = JsonDocument.Parse(handler.AppliedBalanceBodies[0]);
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
    public async Task OpenWhatsAppAsync_CloudApiReportsInProgress_ReturnsSendPendingWithoutLaunching()
    {
        // in_progress "sonucu bilmiyorum" demek, "gönderildi" DEĞİL: ilk deneme
        // yarıda kesilmişse (deploy sırasında sunucu yeniden başladı, bağlantı
        // koptu, proxy 502) rezervasyon bilerek pending bırakılır ve müşteriye
        // hiçbir şey gitmemiştir — saniyeler sonraki yeniden-deneme yine de
        // in_progress alır. Burada Sent dönmek operatöre SESSİZ bir yalan söyler,
        // çünkü ViewModel'ler Sent için hiçbir şey göstermiyor. wa.me de
        // açılmıyor: gönderim gerçekten uçuştaysa ikinci faturalı kopya gider.
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.SendResponseJson =
            """{"ok":false,"errorCode":"in_progress","errorMessage":"işleniyor","messageId":null}""";

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

        result.Should().Be(PaymentRequestResult.SendPending);
        _launcher.LaunchedUrls.Should().BeEmpty();
    }

    // ── Pencere kapalıyken şablona düşme (2026-07-29) ───────────────────

    /// <summary>Gönderim gövdesindeki <c>template</c> alanı; yoksa null.</summary>
    private static JsonElement? SentTemplate(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("template", out var t) && t.ValueKind != JsonValueKind.Null
            ? t.Clone()
            : null;
    }

    [Fact]
    public async Task OpenWhatsAppAsync_PaymentDetailsPresent_SendsTemplateAlongsideText()
    {
        // Prodda 24s pencere çoğunlukla kapalı → gerçek gönderim yolu şablon.
        // Kararı sunucu veriyor, biz her iki gövdeyi de tek istekte yolluyoruz.
        EnableCloudApi(WithPaymentDetails);
        var (sut, handler) = MakeCloudSut(_store, _launcher);

        await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

        handler.SentBodies.Should().ContainSingle();
        var template = SentTemplate(handler.SentBodies[0]);
        template.Should().NotBeNull();
        template!.Value.GetProperty("name").GetString().Should().Be("odeme_hatirlatma");
        template.Value.GetProperty("languageCode").GetString().Should().Be("tr");

        // 7 parametre: onaylı gövdedeki {{1}}..{{7}} ile birebir.
        var parameters = template.Value.GetProperty("bodyParams").EnumerateArray()
            .Select(p => p.GetString()).ToList();
        parameters.Should().HaveCount(7);
        parameters[0].Should().Be("Alice");
        parameters[5].Should().Be("TR12 0006 4000 0011");
        parameters[6].Should().Be("Burak S");
        parameters.Should().OnlyContain(
            p => !string.IsNullOrEmpty(p), "Meta boş parametreyi reddediyor");
    }

    [Fact]
    public async Task OpenWhatsAppAsync_MissingIban_OmitsTemplate()
    {
        // IBAN girilmemişse şablon parametrelerinden biri boş kalır; Meta bunu
        // her seferinde reddeder. Denemek yerine hiç göndermiyoruz → sunucu
        // window_closed döner, eski wa.me davranışı sürer.
        EnableCloudApi(s =>
        {
            WithPaymentDetails(s);
            s.Payment.Iban = "";
        });
        var (sut, handler) = MakeCloudSut(_store, _launcher);

        await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

        handler.SentBodies.Should().ContainSingle();
        SentTemplate(handler.SentBodies[0]).Should().BeNull();
    }

    [Fact]
    public async Task OpenWhatsAppAsync_NoSlotMapping_OmitsTemplate()
    {
        // Şablon seçili ama hangi yuvaya ne yazılacağı kurulmamış. Tahminle
        // doldurmak yanlış sayıda/sırada parametre demek; Meta reddeder ve her
        // deneme faturalı. Şablonu hiç göndermeyip wa.me'ye düşmek doğru.
        EnableCloudApi(s =>
        {
            WithPaymentDetails(s);
            s.Payment.CloudTemplateParams = new List<string>();
        });
        var (sut, handler) = MakeCloudSut(_store, _launcher);

        await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

        handler.SentBodies.Should().ContainSingle();
        SentTemplate(handler.SentBodies[0]).Should().BeNull();
    }

    [Fact]
    public async Task OpenWhatsAppAsync_BlankTemplateName_OmitsTemplate()
    {
        // Ayar boşaltılarak şablon yolu tümden kapatılabiliyor: şablon henüz
        // Meta'da onaylanmadıysa her gönderim hata alırdı.
        EnableCloudApi(s =>
        {
            WithPaymentDetails(s);
            s.Payment.CloudTemplateName = "";
        });
        var (sut, handler) = MakeCloudSut(_store, _launcher);

        await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

        handler.SentBodies.Should().ContainSingle();
        SentTemplate(handler.SentBodies[0]).Should().BeNull();
    }

    [Fact]
    public async Task OpenWhatsAppAsync_CloudApiDisabled_LaunchesWaMeAsBefore()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);   // UseCloudApi varsayılan false

        var result = await sut.OpenWhatsAppAsync(
            MakeCustomer("+905551234567"), 250m, new DateTime(2026, 7, 28), "cumulative");

        result.Should().Be(PaymentRequestResult.Opened);
        handler.SentBodies.Should().BeEmpty();
        _launcher.LaunchedUrls.Should().ContainSingle();
    }

    // ── R2-01..04: ödeme işi yaşam döngüsü ──────────────────────────────────
    //
    // Anahtar diske iner (created→apply_uncertain), sunucu cevabı kesinleşince
    // applied/no_balance, mesaj müşteriye ulaşınca iş kapanır. Aynı kapsamda
    // (müşteri+oturum) tutar değişirse revizyon: eski düşüm geri alınır, yeni
    // toplamla taze düşüm. Belirsizlikte mesaj GÖNDERİLMEZ (BalanceUncertain).

    private static readonly DateTime T = new(2026, 9, 11);

    /// <summary>Apply gövdesindeki idempotency anahtarı.</summary>
    private static Guid AppliedKey(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return Guid.Parse(doc.RootElement.GetProperty("idempotencyKey").GetString()!);
    }

    [Fact] // A1
    public async Task OpenWhatsAppAsync_taze_satis_dusum_uygulanir_teslimatta_is_kapanir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().ContainSingle();
        var job = _jobs.Snapshot.Single();
        job.ScopeKey.Should().Be("session:s1");
        job.State.Should().Be(PaymentJobState.Applied);
        job.ApplyKey.Should().Be(AppliedKey(handler.AppliedBalanceBodies[0]));
        job.ClosedAt.Should().NotBeNull("mesaj müşteriye ulaştı");
    }

    [Fact] // A2 — eski N02 çekirdeği, iş anlambilimiyle
    public async Task OpenWhatsAppAsync_LaunchFailed_sonrasi_tekrar_ayni_anahtari_kullanir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        _launcher.ThrowOnLaunch = new InvalidOperationException("no handler");
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.LaunchFailed);
        _jobs.Snapshot.Single().ClosedAt.Should().BeNull("mesaj ulaşmadı — iş açık");

        _launcher.ThrowOnLaunch = null;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Select(AppliedKey).Distinct().Should().HaveCount(1,
            "aynı iş = aynı anahtar; sunucu ilk sonucu oynatır, bakiye ikinci kez düşmez");
        _jobs.Snapshot.Single().ClosedAt.Should().NotBeNull();
    }

    [Fact] // A3
    public async Task OpenWhatsAppAsync_bakiye_yoksa_no_balance_kesinlesir_mesaj_dusussuz_gider()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 0m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().BeEmpty("önizleme 0 — anahtar hiç yazılmaz");
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.NoBalance);
        job.AppliedAmount.Should().Be(0m);
        job.ClosedAt.Should().NotBeNull();
    }

    [Fact] // A4 — K3: belirsizlik mesajı BLOKLAR
    public async Task OpenWhatsAppAsync_apply_belirsiz_kalirsa_mesaj_gonderilmez_tekrar_ayni_anahtarla_cozulur()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        handler.ThrowTimeoutOnApply = true;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        _launcher.LaunchedUrls.Should().BeEmpty("düşüm belirsizken mesaj gitmez");
        handler.SentBodies.Should().BeEmpty();
        var job = _jobs.Snapshot.Single();
        job.State.Should().Be(PaymentJobState.ApplyUncertain);
        job.ClosedAt.Should().BeNull();

        handler.ThrowTimeoutOnApply = false;
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Select(AppliedKey).Distinct().Should().HaveCount(1,
            "çözüm aynı anahtarın replay'i — asla yeni anahtar üretilmez");
        _jobs.Snapshot.Single().State.Should().Be(PaymentJobState.Applied);
    }

    [Fact] // A5 — tekrar paylaşım: finansal çağrı YOK
    public async Task OpenWhatsAppAsync_ayni_kapsam_ayni_tutar_ikinci_cagri_finansal_cagri_yapmaz()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().ContainSingle(
            "aynı satışın tekrar paylaşımı — düşüm sonucu diskten okunur");
        handler.ReverseCalls.Should().BeEmpty();
        _launcher.LaunchedUrls.Should().HaveCount(2);
    }

    [Fact] // A5' — farklı oturum = yeni iş (eski "sonraki satış yeni anahtar")
    public async Task OpenWhatsAppAsync_farkli_oturum_yeni_is_yeni_anahtar_uretir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s2"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().HaveCount(2);
        AppliedKey(handler.AppliedBalanceBodies[1]).Should().NotBe(
            AppliedKey(handler.AppliedBalanceBodies[0]),
            "ayrı oturum ayrı satış — ayrı iş, ayrı düşüm");
        _jobs.Snapshot.Should().HaveCount(2);
    }

    [Fact] // A6 — K2: revizyon = geri al + taze uygula
    public async Task OpenWhatsAppAsync_ayni_kapsamda_tutar_degisirse_eski_dusum_geri_alinir_yenisi_uygulanir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        var ilkKey = AppliedKey(handler.AppliedBalanceBodies[0]);

        (await sut.OpenWhatsAppAsync(customer, 300m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(
            ilkKey, "geri alınan, ilk düşümün ledger satırı (tx id = apply anahtarı)");
        handler.AppliedBalanceBodies.Should().HaveCount(2);
        AppliedKey(handler.AppliedBalanceBodies[1]).Should().NotBe(ilkKey,
            "revizyon YENİ anahtarla taze düşümdür");
        var job = _jobs.Snapshot.Single();
        job.ProductTotal.Should().Be(300m);
        job.Revision.Should().Be(1);
        job.State.Should().Be(PaymentJobState.Applied);

        // Revizyonun asıl çıktısı müşterinin gördüğü rakam: 300 − 100 = 200.
        // İş satırı doğru olup mesajın eski tutarı taşıması sessiz bir para hatasıdır.
        _launcher.LaunchedUrls.Should().HaveCount(2);
        _launcher.LaunchedUrls[0].Should().Contain("150%2C00");
        _launcher.LaunchedUrls[1].Should().Contain("200%2C00");
    }

    [Fact] // A7 — geri alma belirsizse revizyon İLERLEMEZ
    public async Task OpenWhatsAppAsync_geri_alma_belirsizse_BalanceUncertain_yeni_dusum_yapilmaz()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);
        var oncekiSentSayisi = _launcher.LaunchedUrls.Count;

        handler.ThrowTimeoutOnReverse = true;
        (await sut.OpenWhatsAppAsync(customer, 300m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        handler.AppliedBalanceBodies.Should().ContainSingle(
            "geri alma kesinleşmeden yeni düşüm çift tahsilat riskidir");
        _launcher.LaunchedUrls.Should().HaveCount(oncekiSentSayisi, "mesaj gitmedi");
        handler.ReverseCalls.Should().HaveCount(2, "bir deneme + bir anında tekrar (K3)");
        _jobs.Snapshot.Single().State.Should().Be(PaymentJobState.ApplyUncertain);
    }

    [Fact] // A8 — already-reversed = idempotent başarı
    public async Task OpenWhatsAppAsync_geri_alma_already_reversed_donerse_basari_sayilir_revizyon_ilerler()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.ReverseProblemTitle = "already-reversed";
        (await sut.OpenWhatsAppAsync(customer, 300m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().HaveCount(2);
        _jobs.Snapshot.Single().State.Should().Be(PaymentJobState.Applied);
    }

    [Fact] // Legacy (034 taşıması) — replay ile kesinleşir, sonucu satışa devrolur
    public async Task OpenWhatsAppAsync_acik_legacy_is_once_replay_ile_cozulur_sonuc_yeni_kapsama_devrolur()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 100m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));
        var legacyKey = Guid.NewGuid();
        _jobs.Seed(new PaymentJob(
            Guid.NewGuid().ToString("N"), customer.Id, "legacy", 250m, 0,
            legacyKey, null, PaymentJobState.ApplyUncertain, 1, 1, null));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().ContainSingle(
            "legacy replay'i aynı zamanda satışın düşümüdür — ikinci apply gerekmez");
        AppliedKey(handler.AppliedBalanceBodies[0]).Should().Be(legacyKey);
        _jobs.Snapshot.Single(j => j.ScopeKey == "legacy").ClosedAt.Should().NotBeNull();
        var hedef = _jobs.Snapshot.Single(j => j.ScopeKey == "session:s1");
        hedef.ApplyKey.Should().Be(legacyKey);
        hedef.State.Should().Be(PaymentJobState.Applied);
    }

    // R4-04: Göç artık her çözülmemiş 033 anahtarını ayrı bir miras işine
    // taşıyor, yani aynı müşteride birden fazla açık miras işi olabilir.
    // Sözleşme: HEPSİNİN sonucu öğrenilir; en yenisi satışa devrolur, kalanı
    // artık düşümdür → geri alınır ve kapatılır. Eskisini görmezden gelmek,
    // sunucuda durmaya devam eden fazla düşümü yerelde izsiz bırakırdı.
    [Fact]
    public async Task OpenWhatsAppAsync_birden_fazla_acik_miras_is_hepsi_cozulur_eskisi_geri_alinir()
    {
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.PreviewBalance = 1000m;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));
        var eskiKey = Guid.NewGuid();
        var yeniKey = Guid.NewGuid();
        _jobs.Seed(new PaymentJob(
            Guid.NewGuid().ToString("N"), customer.Id, $"legacy:{eskiKey:N}", 100m, 0,
            eskiKey, null, PaymentJobState.ApplyUncertain, 100, 100, null));
        _jobs.Seed(new PaymentJob(
            Guid.NewGuid().ToString("N"), customer.Id, $"legacy:{yeniKey:N}", 250m, 0,
            yeniKey, null, PaymentJobState.ApplyUncertain, 200, 200, null));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        // İki miras anahtarı da replay edildi — hiçbiri "bilinmiyor" kalmadı.
        handler.AppliedBalanceBodies.Select(AppliedKey)
            .Should().BeEquivalentTo(new[] { eskiKey, yeniKey });

        // En yenisi satışa devroldu; eskisi geri alındı.
        handler.ReverseCalls.Should().ContainSingle().Which.Should().Be(eskiKey);

        _jobs.Snapshot.Where(j => j.ScopeKey.StartsWith("legacy:", StringComparison.Ordinal))
            .Should().OnlyContain(j => j.ClosedAt != null);
        var hedef = _jobs.Snapshot.Single(j => j.ScopeKey == "session:s1");
        hedef.ApplyKey.Should().Be(yeniKey);
        hedef.State.Should().Be(PaymentJobState.Applied);
    }

    [Fact] // A9 — R2-04: iki eşzamanlı tıklama TEK anahtar üretir
    public async Task OpenWhatsAppAsync_es_zamanli_iki_cagri_tek_is_tek_anahtar()
    {
        // Fake yerine GERÇEK depo: yarışın hakemi SQLite'taki koşullu UPDATE.
        // Paylaşımlı bellek-DB eşzamanlı yazımda SQLITE_LOCKED verebildiği için
        // geçici DOSYA tabanlı veritabanı kullanılır (WAL — prod ile aynı).
        var dbPath = Path.Combine(Path.GetTempPath(), $"odjob-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(dbPath);
        try
        {
            new MigrationRunner(factory).Run();
            var repo = new PaymentJobRepository(factory);
            var (sut, handler) = MakeCloudSut(_store, _launcher, jobs: repo);
            handler.PreviewBalance = 100m;
            var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

            // Rendezvous: iki istek de preview'a VARANA kadar ikisi de bekler —
            // ikisinin de FindOrCreate'i geçip BeginApply'a yarışarak girmesi garanti.
            var arrived = 0;
            var bothArrived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            handler.OnPreviewAsync = async () =>
            {
                if (Interlocked.Increment(ref arrived) == 2) bothArrived.TrySetResult();
                await bothArrived.Task;
            };

            var t1 = Task.Run(() => sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"));
            var t2 = Task.Run(() => sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"));
            var results = await Task.WhenAll(t1, t2);

            results.Should().AllBeEquivalentTo(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().NotBeEmpty();
            handler.AppliedBalanceBodies.Select(AppliedKey).Distinct().Should().HaveCount(1,
                "kaybeden kazananın anahtarını yeniden kullanır — sunucuda tek düşüm");

            using var conn = factory.Open();
            // Dapper zaten test projesinde: satır sayısı iddiası
            Dapper.SqlMapper.ExecuteScalar<long>(conn,
                "SELECT COUNT(*) FROM PaymentJob").Should().Be(1, "UNIQUE kapsam tek iş");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    // ── R4-01: gecikmiş apply cevabı ────────────────────────────────────────
    //
    // Rapor §7 sırası: 250'lik apply uzak deftere işlendi ama CEVABI yolda
    // takıldı; ikinci tıklama aynı anahtarla replay edip 250 sonucunu öğrendi;
    // üçüncü tıklama satışı 50'ye revize etti (250 geri alındı, 50 uygulandı);
    // SONRA ilk 250 cevabı serbest kaldı.
    //
    // Sözleşme: cevap, GÖNDERİLDİĞİ denemeye aittir. Bayat cevap yazılmaz —
    // yerel düşüm 50 kalır, yeniden paylaşım net 0 gösterir. (Koşul olmadan
    // 250 yazılıyor ve müşteriye −200,00 TL'lik bir mesaj gidiyordu.)
    [Fact]
    public async Task OpenWhatsAppAsync_gecikmis_apply_cevabi_revizyonun_dusumunu_ezmez()
    {
        // Yarış hakemi gerçek depo olmalı (A9 ile aynı gerekçe: dosya + WAL).
        var dbPath = Path.Combine(Path.GetTempPath(), $"odjob-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(dbPath);
        try
        {
            new MigrationRunner(factory).Run();
            var repo = new PaymentJobRepository(factory);
            var (sut, handler) = MakeCloudSut(_store, _launcher, jobs: repo);
            handler.PreviewBalance = 1000m;
            handler.CapAppliedToRequest = true; // sunucu istenen tutarı uygular
            var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

            var ilkApplyVardi = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ilkCevapSerbest = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            handler.OnApplyAsync = async sira =>
            {
                if (sira != 1) return;
                ilkApplyVardi.TrySetResult();
                await ilkCevapSerbest.Task;
            };

            var ilkTiklama = Task.Run(() => sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"));
            await ilkApplyVardi.Task; // 250 sunucuda işlendi, cevap yolda

            (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.Opened);      // replay: 250 öğrenildi
            (await sut.OpenWhatsAppAsync(customer, 50m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.Opened);      // revizyon: 50 uygulandı

            ilkCevapSerbest.TrySetResult();
            await ilkTiklama;                                    // gecikmiş 250 cevabı

            var job = repo.FindOrCreate(customer.Id, "session:s1", 50m);
            job.Revision.Should().Be(1);
            job.ProductTotal.Should().Be(50m);
            job.AppliedAmount.Should().Be(50m, "gecikmiş 250'lik cevap bayattır");

            // Aynı satışın yeniden paylaşımı: 50 − 50 = 0. Finansal çağrı da yok.
            var applyAdedi = handler.AppliedBalanceBodies.Count;
            (await sut.OpenWhatsAppAsync(customer, 50m, T, "session:s1"))
                .Should().Be(PaymentRequestResult.Opened);
            handler.AppliedBalanceBodies.Should().HaveCount(applyAdedi);
            _launcher.LaunchedUrls[^1].Should().Contain("0%2C00").And.NotContain("-200");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    // ── A10: lisans çözülemediğinde blok KARARI yerel iş satırına bakar ──────
    //
    // Lisans id çözümü apply'dan ÖNCE patlar: o tıklamada para adına tek bir
    // istek bile gitmemiştir. Yani "belirsiz" olan sunucuya erişim, satışın
    // sonucu değil. Neyi bildiğimizi diskteki iş satırı söyler (ağ gerekmez):
    //   satır yok / created → bu satış için hiç hareket yok → düşümsüz devam
    //   applied / no_balance + aynı tutar → ne düştüğü yazılı → o tutarla devam
    //   apply_uncertain     → gerçekten bilmiyoruz → blokla (K3)
    //   applied + tutar değişti → revizyon geri-alma ister, o da ağsız olmaz → blokla
    //
    // Aksi hâlde tek bir VPS kesintisi, bakiyesi hiç olmayan müşteriler dahil
    // TÜM ödeme mesajlarını durdururdu — yayın ortasında tam iş durması.

    private PaymentJob SeedJob(
        string customerId, string state, decimal productTotal,
        decimal? appliedAmount, Guid? applyKey, string scopeKey = "session:s1")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _jobs.Seed(new PaymentJob(
            Guid.NewGuid().ToString("N"), customerId, scopeKey, productTotal,
            0, applyKey, appliedAmount, state, now, now, null));
    }

    [Fact] // A10a — iş yok: hiç para hareketi olmamış, mesaj düşümsüz gider
    public async Task OpenWhatsAppAsync_lisans_cozulemez_is_yoksa_dusumsuz_devam_eder()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.ThrowTimeoutOnLicenses = true;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().BeEmpty("apply'a hiç gidilmedi");
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().Contain("250%2C00", "düşüm yok — tam tutar");
    }

    [Fact] // A10b — iş applied ve tutar aynı: düşülen miktar diskte yazılı
    public async Task OpenWhatsAppAsync_lisans_cozulemez_applied_is_varsa_kayitli_dusumle_devam_eder()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.ThrowTimeoutOnLicenses = true;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));
        SeedJob(customer.Id, PaymentJobState.Applied, 250m, 100m, Guid.NewGuid());

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.Opened);

        handler.AppliedBalanceBodies.Should().BeEmpty("ikinci düşüm YOK — sonuç zaten kesin");
        _launcher.LaunchedUrls.Should().ContainSingle()
            .Which.Should().Contain("150%2C00", "250 − diskteki 100");
    }

    [Fact] // A10c — iş belirsiz: gerçekten bilmiyoruz, mesaj GİTMEZ
    public async Task OpenWhatsAppAsync_lisans_cozulemez_is_belirsizse_BalanceUncertain_doner()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.ThrowTimeoutOnLicenses = true;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));
        SeedJob(customer.Id, PaymentJobState.ApplyUncertain, 250m, null, Guid.NewGuid());

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        _launcher.LaunchedUrls.Should().BeEmpty();
        handler.SentBodies.Should().BeEmpty();
    }

    [Fact] // A10d — iş applied ama tutar değişti: revizyon ağsız yapılamaz
    public async Task OpenWhatsAppAsync_lisans_cozulemez_tutar_degistiyse_BalanceUncertain_doner()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.ThrowTimeoutOnLicenses = true;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));
        SeedJob(customer.Id, PaymentJobState.Applied, 250m, 100m, Guid.NewGuid());

        (await sut.OpenWhatsAppAsync(customer, 300m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        _launcher.LaunchedUrls.Should().BeEmpty();
        handler.ReverseCalls.Should().BeEmpty("geri alma ucu zaten erişilemez");
    }

    [Fact] // A10e — açık miras iş: anahtarı var, sonucu bilinmiyor → blokla
    public async Task OpenWhatsAppAsync_lisans_cozulemez_acik_miras_is_varsa_BalanceUncertain_doner()
    {
        EnableCloudApi();
        var (sut, handler) = MakeCloudSut(_store, _launcher);
        handler.ThrowTimeoutOnLicenses = true;
        var customer = MakeCustomer("+905551234567", id: Guid.NewGuid().ToString("N"));
        SeedJob(customer.Id, PaymentJobState.ApplyUncertain, 250m, null, Guid.NewGuid(),
            scopeKey: "legacy");

        (await sut.OpenWhatsAppAsync(customer, 250m, T, "session:s1"))
            .Should().Be(PaymentRequestResult.BalanceUncertain);

        _launcher.LaunchedUrls.Should().BeEmpty();
    }
}
