using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.App.Views;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Labeling;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.ViewModels;

public sealed partial class MainShellViewModel : ViewModelBase, IDisposable
{
    private readonly LabelService _labels;
    private readonly StreamSessionService _sessions;
    private readonly LabelPrinter _printer;
    private readonly CustomerService _customers;
    private readonly CustomerRepository _customerRepo;
    private readonly GiveawayService _giveaways;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _busSubscription;

    private const int MaxChatMessages = 200;

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();
    public ObservableCollection<LabelViewModel>       PrintQueue   { get; } = new();

    public GiveawayBannerViewModel Banner { get; }

    [ObservableProperty] private string _activeCode = "";
    [ObservableProperty] private string _activePriceText = "0";
    [ObservableProperty] private string _streamStatusLabel = "Yayın aktif değil";
    [ObservableProperty] private bool _isGiveawayActive;
    [ObservableProperty] private bool _canStartGiveaway;

    private string? _activeGiveawayId;

    public MainShellViewModel(
        IChatBus bus,
        LabelService labels,
        StreamSessionService sessions,
        LabelPrinter printer,
        CustomerService customers,
        CustomerRepository customerRepo,
        GiveawayService giveaways,
        GiveawayBannerViewModel banner)
    {
        _labels = labels;
        _sessions = sessions;
        _printer = printer;
        _customers = customers;
        _customerRepo = customerRepo;
        _giveaways = giveaways;
        Banner = banner;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _busSubscription = bus.Subscribe(OnChatMessage);

        Banner.AutoDrawRequested += () => DrawGiveawayNowCommand.Execute(null);

        UpdateStreamStatusLabel();
        UpdateGiveawayCanStart();
        ReloadQueueFromActiveSession();
    }

    private void OnChatMessage(ChatMessage m)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Chat panel + blacklist highlight
            var customer = _customerRepo.FindByPlatformAndUsername(m.Platform, m.Username);
            var blacklisted = customer?.IsBlacklisted ?? false;
            ChatMessages.Add(new ChatMessageViewModel(m, blacklisted));
            while (ChatMessages.Count > MaxChatMessages) ChatMessages.RemoveAt(0);

            // Forward to active giveaway, if any
            if (_activeGiveawayId is not null)
                _giveaways.AddParticipantFromChat(_activeGiveawayId, m);
        });
    }

    private void UpdateStreamStatusLabel()
    {
        var session = _sessions.GetActive();
        StreamStatusLabel = session is null
            ? "Yayın aktif değil"
            : $"Yayın aktif (başlangıç: {DateTimeOffset.FromUnixTimeSeconds(session.StartedAt):HH:mm})";
    }

    private void UpdateGiveawayCanStart()
    {
        CanStartGiveaway = _sessions.GetActive() is not null && !IsGiveawayActive;
    }

    private void ReloadQueueFromActiveSession()
    {
        PrintQueue.Clear();
        var session = _sessions.GetActive();
        if (session is null) return;
        foreach (var l in _labels.GetQueue(session.Id))
        {
            var customer = _customerRepo.GetById(l.CustomerId);
            PrintQueue.Add(new LabelViewModel(l, customer?.IsBlacklisted ?? false));
        }
    }

    [RelayCommand] private void StartStream()
    {
        if (_sessions.GetActive() is not null)
        {
            MessageBox.Show("Zaten aktif bir yayın var. Önce mevcut yayını bitir.",
                "Yayın aktif", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _sessions.Start("Yeni Yayın", new[] { "instagram", "tiktok" });
        UpdateStreamStatusLabel();
        UpdateGiveawayCanStart();
        ReloadQueueFromActiveSession();
    }

    [RelayCommand] private void EndStream()
    {
        var session = _sessions.GetActive();
        if (session is null) return;

        if (IsGiveawayActive)
        {
            MessageBox.Show("Aktif çekiliş var. Önce çekilişi tamamla veya iptal et.",
                "Çekiliş aktif", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show("Yayını bitirmek istediğinden emin misin?",
            "Yayını Bitir", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (PrintQueue.Count > 0)
        {
            try { Print(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Yazdırma sırasında hata oluştu, yine de yayını bitiriyorum:\n{ex.Message}",
                    "Yazdırma hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        _sessions.End(session.Id);
        UpdateStreamStatusLabel();
        UpdateGiveawayCanStart();

        var dialog = App.Host.Services.GetRequiredService<StreamReportDialog>();
        dialog.LoadReport(session.Id);
        dialog.Owner = Application.Current?.MainWindow;
        dialog.ShowDialog();
    }

    public void AddChatToQueue(ChatMessageViewModel messageVm)
    {
        var session = _sessions.GetActive();
        if (session is null)
        {
            MessageBox.Show("Önce yayın başlat.",
                "Aktif yayın yok", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryParsePrice(ActivePriceText, out var price))
        {
            MessageBox.Show("Geçerli bir fiyat gir (örn: 100 veya 99.50).",
                "Geçersiz fiyat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var label = _labels.Add(session.Id, messageVm.Message, price,
            string.IsNullOrWhiteSpace(ActiveCode) ? null : ActiveCode.Trim());
        PrintQueue.Add(new LabelViewModel(label, messageVm.IsSenderBlacklisted));
    }

    [RelayCommand]
    private void RemoveSelectedFromQueue(LabelViewModel? selected)
    {
        if (selected is null) return;
        _labels.Delete(selected.Id);
        PrintQueue.Remove(selected);
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (PrintQueue.Count == 0) return;
        var confirm = MessageBox.Show($"Kuyruktaki {PrintQueue.Count} etiket silinecek. Emin misin?",
            "Hepsini Temizle", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var item in PrintQueue.ToList()) _labels.Delete(item.Id);
        PrintQueue.Clear();
    }

    [RelayCommand]
    private void Print()
    {
        if (PrintQueue.Count == 0) return;
        var snapshot = PrintQueue.Select(vm => vm.Label).ToList();
        _printer.Print(snapshot);
        _labels.MarkPrintedAndRecord(snapshot.Select(l => l.Id).ToList());
        PrintQueue.Clear();
    }

    [RelayCommand] private void OpenSettings()
    {
        var dlg = App.Host.Services.GetRequiredService<SettingsDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
        RefreshHighlights();
    }

    [RelayCommand] private void OpenStreamHistory()
    {
        var dlg = App.Host.Services.GetRequiredService<StreamHistoryDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
    }

    [RelayCommand] private void OpenBlacklist()
    {
        var dlg = App.Host.Services.GetRequiredService<BlacklistDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
        RefreshHighlights();
    }

    [RelayCommand]
    private void StartGiveaway()
    {
        var session = _sessions.GetActive();
        if (session is null)
        {
            MessageBox.Show("Önce yayın başlat.", "Aktif yayın yok",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (IsGiveawayActive) return;

        var dlg = new NewGiveawayDialog { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var vm = dlg.ViewModel;
        var g = _giveaways.Start(
            sessionId: session.Id,
            keyword: vm.Keyword.Trim(),
            durationSeconds: vm.SelectedDuration.Seconds,
            winnerCount: vm.WinnerCount,
            platformFilter: vm.SelectedPlatform.Filter,
            preventRewinning: vm.PreventRewinning);

        _activeGiveawayId = g.Id;
        IsGiveawayActive = true;
        UpdateGiveawayCanStart();
        Banner.StartTracking(g);
    }

    [RelayCommand]
    private void DrawGiveawayNow()
    {
        if (_activeGiveawayId is null) return;

        _giveaways.Draw(_activeGiveawayId);
        Banner.StopTracking();
        _activeGiveawayId = null;
        IsGiveawayActive = false;
        UpdateGiveawayCanStart();
    }

    [RelayCommand]
    private void CancelGiveaway()
    {
        if (_activeGiveawayId is null) return;
        var confirm = MessageBox.Show("Çekiliş iptal edilecek. Emin misin?",
            "Çekilişi İptal", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _giveaways.Cancel(_activeGiveawayId);
        Banner.StopTracking();
        _activeGiveawayId = null;
        IsGiveawayActive = false;
        UpdateGiveawayCanStart();
    }

    [RelayCommand]
    private void AddChatSenderToBlacklist(ChatMessageViewModel? msg)
    {
        if (msg is null) return;
        var dlg = new AddToBlacklistDialog
        {
            Mode = AddToBlacklistDialog.DialogMode.Prefilled,
            UsernameText = msg.Username,
            PlatformText = msg.Platform
        };
        dlg.Owner = Application.Current?.MainWindow;
        if (dlg.ShowDialog() != true) return;

        _customers.EnsureBlacklistedManual(msg.Platform, msg.Username, dlg.ReasonText);
        RefreshHighlights();
    }

    [RelayCommand]
    private void AddQueueRowToBlacklist(LabelViewModel? row)
    {
        if (row is null) return;
        var dlg = new AddToBlacklistDialog
        {
            Mode = AddToBlacklistDialog.DialogMode.Prefilled,
            UsernameText = row.Username,
            PlatformText = row.Label.Platform
        };
        dlg.Owner = Application.Current?.MainWindow;
        if (dlg.ShowDialog() != true) return;

        _customers.EnsureBlacklistedManual(row.Label.Platform, row.Username, dlg.ReasonText);
        RefreshHighlights();
    }

    [RelayCommand]
    private void OpenCustomerDetailFromChat(ChatMessageViewModel? msg)
    {
        if (msg is null) return;
        var customer = _customerRepo.FindByPlatformAndUsername(msg.Platform, msg.Username);
        if (customer is null)
        {
            MessageBox.Show("Bu kullanıcı henüz kayıtlı değil.", "Müşteri yok",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowCustomerDetail(customer.Id);
    }

    [RelayCommand]
    private void OpenCustomerDetailFromQueue(LabelViewModel? row)
    {
        if (row is null) return;
        ShowCustomerDetail(row.CustomerId);
    }

    [RelayCommand]
    private void OpenCustomerSearch()
    {
        var dlg = App.Host.Services.GetRequiredService<Views.CustomerSearchDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
    }

    private static void ShowCustomerDetail(string customerId)
    {
        var dlg = App.Host.Services.GetRequiredService<Views.CustomerDetailDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.Open(customerId);
    }

    private void RefreshHighlights()
    {
        foreach (var vm in ChatMessages)
        {
            var c = _customerRepo.FindByPlatformAndUsername(vm.Platform, vm.Username);
            vm.IsSenderBlacklisted = c?.IsBlacklisted ?? false;
        }
        foreach (var vm in PrintQueue)
        {
            var c = _customerRepo.GetById(vm.CustomerId);
            vm.IsCustomerBlacklisted = c?.IsBlacklisted ?? false;
        }
    }

    private static bool TryParsePrice(string text, out decimal price)
    {
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out price)
            || decimal.TryParse(text, NumberStyles.Any, Formatting.TrFormats.TR, out price);
    }

    public void Dispose()
    {
        Banner.Dispose();
        _busSubscription.Dispose();
    }
}
