using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Yayıncının elinde olan banka dekontunu (WhatsApp/email PDF) manuel
/// olarak OrderDeck'e girmesi için form ViewModel. Save sonrası Payment
/// SyncedAt=null ile yaratılır → PaymentSyncHostedService 30 sn içinde
/// LicenseServer'a push'lar → mobile Panel app dekont kuyruğunda görür.
///
/// Kargo PR D (2026-05-11): Müşteri seçimi + matcher entegrasyonu eklendi.
/// Vendor Platform+Username doldurursa ve aktif yayın varsa, dekont
/// tutarı müşterinin ürün toplamı + kargo eşiğiyle karşılaştırılır.
/// Kargo ücreti eksikse <c>SaveResult.NeedsShipmentDecision</c> dönülür —
/// dialog code-behind ShipmentDirectiveDialog ile vendor'a sorar.
/// </summary>
public sealed partial class DekontEkleViewModel : ObservableObject
{
    private readonly PaymentRepository _payments;
    private readonly CustomerRepository _customers;
    private readonly SessionRepository _sessions;
    private readonly PaymentMatcherService _matcher;
    private readonly IClock _clock;
    private readonly ILogger<DekontEkleViewModel> _log;

    [ObservableProperty] private string _payerName = "";
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private DateTime _paidAt = DateTime.Today;
    [ObservableProperty] private string _referansNo = "";

    // Kargo PR D: müşteri seçimi (opsiyonel — boş bırakılırsa matcher
    // çalışmaz, basit Payment kaydı yapılır).
    [ObservableProperty] private string _customerPlatform = "";
    [ObservableProperty] private string _customerUsername = "";

    [ObservableProperty] private string? _errorMessage;

    /// <summary>PDF parse sonrası alıcı IBAN'ı Settings.Payment.Iban ile
    /// uyuşmazsa burada warning gösterilir (Save'i engellemez — edge case'lerde
    /// operatör override edebilir, ama görsel uyarı kritik bilgi).</summary>
    [ObservableProperty] private string? _ibanWarning;

    // PDF parse hash (Payment.PdfHash'e CommitInternal'da yazılır — duplicate
    // dedect için). Operator PDF yüklemediyse null kalır.
    private string? _pendingPdfHash;

    private readonly PdfDekontParser _pdfParser;
    private readonly AppSettings _settings;

    public DekontEkleViewModel(
        PaymentRepository payments,
        CustomerRepository customers,
        SessionRepository sessions,
        PaymentMatcherService matcher,
        PdfDekontParser pdfParser,
        AppSettings settings,
        IClock clock,
        ILogger<DekontEkleViewModel> log)
    {
        _payments = payments;
        _customers = customers;
        _sessions = sessions;
        _matcher = matcher;
        _pdfParser = pdfParser;
        _settings = settings;
        _clock = clock;
        _log = log;
    }

    /// <summary>PDF Yükle butonu çağırır: PdfDekontParser'la 4 alan + hash
    /// çıkarılır, form alanlarına best-effort doldurulur. Bulunamayanlar boş
    /// kalır; operatör elle düzeltir. Çağrı dışsal (dialog code-behind) çünkü
    /// file dialog WPF tarafında.</summary>
    public string? TryFillFromPdf(byte[] pdfBytes)
    {
        try
        {
            var result = _pdfParser.Parse(pdfBytes);
            if (!string.IsNullOrWhiteSpace(result.PayerName)) PayerName = result.PayerName;
            if (result.Amount is { } amount) AmountText = amount.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
            if (result.PaidAt is { } paidAt) PaidAt = paidAt;
            if (!string.IsNullOrWhiteSpace(result.ReferansNo)) ReferansNo = result.ReferansNo;
            _pendingPdfHash = result.PdfHash;
            ErrorMessage = null;

            // Alıcı IBAN check (2026-05-12): müşteri yanlış IBAN'a transfer
            // yapmış olabilir. Warning olarak göster — Save'i engelleme
            // (PDF parse hatası, vendor sub-account, manuel doğrulama
            // edge case'leri için).
            IbanWarning = CheckIbanMatch(result.RecipientIban);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDF parse failed");
            return "PDF okunamadı: " + ex.Message;
        }
    }

    private string? CheckIbanMatch(string? pdfRecipientIban)
    {
        if (string.IsNullOrWhiteSpace(pdfRecipientIban)) return null; // parser bulamadı, sessiz geç

        var expected = NormalizeIban(_settings.Payment.Iban);
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Vendor henüz Settings'te IBAN tanımlamamış → karşılaştırma yapma
            return null;
        }

        var actual = NormalizeIban(pdfRecipientIban);
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            // Eşleşti — uyarı yok
            _log.LogInformation("PDF recipient IBAN matches Settings IBAN");
            return null;
        }

        _log.LogWarning(
            "PDF recipient IBAN does NOT match Settings IBAN. Expected={Expected}, Actual={Actual}",
            expected, actual);
        return $"⚠ DİKKAT: Dekontun alıcı IBAN'ı sizin IBAN'ınızla eşleşmiyor!\n" +
               $"Dekont alıcı:  {actual}\n" +
               $"Sizin IBAN:    {expected}\n" +
               $"Para başka bir hesaba gitmiş olabilir — kayıttan önce kontrol edin.";
    }

    private static string NormalizeIban(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? ""
            : System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", "").ToUpperInvariant();

    public bool CanSave => Validate() is null;

    partial void OnPayerNameChanged(string value) => RefreshCanSave();
    partial void OnAmountTextChanged(string value) => RefreshCanSave();
    partial void OnReferansNoChanged(string value) => RefreshCanSave();

    private void RefreshCanSave()
    {
        OnPropertyChanged(nameof(CanSave));
        ErrorMessage = null;
    }

    /// <summary>Outcome of the first save attempt. View code-behind reacts:
    /// <list type="bullet">
    ///   <item><c>Saved</c>: dialog closes successful.</item>
    ///   <item><c>NeedsShipmentDecision</c>: open ShipmentDirectiveDialog with
    ///   <c>Shortage</c>, then call <see cref="CommitWithDirective"/>.</item>
    ///   <item><c>Error</c>: stay in dialog, show <c>Error</c>.</item>
    /// </list></summary>
    public sealed record SaveResult(
        SaveResultKind Kind,
        PaymentMatcherService.MatchResult? Shortage = null,
        string? Error = null);

    public enum SaveResultKind { Saved, NeedsShipmentDecision, Error }

    /// <summary>Initial save attempt: validate + duplicate check + matcher.
    /// Caller (dialog code-behind) inspects SaveResultKind.</summary>
    public SaveResult TrySave()
    {
        var validationError = Validate();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return new SaveResult(SaveResultKind.Error, Error: validationError);
        }

        var refNo = ReferansNo.Trim();
        if (_payments.FindByReferansNo(refNo) is not null)
        {
            ErrorMessage = "Bu referans no zaten kayıtlı.";
            return new SaveResult(SaveResultKind.Error, Error: ErrorMessage);
        }

        // Customer + session resolution (her ikisi de varsa matcher çalıştır).
        // Operatörlerin %90'ı yayın bittikten sonra dekont giriyor → aktif
        // yayın yokken matcher devre dışı kalmasın diye son yayına fallback.
        // (PR F'de cross-session aggregation eklenecek; bu hotfix tek-yayın
        // senaryolarını kapsar.)
        var customer = ResolveCustomer();
        var session = _sessions.GetActive() ?? _sessions.GetLatestEnded();
        var amount = ParseAmount(AmountText)!.Value;

        if (customer is not null && session is not null)
        {
            var match = _matcher.Match(customer.Id, session.Id, amount);
            if (match.Outcome == PaymentMatcherService.MatchOutcome.ShippingShortage)
            {
                // Save henüz yok — vendor'dan directive bekleniyor.
                return new SaveResult(SaveResultKind.NeedsShipmentDecision, Shortage: match);
            }
        }
        else if (!string.IsNullOrWhiteSpace(CustomerUsername))
        {
            // Operator müşteri girdi ama bulunamadı — log + sessizce devam
            _log.LogInformation(
                "Dekont save: customer not found by {Platform}/{Username}; matcher skipped.",
                CustomerPlatform, CustomerUsername);
        }

        return CommitInternal(ShipmentDirective.Normal);
    }

    /// <summary>Called by dialog code-behind after vendor picked a directive
    /// via ShipmentDirectiveDialog. Persists Payment with the chosen directive.</summary>
    public SaveResult CommitWithDirective(ShipmentDirective directive)
    {
        return CommitInternal(directive);
    }

    private SaveResult CommitInternal(ShipmentDirective directive)
    {
        var refNo = ReferansNo.Trim();
        var paidAtUtc = new DateTimeOffset(PaidAt.Date, TimeZoneInfo.Local.GetUtcOffset(PaidAt.Date));
        var amount = ParseAmount(AmountText)!.Value;
        var now = _clock.UnixNow();

        var payment = new Payment(
            Id: Guid.NewGuid().ToString(),
            PayerName: PayerName.Trim(),
            Amount: amount,
            PaidAt: paidAtUtc.ToUnixTimeSeconds(),
            ReferansNo: refNo,
            PdfHash: _pendingPdfHash,   // PDF parse'tan geldiyse set, yoksa null
            Status: PaymentStatus.Pending,
            CreatedAt: now,
            UpdatedAt: now,
            SyncedAt: null,
            ApprovedAt: null,
            RejectedAt: null,
            RejectReason: null,
            ShipmentDirective: directive);

        try
        {
            _payments.Insert(payment);

            // Kargo PR F: RecipientPays seçildiyse müşterinin sticky flag'i set
            // edilir; print template etikete "ALICI ÖDEMELİ" render edecek.
            // Resolved customer null ise (operatör müşteri girmediyse) bu nokta
            // hiç çalışmaz — flag set etmek için CustomerId şart.
            if (directive == ShipmentDirective.RecipientPays)
            {
                var resolved = ResolveCustomer();
                if (resolved is not null)
                {
                    _customers.SetRecipientPaysActive(resolved.Id, true);
                    _log.LogInformation(
                        "Customer {Id} RecipientPaysActive=true (kargo bedeli al\u0131c\u0131dan tahsil edilecek)",
                        resolved.Id);
                }
            }

            _log.LogInformation(
                "Dekont eklendi: Id={Id} ref={Ref} tutar={Amount} directive={Directive}",
                payment.Id, payment.ReferansNo, payment.Amount, directive);
            return new SaveResult(SaveResultKind.Saved);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dekont kaydedilemedi");
            ErrorMessage = "Kaydetme başarısız: " + ex.Message;
            return new SaveResult(SaveResultKind.Error, Error: ErrorMessage);
        }
    }

    private Customer? ResolveCustomer()
    {
        var platform = CustomerPlatform?.Trim();
        var username = CustomerUsername?.Trim();
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(username))
            return null;
        return _customers.FindByPlatformAndUsername(platform, username);
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(PayerName))
            return "Ödeyenin adı boş olamaz.";
        if (string.IsNullOrWhiteSpace(ReferansNo))
            return "Referans no boş olamaz.";

        var amount = ParseAmount(AmountText);
        if (amount is null)
            return "Tutar geçersiz (örn: 250,50 veya 250.50).";
        if (amount.Value <= 0)
            return "Tutar 0'dan büyük olmalı.";

        if (PaidAt > DateTime.Today.AddDays(1))
            return "Dekont tarihi gelecekte olamaz.";

        return null;
    }

    private static decimal? ParseAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().Replace(',', '.');
        return decimal.TryParse(normalized,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var amount) ? amount : null;
    }
}
