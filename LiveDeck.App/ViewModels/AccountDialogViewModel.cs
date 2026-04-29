using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;

namespace LiveDeck.App.ViewModels;

public sealed partial class AccountDialogViewModel : ObservableObject
{
    private readonly LicenseService _licenseService;
    private readonly LoginService _loginService;

    public AccountDialogViewModel(LicenseService licenseService, LoginService loginService)
    {
        _licenseService = licenseService;
        _loginService = loginService;

        Email = _licenseService.CurrentAuth?.Email ?? "";
        Name = _licenseService.CurrentAuth?.Name ?? "";
        LicenseKey = _licenseService.CurrentLicense?.LicenseKey ?? "—";
        SkuCode = _licenseService.CurrentLicense?.SkuCode ?? "—";
        ExpiresAt = _licenseService.CurrentLicense?.ExpiresAt;
        StatusText = _licenseService.CurrentStatus.ToString();

        LogoutCommand = new RelayCommand(Logout);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
    }

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _licenseKey = "—";
    [ObservableProperty] private string _skuCode = "—";
    [ObservableProperty] private DateTimeOffset? _expiresAt;
    [ObservableProperty] private string _statusText = "";

    public ICommand LogoutCommand { get; }
    public ICommand ReconnectCommand { get; }

    public event EventHandler? RequestClose;

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
