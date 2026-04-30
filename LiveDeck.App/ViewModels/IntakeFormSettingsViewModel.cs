using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;

namespace LiveDeck.App.ViewModels;

public sealed partial class IntakeFormSettingsViewModel : ObservableObject
{
    private readonly LicenseApiClient _api;

    public IntakeFormSettingsViewModel(LicenseApiClient api)
    {
        _api = api;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        CopyLinkCommand = new RelayCommand(CopyLink, () => HasFormUrl);
    }

    [ObservableProperty] private string _slug = "";
    [ObservableProperty] private string _whatsAppPhone = "";
    [ObservableProperty] private string _customTitle = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _formUrl = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isBusy;

    public bool HasFormUrl => !string.IsNullOrEmpty(FormUrl);

    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CopyLinkCommand { get; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var cfg = await _api.GetIntakeFormAsync();
            if (cfg is null)
            {
                StatusMessage = "Henüz form linkin yok. Slug ve WhatsApp telefonunu girip kaydet.";
                StatusBrush = Brushes.Gray;
                return;
            }
            Slug = cfg.Slug;
            WhatsAppPhone = cfg.WhatsAppPhone;
            CustomTitle = cfg.CustomTitle ?? "";
            IsActive = cfg.IsActive;
            FormUrl = cfg.FormUrl;
            StatusMessage = "Yüklendi.";
            StatusBrush = Brushes.SeaGreen;
            OnPropertyChanged(nameof(HasFormUrl));
        }
        catch (LicenseApiNetworkException)
        {
            StatusMessage = "Sunucuya ulaşılamadı.";
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiException ex)
        {
            StatusMessage = "Hata: " + ex.Message;
            StatusBrush = Brushes.Crimson;
        }
        finally { IsBusy = false; }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var req = new IntakeFormUpsertRequest(
                Slug.Trim().ToLowerInvariant(),
                WhatsAppPhone.Trim(),
                string.IsNullOrWhiteSpace(CustomTitle) ? null : CustomTitle.Trim(),
                IsActive);
            var cfg = await _api.UpsertIntakeFormAsync(req);
            Slug = cfg.Slug;
            WhatsAppPhone = cfg.WhatsAppPhone;
            CustomTitle = cfg.CustomTitle ?? "";
            IsActive = cfg.IsActive;
            FormUrl = cfg.FormUrl;
            StatusMessage = "Kaydedildi: " + cfg.FormUrl;
            StatusBrush = Brushes.SeaGreen;
            OnPropertyChanged(nameof(HasFormUrl));
        }
        catch (ValidationException ex) when (ex.Code != "slug-already-taken" && !ex.Message.Contains("slug-already-taken"))
        {
            StatusMessage = TranslateValidationCode(ex.Code);
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiException ex) when (ex.Code == "slug-already-taken" || ex.Message.Contains("slug-already-taken"))
        {
            StatusMessage = "Bu slug başka bir yayıncı tarafından alındı. Farklı bir slug seç.";
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiNetworkException)
        {
            StatusMessage = "Sunucuya ulaşılamadı.";
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiException ex)
        {
            StatusMessage = "Hata: " + ex.Message;
            StatusBrush = Brushes.Crimson;
        }
        finally { IsBusy = false; }
    }

    private void CopyLink()
    {
        if (!HasFormUrl) return;
        try
        {
            Clipboard.SetText(FormUrl);
            StatusMessage = "Link panoya kopyalandı.";
            StatusBrush = Brushes.DodgerBlue;
        }
        catch
        {
            // Clipboard sometimes fails on remote desktop; ignore
        }
    }

    private static string TranslateValidationCode(string code) => code switch
    {
        "invalid-slug-empty"         => "Slug boş olamaz.",
        "invalid-slug-invalidlength" => "Slug 3-32 karakter arası olmalı.",
        "invalid-slug-invalidformat" => "Slug sadece küçük harf, rakam ve tire içerebilir.",
        "invalid-slug-reserved"      => "Bu slug rezerve. Farklı bir tane dene.",
        "invalid-phone-format"       => "Telefon E.164 formatında olmalı (örn. +905551234567).",
        _                            => "Doğrulama hatası: " + code
    };
}
