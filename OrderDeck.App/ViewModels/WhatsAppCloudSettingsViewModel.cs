using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Şablon gövdesindeki tek bir <c>{{n}}</c> yuvası ve o yuvaya hangi alanın
/// yazılacağı.
/// </summary>
public sealed partial class TemplateSlotViewModel : ObservableObject
{
    public TemplateSlotViewModel(int index, string? example, IReadOnlyList<WhatsAppField> fields)
    {
        Index = index;
        Example = example;
        Fields = fields;
    }

    /// <summary>1 tabanlı: gövdedeki <c>{{1}}</c> bu nesnenin Index'i 1.</summary>
    public int Index { get; }

    public string Label => $"{{{{{Index}}}}}";

    /// <summary>Meta'ya onaya giderken verilen örnek değer. Yayıncının hangi
    /// yuvanın ne olduğunu hatırlamasının tek yolu bu genelde — gövde metni
    /// "{{2}} TL" gibi çıplak yer tutucular içeriyor.</summary>
    public string? Example { get; }

    public IReadOnlyList<WhatsAppField> Fields { get; }

    /// <summary>null = henüz seçilmedi; kaydetmeye izin verilmez.</summary>
    [ObservableProperty] private WhatsAppField? _selectedField;
}

/// <summary>
/// Ayarlar → Ödeme &amp; Kargo → "WhatsApp Cloud API" bölümü.
///
/// <para><b>Neden var:</b> 24 saatlik pencere kapalıyken serbest metin
/// gönderilemiyor, yalnız Meta'da ONAYLI bir şablon gönderilebiliyor — yani
/// prodda asıl gönderim yolu şablon. Şablonun adı ve dili Meta'da onaydan sonra
/// DEĞİŞTİRİLEMİYOR, dolayısıyla uygulamanın beklediği şekle getirilemiyor;
/// uyum sağlaması gereken taraf biz olduğumuz için yuva→alan eşlemesini
/// yayıncı burada kuruyor. Eşleme koda gömülüyken (sabit yedi değer) sahadaki
/// dört parametreli onaylı şablonla hiç tutmadı ve her ödeme mesajı sessizce
/// <c>wa.me</c>'ye düştü.</para>
///
/// <para><b>Şablon oluşturma burada YOK, bilerek:</b> oluşturma Meta'nın
/// editörünü, onay durumunu (asenkron, webhook'la gelir), ret gerekçelerini ve
/// kategori/ücretlendirme seçimini uygulamaya taşımak demek — yılda bir iki kez
/// yapılan bir iş için. Seçmek ve eşlemek yeterli.</para>
/// </summary>
public sealed partial class WhatsAppCloudSettingsViewModel : ObservableObject
{
    private readonly LicenseApiClient _api;
    private readonly ICurrentLicenseProvider _currentLicense;
    private readonly ILogger<WhatsAppCloudSettingsViewModel> _log;

    public WhatsAppCloudSettingsViewModel(
        LicenseApiClient api,
        ICurrentLicenseProvider currentLicense,
        ILogger<WhatsAppCloudSettingsViewModel> log)
    {
        _api = api;
        _currentLicense = currentLicense;
        _log = log;
    }

    /// <summary>Açıkken pencere kapalıysa onaylı şablon, açıksa serbest metin
    /// gönderilir. Kapalıyken eski davranış: <c>wa.me</c> bağlantısı açılır.</summary>
    [ObservableProperty] private bool _useCloudApi;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>"Şablonları getir" düğmesinin IsEnabled bağı.</summary>
    public bool IsIdle => !IsLoading;
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    public ObservableCollection<ApprovedTemplateDto> Templates { get; } = new();
    public ObservableCollection<TemplateSlotViewModel> Slots { get; } = new();

    [ObservableProperty] private ApprovedTemplateDto? _selectedTemplate;

    /// <summary>Seçilen şablonun gönderilemez olma sebebi (null = gönderilebilir).</summary>
    public string? SelectedUnsupportedReason => SelectedTemplate?.UnsupportedReason;

    public bool HasSelectedTemplate => SelectedTemplate is not null;

    /// <summary>Yayıncı listeyi hiç çekmeden diyaloğu açtığında kayıtlı seçimin
    /// silinmemesi için saklanır; <see cref="CommitTo"/> listede eşleşme yoksa
    /// bunları geri yazar.</summary>
    private string _persistedName = "";
    private string _persistedLanguage = "";
    private List<string> _persistedParams = new();

    public void LoadFrom(PaymentSettings payment)
    {
        UseCloudApi = payment.UseCloudApi;
        _persistedName = payment.CloudTemplateName ?? "";
        _persistedLanguage = payment.CloudTemplateLanguage ?? "";
        _persistedParams = new List<string>(payment.CloudTemplateParams ?? new List<string>());

        StatusMessage = string.IsNullOrWhiteSpace(_persistedName)
            ? "Şablon seçilmedi — pencere kapalıyken wa.me bağlantısı açılır."
            : $"Kayıtlı şablon: {_persistedName} ({_persistedLanguage})";
    }

    /// <summary>
    /// Onaylı şablonları Meta'dan çeker (sunucu üzerinden). Liste her açılışta
    /// taze isteniyor: onay durumu Meta'da değişebiliyor ve bayat bir kopya
    /// yayıncıya gönderemeyeceği bir şablonu seçtirirdi.
    /// </summary>
    [RelayCommand]
    private async Task LoadTemplatesAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        try
        {
            IsLoading = true;
            var licenseId = await ResolveLicenseIdAsync(ct);
            if (licenseId is null)
            {
                ErrorMessage = "Lisans çözülemedi. Önce aktivasyon yap.";
                return;
            }

            var list = await _api.GetApprovedWhatsAppTemplatesAsync(licenseId.Value, ct);

            Templates.Clear();
            foreach (var t in list) Templates.Add(t);

            if (Templates.Count == 0)
            {
                StatusMessage = "Meta'da onaylı şablon bulunamadı.";
                return;
            }

            // Kayıtlı seçimi listede bul; yoksa seçim yapılmamış say. Ad+dil
            // birlikte aranıyor: aynı şablonun birden çok dil sürümü olabiliyor
            // ve Meta için ikisi ayrı şablon.
            SelectedTemplate = Templates.FirstOrDefault(t =>
                string.Equals(t.Name, _persistedName, StringComparison.Ordinal) &&
                string.Equals(t.Language, _persistedLanguage, StringComparison.OrdinalIgnoreCase));

            StatusMessage = SelectedTemplate is null && !string.IsNullOrWhiteSpace(_persistedName)
                ? $"Kayıtlı şablon ({_persistedName}/{_persistedLanguage}) onaylı listede yok — yeniden seç."
                : null;
        }
        // LicenseApiClient başarısız yanıtı ThrowMappedAsync ile sarmalıyor:
        // 5xx → LicenseApiUnknownException. HttpRequestException buraya hiç
        // ulaşmıyor, dolayısıyla onu yakalamak bu dalı ölü bırakıyordu.
        catch (LicenseApiUnknownException ex) when (ex.StatusCode == 503)
        {
            ErrorMessage = "Bu lisansa bağlı WhatsApp hesabı yok. Önce panelden WhatsApp'ı bağla.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Onaylı WhatsApp şablonları okunamadı");
            ErrorMessage = "Şablonlar okunamadı. Daha sonra tekrar dene.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedTemplateChanged(ApprovedTemplateDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedTemplate));
        OnPropertyChanged(nameof(SelectedUnsupportedReason));
        RebuildSlots(value);
    }

    /// <summary>Şablon değiştiğinde yuvaları baştan kurar. Kayıtlı eşleme
    /// yalnız <b>aynı</b> şablon için geri yükleniyor: başka bir şablonun
    /// yuvalarına eski sırayı taşımak, yayıncıya doğru görünen ama yanlış
    /// alanları taşıyan bir eşleme bırakırdı.</summary>
    private void RebuildSlots(ApprovedTemplateDto? template)
    {
        Slots.Clear();
        if (template is null) return;

        var isPersisted =
            string.Equals(template.Name, _persistedName, StringComparison.Ordinal) &&
            string.Equals(template.Language, _persistedLanguage, StringComparison.OrdinalIgnoreCase) &&
            _persistedParams.Count == template.ParameterCount;

        for (var i = 0; i < template.ParameterCount; i++)
        {
            var example = i < template.ParameterExamples.Count ? template.ParameterExamples[i] : null;
            var slot = new TemplateSlotViewModel(i + 1, example, WhatsAppFieldCatalog.All);
            if (isPersisted) slot.SelectedField = WhatsAppFieldCatalog.TryFind(_persistedParams[i]);
            Slots.Add(slot);
        }
    }

    /// <summary>Kaydedilebilir mi? Değilse sebebini döner (null = sorun yok).
    ///
    /// <para>Eksik eşlemeyi kaydettirmemek bilinçli: yarım eşleme Meta'ya yanlış
    /// sayıda parametre gönderir ve şablon <b>faturalı</b> olduğu için yayıncı
    /// parasını denemelere yatırır.</para></summary>
    public string? ValidationError()
    {
        if (!UseCloudApi) return null;
        if (SelectedTemplate is null) return null; // şablon yolu kapalı, serbest metin yine çalışır

        if (SelectedTemplate.UnsupportedReason is { } reason)
            return $"Bu şablon gönderilemiyor: {reason}";

        if (Slots.Any(s => s.SelectedField is null))
            return "Şablondaki her {{n}} yuvası için bir alan seç.";

        return null;
    }

    /// <summary>Seçimi ayarlara yazar. Liste hiç çekilmediyse kayıtlı değerler
    /// aynen korunur — ayar ekranını açıp kapatmak, çalışan bir yapılandırmayı
    /// sıfırlamamalı.</summary>
    public void CommitTo(PaymentSettings payment)
    {
        payment.UseCloudApi = UseCloudApi;

        if (Templates.Count == 0)
        {
            payment.CloudTemplateName = _persistedName;
            payment.CloudTemplateLanguage = _persistedLanguage;
            payment.CloudTemplateParams = new List<string>(_persistedParams);
            return;
        }

        if (SelectedTemplate is null)
        {
            payment.CloudTemplateName = "";
            payment.CloudTemplateLanguage = "";
            payment.CloudTemplateParams = new List<string>();
            return;
        }

        payment.CloudTemplateName = SelectedTemplate.Name;
        payment.CloudTemplateLanguage = SelectedTemplate.Language;
        payment.CloudTemplateParams = Slots.Select(s => s.SelectedField!.Key).ToList();

        _persistedName = payment.CloudTemplateName;
        _persistedLanguage = payment.CloudTemplateLanguage;
        _persistedParams = new List<string>(payment.CloudTemplateParams);
    }

    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _currentLicense.CurrentLicenseKey;
        if (string.IsNullOrEmpty(key)) return null;

        var licenses = await _api.GetMyLicensesAsync(ct);
        return licenses.FirstOrDefault(l => l.LicenseKey == key)?.Id;
    }
}
