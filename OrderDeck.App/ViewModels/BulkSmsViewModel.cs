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

    // F09: gönderim eylemi başına idempotency anahtarı. İlk denemede üretilir,
    // hata olursa SAKLANIR — kullanıcı tekrar "Gönder" dediğinde (veya HTTP
    // resilience handler yeniden denediğinde) aynı anahtar gider ve server
    // ikinci kampanya açmaz. Başarıda ya da mesaj değişince sıfırlanır.
    private Guid? _pendingSendRequestId;

    // Test kancası (N06): durum yoklaması arasındaki bekleme. Prod'da 1.5 sn;
    // testler kısaltır ki yoklama hatası senaryosu saniyeler sürmesin.
    internal TimeSpan StatusPollDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    partial void OnMessageBodyChanged(string value)
    {
        CharCount = value?.Length ?? 0;
        // Mesaj değişince önizleme geçersiz → tekrar "Önizle" gerekir.
        PreviewDone = false;
        // Yeni içerik = yeni kampanya; eski gönderimin anahtarı taşınmaz.
        _pendingSendRequestId = null;
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

        await SendCoreAsync();
    }

    /// <summary>MessageBox onayı sonrası gövde. internal: N06 testleri onay
    /// penceresi olmadan çağırabilsin.
    ///
    /// N06: Create ile durum yoklaması AYRI hata alanları. Eskiden tek
    /// try/catch'ti: Create başarılı olup anahtar sıfırlandıktan sonra durum
    /// yoklaması patlarsa "Gönderim başarısız" görünüyor, form dolu ve Gönder
    /// aktif kalıyordu — operatör tekrar tıklayınca yeni ClientRequestId ile
    /// İKİNCİ kampanya açılıyordu (aynı alıcılara ikinci SMS + ikinci kredi
    /// düşümü). Şimdi Create başarısında form ÖNCE temizlenir; yoklama hatası
    /// yalnız "izlenemedi" der, gönderilebilir durum bırakmaz.</summary>
    internal async Task SendCoreAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            Guid licenseId;
            SmsCreateResponse created;

            // ── Faz 1: kampanya oluşturma. Burada hata = kampanya AÇILMADI;
            // anahtar saklanır (F09) ve form olduğu gibi kalır — tekrar
            // Gönder aynı ClientRequestId ile aynı kampanyayı hedefler.
            try
            {
                var resolved = await ResolveLicenseIdAsync(CancellationToken.None);
                if (resolved is null) { ErrorMessage = "Aktif lisans bulunamadı."; return; }
                licenseId = resolved.Value;

                _pendingSendRequestId ??= Guid.NewGuid();
                created = await _api.CreateSmsCampaignAsync(
                    licenseId,
                    new SmsCreateRequest(MessageBody.Trim(), _pendingSendRequestId),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Gönderim başarısız: {ex.Message}";
                return;
            }

            // ── Faz 2: kampanya sunucuda açıldı, gönderim arka planda.
            // Form HEMEN temizlenir ki aşağıdaki yoklama patlasa bile Gönder
            // yeniden aktifleşmesin.
            _pendingSendRequestId = null; // başarı — sonraki gönderim yeni eylem
            MessageBody = "";
            PreviewDone = false;
            Sufficient = false;
            StatusMessage = $"Kampanya oluşturuldu ({created.RecipientCount} alıcı). Gönderim arka planda sürüyor…";

            try
            {
                // Durumu birkaç kez yokla (Hangfire job arka planda işliyor).
                for (var i = 0; i < 6; i++)
                {
                    await Task.Delay(StatusPollDelay);
                    var st = await _api.GetSmsCampaignStatusAsync(
                        licenseId, created.CampaignId, CancellationToken.None);
                    StatusMessage = $"Durum: {StatusLabel(st.Status)} — {st.Sent} gönderildi, "
                                    + $"{st.Failed} başarısız, {st.Skipped} atlandı.";
                    if (st.Status is "completed" or "failed") break;
                }

                // Bakiye + geçmiş yenile.
                await ReloadBalanceAndHistoryAsync(licenseId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // İzleme hatası ≠ gönderim hatası: SMS'ler arka planda gidiyor.
                // "Gönderim başarısız" demek operatörü tekrar göndermeye iterdi.
                ErrorMessage = "Kampanya oluşturuldu ancak durum izlenemedi: "
                    + ex.Message + " — geçmişi Yenile ile kontrol edebilirsin.";
            }
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
