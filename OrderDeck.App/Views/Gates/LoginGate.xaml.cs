using System;
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// LoginDialog'un gate karşılığı. Aynı view iki bağlamda kullanılıyor:
/// açılışta (ShellHost boş) ve çalışırken (hesap değiştirme, shell altta).
/// Tek fark çıkış düğmesinin metni.
///
/// <paramref name="vm"/> null olabiliyor: kompozisyon testi bu view'ı
/// servissiz çiziyor (ölçtüğü şey kaynak çözümlemesi).
/// </summary>
public partial class LoginGate : UserControl
{
    private readonly LoginDialogViewModel? _vm;
    private readonly AppGate _gate;

    private LoginGate(AppGate gate, LoginDialogViewModel? vm, bool isStartupGate)
    {
        InitializeComponent();
        _gate = gate;
        _vm = vm;
        DataContext = vm;

        // Açılışta iptal = uygulamadan çıkış (StartupFlow false'u Shutdown'a
        // çeviriyor). Çalışırken iptal = sadece bu ekranı kapat, shell altta
        // duruyor. Davranış aynı, anlatım farklı.
        ExitButton.Content = isStartupGate ? "Çıkış" : "Vazgeç";

        if (vm is not null)
            vm.RequestClose += OnRequestClose;
    }

    /// <summary>Fabrika: içerik gate'in kendisini alır (yığın kalıbı).</summary>
    public static LoginGate Create(AppGate gate, LoginDialogViewModel? vm, bool isStartupGate)
        => new(gate, vm, isStartupGate);

    private void OnRequestClose(object? sender, EventArgs e) => Close(true);

    // OnExit XAML'den bağlı (Click="OnExit").
    private void OnExit(object sender, RoutedEventArgs e) => Close(false);

    // Her iki kapanış yolu aynı temizliği yapsın diye tek yerde toplandı.
    private void Close(bool confirmed)
    {
        if (_vm is not null) _vm.RequestClose -= OnRequestClose;
        _gate.Close(confirmed);
    }

    private void OnLoginPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Password = LoginPassword.Password;
    }

    private void OnRegisterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Password = RegisterPassword.Password;
    }

    private void OnRegisterPasswordConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.PasswordConfirm = RegisterPasswordConfirm.Password;
    }
}
