using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.ViewModels;

public sealed partial class BlacklistViewModel : ViewModelBase
{
    private readonly CustomerRepository _repo;
    private readonly CustomerService _customers;

    public ObservableCollection<Customer> Items { get; } = new();

    [ObservableProperty] private Customer? _selected;
    [ObservableProperty] private int _totalCount;

    public BlacklistViewModel(CustomerRepository repo, CustomerService customers)
    {
        _repo = repo;
        _customers = customers;
        Reload();
    }

    public void Reload()
    {
        Items.Clear();
        foreach (var c in _repo.GetBlacklisted()) Items.Add(c);
        TotalCount = Items.Count;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is null) return;
        var confirm = MessageBox.Show(
            $"{Selected.Username} kara listeden çıkarılacak. Emin misin?",
            "Onayla", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _customers.RemoveFromBlacklist(Selected.Id);
        Reload();
    }

    [RelayCommand]
    private void AddManual()
    {
        var dialog = new Views.AddToBlacklistDialog
        {
            Mode = Views.AddToBlacklistDialog.DialogMode.Manual
        };
        // WPF auto-registers a Window in Application.Current.Windows the moment
        // it's constructed. Picking the *last* window therefore returns `dialog`
        // itself — and Window.Owner = self throws ArgumentException at ShowDialog.
        // Walk back skipping the dialog being parented; falls through to
        // MainWindow when nothing else matches.
        dialog.Owner = ResolveOwnerWindow(dialog);
        if (dialog.ShowDialog() != true) return;

        _customers.EnsureBlacklistedManual(
            dialog.PlatformText ?? "instagram",
            dialog.UsernameText ?? "",
            dialog.ReasonText);
        Reload();
    }

    [RelayCommand]
    private void OpenCustomerDetail(string? customerId)
    {
        if (string.IsNullOrEmpty(customerId)) return;
        var dlg = App.Host.Services.GetRequiredService<Views.CustomerDetailDialog>();
        dlg.Owner = ResolveOwnerWindow(dlg);
        dlg.Open(customerId);
    }

    /// <summary>Returns a sensible Owner window for a modal dialog, EXCLUDING the
    /// dialog being parented. WPF registers a Window in Application.Current.Windows
    /// during construction, so naive "last window" picks return the dialog itself,
    /// which Window.Owner.set rejects with ArgumentException.</summary>
    private static Window? ResolveOwnerWindow(Window self)
    {
        var windows = Application.Current?.Windows;
        if (windows is null) return null;
        // Walk most-recent → first, skip self, return the first visible candidate.
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var w = windows[i];
            if (!ReferenceEquals(w, self) && w.IsLoaded) return w;
        }
        return Application.Current?.MainWindow == self ? null : Application.Current?.MainWindow;
    }
}
