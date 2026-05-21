using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.Chat.YouTube;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Labeling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;

namespace OrderDeck.App.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class SettingsViewModel : ViewModelBase
{
    public const string DefaultPrinterSentinel = "(Windows varsayılanı)";

    private readonly AppSettings _liveSettings;
    private readonly SettingsStore _store;
    private readonly int _originalOverlayPort;

    public ObservableCollection<string> AvailablePrinters { get; } = new();
    public ObservableCollection<string> AvailableFonts    { get; } = new();
    public ObservableCollection<string> AvailableThemes   { get; } = new() { "minimal" };

    [ObservableProperty] private string _selectedPrinter = DefaultPrinterSentinel;
    [ObservableProperty] private int    _labelWidthMm;
    [ObservableProperty] private int    _labelHeightMm;
    [ObservableProperty] private int    _labelGapMm;
    [ObservableProperty] private string _labelFontFamily = "Arial";
    [ObservableProperty] private int    _labelUserFontSize;
    [ObservableProperty] private int    _labelMessageFontSize;

    [ObservableProperty] private int    _overlayPort;
    [ObservableProperty] private string _chatTheme = "minimal";

    // Phase 4g — Payment settings
    [ObservableProperty] private string _paymentTemplate = "";
    [ObservableProperty] private string _iban = "";
    [ObservableProperty] private string _accountHolder = "";
    [ObservableProperty] private string _papara = "";

    // Kargo PR A — Shipping threshold + fee. Empty string → "feature off"
    // user-friendly olur; TryParse'la decimal'a çevriliyor. CommitToSettings'te
    // empty/0/negative → null (feature kapalı).
    [ObservableProperty] private string _freeShippingThresholdText = "";
    [ObservableProperty] private string _shippingFeeText = "";

    // PR-E — Kümülatif kargo "kazandın" WhatsApp şablonu.
    [ObservableProperty] private string _shippingWonTemplate = "";

    // Phase 5c — YouTube Live chat scraper
    [ObservableProperty] private string _youTubeChannelHandle = "";

    // Phase 5d — YouTube OAuth (moderation)
    [ObservableProperty] private string _youTubeConnectionStatus = "Bağlı değil";
    [ObservableProperty] private bool _isYouTubeConnected;
    [ObservableProperty] private bool _isYouTubeBusy;

    /// <summary>Inverse of <see cref="IsYouTubeBusy"/> for IsEnabled bindings
    /// on the Connect/Disconnect buttons.</summary>
    public bool IsYouTubeIdle => !IsYouTubeBusy;
    partial void OnIsYouTubeBusyChanged(bool value) => OnPropertyChanged(nameof(IsYouTubeIdle));

    // Phase 5f — Spam / troll filter toggles
    [ObservableProperty] private bool _spamFilterEnabled = true;
    [ObservableProperty] private bool _spamDropShortMessages;
    [ObservableProperty] private int _spamMinMessageLength = 2;
    [ObservableProperty] private bool _spamDropDuplicates = true;
    [ObservableProperty] private bool _spamDropAllCaps;
    [ObservableProperty] private bool _spamDropLinks = true;
    [ObservableProperty] private bool _spamDropProfanity;
    [ObservableProperty] private string _spamBlockedWordsText = "";

    // Giveaway animation (Task 20)
    [ObservableProperty] private AnimationPickerViewModel _animationPicker = new();
    [ObservableProperty] private double _animationVolume;
    [ObservableProperty] private bool _animationMuted;

    [ObservableProperty] private string? _validationError;

    /// <summary>True iff Save was called and OverlayPort changed (caller checks for restart prompt).</summary>
    public bool OverlayPortChanged { get; private set; }

    /// <summary>True iff Save committed changes; dialog uses to set DialogResult.</summary>
    public bool Saved { get; private set; }

    public ShortcutsTabViewModel ShortcutsTab { get; }
    public IntakeFormSettingsViewModel IntakeForm { get; }
    public ShopperAppSettingsViewModel ShopperApp { get; }

    private readonly YouTubeOAuthService? _youTubeOAuth;
    private readonly Services.Sync.WhatsAppTemplateSyncService? _waTemplateSync;

    public SettingsViewModel(AppSettings settings, SettingsStore store, ShortcutsTabViewModel shortcutsTab,
        IntakeFormSettingsViewModel intakeForm,
        ShopperAppSettingsViewModel shopperApp,
        YouTubeOAuthService? youTubeOAuth = null,
        AnimationCatalogClient? catalogClient = null,
        Services.Sync.WhatsAppTemplateSyncService? waTemplateSync = null)
    {
        _liveSettings = settings;
        _store = store;
        _originalOverlayPort = settings.OverlayPort;
        ShortcutsTab = shortcutsTab;
        IntakeForm = intakeForm;
        ShopperApp = shopperApp;
        _youTubeOAuth = youTubeOAuth;
        _waTemplateSync = waTemplateSync;

        LoadFromSettings();
        LoadInstalledPrinters();
        LoadInstalledFonts();
        _ = IntakeForm.LoadAsync();
        _ = ShopperApp.LoadAsync();
        _ = RefreshYouTubeConnectionStatusAsync();

        // Use the source-generated setters (NOT the backing fields) so
        // OnPropertyChanged fires — XAML bindings activate AFTER the dialog
        // shows, but using the property surface keeps the contract clean and
        // future-proofs against ordering bugs (e.g. if the dialog is reused
        // or DataContext is swapped at runtime). Defensive sanity floor on
        // Volume protects users whose settings.json was saved with the
        // earlier broken UI (slider rendered at 0% before the
        // UniformGrid-clip fix).
        AnimationVolume = settings.GiveawayAnimation.Volume <= 0 && !settings.GiveawayAnimation.MutedMode
            ? 0.7
            : settings.GiveawayAnimation.Volume;
        AnimationMuted = settings.GiveawayAnimation.MutedMode;
        AnimationPicker.SelectedId = settings.GiveawayAnimation.DefaultId;

        if (catalogClient is not null)
        {
            _ = LoadCatalogAsync(catalogClient, settings.GiveawayAnimation.DefaultId);
        }
    }

    private async System.Threading.Tasks.Task LoadCatalogAsync(AnimationCatalogClient client, string persistedSelection)
    {
        try
        {
            var entries = await client.LoadAsync();
            AnimationPicker.LoadAnimations(entries);
            // After loading, re-apply persisted selection (LoadAnimations may have
            // reset SelectedId to entries[0].Id if it didn't match the empty/initial bootstrap state).
            AnimationPicker.SelectedId = persistedSelection;
        }
        catch
        {
            // Silently fall back to the bootstrap state. Catalog fetch failures
            // shouldn't break Settings UI; the operator can still save volume/muted.
        }
    }

    /// <summary>
    /// Updates <see cref="YouTubeConnectionStatus"/> + <see cref="IsYouTubeConnected"/>
    /// from the OAuth service. Called on dialog open and after Connect/Disconnect.
    /// Swallows errors — settings dialog must never throw on a probe.
    /// </summary>
    public async System.Threading.Tasks.Task RefreshYouTubeConnectionStatusAsync()
    {
        if (_youTubeOAuth is null)
        {
            YouTubeConnectionStatus = "OAuth servisi yapılandırılmamış";
            IsYouTubeConnected = false;
            return;
        }

        try
        {
            var connected = await _youTubeOAuth.IsConnectedAsync().ConfigureAwait(true);
            if (!connected)
            {
                YouTubeConnectionStatus = "Bağlı değil";
                IsYouTubeConnected = false;
                return;
            }

            var title = await _youTubeOAuth.GetConnectedChannelTitleAsync().ConfigureAwait(true);
            YouTubeConnectionStatus = string.IsNullOrEmpty(title)
                ? "Bağlı"
                : $"Bağlı: {title}";
            IsYouTubeConnected = true;
        }
        catch (Exception ex)
        {
            YouTubeConnectionStatus = $"Durum okunamadı: {ex.Message}";
            IsYouTubeConnected = false;
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ConnectYouTube()
    {
        if (_youTubeOAuth is null) return;
        try
        {
            IsYouTubeBusy = true;
            YouTubeConnectionStatus = "Tarayıcıdan onay bekleniyor...";
            await _youTubeOAuth.ConnectAsync().ConfigureAwait(true);
            await RefreshYouTubeConnectionStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            YouTubeConnectionStatus = $"Bağlantı başarısız: {ex.Message}";
            IsYouTubeConnected = false;
        }
        finally
        {
            IsYouTubeBusy = false;
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DisconnectYouTube()
    {
        if (_youTubeOAuth is null) return;
        try
        {
            IsYouTubeBusy = true;
            await _youTubeOAuth.DisconnectAsync().ConfigureAwait(true);
            await RefreshYouTubeConnectionStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsYouTubeBusy = false;
        }
    }

    private void LoadFromSettings()
    {
        SelectedPrinter      = _liveSettings.PrinterName ?? DefaultPrinterSentinel;
        LabelWidthMm         = _liveSettings.LabelWidthMm;
        LabelHeightMm        = _liveSettings.LabelHeightMm;
        LabelGapMm           = _liveSettings.LabelGapMm;
        LabelFontFamily      = _liveSettings.LabelFontFamily;
        LabelUserFontSize    = _liveSettings.LabelUserFontSize;
        LabelMessageFontSize = _liveSettings.LabelMessageFontSize;
        OverlayPort          = _liveSettings.OverlayPort;
        ChatTheme            = _liveSettings.ChatTheme;

        // Phase 4g — Payment
        PaymentTemplate = _liveSettings.Payment.WhatsAppMessageTemplate;
        Iban            = _liveSettings.Payment.Iban;
        AccountHolder   = _liveSettings.Payment.AccountHolder;
        Papara          = _liveSettings.Payment.Papara;

        // Kargo PR A — Shipping. Empty string olarak göster eğer ayar null;
        // operator boş bırakmayı = feature off olarak görür.
        FreeShippingThresholdText = FormatOptionalDecimal(_liveSettings.Shipping.FreeShippingThreshold);
        ShippingFeeText           = FormatOptionalDecimal(_liveSettings.Shipping.ShippingFee);

        // PR-E — Kümülatif kargo "kazandın" template.
        ShippingWonTemplate = _liveSettings.Payment.ShippingWonTemplate;

        // Phase 5c — YouTube
        YouTubeChannelHandle = _liveSettings.YouTubeChannelHandle ?? string.Empty;

        // Phase 5f — Spam filter
        SpamFilterEnabled       = _liveSettings.SpamFilter.Enabled;
        SpamDropShortMessages   = _liveSettings.SpamFilter.DropShortMessages;
        SpamMinMessageLength    = _liveSettings.SpamFilter.MinMessageLength;
        SpamDropDuplicates      = _liveSettings.SpamFilter.DropDuplicates;
        SpamDropAllCaps         = _liveSettings.SpamFilter.DropAllCaps;
        SpamDropLinks           = _liveSettings.SpamFilter.DropLinks;
        SpamDropProfanity       = _liveSettings.SpamFilter.DropProfanity;
        SpamBlockedWordsText    = string.Join(", ", _liveSettings.SpamFilter.BlockedWords);
    }

    private void LoadInstalledPrinters()
    {
        AvailablePrinters.Clear();
        AvailablePrinters.Add(DefaultPrinterSentinel);
        foreach (string p in PrinterSettings.InstalledPrinters)
            AvailablePrinters.Add(p);
    }

    private void LoadInstalledFonts()
    {
        AvailableFonts.Clear();
        using var fonts = new InstalledFontCollection();
        foreach (var f in fonts.Families.OrderBy(f => f.Name))
            AvailableFonts.Add(f.Name);
    }

    private bool Validate()
    {
        if (OverlayPort < 1024 || OverlayPort > 65535)
        { ValidationError = "Port 1024-65535 arasında olmalı."; return false; }

        if (LabelWidthMm < 10 || LabelWidthMm > 200)
        { ValidationError = "Etiket genişliği 10-200 mm arasında olmalı."; return false; }

        if (LabelHeightMm < 10 || LabelHeightMm > 200)
        { ValidationError = "Etiket yüksekliği 10-200 mm arasında olmalı."; return false; }

        if (LabelGapMm < 0 || LabelGapMm > 50)
        { ValidationError = "Etiket aralığı 0-50 mm arasında olmalı."; return false; }

        if (LabelUserFontSize < 6 || LabelUserFontSize > 72 ||
            LabelMessageFontSize < 6 || LabelMessageFontSize > 72)
        { ValidationError = "Font boyutu 6-72 pt arasında olmalı."; return false; }

        // YouTube channel handle / URL — empty is fine (= disabled), but
        // a non-empty value that we can't recognise as either an @handle
        // or a watch URL will silently make the scraper idle forever.
        // Extract logic mirrors YouTubeChatHostedService line 86-94.
        var ytTrim = YouTubeChannelHandle?.Trim();
        if (!string.IsNullOrEmpty(ytTrim))
        {
            var looksLikeHandle = ytTrim.StartsWith('@') || !ytTrim.Contains('/');
            var looksLikeUrl = ytTrim.Contains("youtube.com", System.StringComparison.OrdinalIgnoreCase)
                            || ytTrim.Contains("youtu.be", System.StringComparison.OrdinalIgnoreCase);
            if (!looksLikeHandle && !looksLikeUrl)
            {
                ValidationError = "YouTube alanı @handle veya tam YouTube URL'si olmalı (örn: @kanaladi veya https://youtube.com/watch?v=...).";
                return false;
            }
        }

        ValidationError = null;
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (!Validate()) return;

        // Mutate the live AppSettings instance so dependents (LabelPrinter, OverlayHost)
        // pick up the new values immediately. AppSettings is a class with public setters.
        _liveSettings.PrinterName          = SelectedPrinter == DefaultPrinterSentinel ? null : SelectedPrinter;
        _liveSettings.LabelWidthMm         = LabelWidthMm;
        _liveSettings.LabelHeightMm        = LabelHeightMm;
        _liveSettings.LabelGapMm           = LabelGapMm;
        _liveSettings.LabelFontFamily      = LabelFontFamily;
        _liveSettings.LabelUserFontSize    = LabelUserFontSize;
        _liveSettings.LabelMessageFontSize = LabelMessageFontSize;
        _liveSettings.OverlayPort          = OverlayPort;
        _liveSettings.ChatTheme            = ChatTheme;

        // Phase 4g — Payment
        _liveSettings.Payment.WhatsAppMessageTemplate = PaymentTemplate;
        _liveSettings.Payment.Iban                    = Iban;
        _liveSettings.Payment.AccountHolder           = AccountHolder;
        _liveSettings.Payment.Papara                  = Papara;

        // Kargo PR A — Shipping. Empty/0/negative → null (feature kapalı).
        _liveSettings.Shipping.FreeShippingThreshold = ParseOptionalDecimal(FreeShippingThresholdText);
        _liveSettings.Shipping.ShippingFee           = ParseOptionalDecimal(ShippingFeeText);

        // PR-E — Kümülatif kargo "kazandın" template.
        _liveSettings.Payment.ShippingWonTemplate = ShippingWonTemplate ?? string.Empty;

        // Phase 5c — YouTube. Empty string → null so the hosted service idles
        // instead of attempting to resolve "".
        var trimmedHandle = YouTubeChannelHandle?.Trim();
        _liveSettings.YouTubeChannelHandle = string.IsNullOrEmpty(trimmedHandle) ? null : trimmedHandle;

        // Phase 5f — Spam filter. The SpamFilter service reads this object on
        // every message via Func<AppSettings>, so changes take effect the
        // moment Save runs — no restart needed.
        _liveSettings.SpamFilter.Enabled            = SpamFilterEnabled;
        _liveSettings.SpamFilter.DropShortMessages  = SpamDropShortMessages;
        _liveSettings.SpamFilter.MinMessageLength   = SpamMinMessageLength;
        _liveSettings.SpamFilter.DropDuplicates     = SpamDropDuplicates;
        _liveSettings.SpamFilter.DropAllCaps        = SpamDropAllCaps;
        _liveSettings.SpamFilter.DropLinks          = SpamDropLinks;
        _liveSettings.SpamFilter.DropProfanity      = SpamDropProfanity;
        _liveSettings.SpamFilter.BlockedWords       = (SpamBlockedWordsText ?? string.Empty)
            .Split(new[] { ',', '\n', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .ToList();

        // Giveaway animation (Task 20)
        _liveSettings.GiveawayAnimation.DefaultId = AnimationPicker.SelectedId;
        _liveSettings.GiveawayAnimation.Volume    = AnimationVolume;
        _liveSettings.GiveawayAnimation.MutedMode = AnimationMuted;

        _store.Save(_liveSettings);

        // Faz 2 (2026-05-15): WhatsApp template'leri server'a push'la (mobile
        // preview için). Fire-and-forget — sonuç dialog akışını engellemesin.
        if (_waTemplateSync is not null)
        {
            _ = _waTemplateSync.PushFromSettingsAsync(_liveSettings.Payment);
        }

        OverlayPortChanged = (OverlayPort != _originalOverlayPort);
        Saved = true;
    }

    [RelayCommand]
    private void TestPrint()
    {
        if (!Validate()) return;

        // Build a temporary AppSettings snapshot using the IN-FORM (unsaved) values.
        var temp = new AppSettings
        {
            PrinterName          = SelectedPrinter == DefaultPrinterSentinel ? null : SelectedPrinter,
            LabelWidthMm         = LabelWidthMm,
            LabelHeightMm        = LabelHeightMm,
            LabelGapMm           = LabelGapMm,
            LabelFontFamily      = LabelFontFamily,
            LabelUserFontSize    = LabelUserFontSize,
            LabelMessageFontSize = LabelMessageFontSize,
            OverlayPort          = OverlayPort,
            ChatTheme            = ChatTheme
        };

        var sample = new Label(
            Id: "test", SessionId: "test", CustomerId: "test",
            Platform: "instagram", Username: "@test",
            MessageText: "Test mesajı",
            Code: null, Price: 100m,
            AddedAt: 0, PrintedAt: null);

        try
        {
            var printer = new LabelPrinter(temp, NullLogger<LabelPrinter>.Instance);
            printer.Print(new[] { sample });
            MessageBox.Show("Test etiketi yazıcıya gönderildi.",
                "Test başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Yazdırma başarısız: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Settings için decimal ↔ string dönüşümü. Türkçe input
    /// (250,50) ve invariant (250.50) ikisini de kabul eder; depolanan değer
    /// invariant format. Empty/whitespace/0/negatif → null (feature off).</summary>
    private static decimal? ParseOptionalDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)) return null;
        return value > 0 ? value : null;
    }

    private static string FormatOptionalDecimal(decimal? value)
        => value is null
            ? string.Empty
            : value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
