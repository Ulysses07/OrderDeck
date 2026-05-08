using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Services;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Five-step setup wizard shown once on first launch after install:
///   1. Welcome
///   2. License activation (skipped if license already Active)
///   3. YouTube channel handle (optional)
///   4. Printer settings (optional, link to Settings dialog)
///   5. Chrome extension install (sideload guide + verify button)
/// Plus a final summary step.
///
/// Persisted gate: AppSettings.HasCompletedFirstRun. Only flipped to true
/// from the Finish button — closing/cancelling the dialog leaves the flag
/// false so the wizard re-runs on next launch.
/// </summary>
public sealed partial class FirstRunWizardViewModel : ObservableObject
{
    private readonly LicenseService _license;
    private readonly SettingsStore _settingsStore;
    private readonly IServiceProvider _services;
    private readonly HttpClient _http;

    public event EventHandler? RequestClose;

    public FirstRunWizardViewModel(
        LicenseService license,
        SettingsStore settingsStore,
        IServiceProvider services,
        IHttpClientFactory httpFactory)
    {
        _license = license;
        _settingsStore = settingsStore;
        _services = services;
        _http = httpFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(3);

        var s = _settingsStore.Load();
        YouTubeHandle = s.YouTubeChannelHandle ?? string.Empty;
        ExtensionPath = Path.Combine(AppContext.BaseDirectory, "Extension");
        UpdateLicenseStepStatus();
    }

    /// <summary>1-based for human-friendly step display ("Adım 2 / 6").
    /// 1=Welcome, 2=License, 3=YouTube, 4=Printer, 5=Extension, 6=Done.</summary>
    [ObservableProperty] private int _currentStep = 1;

    public const int TotalSteps = 6;

    public string StepLabel => $"Adım {CurrentStep} / {TotalSteps}";

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(StepLabel));
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
        OnPropertyChanged(nameof(IsStep5));
        OnPropertyChanged(nameof(IsStep6));
        OnPropertyChanged(nameof(CanGoBack));
    }

    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;
    public bool IsStep6 => CurrentStep == 6;

    public bool CanGoBack => CurrentStep > 1;

    // ── Step 2: License ───────────────────────────────────────────────
    [ObservableProperty] private string _licenseStatusText = "";
    [ObservableProperty] private bool _isLicenseActivated;

    private void UpdateLicenseStepStatus()
    {
        var status = _license.CurrentStatus;
        IsLicenseActivated = status switch
        {
            LicenseStatus.Active => true,
            LicenseStatus.OfflineGrace => true,
            LicenseStatus.TrialActive => true,
            _ => false
        };
        LicenseStatusText = status switch
        {
            LicenseStatus.Active => "Lisansın aktif ✓",
            LicenseStatus.OfflineGrace => "Çevrimdışı modda lisanslı ✓",
            LicenseStatus.TrialActive => "Deneme sürümü aktif ✓",
            LicenseStatus.NoLicense => "Henüz lisans yok — etkinleştir",
            _ => "Lisans gerekli"
        };
    }

    [RelayCommand]
    private void ActivateLicense()
    {
        // Reuse the existing LoginDialog (already wired with all auth flows).
        var loginDlg = _services.GetRequiredService<Views.LoginDialog>();
        loginDlg.ShowDialog();
        UpdateLicenseStepStatus();
    }

    // ── Step 3: YouTube ────────────────────────────────────────────────
    [ObservableProperty] private string _youTubeHandle = "";

    // ── Step 5: Extension verify ───────────────────────────────────────
    [ObservableProperty] private string _extensionPath = "";
    [ObservableProperty] private string _extensionVerifyResult = "";
    [ObservableProperty] private bool _isExtensionConnected;

    [RelayCommand]
    private void OpenExtensionFolder()
    {
        try
        {
            if (!Directory.Exists(ExtensionPath))
            {
                ExtensionVerifyResult = "Eklenti klasörü bulunamadı: " + ExtensionPath;
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = ExtensionPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ExtensionVerifyResult = "Klasör açılamadı: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenChromeExtensionsPage()
    {
        try
        {
            // chrome://extensions cannot be launched as a URL via the default
            // browser handler (chrome:// scheme is special); use the chrome
            // executable directly. Falls back to default handler if chrome
            // isn't on PATH.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chrome",
                    Arguments = "chrome://extensions",
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chrome://extensions",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            ExtensionVerifyResult = "Chrome açılamadı (manuel olarak chrome://extensions adresine git): " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task VerifyExtensionAsync()
    {
        ExtensionVerifyResult = "Kontrol ediliyor...";
        try
        {
            var resp = await _http.GetAsync("http://localhost:4748/_health");
            if (!resp.IsSuccessStatusCode)
            {
                IsExtensionConnected = false;
                ExtensionVerifyResult = "Köprü servisi yanıt vermedi (HTTP " + (int)resp.StatusCode + ")";
                return;
            }
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var connected = doc.RootElement.TryGetProperty("connected", out var c) && c.GetBoolean();
            var clientCount = doc.RootElement.TryGetProperty("clientCount", out var n) ? n.GetInt32() : 0;
            IsExtensionConnected = connected;
            ExtensionVerifyResult = connected
                ? $"Eklenti bağlı ✓ ({clientCount} bağlantı)"
                : "Eklenti henüz bağlanmadı — adımları tekrar kontrol et veya Chrome'u açık bıraktığından emin ol";
        }
        catch (Exception ex)
        {
            IsExtensionConnected = false;
            ExtensionVerifyResult = "Köprüye erişilemedi: " + ex.Message;
        }
    }

    // ── Navigation ─────────────────────────────────────────────────────
    [RelayCommand]
    private void Next()
    {
        if (CurrentStep < TotalSteps) CurrentStep++;
        if (IsStep2) UpdateLicenseStepStatus();
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep > 1) CurrentStep--;
    }

    [RelayCommand]
    private void Finish()
    {
        // Persist YouTube handle if the operator typed one.
        var live = _settingsStore.Load();
        var trimmedYouTube = YouTubeHandle?.Trim();
        live.YouTubeChannelHandle = string.IsNullOrEmpty(trimmedYouTube) ? null : trimmedYouTube;
        live.HasCompletedFirstRun = true;
        _settingsStore.Save(live);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SkipFirstRun()
    {
        // "Daha sonra" — operator skipped without completing. Do NOT set
        // HasCompletedFirstRun so the wizard runs again next launch. They
        // can still close the dialog (DialogResult will be false in the
        // window code-behind, App.OnStartup checks for that and bails).
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
