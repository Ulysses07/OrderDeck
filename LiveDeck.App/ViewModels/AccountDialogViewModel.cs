using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.ViewModels;

public sealed partial class AccountDialogViewModel : ObservableObject
{
    private readonly LicenseService _licenseService;
    private readonly LoginService _loginService;

    public AccountDialogViewModel(LicenseService licenseService, LoginService loginService)
    {
        _licenseService = licenseService;
        _loginService = loginService;
        _currentStatus = licenseService.CurrentStatus;

        Email = licenseService.CurrentAuth?.Email ?? "—";
        Name = licenseService.CurrentAuth?.Name ?? "—";
        LicenseKey = licenseService.CurrentLicense?.LicenseKey ?? "—";
        SkuCode = licenseService.CurrentLicense?.SkuCode ?? "—";
        ExpiresAt = licenseService.CurrentLicense?.ExpiresAt;
        StatusText = licenseService.CurrentStatus.ToString();

        ApplyModeFlags();

        LogoutCommand = new RelayCommand(Logout);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        OpenLoginCommand = new RelayCommand(OpenLogin);
    }

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _licenseKey = "—";
    [ObservableProperty] private string _skuCode = "—";
    [ObservableProperty] private DateTimeOffset? _expiresAt;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private LicenseStatus _currentStatus;
    [ObservableProperty] private string _trialLine = "";
    [ObservableProperty] private bool _isAccountSection;
    [ObservableProperty] private bool _isLicenseSection;
    [ObservableProperty] private bool _isTrialSection;
    [ObservableProperty] private bool _isLogoutAvailable;
    [ObservableProperty] private bool _isReconnectAvailable;
    [ObservableProperty] private bool _isOpenLoginAvailable;

    public ICommand LogoutCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand OpenLoginCommand { get; }

    public event EventHandler? RequestClose;

    private void ApplyModeFlags()
    {
        IsAccountSection = _licenseService.CurrentAuth is not null;
        IsLicenseSection = _licenseService.CurrentLicense is not null;
        IsTrialSection = _licenseService.CurrentTrial is not null;
        IsLogoutAvailable = _licenseService.CurrentAuth is not null;
        IsReconnectAvailable = _licenseService.CurrentAuth is not null
                             && _licenseService.CurrentLicense is not null;
        IsOpenLoginAvailable = _licenseService.CurrentAuth is null;

        TrialLine = _licenseService.CurrentTrial switch
        {
            LiveDeck.Licensing.Trial.TrialState.Active a =>
                $"Deneme süresi: {a.RemainingDays} gün kaldı (bitiş {a.ExpiresAt:dd.MM.yyyy})",
            LiveDeck.Licensing.Trial.TrialState.Expired e =>
                $"Deneme süresi doldu ({e.ExpiredAt:dd.MM.yyyy})",
            _ => ""
        };
    }

    private void OpenLogin()
    {
        var dlg = global::LiveDeck.App.App.Host.Services
            .GetRequiredService<global::LiveDeck.App.Views.LoginDialog>();
        var owner = System.Windows.Application.Current.MainWindow;
        if (owner is not null) dlg.Owner = owner;
        dlg.ShowDialog();
        // After login completes, refresh account dialog state — easier just to close
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void Logout()
    {
        _licenseService.Logout();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await _licenseService.RefreshAsync();
            StatusText = _licenseService.CurrentStatus.ToString();
        }
        catch (LicenseApiException ex)
        {
            StatusText = "Hata: " + ex.Message;
        }
    }
}
