using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Phase 4g: müşterinin telefonu yokken inline collect.
/// Save → PhoneNormalizer → invalid:error / valid: UpdatePhone + close callback.
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

        ValidationError = null;
        _customers.UpdatePhone(_customerId, normalized);
        _closeAction();
    }
}
