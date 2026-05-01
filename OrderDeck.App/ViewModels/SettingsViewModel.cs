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
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Labeling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    // Phase 5c — YouTube Live chat scraper
    [ObservableProperty] private string _youTubeChannelHandle = "";

    [ObservableProperty] private string? _validationError;

    /// <summary>True iff Save was called and OverlayPort changed (caller checks for restart prompt).</summary>
    public bool OverlayPortChanged { get; private set; }

    /// <summary>True iff Save committed changes; dialog uses to set DialogResult.</summary>
    public bool Saved { get; private set; }

    public ShortcutsTabViewModel ShortcutsTab { get; }
    public IntakeFormSettingsViewModel IntakeForm { get; }

    public SettingsViewModel(AppSettings settings, SettingsStore store, ShortcutsTabViewModel shortcutsTab,
        IntakeFormSettingsViewModel intakeForm)
    {
        _liveSettings = settings;
        _store = store;
        _originalOverlayPort = settings.OverlayPort;
        ShortcutsTab = shortcutsTab;
        IntakeForm = intakeForm;

        LoadFromSettings();
        LoadInstalledPrinters();
        LoadInstalledFonts();
        _ = IntakeForm.LoadAsync();
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

        // Phase 5c — YouTube
        YouTubeChannelHandle = _liveSettings.YouTubeChannelHandle ?? string.Empty;
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

        // Phase 5c — YouTube. Empty string → null so the hosted service idles
        // instead of attempting to resolve "".
        var trimmedHandle = YouTubeChannelHandle?.Trim();
        _liveSettings.YouTubeChannelHandle = string.IsNullOrEmpty(trimmedHandle) ? null : trimmedHandle;

        _store.Save(_liveSettings);

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
}
