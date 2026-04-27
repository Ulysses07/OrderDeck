using System.Windows;
using LiveDeck.App.Services;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Labeling;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App;

public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkey;
    private readonly ClipboardService _clipboard;
    private readonly ClipboardLabelFormatter _formatter;
    private readonly OrderQueueViewModel _orderQueue;
    private readonly CustomerRepository _customers;

    public MainWindow()
    {
        InitializeComponent();

        var sp = App.Host.Services;
        DataContext       = sp.GetRequiredService<MainViewModel>();
        _hotkey           = sp.GetRequiredService<HotkeyService>();
        _clipboard        = sp.GetRequiredService<ClipboardService>();
        _formatter        = sp.GetRequiredService<ClipboardLabelFormatter>();
        _orderQueue       = sp.GetRequiredService<OrderQueueViewModel>();
        _customers        = sp.GetRequiredService<CustomerRepository>();

        Loaded += OnLoaded;
        Closed += (_, _) => _hotkey.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkey.Attach(this);
        _hotkey.HotkeyPressed += OnF9;
    }

    private void OnF9()
    {
        var order = _orderQueue.Selected;
        if (order is null) return;

        // Optional: write price into etiket.exe before clipboard
        var etiket = App.Host.Services.GetRequiredService<EtiketIntegration>();
        etiket.TrySetPrice(order.UnitPrice);

        var customer = _customers.GetById(order.CustomerId);
        var username = customer?.Username ?? "@unknown";

        var payload = _formatter.Format(username, order.OriginalMessageText);
        _clipboard.SetText(payload);
    }
}
