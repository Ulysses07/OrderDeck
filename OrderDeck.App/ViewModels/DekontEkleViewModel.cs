using OrderDeck.Core.Payments;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Yayıncının elinde olan banka dekontunu (WhatsApp/email PDF) manuel
/// olarak OrderDeck'e girmesi için form ViewModel. Save sonrası Payment
/// SyncedAt=null ile yaratılır → <c>PaymentSyncHostedService</c> 30 sn
/// içinde LicenseServer'a push'lar → mobile Panel app dekont kuyruğunda
/// görür.
///
/// PDF parse otomasyonu (PdfPig) sonraki faz; bu PR'da operatör yalnız
/// gözle okuduğu değerleri form'a giriyor.
/// </summary>
public sealed partial class DekontEkleViewModel : ObservableObject
{
    private readonly PaymentRepository _payments;
    private readonly IClock _clock;
    private readonly ILogger<DekontEkleViewModel> _log;

    [ObservableProperty]
    private string _payerName = "";

    [ObservableProperty]
    private string _amountText = "";

    [ObservableProperty]
    private DateTime _paidAt = DateTime.Today;

    [ObservableProperty]
    private string _referansNo = "";

    [ObservableProperty]
    private string? _errorMessage;

    public DekontEkleViewModel(
        PaymentRepository payments,
        IClock clock,
        ILogger<DekontEkleViewModel> log)
    {
        _payments = payments;
        _clock = clock;
        _log = log;
    }

    /// <summary>UI bind eder: validation hatası varsa false → Save butonu disabled.</summary>
    public bool CanSave => Validate() is null;

    partial void OnPayerNameChanged(string value) => RefreshCanSave();
    partial void OnAmountTextChanged(string value) => RefreshCanSave();
    partial void OnReferansNoChanged(string value) => RefreshCanSave();

    private void RefreshCanSave()
    {
        OnPropertyChanged(nameof(CanSave));
        ErrorMessage = null;   // clear stale errors while typing
    }

    /// <summary>Returns null on success (Payment yaratıldı) veya kullanıcıya
    /// gösterilecek hata mesajı. UI dialog kapanıp kapanmayacağına buna göre
    /// karar verir.</summary>
    public string? TrySave()
    {
        var error = Validate();
        if (error is not null)
        {
            ErrorMessage = error;
            return error;
        }

        var refNo = ReferansNo.Trim();
        var existing = _payments.FindByReferansNo(refNo);
        if (existing is not null)
        {
            ErrorMessage = "Bu referans no zaten kayıtlı.";
            return ErrorMessage;
        }

        // PaidAt UI'da local date → Unix UTC seconds'a çevir. Operatör saat
        // girmiyor; gün başlangıcı (00:00 yerel) → UTC.
        var paidAtUtc = new DateTimeOffset(PaidAt.Date, TimeZoneInfo.Local.GetUtcOffset(PaidAt.Date));
        var amount = ParseAmount(AmountText)!.Value;   // Validate ile garanti var
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
            RejectReason: null);

        try
        {
            _payments.Insert(payment);
            _log.LogInformation("Dekont eklendi: Id={Id} ref={Ref} tutar={Amount}",
                payment.Id, payment.ReferansNo, payment.Amount);
            return null;
        }
        catch (Exception ex)
        {
            // SQLite UNIQUE constraint vs. — yarış durumu (eşzamanlı insert)
            _log.LogWarning(ex, "Dekont kaydedilemedi");
            ErrorMessage = "Kaydetme başarısız: " + ex.Message;
            return ErrorMessage;
        }
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

    /// <summary>Hem virgül hem nokta desimal ayraç olarak kabul edilir
    /// (Türkçe input'ta hem 250,50 hem 250.50 yaygın).</summary>
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
