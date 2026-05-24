using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Formatting;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;

namespace OrderDeck.App.ViewModels;

public sealed partial class CustomerDetailViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly LabelRepository _labels;
    private readonly LabelService _labelService;
    private readonly GiveawayRepository _giveaways;
    private readonly StreamSessionService _sessions;
    private readonly LicenseApiClient _api;
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

    /// <summary>Header for the labels section. Reflects whether we're scoped to
    /// the active stream session (most common path) or the full lifetime
    /// history (when no stream is running).</summary>
    [ObservableProperty] private string _labelsSectionTitle = "Etiketler";

    /// <summary>Bound to the DataGrid SelectionChanged in the dialog code-behind
    /// so the cancel/uncancel buttons can flip CanExecute based on whether the
    /// current selection contains active or already-cancelled rows.</summary>
    public ObservableCollection<CustomerLabelRow> SelectedLabels { get; } = new();

    public ObservableCollection<CustomerLabelRow>    Labels    { get; } = new();
    public ObservableCollection<CustomerGiveawayRow> Giveaways { get; } = new();

    /// <summary>Müşteri bakiye satırları (server-side ledger).</summary>
    public ObservableCollection<BalanceTransactionRow> BalanceTransactions { get; } = new();

    [ObservableProperty] private decimal _balanceAmount;
    [ObservableProperty] private string _balanceLabel = "0,00 TL";
    [ObservableProperty] private bool _balanceLoaded;
    [ObservableProperty] private string? _balanceError;

    public CustomerDetailViewModel(
        CustomerRepository customers,
        LabelRepository labels,
        LabelService labelService,
        GiveawayRepository giveaways,
        StreamSessionService sessions,
        LicenseApiClient api)
    {
        _customers = customers;
        _labels = labels;
        _labelService = labelService;
        _giveaways = giveaways;
        _sessions = sessions;
        _api = api;

        SelectedLabels.CollectionChanged += (_, _) =>
        {
            CancelSelectedCommand.NotifyCanExecuteChanged();
            UncancelSelectedCommand.NotifyCanExecuteChanged();
        };
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

        ReloadLabels();

        Giveaways.Clear();
        foreach (var g in _giveaways.GetParticipationsByCustomer(customerId)) Giveaways.Add(g);

        // Bakiye fire-and-forget — UI hemen açılır, balance gelince güncellenir.
        _ = ReloadBalanceAsync();

        return true;
    }

    /// <summary>Customer.Id'yi server projection ID'ye parse eder.
    /// PR #88 sonrası ingest edilen müşteriler için Id zaten hex N format
    /// (== WpfCustomerProjection.Id). Eski Customer'lar (shopper app öncesi)
    /// projection ile eşleşmemiş olabilir — bu durumda parse fail, balance
    /// section gizli kalır.</summary>
    private bool TryGetProjectionId(out Guid id)
    {
        id = Guid.Empty;
        if (_customerId is null) return false;
        return Guid.TryParseExact(_customerId, "N", out id);
    }

    public async Task ReloadBalanceAsync()
    {
        if (!TryGetProjectionId(out var projectionId))
        {
            BalanceLoaded = false;
            BalanceError = "Bu müşteri server eşlemesi olmadığı için bakiye gösterilemiyor.";
            BalanceTransactions.Clear();
            return;
        }
        try
        {
            BalanceError = null;
            var resp = await _api.GetCustomerBalanceAsync(projectionId, take: 50, CancellationToken.None);
            BalanceAmount = resp.Balance.Balance;
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            BalanceLabel = $"{resp.Balance.Balance.ToString("N2", tr)} TL";
            BalanceTransactions.Clear();
            foreach (var t in resp.Transactions)
                BalanceTransactions.Add(BalanceTransactionRow.FromDto(t));
            BalanceLoaded = true;
        }
        catch (Exception ex)
        {
            BalanceError = $"Bakiye yüklenemedi: {ex.Message}";
            BalanceLoaded = false;
        }
    }

    [RelayCommand]
    private async Task AddBalanceAsync()
    {
        if (!TryGetProjectionId(out var projectionId))
        {
            MessageBox.Show(
                "Bu müşteri server eşlemesi olmadığı için bakiye eklenemez. "
                + "Müşteri Sipariş app'ı üzerinden bağlanınca aktif olur.",
                "Bakiye", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var label = DisplayName ?? Username;
        var dlg = new Views.AddBalanceDialog(_api, projectionId, label)
        {
            Owner = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsActive && w.IsVisible)
        };
        if (dlg.ShowDialog() == true && dlg.Saved)
            await ReloadBalanceAsync();
    }

    /// <summary>Re-reads labels from storage and rebuilds the bound collection.
    /// Called after Load and after every cancel/uncancel command.</summary>
    private void ReloadLabels()
    {
        if (_customerId is null) return;

        Labels.Clear();
        SelectedLabels.Clear();

        // Scope: when an auction stream is currently active, show only the labels
        // this customer added during THIS stream — the auctioneer's most common
        // mid-stream task is correcting orders just placed, not auditing history.
        // When no stream is active (between sessions) fall back to the full
        // lifetime view so support / ops can still inspect the past.
        var active = _sessions.GetActive();
        IReadOnlyList<CustomerLabelRow> rows;
        if (active is not null)
        {
            rows = _labels.GetByCustomerAndSession(_customerId, active.Id);
            LabelsSectionTitle = "Bu yayındaki etiketler";
        }
        else
        {
            rows = _labels.GetByCustomer(_customerId);
            LabelsSectionTitle = "Tüm etiketler";
        }

        foreach (var l in rows) Labels.Add(l);
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

    /// <summary>Sebep dialog'unu açar; onaylanırsa seçili (henüz iptal edilmemiş)
    /// satırları soft-cancel'lar ve listeyi tazeler.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private void CancelSelected()
    {
        var targets = SelectedLabels.Where(l => !l.IsCancelled).Select(l => l.Id).ToList();
        if (targets.Count == 0) return;

        var dlg = new Views.CancelLabelDialog
        {
            Owner = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsActive && w.IsVisible)
        };
        if (dlg.ShowDialog() != true) return;
        var reason = dlg.SelectedReasonCode;
        if (string.IsNullOrEmpty(reason)) return;

        try
        {
            _labelService.Cancel(targets, reason);
            ReloadLabels();

            // For each cancelled label that has backups, surface the transfer
            // dialog so the operator can promote a backup to a new label.
            // Multi-select cancels open multiple dialogs sequentially — rare in
            // practice, common cancel is one row at a time.
            foreach (var labelId in targets)
            {
                var backups = _labelService.GetBackups(labelId);
                if (backups.Count == 0) continue;

                var parent = _labels.GetById(labelId);
                if (parent is null) continue;

                var dialog = new Views.BackupTransferDialog(_labelService)
                {
                    Owner = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.IsActive && w.IsVisible)
                };
                dialog.Load(parent, backups);
                dialog.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"İptal başarısız: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUncancelSelected))]
    private void UncancelSelected()
    {
        var targets = SelectedLabels.Where(l => l.IsCancelled).Select(l => l.Id).ToList();
        if (targets.Count == 0) return;
        try
        {
            _labelService.Uncancel(targets);
            ReloadLabels();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"İptal geri alınamadı: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool CanCancelSelected() => SelectedLabels.Any(l => !l.IsCancelled);
    private bool CanUncancelSelected() => SelectedLabels.Any(l => l.IsCancelled);
}

/// <summary>UI row tipi — server BalanceTransactionDto'sundan map'lenir.</summary>
public sealed class BalanceTransactionRow
{
    public Guid Id { get; init; }
    public decimal Amount { get; init; }
    public string Kind { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public string AmountLabel { get; init; } = "";
    public bool IsPositive { get; init; }
    public string? Reason { get; init; }
    public string CreatedAtLabel { get; init; } = "";

    public static BalanceTransactionRow FromDto(OrderDeck.Licensing.Api.Models.CustomerBalanceTransactionDto t)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        return new BalanceTransactionRow
        {
            Id = t.Id,
            Amount = t.Amount,
            Kind = t.Kind,
            KindLabel = LabelOf(t.Kind),
            AmountLabel = (t.Amount > 0 ? "+" : "") + t.Amount.ToString("N2", tr) + " TL",
            IsPositive = t.Amount > 0,
            Reason = t.Reason,
            CreatedAtLabel = t.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm", tr),
        };
    }

    private static string LabelOf(string kind) => kind switch
    {
        "refund-full" => "Hatalı ürün iadesi",
        "refund-net" => "Müşteri iadesi (kargo düşülmüş)",
        "purchase-deduction" => "Ödemede kullanıldı",
        "manual-adjustment" => "Manuel ayar",
        "reversal" => "İptal edildi",
        _ => kind,
    };
}
