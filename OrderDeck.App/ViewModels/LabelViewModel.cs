using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// UI-side wrapper around <see cref="Label"/> for the print-queue panel. Exposes
/// <see cref="IsCustomerBlacklisted"/> so queued rows from blacklisted users get the
/// same red highlight as their chat messages.
/// </summary>
public sealed partial class LabelViewModel : ObservableObject
{
    public Label Label { get; }

    [ObservableProperty] private bool _isCustomerBlacklisted;

    public string Username    => Label.Username;
    public string MessageText => Label.MessageText;
    public decimal Price      => Label.Price;
    public string Id          => Label.Id;
    public string CustomerId  => Label.CustomerId;

    public LabelViewModel(Label label, bool isCustomerBlacklisted)
    {
        Label = label;
        IsCustomerBlacklisted = isCustomerBlacklisted;
    }
}
