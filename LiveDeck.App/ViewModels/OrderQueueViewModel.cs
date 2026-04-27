using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;

namespace LiveDeck.App.ViewModels;

public sealed partial class OrderQueueViewModel : ViewModelBase
{
    private readonly OrderService _orders;
    private readonly StreamSessionService _sessions;
    private readonly Core.Storage.Repositories.OrderRepository _repo;

    public ObservableCollection<OrderItem> Orders { get; } = new();

    [ObservableProperty] private OrderItem? _selected;
    [ObservableProperty] private string _activeTab = OrderStatus.New;
    [ObservableProperty] private string _streamStatusLabel = "Yayın aktif değil";

    public OrderQueueViewModel(
        OrderService orders,
        StreamSessionService sessions,
        Core.Storage.Repositories.OrderRepository repo)
    {
        _orders = orders;
        _sessions = sessions;
        _repo = repo;
        Refresh();
    }

    public void Refresh()
    {
        Orders.Clear();
        var session = _sessions.GetActive();
        StreamStatusLabel = session is null
            ? "Yayın aktif değil — başlatmak için 'Yayın Başlat' tıklayın"
            : $"Yayın aktif (başlangıç: {DateTimeOffset.FromUnixTimeSeconds(session.StartedAt):HH:mm})";
        if (session is null) return;

        foreach (var o in _repo.GetBySessionAndStatus(session.Id, ActiveTab))
            Orders.Add(o);
    }

    partial void OnActiveTabChanged(string value) => Refresh();

    [RelayCommand] private void StartStream()
    {
        var session = _sessions.GetActive();
        if (session is not null)
        {
            MessageBox.Show("Zaten aktif bir yayın var. Önce mevcut yayını bitir.",
                "Yayın aktif", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _sessions.Start("Yeni Yayın", new[] { "instagram" });
        Refresh();
    }

    [RelayCommand] private void EndStream()
    {
        var session = _sessions.GetActive();
        if (session is null) return;

        var confirm = MessageBox.Show("Yayını bitirmek istediğinden emin misin?",
            "Yayını Bitir", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _sessions.End(session.Id);
        Refresh();
    }

    [RelayCommand] private void Approve()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.New);
        Refresh();
    }

    [RelayCommand] private void MarkDmSent()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.DmSent);
        Refresh();
    }

    [RelayCommand] private void MarkPaid()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Paid);
        Refresh();
    }

    [RelayCommand] private void MarkShipped()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Shipped);
        Refresh();
    }

    [RelayCommand] private void MarkCompleted()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Completed);
        Refresh();
    }

    [RelayCommand] private void Cancel()
    {
        if (Selected is null) return;
        _orders.UpdateStatus(Selected.Id, OrderStatus.Cancelled);
        Refresh();
    }
}
