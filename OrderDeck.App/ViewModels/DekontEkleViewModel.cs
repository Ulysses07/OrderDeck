using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sessions;
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

    public DekontEkleViewModel(
        PaymentRepository payments,
        CustomerRepository customers,
        SessionRepository sessions,
        PaymentMatcherService matcher,
        IClock clock,
        ILogger<DekontEkleViewModel> log)
    {
        _payments = payments;
        _customers = customers;
        _sessions = sessions;
        _matcher = matcher;
        _clock = clock;
        _log = log;
    }

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

        // Customer + session resolution (her ikisi de varsa matcher çalıştır)
        var customer = ResolveCustomer();
        var session = _sessions.GetActive();
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
            PdfHash: null,
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
