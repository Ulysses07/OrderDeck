using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;

namespace LiveDeck.App.ViewModels;

public enum LoginDialogMode
{
    Login,
    Register,
    ConfirmPending,
    LicenseSelection
}

public sealed partial class LoginDialogViewModel : ObservableObject
{
    private readonly LoginService _login;
    private readonly LicenseService _licenseService;
    private readonly AuthStore _authStore;

    public LoginDialogViewModel(LoginService login, LicenseService licenseService, AuthStore authStore)
    {
        _login = login;
        _licenseService = licenseService;
        _authStore = authStore;

        SubmitLoginCommand = new AsyncRelayCommand(SubmitLoginAsync, () => !IsBusy);
        SubmitRegisterCommand = new AsyncRelayCommand(SubmitRegisterAsync, () => !IsBusy);
        ResendCommand = new AsyncRelayCommand(ResendAsync, () => !IsBusy);
        ActivateSelectedCommand = new AsyncRelayCommand(ActivateSelectedAsync, () => !IsBusy && Selected is not null);
        SwitchToRegisterCommand = new RelayCommand(() => Mode = LoginDialogMode.Register);
        SwitchToLoginCommand = new RelayCommand(() => Mode = LoginDialogMode.Login);
    }

    [ObservableProperty] private LoginDialogMode _mode = LoginDialogMode.Login;
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _passwordConfirm = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<LicenseSummary> _licenses = new();
    [ObservableProperty] private LicenseSummary? _selected;

    public ICommand SubmitLoginCommand { get; }
    public ICommand SubmitRegisterCommand { get; }
    public ICommand ResendCommand { get; }
    public ICommand ActivateSelectedCommand { get; }
    public ICommand SwitchToRegisterCommand { get; }
    public ICommand SwitchToLoginCommand { get; }

    /// <summary>Set when the dialog should close successfully — caller reads CurrentStatus.</summary>
    public event EventHandler? RequestClose;

    private async Task SubmitLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "E-posta ve şifre boş olamaz";
            return;
        }

        IsBusy = true; ErrorMessage = null;
        try
        {
            await _login.LoginAsync(Email, Password);
            // Now check user's licenses
            var licenses = await _login.GetMyLicensesAsync();
            if (licenses.Count == 0)
            {
                ErrorMessage = "Bu hesaba bağlı aktif lisans yok. Yöneticinize başvurun.";
                _login.Logout();
                return;
            }
            if (licenses.Count == 1)
            {
                await _licenseService.ActivateAsync(licenses[0].LicenseKey, machineName: Environment.MachineName);
                RequestClose?.Invoke(this, EventArgs.Empty);
                return;
            }

            Licenses.Clear();
            foreach (var l in licenses) Licenses.Add(l);
            Selected = Licenses[0];
            Mode = LoginDialogMode.LicenseSelection;
        }
        catch (InvalidCredentialsException) { ErrorMessage = "E-posta veya şifre yanlış"; }
        catch (EmailNotConfirmedException)
        {
            ErrorMessage = "E-postanı doğrula. Onay linki için e-posta kutunu kontrol et.";
        }
        catch (LicenseApiNetworkException) { ErrorMessage = "Sunucuya ulaşılamıyor. İnternet bağlantını kontrol et."; }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task SubmitRegisterAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Tüm alanları doldur";
            return;
        }
        if (Password.Length < 8) { ErrorMessage = "Şifre en az 8 karakter olmalı"; return; }
        if (Password != PasswordConfirm) { ErrorMessage = "Şifreler eşleşmiyor"; return; }

        IsBusy = true;
        try
        {
            await _login.RegisterAsync(Email, Name, Password);
            Mode = LoginDialogMode.ConfirmPending;
        }
        catch (ValidationException ex) { ErrorMessage = ex.Message; }
        catch (LicenseApiNetworkException) { ErrorMessage = "Sunucuya ulaşılamıyor"; }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task ResendAsync()
    {
        IsBusy = true; ErrorMessage = null;
        try
        {
            await _login.ResendConfirmationAsync(Email);
            ErrorMessage = "Yeni doğrulama linki gönderildi.";
        }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task ActivateSelectedAsync()
    {
        if (Selected is null) return;
        IsBusy = true; ErrorMessage = null;
        try
        {
            await _licenseService.ActivateAsync(Selected.LicenseKey, machineName: Environment.MachineName);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (SlotFullException) { ErrorMessage = "Tüm cihaz slotları dolu. Diğer cihazda çıkış yap."; }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public bool IsLoginMode => Mode == LoginDialogMode.Login;
    public bool IsRegisterMode => Mode == LoginDialogMode.Register;
    public bool IsConfirmPendingMode => Mode == LoginDialogMode.ConfirmPending;
    public bool IsLicenseSelectionMode => Mode == LoginDialogMode.LicenseSelection;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnModeChanged(LoginDialogMode value)
    {
        OnPropertyChanged(nameof(IsLoginMode));
        OnPropertyChanged(nameof(IsRegisterMode));
        OnPropertyChanged(nameof(IsConfirmPendingMode));
        OnPropertyChanged(nameof(IsLicenseSelectionMode));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }
}
