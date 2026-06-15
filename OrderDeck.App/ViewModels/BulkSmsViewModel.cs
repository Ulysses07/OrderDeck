using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services.Sync;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Yayıncı toplu SMS ekranı. Mesaj yaz → Önizle (alıcı/segment/kredi) → Gönder.
/// Bakiye göstergesi + kampanya geçmişi. Segment/kredi otoritesi server'ın
/// Preview endpoint'i (SmsSegmentCalculator server-only); CharCount yalnız bilgi.
/// Gerçek gönderim İYS onayına bağlı (ops); UI hazır, kredi iadesi otomatik.
/// </summary>
public sealed partial class BulkSmsViewModel : ViewModelBase
{
    private const int MaxMessageLength = 2000;
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly LicenseApiClient _api;
    private readonly ICurrentLicenseProvider _currentLicense;

    public ObservableCollection<CampaignRow> History { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private string _messageBody = "";

    [ObservableProperty] private int _charCount;
    [ObservableProperty] private int _creditsRemaining;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _previewDone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _sufficient;

    [ObservableProperty] private int _recipientCount;
    [ObservableProperty] private int _segments;
    [ObservableProperty] private int _totalCredits;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isHistoryEmpty;

    public BulkSmsViewModel(LicenseApiClient api, ICurrentLicenseProvider currentLicense)
    {
        _api = api;
        _currentLicense = currentLicense;
    }

    partial void OnMessageBodyChanged(string value)
    {
        CharCount = value?.Length ?? 0;
        // Mesaj değişince önizleme geçersiz → tekrar "Önizle" gerekir.
        PreviewDone = false;
    }

    // License key → Guid LicenseId çözümü (PaymentRequestService ile aynı pattern;
    // LicenseKey değişene kadar cache).
    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _currentLicense.CurrentLicenseKey;
        if (string.IsNullOrEmpty(key)) return null;
        if (_cachedLicenseId is not null && _cachedLicenseKey == key)
            return _cachedLicenseId;
        var licenses = await _api.GetMyLicensesAsync(ct);
        var match = licenses.FirstOrDefault(l => l.LicenseKey == key);
        if (match?.Id is null) return null;
        _cachedLicenseId = match.Id;
        _cachedLicenseKey = key;
        return _cachedLicenseId;
    }

    /// <summary>Dialog açılışında: lisans çöz, bakiye + geçmiş yükle.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var licenseId = await ResolveLicenseIdAsync(CancellationToken.None);
            if (licenseId is null)
            {
                ErrorMessage = "Aktif lisans bulunamadı. Giriş yapıp lisansı doğrulayın.";
                return;
            }
            await ReloadBalanceAndHistoryAsync(licenseId.Value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Yüklenemedi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadBalanceAndHistoryAsync(Guid licenseId, CancellationToken ct)
    {
        var balance = await _api.GetSmsBalanceAsync(licenseId, ct);
        CreditsRemaining = balance.CreditsRemaining;

        var rows = await _api.ListSmsCampaignsAsync(licenseId, take: 50, ct);
        History.Clear();
        foreach (var r in rows)
            History.Add(CampaignRow.From(r));
        IsHistoryEmpty = History.Count == 0;
    }

    private bool CanPreview() => !IsBusy && !string.IsNullOrWhiteSpace(MessageBody);

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var licenseId = await ResolveLicenseIdAsync(CancellationToken.None);
            if (licenseId is null) { ErrorMessage = "Aktif lisans bulunamadı."; return; }

            var resp = await _api.PreviewSmsCampaignAsync(
                licenseId.Value, new SmsPreviewRequest(MessageBody.Trim()), CancellationToken.None);

            RecipientCount = resp.RecipientCount;
            Segments = resp.SegmentsPerMessage;
            TotalCredits = resp.TotalCredits;
            CreditsRemaining = resp.CreditsRemaining;
            Sufficient = resp.Sufficient;
            PreviewDone = true;

            if (resp.RecipientCount == 0)
                StatusMessage = "SMS izinli, bağlı ve telefonu olan müşteri yok.";
            else if (!resp.Sufficient)
                StatusMessage = $"Yetersiz kredi: {resp.TotalCredits} gerekli, {resp.CreditsRemaining} mevcut.";
            else
                StatusMessage = $"{resp.RecipientCount} alıcı × {resp.SegmentsPerMessage} segment = {resp.TotalCredits} kredi.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Önizleme başarısız: {ex.Message}";
            PreviewDone = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSend() => PreviewDone && Sufficient && RecipientCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var confirm = MessageBox.Show(
            $"{RecipientCount} alıcıya toplu SMS gönderilecek.\n" +
            $"Tahmini {TotalCredits} kredi kullanılacak.\n\nDevam edilsin mi?",
            "Toplu SMS Gönder", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var licenseId = await ResolveLicenseIdAsync(CancellationToken.None);
            if (licenseId is null) { ErrorMessage = "Aktif lisans bulunamadı."; return; }

            var created = await _api.CreateSmsCampaignAsync(
                licenseId.Value, new SmsCreateRequest(MessageBody.Trim()), CancellationToken.None);
            StatusMessage = $"Kampanya oluşturuldu ({created.RecipientCount} alıcı). Gönderim arka planda sürüyor…";

            // Durumu birkaç kez yokla (Hangfire job arka planda işliyor).
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(1500);
                var st = await _api.GetSmsCampaignStatusAsync(
                    licenseId.Value, created.CampaignId, CancellationToken.None);
                StatusMessage = $"Durum: {StatusLabel(st.Status)} — {st.Sent} gönderildi, "
                                + $"{st.Failed} başarısız, {st.Skipped} atlandı.";
                if (st.Status is "completed" or "failed") break;
            }

            // Formu temizle, bakiye + geçmiş yenile.
            MessageBody = "";
            PreviewDone = false;
            Sufficient = false;
            await ReloadBalanceAndHistoryAsync(licenseId.Value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Gönderim başarısız: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var licenseId = await ResolveLicenseIdAsync(CancellationToken.None);
            if (licenseId is null) { ErrorMessage = "Aktif lisans bulunamadı."; return; }
            await ReloadBalanceAndHistoryAsync(licenseId.Value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Yenilenemedi: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static string StatusLabel(string status) => status switch
    {
        "pending" => "Bekliyor",
        "sending" => "Gönderiliyor",
        "completed" => "Tamamlandı",
        "failed" => "Başarısız",
        _ => status,
    };

    public sealed class CampaignRow
    {
        public string StatusText { get; init; } = "";
        public string MessagePreview { get; init; } = "";
        public string CountsLabel { get; init; } = "";
        public string CreatedAtLabel { get; init; } = "";

        public static CampaignRow From(SmsCampaignListItem d) => new()
        {
            StatusText = StatusLabel(d.Status),
            MessagePreview = d.MessagePreview,
            CountsLabel = $"{d.RecipientCount} alıcı · {d.Sent} gönderildi · {d.Failed} başarısız"
                          + (d.CreditsRefunded > 0 ? $" · {d.CreditsRefunded} kredi iade" : ""),
            CreatedAtLabel = d.CreatedAt.LocalDateTime.ToString("dd MMM yyyy HH:mm", Tr),
        };
    }
}
