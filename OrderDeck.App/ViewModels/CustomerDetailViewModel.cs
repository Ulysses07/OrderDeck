using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Formatting;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

public sealed partial class CustomerDetailViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly LabelRepository _labels;
    private readonly GiveawayRepository _giveaways;
    private string? _customerId;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _platform = "";
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string _firstSeenLabel = "";
    [ObservableProperty] private string _lastSeenLabel  = "";
    [ObservableProperty] private int    _totalLabelsPrinted;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private bool   _isBlacklisted;
    [ObservableProperty] private string? _blacklistReason;
    [ObservableProperty] private string _blacklistedAtLabel = "";
    [ObservableProperty] private string _notesEdit = "";

    public ObservableCollection<CustomerLabelRow>    Labels    { get; } = new();
    public ObservableCollection<CustomerGiveawayRow> Giveaways { get; } = new();

    public CustomerDetailViewModel(
        CustomerRepository customers, LabelRepository labels, GiveawayRepository giveaways)
    {
        _customers = customers;
        _labels = labels;
        _giveaways = giveaways;
    }

    /// <summary>Loads customer summary + label/giveaway history. Returns false if customer not found.</summary>
    public bool Load(string customerId)
    {
        var c = _customers.GetById(customerId);
        if (c is null) return false;

        _customerId = customerId;
        Username = c.Username;
        Platform = c.Platform;
        DisplayName = c.DisplayName;
        FirstSeenLabel = TrFormats.DateTime(c.FirstSeenAt);
        LastSeenLabel  = TrFormats.DateTime(c.LastSeenAt);
        TotalLabelsPrinted = c.TotalLabelsPrinted;
        TotalAmount = c.TotalAmount;
        IsBlacklisted = c.IsBlacklisted;
        BlacklistReason = c.BlacklistReason;
        BlacklistedAtLabel = c.BlacklistedAt is long t ? TrFormats.DateTime(t) : "";
        NotesEdit = c.Notes ?? "";

        Labels.Clear();
        foreach (var l in _labels.GetByCustomer(customerId)) Labels.Add(l);

        Giveaways.Clear();
        foreach (var g in _giveaways.GetParticipationsByCustomer(customerId)) Giveaways.Add(g);

        return true;
    }

    [RelayCommand]
    private void SaveNotes()
    {
        if (_customerId is null) return;
        try
        {
            _customers.UpdateNotes(_customerId, NotesEdit);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Notlar kaydedilemedi: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
