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

    /// <summary>How many backup buyers are attached to this label. Bulk-refreshed
    /// from <c>LabelService.GetBackupCounts</c> whenever the queue is rebuilt;
    /// also bumped/decremented in-place when the user adds/removes a single
    /// backup so the chip badge updates without a full requery.</summary>
    [ObservableProperty] private int _backupCount;

    public string Username    => Label.Username;
    public string MessageText => Label.MessageText;
    public decimal Price      => Label.Price;
    public string Id          => Label.Id;
    public string CustomerId  => Label.CustomerId;

    public LabelViewModel(Label label, bool isCustomerBlacklisted, int backupCount = 0)
    {
        Label = label;
        IsCustomerBlacklisted = isCustomerBlacklisted;
        BackupCount = backupCount;
    }
}
