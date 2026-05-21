using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

public sealed partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly CustomerService _customerService;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly PaymentRequestService _paymentService;
    private readonly IDialogService _dialogService;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _platformFilter;
    [ObservableProperty] private bool _lastStreamShoppersOnly;

    public ObservableCollection<Customer> Results { get; } = new();
    private readonly Dictionary<string, decimal> _streamAmounts = new();

    public CustomerSearchViewModel(
        CustomerRepository customers,
        CustomerService customerService,
        SessionRepository sessions,
        LabelRepository labels,
        PaymentRequestService paymentService,
        IDialogService dialogService)
    {
        _customers = customers;
        _customerService = customerService;
        _sessions = sessions;
        _labels = labels;
        _paymentService = paymentService;
        _dialogService = dialogService;
    }

    partial void OnQueryChanged(string value) => ApplySearch(value);
    partial void OnPlatformFilterChanged(string? value) => RefreshSearch();
    partial void OnLastStreamShoppersOnlyChanged(bool value) => RefreshSearch();

    /// <summary>Phase 4f: external trigger to re-run search after PlatformFilter changes.</summary>
    public void RefreshSearch() => ApplySearch(Query);

    private void ApplySearch(string value)
    {
        Results.Clear();
        _streamAmounts.Clear();

        if (LastStreamShoppersOnly)
        {
            var shoppers = _customerService.GetLastStreamShoppers();
            var session = _sessions.GetLatestEnded();
            if (session is not null)
            {
                var top = _labels.GetTopCustomersBySession(session.Id, int.MaxValue);
                foreach (var t in top)
                {
                    var c = shoppers.FirstOrDefault(s => s.Platform == t.Platform && s.Username == t.Username);
                    if (c is not null) _streamAmounts[c.Id] = t.TotalAmount;
                }
            }

            IEnumerable<Customer> filtered = shoppers;
            if (!string.IsNullOrWhiteSpace(value))
            {
                var q = value.Trim();
                filtered = filtered.Where(c =>
                    c.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (c.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            if (!string.IsNullOrEmpty(PlatformFilter))
                filtered = filtered.Where(c => c.Platform == PlatformFilter);

            foreach (var c in filtered) Results.Add(c);
            return;
        }

        // Empty query: show all customers so the operator can discover
        // newly-registered shoppers (e.g. via the shopper app) who don't
        // yet have any orders. Previously this returned nothing, leaving
        // registered customers invisible until someone typed.
        var raw = string.IsNullOrWhiteSpace(value)
            ? _customers.GetAll()
            : _customers.Search(value.Trim(), limit: 50);
        var f = string.IsNullOrEmpty(PlatformFilter)
            ? raw
            : raw.Where(c => c.Platform == PlatformFilter);
        foreach (var c in f) Results.Add(c);
    }

    [RelayCommand]
    private async Task OpenWhatsAppAsync(Customer? customer)
    {
        if (customer is null) return;

        var amount = _streamAmounts.TryGetValue(customer.Id, out var perStream)
            ? perStream
            : customer.TotalAmount;

        var session = _sessions.GetLatestEnded();
        var streamDate = session?.EndedAt is long ended
            ? DateTimeOffset.FromUnixTimeSeconds(ended).LocalDateTime
            : DateTime.Now;

        var result = _paymentService.OpenWhatsApp(customer, amount, streamDate);

        if (result == PaymentRequestResult.PhoneRequired)
        {
            var saved = _dialogService.ShowPhoneEntryDialog(customer.Id);
            if (saved)
            {
                var updated = _customers.GetById(customer.Id);
                if (updated is not null)
                    _paymentService.OpenWhatsApp(updated, amount, streamDate);
            }
        }
        else if (result == PaymentRequestResult.LaunchFailed)
        {
            _dialogService.ShowError("WhatsApp açılamadı. WhatsApp Desktop kurulu mu?");
        }

        await Task.CompletedTask;
    }
}
