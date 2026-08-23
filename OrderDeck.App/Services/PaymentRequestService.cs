using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services;

public enum PaymentRequestResult
{
    Opened,
    PhoneRequired,
    LaunchFailed,

    /// <summary>Mesaj WhatsApp Cloud API ile doğrudan gönderildi — operatörün
    /// gönder tuşuna basmasına gerek yok, wa.me penceresi açılmadı.</summary>
    Sent,

    /// <summary>Sunucu aynı gönderimin hâlâ işlendiğini söyledi (<c>in_progress</c>)
    /// — sonuç BİLİNMİYOR.
    ///
    /// <para>Neden <see cref="Sent"/> değil: bu cevap, ilk denemenin yarıda
    /// kesildiği (deploy sırasında sunucu yeniden başladı, bağlantı koptu, proxy
    /// 502) durumda da geliyor. Orada rezervasyon bilerek pending bırakılır ve
    /// müşteriye HİÇBİR ŞEY gitmemiştir. "Gönderildi" demek operatöre yalan
    /// söylemek olur; wa.me'yi de açmıyoruz çünkü gönderim gerçekten uçuştaysa
    /// ikinci bir faturalı kopya gider. Doğru davranış: operatörü uyarıp
    /// doğrulamaya yönlendirmek.</para></summary>
    SendPending
}

/// <summary>
/// Phase 4g: Customer + tutar + tarih → Settings template substitution + wa.me launch.
/// Phone null/invalid ise PhoneRequired (caller PhoneEntryDialog açar).
///
/// Kargo entegrasyon (2026-05-12): caller ürün toplamını geçer, service
/// AppSettings.Shipping + Customer.RecipientPaysActive flag'ine göre kargo
/// ücretini hesaplar, template'e ShippingNote + final TotalAmount yansıtır.
/// Mevcut caller signature'ları aynı kalır (productTotal eski "totalAmount"un
/// semantic karşılığı — caller zaten label sum geçiriyordu).
/// </summary>
public sealed class PaymentRequestService
{
    private readonly SettingsStore _settingsStore;
    private readonly WhatsAppMessageBuilder _messageBuilder;
    private readonly IUrlLauncher _launcher;
    private readonly LicenseApiClient _api;
    private readonly ICurrentLicenseProvider _currentLicense;
    private readonly Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? _log;

    public PaymentRequestService(
        SettingsStore settingsStore,
        WhatsAppMessageBuilder messageBuilder,
        IUrlLauncher launcher,
        LicenseApiClient api,
        ICurrentLicenseProvider currentLicense,
        Microsoft.Extensions.Logging.ILogger<PaymentRequestService>? log = null)
    {
        _settingsStore = settingsStore;
        _messageBuilder = messageBuilder;
        _launcher = launcher;
        _api = api;
        _currentLicense = currentLicense;
        _log = log;
    }

    // License key → Guid LicenseId resolution. Aynı pattern ShopperRegistrationIngest /
    // WpfCustomerProjectionSync'te de var. Caching: LicenseKey değişene kadar tutar.
    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _currentLicense.CurrentLicenseKey;
        if (string.IsNullOrEmpty(key)) return null;
        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null) return null;
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = key;
            return _cachedLicenseId;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.LogDebug("Lisans id çözümü zaman aşımına uğradı");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogDebug(ex, "License id resolve failed for payment request");
            return null;
        }
    }

    /// <param name="productTotal">Müşterinin ürün label'larının toplamı
    /// (kargo HARİÇ). Service kargo ücretini settings + customer flag'e göre
    /// otomatik ekler.</param>
    public PaymentRequestResult OpenWhatsApp(Customer customer, decimal productTotal, DateTime streamDate)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var (totalAmount, shippingFee, shippingNote) = ComputeShipping(customer, productTotal, settings);

        var ctx = new PaymentContext(
            DisplayName: customer.DisplayName ?? customer.Username,
            TotalAmount: totalAmount,
            StreamDate: streamDate,
            Iban: settings.Payment.Iban,
            AccountHolder: settings.Payment.AccountHolder,
            Papara: settings.Payment.Papara,
            ProductTotal: productTotal,
            ShippingFee: shippingFee,
            ShippingNote: shippingNote);

        var message = _messageBuilder.BuildMessage(settings.Payment.WhatsAppMessageTemplate, ctx);
        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>
    /// E3b (2026-06): bakiye-aware async versiyon. Müşterinin yayıncı bazlı
    /// bakiyesi varsa <see cref="LicenseApiClient.ApplyBalanceAsync"/> çağırıp
    /// ledger'a purchase-deduction kaydı düşer, WhatsApp template'inde
    /// {bakiye} / {net_tutar} placeholder'ları dolar. Sonra wa.me link açılır.
    ///
    /// Bakiye hatası WhatsApp akışını engellemez — apply başarısızsa eski
    /// davranış (bakiye uygulanmamış) ile mesaj gönderilir. Operatör mobile
    /// panel'den manuel ekleyebilir.
    /// </summary>
    public async Task<PaymentRequestResult> OpenWhatsAppAsync(
        Customer customer, decimal productTotal, DateTime streamDate, CancellationToken ct = default)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var (totalAmount, shippingFee, shippingNote) = ComputeShipping(customer, productTotal, settings);

        // Bakiye uygulaması (best-effort).
        decimal appliedBalance = 0m;
        var totalBeforeBalance = totalAmount;
        if (totalAmount > 0 && Guid.TryParseExact(customer.Id, "N", out var wpfCustomerId))
        {
            try
            {
                var licenseId = await ResolveLicenseIdAsync(ct);
                if (licenseId is not null)
                {
                    var preview = await _api.GetBalancePreviewAsync(
                        licenseId.Value, wpfCustomerId, ct);
                    if (preview.Balance > 0)
                    {
                        var amount = Math.Min(preview.Balance, totalAmount);
                        // Çağrı başına yeni anahtar — WhatsApp gönderimiyle aynı
                        // gerekçe: dayanıklılık katmanı 5xx/ağ hatasında bu POST'u
                        // da yeniden deniyor ve burası gerçek para düşüyor.
                        var apply = await _api.ApplyBalanceAsync(
                            licenseId.Value,
                            new CustomerBalanceApplyRequest(
                                wpfCustomerId, amount, totalAmount, Guid.NewGuid()),
                            ct);
                        appliedBalance = apply.AppliedAmount;
                        totalAmount -= apply.AppliedAmount;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex,
                    "Balance apply failed for customer {CustomerId} — sending WhatsApp without deduction",
                    customer.Id);
                // Fail-silent → eski davranışla devam
            }
        }

        var ctx = new PaymentContext(
            DisplayName: customer.DisplayName ?? customer.Username,
            TotalAmount: totalAmount,
            StreamDate: streamDate,
            Iban: settings.Payment.Iban,
            AccountHolder: settings.Payment.AccountHolder,
            Papara: settings.Payment.Papara,
            ProductTotal: productTotal,
            ShippingFee: shippingFee,
            ShippingNote: shippingNote,
            AppliedBalance: appliedBalance,
            TotalBeforeBalance: totalBeforeBalance);

        var message = _messageBuilder.BuildMessage(settings.Payment.WhatsAppMessageTemplate, ctx);

        // Cloud API açıksa doğrudan gönder. Başarısızsa (pencere kapalı, hesap
        // yok, ağ hatası) sessizce wa.me'ye düşeriz — yayıncı elle gönderir,
        // yani en kötü ihtimalde eski davranış.
        if (settings.Payment.UseCloudApi)
        {
            switch (await TrySendViaCloudApiAsync(
                customer.Phone!, message, BuildTemplateRef(settings, ctx), "wpf-payment", ct))
            {
                case CloudSendOutcome.Sent:
                    return PaymentRequestResult.Sent;
                case CloudSendOutcome.Pending:
                    return PaymentRequestResult.SendPending;
            }
        }

        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>
    /// Kümülatif kargo PR-E (2026-05-12): Müşteri ücretsiz kargo eşiğini
    /// aştığında vendor "Evet kargolansın" dedikten sonra çağrılır. WhatsApp
    /// linki açar; phone yoksa PhoneRequired döner. Template'i AppSettings'ten
    /// okur; boşsa Opened ile fail-silent çıkar (mesaj atılmaz).
    /// </summary>
    public PaymentRequestResult OpenShippingWonWhatsApp(Customer customer, decimal cumulativeAmount)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var template = settings.Payment.ShippingWonTemplate;
        if (string.IsNullOrWhiteSpace(template))
            return PaymentRequestResult.Opened; // template kapalı, sessiz geç

        var message = _messageBuilder.BuildShippingWonMessage(
            template,
            customer.DisplayName ?? customer.Username,
            cumulativeAmount);
        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>Cloud API denemesinin üç ayrı sonucu. bool yetmiyor: "gönderildi"
    /// ile "sonucu bilmiyoruz" ikisi de wa.me'yi açtırmıyor ama operatöre
    /// söylenecek şey taban tabana zıt.</summary>
    private enum CloudSendOutcome
    {
        /// <summary>Sunucu gönderimi onayladı.</summary>
        Sent,

        /// <summary>Çağıran wa.me'ye düşmeli (hesap bağlı değil, pencere kapalı,
        /// lisans çözülemedi, ağ hatası).</summary>
        FallBack,

        /// <summary>Aynı gönderim sunucuda hâlâ işleniyor — sonuç bilinmiyor.</summary>
        Pending
    }

    /// <summary>
    /// Pencere kapalıyken kullanılacak onaylı şablonu hazırlar; hazırlanamıyorsa
    /// null döner ve sunucu eski davranışa (<c>window_closed</c> → wa.me) düşer.
    ///
    /// <para>Prodda 24 saatlik pencere çoğu müşteride KAPALI olacağı için asıl
    /// gönderim yolu burasıdır — serbest metin yalnız müşteri son 24 saatte
    /// yazmışsa geçerli.</para>
    ///
    /// <para>Şablon adı ya da alan eşlemesi boşsa yol bilerek kapalıdır: Meta'da
    /// onaylanmamış bir ad göndermek her denemede hata almak demek, eşlemesiz
    /// şablon da yanlış sayıda parametre demek. Parametrelerden biri (ör. IBAN
    /// ya da hesap sahibi girilmemişse) boşsa da null döner — Meta boş parametre
    /// kabul etmiyor.</para>
    /// </summary>
    private WhatsAppTemplateRef? BuildTemplateRef(AppSettings settings, PaymentContext ctx)
    {
        var name = settings.Payment.CloudTemplateName;
        var language = settings.Payment.CloudTemplateLanguage;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language))
            return null;

        // Eşleme ayardan geliyor çünkü şablonun şeklini Meta belirliyor, biz
        // değil (ad ve dil onaydan sonra değiştirilemiyor). Eşleme kurulmamışsa
        // parametre sayısını tahmin etmektense şablon yolunu hiç açmıyoruz.
        var mapping = settings.Payment.CloudTemplateParams;
        if (mapping is null || mapping.Count == 0)
        {
            _log?.LogInformation(
                "WhatsApp şablonu atlandı: '{Name}' için alan eşlemesi kurulmamış (Ayarlar → WhatsApp Cloud API)",
                name);
            return null;
        }

        var parameters = _messageBuilder.BuildPaymentTemplateParams(ctx, mapping);
        if (parameters is null)
        {
            _log?.LogInformation(
                "WhatsApp şablonu atlandı: gövde parametrelerinden biri boş (IBAN/hesap sahibi girili mi?)");
            return null;
        }

        return new WhatsAppTemplateRef(name.Trim(), language.Trim(), parameters);
    }

    /// <summary>
    /// Mesajı Cloud API ile göndermeyi dener.
    ///
    /// Yalnızca çağıranın açıkça iptal ettiği durum (ct iptal edilmişse)
    /// dışarı sızar; diğer tüm hatalar — HTTP zaman aşımı dahil — yutulur:
    /// ödeme isteme akışı, opsiyonel bir gönderim yolunun hatasıyla durmamalı.
    /// </summary>
    private async Task<CloudSendOutcome> TrySendViaCloudApiAsync(
        string phone, string message, WhatsAppTemplateRef? template,
        string origin, CancellationToken ct)
    {
        // Çağrı başına yeni anahtar: dayanıklılık katmanı bu POST'u yeniden
        // denerse gövde (dolayısıyla anahtar) aynı kalır ve sunucu tekrarı eler.
        // try'ın DIŞINDA duruyor ki zaman aşımı kaydında da yer alsın.
        var idempotencyKey = Guid.NewGuid();
        try
        {
            var licenseId = await ResolveLicenseIdAsync(ct);
            if (licenseId is null) return CloudSendOutcome.FallBack;

            var resp = await _api.SendWhatsAppTextAsync(
                licenseId.Value,
                new WhatsAppSendRequest(phone, message, origin, idempotencyKey, template), ct);

            if (!resp.Ok)
            {
                if (resp.ErrorCode == WhatsAppSendErrorCodes.InProgress)
                {
                    // wa.me açmak operatörü ikinci bir kopya yollamaya davet eder;
                    // ama "gönderildi" demek de yalan olur — sunucu sadece
                    // "bilmiyorum" diyor. Kararı operatöre bırakıyoruz.
                    _log?.LogWarning(
                        "Aynı WhatsApp gönderimi hâlâ işleniyor — sonuç bilinmiyor, wa.me açılmıyor (key={Key})",
                        idempotencyKey);
                    return CloudSendOutcome.Pending;
                }

                _log?.LogInformation(
                    "WhatsApp Cloud API gönderimi yapılamadı ({Code}) — wa.me'ye düşülüyor (key={Key})",
                    resp.ErrorCode, idempotencyKey);
                return CloudSendOutcome.FallBack;
            }

            return CloudSendOutcome.Sent;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Zaman aşımında sunucunun mesajı gönderip göndermediğini bilmiyoruz;
            // wa.me açılıyor, yani operatör elle ikinci bir kopya yollayabilir.
            // Anahtarı loga yazıyoruz ki "müşteriye iki mesaj gitti" şikâyetinde
            // gönderim sunucu tarafında izlenebilsin.
            _log?.LogWarning(
                "WhatsApp Cloud API zaman aşımı — wa.me'ye düşülüyor (key={Key})", idempotencyKey);
            return CloudSendOutcome.FallBack;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "WhatsApp Cloud API gönderimi hata verdi — wa.me'ye düşülüyor (key={Key})", idempotencyKey);
            return CloudSendOutcome.FallBack;
        }
    }

    /// <summary>
    /// Kargo karar matrisi:
    /// <list type="bullet">
    ///   <item>Customer.RecipientPaysActive → "Kargo: alıcı ödemeli" (kargo fee
    ///     müşteriden istenmez; kargo şirketi tahsil eder)</item>
    ///   <item>Shipping feature kapalı → ShippingNote boş, total = productTotal</item>
    ///   <item>productTotal &gt;= FreeShippingThreshold → "Ücretsiz kargo"</item>
    ///   <item>productTotal &lt; threshold → "Kargo: X TL", total = productTotal + fee</item>
    /// </list>
    /// </summary>
    internal static (decimal Total, decimal? Fee, string Note) ComputeShipping(
        Customer customer, decimal productTotal, AppSettings settings)
    {
        if (customer.RecipientPaysActive)
        {
            return (productTotal, null, "Kargo: alıcı ödemeli");
        }

        var shipping = settings.Shipping;
        if (!shipping.IsEnabled)
        {
            return (productTotal, null, "");
        }

        if (productTotal >= shipping.FreeShippingThreshold!.Value)
        {
            return (productTotal, null, "Ücretsiz kargo");
        }

        var fee = shipping.ShippingFee!.Value;
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var note = $"Kargo: {fee.ToString("N2", tr)} TL";
        return (productTotal + fee, fee, note);
    }
}
