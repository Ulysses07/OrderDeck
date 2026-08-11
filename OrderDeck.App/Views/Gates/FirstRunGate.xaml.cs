using System;
using System.Windows.Controls;
using OrderDeck.App.Services.Gates;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Gates;

/// <summary>
/// İlk açılış sihirbazı — tam ekran gate.
///
/// Gate <c>true</c> ile kapanırsa sihirbaz BİTİRİLDİ, <c>false</c> ile
/// kapanırsa atlandı. Hem "Bitir" hem "Daha sonra hallederim" aynı
/// <c>RequestClose</c> olayını yükselttiği için ayrımı VM'in
/// <see cref="FirstRunWizardViewModel.IsStep6"/> bayrağından okuyoruz —
/// "Bitir" yalnız son adımda görünür. (Kalıcı ayrım da var: "Bitir"
/// <c>AppSettings.HasCompletedFirstRun</c>'ı true'ya çeviriyor. Onu okumak
/// ayarları yeniden yüklemek demek olurdu, view'ın elindeki bayrak yeterli.)
///
/// <paramref name="vm"/> null olabiliyor: kompozisyon testi bu view'ı
/// servissiz çiziyor (ölçtüğü şey kaynak çözümlemesi).
/// </summary>
public partial class FirstRunGate : UserControl
{
    private readonly AppGate _gate;
    private readonly FirstRunWizardViewModel? _vm;

    private FirstRunGate(AppGate gate, FirstRunWizardViewModel? vm)
    {
        InitializeComponent();
        _gate = gate;
        _vm = vm;
        DataContext = vm;
        if (vm is not null) vm.RequestClose += OnRequestClose;
    }

    /// <summary>Fabrika: içerik gate'in kendisini alır (yığın kalıbı).</summary>
    public static FirstRunGate Create(AppGate gate, FirstRunWizardViewModel? vm)
        => new(gate, vm);

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.RequestClose -= OnRequestClose;
        _gate.Close(_vm?.IsStep6 ?? false);
    }
}
