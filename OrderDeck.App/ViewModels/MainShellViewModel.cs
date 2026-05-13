using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.App.Services.IntakeForm;
using OrderDeck.App.Views;
using OrderDeck.Chat.YouTube;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Labeling;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Services;
using OrderDeck.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.ViewModels;

public sealed partial class MainShellViewModel : ViewModelBase, IDisposable
{
    private readonly LabelService _labels;
    private readonly StreamSessionService _sessions;
    private readonly ILabelPrinter _printer;
    private readonly CustomerService _customers;
    private readonly CustomerRepository _customerRepo;
    private readonly GiveawayService _giveaways;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _busSubscription;
    private readonly LicenseService _licenseService;
    private readonly IntakeFormSyncService _intakeSync;
    private readonly SettingsStore _settingsStore;
    private readonly AnimationCatalogClient? _animationCatalogClient;

    // 500 messages = ~30 seconds of scroll-back at the projected 30 msg/sec
    // peak across IG + TT + FB + YT, ~70 seconds at the realistic 7 msg/sec
    // average. Matches the ChatBus ring buffer cap so the UI never lags
    // behind the bus's history; WPF's VirtualizingStackPanel keeps render
    // cost flat at this size.
    private const int MaxChatMessages = 500;

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();
    public ObservableCollection<LabelViewModel>       PrintQueue   { get; } = new();

    /// <summary>Multi-select kuyrukta seçili etiketler. Code-behind QueueList.SelectionChanged
    /// event'inden senkronize eder. Boş = hiç seçim yok.</summary>
    public ObservableCollection<LabelViewModel>       SelectedQueueItems { get; } = new();

    /// <summary>Yazdır butonu için dinamik label.</summary>
    public string PrintButtonLabel => SelectedQueueItems.Count > 0
        ? $"Yazdır ({SelectedQueueItems.Count})"
        : "Yazdır";

    /// <summary>Sil butonu için dinamik label.</summary>
    public string DeleteButtonLabel => SelectedQueueItems.Count switch
    {
        0 => "Seçileni Sil",
        1 => "Seçileni Sil",
        _ => $"Seçilenleri Sil ({SelectedQueueItems.Count})"
    };

    public GiveawayBannerViewModel Banner { get; }

    [ObservableProperty] private string _activeCode = "";
    [ObservableProperty] private string _activePriceText = "0";
    [ObservableProperty] private string _streamStatusLabel = "Yayın aktif değil";
    [ObservableProperty] private bool _isGiveawayActive;
    [ObservableProperty] private bool _canStartGiveaway;
    [ObservableProperty] private LabelViewModel? _selectedQueueItem;

    private string? _activeGiveawayId;

    /// <summary>
    /// When the user clicks the "Yedek+" chip on a label, this stays set until
    /// they pick a chat message (or press ESC). The chat double-click handler
    /// branches on <see cref="IsInBackupSelectionMode"/> to route the click to
    /// <see cref="AssignChatAsBackup"/> instead of the normal queue-add flow.
    /// Cleared when assignment completes, ESC is pressed, or focus leaves the
    /// queue/chat panels for too long.
    /// </summary>
    [ObservableProperty] private LabelViewModel? _backupTargetLabel;

    public bool IsInBackupSelectionMode => BackupTargetLabel is not null;

    public string? BackupModeBanner => BackupTargetLabel is null
        ? null
        : $"Yedek modu: '{BackupTargetLabel.Username}' etiketi için chat'ten birini seç (ESC iptal)";

    partial void OnBackupTargetLabelChanged(LabelViewModel? value)
    {
        OnPropertyChanged(nameof(IsInBackupSelectionMode));
        OnPropertyChanged(nameof(BackupModeBanner));
    }

    [ObservableProperty] private bool _isLicenseWritable = true;
    [ObservableProperty] private string _licenseStatusText = "";
    [ObservableProperty] private Brush _licenseStatusBrush = Brushes.Gray;

    /// <summary>Non-null when the heartbeat has failed enough consecutive
    /// times that the operator should know "license server unreachable".
    /// Bound by MainShellView.xaml to a yellow banner above the queue.</summary>
    [ObservableProperty] private string? _serverOfflineBanner;

    /// <summary>Non-null when the active license has fewer than 7 days
    /// remaining. Yellow banner — week's heads-up to renew.</summary>
    [ObservableProperty] private string? _licenseExpiryBanner;

    /// <summary>3-state chat ingestion health dot. "ok" / "idle" / "off".
    /// Polled by a 5s DispatcherTimer (see UpdateChatHealth).</summary>
    [ObservableProperty] private string _chatHealthState = "off";
    [ObservableProperty] private string _chatHealthTooltip = "Chat takibi kapalı (yayın aktif değil)";

    [ObservableProperty] private int _newIntakeSubmissionsCount;

    public bool HasNewIntakeSubmissions => NewIntakeSubmissionsCount > 0;

    partial void OnNewIntakeSubmissionsCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasNewIntakeSubmissions));
    }

    public IAsyncRelayCommand OpenAccountCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenIntakeSubmissionsCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenDekontEkleCommand { get; private set; } = null!;

    private readonly YouTubeModerationService? _youTubeModeration;

    public MainShellViewModel(
        IChatBus bus,
        LabelService labels,
        StreamSessionService sessions,
        ILabelPrinter printer,
        CustomerService customers,
        CustomerRepository customerRepo,
        GiveawayService giveaways,
        GiveawayBannerViewModel banner,
        LicenseService licenseService,
        IntakeFormSyncService intakeSync,
        SettingsStore settingsStore,
        YouTubeModerationService? youTubeModeration = null,
        AnimationCatalogClient? animationCatalogClient = null)
    {
        _labels = labels;
        _sessions = sessions;
        _printer = printer;
        _customers = customers;
        _customerRepo = customerRepo;
        _giveaways = giveaways;
        _youTubeModeration = youTubeModeration;
        Banner = banner;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // _settingsStore must be assigned BEFORE EnsureChatFlushTimer() because
        // that method calls UpdateChatHealth() synchronously, which reads
        // _settingsStore. Previously a first-chance NRE leaked here (caught by
        // UpdateChatHealth's defensive catch) and the chat health dot stuck on
        // "off" until the next timer tick.
        _settingsStore = settingsStore;
        _animationCatalogClient = animationCatalogClient;
        _intakeSync = intakeSync;
        _intakeSync.SubmissionsSynced += OnIntakeSubmissionsSynced;

        _busSubscription = bus.Subscribe(OnChatMessage);
        EnsureChatFlushTimer();
        _licenseService = licenseService;
        _licenseService.StatusChanged += OnLicenseStatusChanged;
        _licenseService.HeartbeatStateChanged += OnHeartbeatStateChanged;
        UpdateLicenseUiFromService();
        UpdateServerOfflineBanner();

        Banner.AutoDrawRequested += () => DrawGiveawayNowCommand.Execute(null);

        SelectedQueueItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PrintButtonLabel));
            OnPropertyChanged(nameof(DeleteButtonLabel));
        };

        OpenAccountCommand = new AsyncRelayCommand(OpenAccountAsync);
        OpenIntakeSubmissionsCommand = new AsyncRelayCommand(OpenIntakeSubmissionsAsync);
        OpenDekontEkleCommand = new AsyncRelayCommand(OpenDekontEkleAsync);

        UpdateStreamStatusLabel();
        UpdateGiveawayCanStart();
        ReloadQueueFromActiveSession();
    }

    // Background queue of chat messages awaiting a UI flush. Bounded so a
    // sudden spike (1500 viewers all typing at once) can't blow heap before
    // the dispatcher gets around to draining; 1000 is ~30 seconds at our
    // worst projected throughput.
    private readonly System.Collections.Concurrent.ConcurrentQueue<ChatMessage> _pendingChat = new();
    private const int PendingChatHardCap = 1000;
    // 100 ms batch window: at 30 msg/sec we coalesce 3 messages per dispatcher
    // hop instead of doing 30 individual BeginInvokes per second. Lower than
    // ~120 ms keeps the chat feeling live; above that the auctioneer notices
    // the lag before clicks register.
    private static readonly TimeSpan ChatFlushInterval = TimeSpan.FromMilliseconds(100);
    private System.Windows.Threading.DispatcherTimer? _chatFlushTimer;

    // Chat health surface (status-bar dot). Set on every incoming message;
    // a 5s timer compares it against the configured handle + active session
    // to derive the 3-state UI signal. Volatile so the publishing thread
    // (chat bus subscriber) and the polling timer agree without a lock.
    private DateTime _lastChatMessageAtUtc = DateTime.MinValue;
    private static readonly TimeSpan ChatHealthPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChatHealthIdleThreshold = TimeSpan.FromSeconds(60);
    private System.Windows.Threading.DispatcherTimer? _chatHealthTimer;

    private void OnChatMessage(ChatMessage m)
    {
        // Hot path: never block the publisher. Just enqueue + drop oldest if
        // the queue is full (rather than letting it grow unbounded under a
        // pathological burst).
        _pendingChat.Enqueue(m);
        while (_pendingChat.Count > PendingChatHardCap && _pendingChat.TryDequeue(out _)) { }
        _lastChatMessageAtUtc = DateTime.UtcNow;
    }

    private void EnsureChatFlushTimer()
    {
        if (_chatFlushTimer is not null) return;
        _chatFlushTimer = new System.Windows.Threading.DispatcherTimer(
            ChatFlushInterval, System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => DrainPendingChat(), _dispatcher);
        _chatFlushTimer.Start();

        // Separate timer for the chat-health dot. Cheap (one comparison +
        // two settings reads every 5s); deliberately not piggy-backing on
        // ChatFlushTimer because that one fires 10×/sec at Background
        // priority and we don't need that resolution for a status dot.
        _chatHealthTimer = new System.Windows.Threading.DispatcherTimer(
            ChatHealthPollInterval, System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => UpdateChatHealth(), _dispatcher);
        _chatHealthTimer.Start();
        UpdateChatHealth();
    }

    private void UpdateChatHealth()
    {
        bool hasActiveSession;
        bool hasYouTubeHandle;
        try
        {
            hasActiveSession = _sessions.GetActive() is not null;
            var settings = _settingsStore.Load();
            hasYouTubeHandle = !string.IsNullOrWhiteSpace(settings.YouTubeChannelHandle);
        }
        catch
        {
            // Defensive: test harnesses sometimes wire mock services that
            // throw on these calls (e.g. SQLite-less builds). Treat as "off"
            // rather than poisoning the dispatcher with an NRE.
            ChatHealthState = "off";
            ChatHealthTooltip = "Chat takibi kapalı";
            return;
        }
        var hasAnyChatSource = hasActiveSession; // bridge ingestor is always on

        // 3-state signal:
        // off  — operator hasn't started a stream OR has no chat source wired
        //        (e.g. session active but no YouTube handle and the Chrome
        //        extension hasn't connected). Gray dot, no alarm.
        // idle — chat source is supposed to be running but no message has
        //        arrived in 60s. Yellow dot — could be a quiet moment, could
        //        be the scraper is wedged.
        // ok   — at least one message in the last 60s. Green dot.
        if (!hasAnyChatSource)
        {
            ChatHealthState = "off";
            ChatHealthTooltip = "Chat takibi kapalı (yayın aktif değil)";
            return;
        }

        var elapsed = DateTime.UtcNow - _lastChatMessageAtUtc;
        if (_lastChatMessageAtUtc != DateTime.MinValue && elapsed < ChatHealthIdleThreshold)
        {
            var seconds = (int)elapsed.TotalSeconds;
            ChatHealthState = "ok";
            ChatHealthTooltip = $"Chat aktif (son mesaj {seconds}sn önce)";
        }
        else
        {
            ChatHealthState = "idle";
            var sourceList = hasYouTubeHandle
                ? "YouTube + eklenti köprüsü"
                : "eklenti köprüsü";
            ChatHealthTooltip = _lastChatMessageAtUtc == DateTime.MinValue
                ? $"Chat takibi açık ({sourceList}) — henüz mesaj yok"
                : $"Chat takibi açık ({sourceList}) — son mesaj 60sn'den uzun süre önce, scraper takılmış olabilir";
        }
    }

    private void DrainPendingChat()
    {
        if (_pendingChat.IsEmpty) return;

        // Pull everything currently queued; keep new arrivals for the next tick.
        var batch = new System.Collections.Generic.List<ChatMessage>(_pendingChat.Count);
        while (_pendingChat.TryDequeue(out var m)) batch.Add(m);

        foreach (var m in batch)
        {
            var customer = _customerRepo.FindByPlatformAndUsername(m.Platform, m.Username);
            var blacklisted = customer?.IsBlacklisted ?? false;
            ChatMessages.Add(new ChatMessageViewModel(m, blacklisted));

            // Forward to active giveaway. Same per-message work as before;
            // batching only saves dispatcher overhead, not domain logic.
            if (_activeGiveawayId is not null)
                _giveaways.AddParticipantFromChat(_activeGiveawayId, m);
        }

        // Trim once after the batch instead of once per message.
        while (ChatMessages.Count > MaxChatMessages) ChatMessages.RemoveAt(0);
    }

    private void OnLicenseStatusChanged(object? sender, LicenseStatus status)
    {
        // Marshal to UI thread
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(UpdateLicenseUiFromService);
            return;
        }
        UpdateLicenseUiFromService();
    }

    private void OnHeartbeatStateChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(UpdateServerOfflineBanner);
            return;
        }
        UpdateServerOfflineBanner();
    }

    private void UpdateServerOfflineBanner()
    {
        // 2+ consecutive failures = at least one full heartbeat interval (1h
        // by default) of unreachable. Single transient miss is normal and
        // not worth alarming the operator about. Status comes through the
        // existing pipeline anyway (OfflineGrace state).
        var fails = _licenseService.ConsecutiveHeartbeatFailures;
        if (fails >= 2)
        {
            var lastSuccess = _licenseService.LastHeartbeatSuccessAt is { } s
                ? s.LocalDateTime.ToString("HH:mm")
                : "—";
            ServerOfflineBanner =
                $"Lisans sunucusu erişilemiyor ({fails}× başarısız, son başarılı kontrol {lastSuccess}). " +
                "Çevrimdışı çalışma modunda yayına devam edebilirsin; uzun süre devam ederse trial moduna düşersin.";
        }
        else
        {
            ServerOfflineBanner = null;
        }
    }

    private void UpdateLicenseUiFromService()
    {
        var status = _licenseService.CurrentStatus;
        IsLicenseWritable = status.IsWritable();
        (LicenseStatusText, LicenseStatusBrush) = status switch
        {
            LicenseStatus.Active        => ($"Lisans aktif — {_licenseService.CurrentLicense?.RemainingDaysAtLastCheck ?? 0} gün",
                                             (Brush)Brushes.SeaGreen),
            LicenseStatus.OfflineGrace  => ("Çevrimdışı (grace)", Brushes.Goldenrod),
            LicenseStatus.OfflineExpired or LicenseStatus.ExpiredOnline or LicenseStatus.Revoked
                                        => ("Lisans gerekli", Brushes.Crimson),
            LicenseStatus.NoLicense     => ("Lisans yok", Brushes.Gray),
            LicenseStatus.TrialActive   => ($"Deneme: {RemainingTrialDays()} gün kaldı", (Brush)Brushes.DodgerBlue),
            LicenseStatus.TrialExpired  => ("Deneme süresi doldu — Lisans gerekli", Brushes.Crimson),
            _                           => ("Başlatılıyor", Brushes.Gray)
        };
        UpdateLicenseExpiryBanner();
    }

    private void UpdateLicenseExpiryBanner()
    {
        // Active license expiring soon → operator needs at least a week to
        // renew. Trial expiry is already loud (status text + crimson brush)
        // so we only banner the silent "active but about to flip to
        // OfflineExpired" case.
        if (_licenseService.CurrentStatus == LicenseStatus.Active &&
            _licenseService.CurrentLicense is { } lic &&
            lic.RemainingDaysAtLastCheck >= 0 &&
            lic.RemainingDaysAtLastCheck <= 7)
        {
            var d = lic.RemainingDaysAtLastCheck;
            LicenseExpiryBanner = d == 0
                ? "Lisansın bugün doluyor — yenilemezsen yarın YouTube chat ve premium özellikler kapanacak."
                : $"Lisansın {d} gün içinde dolacak — yenilemezsen YouTube chat ve premium özellikler kapanacak.";
        }
        else
        {
            LicenseExpiryBanner = null;
        }
    }

    private int RemainingTrialDays()
    {
        if (_licenseService.CurrentTrial is OrderDeck.Licensing.Trial.TrialState.Active a)
            return a.RemainingDays;
        return 0;
    }

    partial void OnIsLicenseWritableChanged(bool value)
    {
        // Refresh CanExecute on all write commands that depend on license state
        (StartStreamCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (EndStreamCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (PrintCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (RemoveSelectedFromQueueCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (ClearQueueCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StartGiveawayCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (DrawGiveawayNowCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (CancelGiveawayCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (AddChatSenderToBlacklistCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (AddQueueRowToBlacklistCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (DeleteSelectedFromQueueViaShortcutCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }

    private void OnIntakeSubmissionsSynced(object? sender, int count)
    {
        // UI thread dispatch (Phase 4b pattern)
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(() => NewIntakeSubmissionsCount += count);
            return;
        }
        NewIntakeSubmissionsCount += count;
    }

    private async Task OpenIntakeSubmissionsAsync()
    {
        await Task.Yield();
        var dlg = global::OrderDeck.App.App.Host.Services.GetRequiredService<global::OrderDeck.App.Views.CustomerSearchDialog>();
        var vm = (CustomerSearchViewModel)dlg.DataContext;
        vm.PlatformFilter = "form";
        vm.RefreshSearch();

        dlg.Owner = System.Windows.Application.Current?.MainWindow;
        dlg.ShowDialog();

        NewIntakeSubmissionsCount = 0;
    }

    private async Task OpenAccountAsync()
    {
        await Task.Yield(); // ensure UI thread
        var dlg = global::OrderDeck.App.App.Host.Services.GetRequiredService<global::OrderDeck.App.Views.AccountDialog>();
        dlg.Owner = System.Windows.Application.Current?.MainWindow;
        dlg.ShowDialog();
    }

    private async Task OpenDekontEkleAsync()
    {
        await Task.Yield(); // ensure UI thread
        var dlg = global::OrderDeck.App.App.Host.Services.GetRequiredService<global::OrderDeck.App.Views.DekontEkleDialog>();
        dlg.Owner = System.Windows.Application.Current?.MainWindow;
        dlg.ShowDialog();
        // Payment outbox 30sn içinde sync'ler — UI feedback yok, mobile app
        // listede otomatik görünür.
    }

    private void UpdateStreamStatusLabel()
    {
        var session = _sessions.GetActive();
        StreamStatusLabel = session is null
            ? "Yayın aktif değil"
            // session.StartedAt is unix seconds (UTC). Format the LocalDateTime
            // so the user sees the wall-clock time on their machine — formatting
            // the DateTimeOffset directly would print UTC and the user reported
            // a 3-hour shift (Europe/Istanbul = UTC+3).
            : $"Yayın aktif (başlangıç: {DateTimeOffset.FromUnixTimeSeconds(session.StartedAt).LocalDateTime:HH:mm})";
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
        var labels = _labels.GetQueue(session.Id);
        // Single round-trip for backup counts so each row's chip badge is
        // accurate without N+1 queries.
        var backupCounts = labels.Count == 0
            ? new System.Collections.Generic.Dictionary<string, int>()
            : (System.Collections.Generic.IReadOnlyDictionary<string, int>)
                _labels.GetBackupCounts(labels.Select(l => l.Id));
        foreach (var l in labels)
        {
            var customer = _customerRepo.GetById(l.CustomerId);
            backupCounts.TryGetValue(l.Id, out var count);
            PrintQueue.Add(new LabelViewModel(l, customer?.IsBlacklisted ?? false, count));
        }
    }

    private bool CanWrite() => IsLicenseWritable;

    [RelayCommand(CanExecute = nameof(CanWrite))] private void StartStream()
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

    [RelayCommand(CanExecute = nameof(CanWrite))] private async Task EndStream()
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
            try { await Print(); }
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

    /// <summary>
    /// Activates backup-selection mode for a label: subsequent chat double-clicks
    /// will be routed to <see cref="AssignChatAsBackup"/> until ESC or assignment.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void BeginAddBackup(LabelViewModel? labelVm)
    {
        if (labelVm is null) return;
        BackupTargetLabel = labelVm;
    }

    [RelayCommand]
    private void CancelBackupSelection() => BackupTargetLabel = null;

    /// <summary>
    /// Called from MainShellView's chat-activation handler when in backup mode.
    /// Returns true when the click was consumed as a backup assignment; the
    /// caller (View code-behind) should NOT fall through to AddChatToQueue.
    ///
    /// Creates a tentative-backup Label that goes straight into the print
    /// queue with the parent label's price + code; the operator clicks Yazdır
    /// to physically produce the spare sticker, which carries the Y stamp.
    /// </summary>
    public bool TryAssignChatAsBackup(ChatMessageViewModel messageVm)
    {
        if (BackupTargetLabel is null) return false;

        var target = BackupTargetLabel;
        try
        {
            var backup = _labels.AddBackup(
                target.Id,
                messageVm.Message.Platform,
                messageVm.Message.Username,
                messageVm.Message.DisplayName ?? messageVm.Message.Username,
                messageVm.Message.Text);

            // Reflect the new tentative row in the queue UI (same pattern as
            // AddChatToQueue) so the operator can hit Yazdır immediately.
            PrintQueue.Add(new LabelViewModel(backup, messageVm.IsSenderBlacklisted));

            // Bump the parent's chip in-place — avoids a full queue requery.
            target.BackupCount++;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Yedek eklenemedi: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BackupTargetLabel = null;
        }
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void RemoveSelectedFromQueue()
    {
        if (SelectedQueueItems.Count == 0) return;
        foreach (var vm in SelectedQueueItems.ToList())
        {
            _labels.Delete(vm.Id);
            PrintQueue.Remove(vm);
        }
        SelectedQueueItems.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void ClearQueue()
    {
        if (PrintQueue.Count == 0) return;
        var confirm = MessageBox.Show($"Kuyruktaki {PrintQueue.Count} etiket silinecek. Emin misin?",
            "Hepsini Temizle", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var item in PrintQueue.ToList()) _labels.Delete(item.Id);
        PrintQueue.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task Print()
    {
        var snapshot = SelectedQueueItems.Count > 0
            ? SelectedQueueItems.ToList()
            : PrintQueue.ToList();
        if (snapshot.Count == 0) return;

        var labels = snapshot.Select(vm => vm.Label).ToList();

        // Kargo PR F: customer'ı RecipientPaysActive olan label'ların id'lerini
        // toplayıp print pass'a geçir → etikette "ALICI ÖDEMELİ" kırmızı yazı.
        // Distinct customerId üzerinden 1 lookup query (büyük queue'larda
        // N+1 önlemi).
        var recipientPaysIds = ComputeRecipientPaysLabelIds(labels);

        // UI freeze fix (2026-05-13): _printer.Print() System.Drawing.Printing
        // PrintDocument.Print()'i sync çağırıyor → printer driver/spooler
        // asılırsa UI thread süresiz block olur. Task.Run ile background'a al.
        // Süre LabelPrinter kendi log'unda görünür; burada sadece hata
        // gösteren MessageBox.
        try
        {
            await Task.Run(() => _printer.Print(labels, recipientPaysIds));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Etiket yazdırma başarısız: {ex.Message}\n\n" +
                "Yazıcı bağlantısını kontrol et veya Ayarlar > Yazıcı'dan farklı bir yazıcı seç.",
                "Yazdırma Hatası",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        _labels.MarkPrintedAndRecord(labels.Select(l => l.Id).ToList());

        // Sadece yazdırılanları kuyruktan kaldır (smart mode'da kalan seçimsizler korunur).
        foreach (var vm in snapshot) PrintQueue.Remove(vm);
        SelectedQueueItems.Clear();
    }

    /// <summary>Kargo PR F: print'lenecek label'lar arasında müşterisi
    /// RecipientPaysActive=true olanları seç. CustomerRepository.FindById
    /// her customerId için bir kez çağrılır (distinct over snapshot).</summary>
    private System.Collections.Generic.IReadOnlySet<string> ComputeRecipientPaysLabelIds(
        System.Collections.Generic.IReadOnlyList<OrderDeck.Core.Sales.Label> labels)
    {
        var recipientCustomers = new System.Collections.Generic.HashSet<string>();
        foreach (var customerId in labels.Select(l => l.CustomerId).Distinct())
        {
            var c = _customerRepo.GetById(customerId);
            if (c?.RecipientPaysActive == true) recipientCustomers.Add(customerId);
        }
        return labels
            .Where(l => recipientCustomers.Contains(l.CustomerId))
            .Select(l => l.Id)
            .ToHashSet();
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

    [RelayCommand(CanExecute = nameof(CanWrite))]
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

        var dlg = new NewGiveawayDialog(_settingsStore.Load(), _animationCatalogClient)
            { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var vm = dlg.ViewModel;
        var animationId = vm.SelectedAnimationId ?? _settingsStore.Load().GiveawayAnimation.DefaultId;
        var g = _giveaways.Start(
            sessionId: session.Id,
            keyword: vm.Keyword.Trim(),
            durationSeconds: vm.SelectedDuration.Seconds,
            winnerCount: vm.WinnerCount,
            platformFilter: vm.SelectedPlatform.Filter,
            preventRewinning: vm.PreventRewinning,
            animationId: animationId);

        _activeGiveawayId = g.Id;
        IsGiveawayActive = true;
        UpdateGiveawayCanStart();
        Banner.StartTracking(g);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void DrawGiveawayNow()
    {
        if (_activeGiveawayId is null) return;

        try
        {
            _giveaways.Draw(_activeGiveawayId);
        }
        catch (OrderDeck.Core.Sales.GiveawayHasNoParticipantsException ex)
        {
            // Empty draw — used to silently broadcast a winners-empty event,
            // which animated a blank wheel for ~10s and confused operators
            // ("did the draw fire?"). Now: explicit MessageBox + leave the
            // giveaway active so the operator can either wait for entries
            // or cancel it deliberately.
            MessageBox.Show(
                $"'{ex.Keyword}' anahtar kelimesiyle hiç katılımcı yok.\n\n" +
                "Çekiliş hâlâ açık — biraz daha bekleyebilir veya iptal edebilirsin.",
                "Çekiliş — Katılımcı Yok",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Banner.StopTracking();
        _activeGiveawayId = null;
        IsGiveawayActive = false;
        UpdateGiveawayCanStart();
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
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

    /// <summary>
    /// Right-click → "YT'de mesajı sil". The chat message id our InnerTube
    /// scraper stores in <c>ChatMessage.ExternalId</c> is the same one
    /// YouTube's API expects for <c>liveChatMessages.delete</c>, so we pass
    /// it through unchanged.
    /// </summary>
    [RelayCommand]
    private async Task DeleteYouTubeMessage(ChatMessageViewModel? msg)
    {
        if (msg is null || _youTubeModeration is null) return;
        if (!string.Equals(msg.Platform, "youtube", StringComparison.OrdinalIgnoreCase)) return;

        var messageId = msg.Message.ExternalId;
        if (string.IsNullOrEmpty(messageId))
        {
            MessageBox.Show(
                "Bu mesaj YouTube üzerinden gelmedi (orijinal ID yok), silinemez.",
                "YouTube moderasyon", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _youTubeModeration.DeleteMessageAsync(messageId).ConfigureAwait(true);
        }
        catch (ModerationException ex)
        {
            MessageBox.Show(ex.Message, "YouTube moderasyon",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Beklenmeyen hata: {ex.Message}", "YouTube moderasyon",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Right-click → "YT'de kullanıcıyı banla". Username for YouTube messages
    /// is the channel id (UCxxx...) — the field <c>liveChatBans.insert</c>
    /// needs as <c>bannedUserChannelId</c>. liveChatId is resolved on demand
    /// by the moderation service.
    /// </summary>
    [RelayCommand]
    private async Task BanYouTubeUser(ChatMessageViewModel? msg)
    {
        if (msg is null || _youTubeModeration is null) return;
        if (!string.Equals(msg.Platform, "youtube", StringComparison.OrdinalIgnoreCase)) return;

        var displayName = msg.Display;
        var confirm = MessageBox.Show(
            $"'{displayName}' kullanıcısı YouTube canlı yayında kalıcı olarak banlansın mı?",
            "YouTube ban", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _youTubeModeration.BanUserAsync(msg.Username).ConfigureAwait(true);
            MessageBox.Show($"{displayName} banlandı.", "YouTube moderasyon",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ModerationException ex)
        {
            MessageBox.Show(ex.Message, "YouTube moderasyon",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Beklenmeyen hata: {ex.Message}", "YouTube moderasyon",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
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

    [RelayCommand(CanExecute = nameof(CanWrite))]
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

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void DeleteSelectedFromQueueViaShortcut() => RemoveSelectedFromQueue();

    [RelayCommand]
    private void OpenShortcutHelp()
    {
        var dlg = App.Host.Services.GetRequiredService<Views.ShortcutHelpDialog>();
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
        _chatFlushTimer?.Stop();
        _chatFlushTimer = null;
        Banner.Dispose();
        _busSubscription.Dispose();
    }
}
