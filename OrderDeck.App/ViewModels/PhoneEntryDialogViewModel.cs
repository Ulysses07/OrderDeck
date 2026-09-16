using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Phase 4g: müşterinin telefonu yokken inline collect.
/// Save → PhoneNormalizer → invalid:error / valid: UpdatePhone + close callback.
///
/// <para>R12-D02 (2026-09-16 denetimi): yazının SONUCU kontrol edilir. Çekmece
/// açıldıktan sonra inen bir KVKK silme kararı yazıyı reddedebiliyor; eskiden
/// çekmece yine de "kaydedildi" diye kapanıyor, çağıran akış (WhatsApp ödeme
/// isteği) olmayan bir numarayla devam ediyordu.</para>
/// </summary>
public sealed partial class PhoneEntryDialogViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly string _customerId;
    private readonly Action _closeAction;

    [ObservableProperty]
    private string _phoneInput = "";

    [ObservableProperty]
    private string? _validationError;

    public PhoneEntryDialogViewModel(
        CustomerRepository customers,
        string customerId,
        Action closeAction)
    {
        _customers = customers;
        _customerId = customerId;
        _closeAction = closeAction;
    }

    [RelayCommand]
    private void Save()
    {
        var normalized = PhoneNormalizer.NormalizeTr(PhoneInput);
        if (normalized is null)
        {
            ValidationError = "Geçersiz telefon numarası. 10 haneli TR mobil numara girin.";
            return;
        }

        if (_customers.UpdatePhone(_customerId, normalized) == 0)
        {
            ValidationError =
                "Bu müşteri kaydı güncellenemiyor: kayıt için silme talebi " +
                "uygulanmış olabilir. Numara kaydedilmedi.";
            return;
        }

        ValidationError = null;
        _closeAction();
    }
}
