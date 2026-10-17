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
    /// <summary>Etiketin ekrandaki kopyası. Kâğıt bundan basılmaz: kuyruk baskısı satırı baskıdan hemen
    /// önce veritabanından yeniden okur ve bu kopyayı <see cref="Replace"/> ile tazeler (§5.6). Fiyat
    /// düzeltmesi (K4/K5) de satırı YERİNDE günceller: satır nesnesi aynı kaldığı için seçim ve sıra
    /// korunur, ekranda eski fiyat kalmaz.</summary>
    public Label Label { get; private set; }

    [ObservableProperty] private bool _isCustomerBlacklisted;

    /// <summary>How many backup buyers are attached to this label. Bulk-refreshed
    /// from <c>LabelService.GetBackupCounts</c> whenever the queue is rebuilt;
    /// also bumped/decremented in-place when the user adds/removes a single
    /// backup so the chip badge updates without a full requery.</summary>
    [ObservableProperty] private int _backupCount;

    public string Username    => Label.Username;

    /// <summary>Pretty name for the queue row UI. Falls back to <see cref="Username"/>
    /// when <c>Label.DisplayName</c> is missing — covers legacy rows from
    /// before migration 012 and platforms whose ingestor doesn't capture a
    /// separate display name.</summary>
    public string Display => string.IsNullOrWhiteSpace(Label.DisplayName)
        ? Label.Username
        : Label.DisplayName!;

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

    /// <summary>Aynı etiketin güncel hâlini yerine koyar (fiyat düzeltmesi). Başka bir etiketle
    /// çağrılmaz — kimlik değişmez.</summary>
    public void Replace(Label updated)
    {
        if (!string.Equals(updated.Id, Label.Id, System.StringComparison.Ordinal))
            throw new System.ArgumentException("Satır başka bir etiketle değiştirilemez.", nameof(updated));
        Label = updated;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Price));
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Username));
    }
}
