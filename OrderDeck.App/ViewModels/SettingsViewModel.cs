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
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Chat.YouTube;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Labeling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Instagram;

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

    // W3-03 (spec 2026-10-13 §4.8, §6.2, sahip kararı Y4) — mod On'da eşik ve ücret lisans geneli ayardır: alanlar
    // onu gösterir, kaydet kargo kısmını sunucuya yazar (LicenseShippingSettingsWriter), yerel AppSettings.Shipping'e
    // dokunmaz. Mod Off'ta (bugün her lisans) bugünkü yerel yazım.

    /// <summary>Kargo alanları lisans geneli ayarı gösteriyor (mod On): Kargo kartında "Lisans geneli · bütün
    /// bilgisayarlar" çipi.</summary>
    [ObservableProperty] private bool _isShippingLicenseWide;

    /// <summary>Lisans geneli kargo yazımının sonucu, satır içi şerit (açılır pencere yok, #508): stale, çevrimdışı,
    /// mode-off, uç yok. "Tamam" kapatır.</summary>
    [ObservableProperty] private string? _shippingNotice;

    /// <summary>Kargo yazımı sunucuda sürüyor: "Kaydet" pasif (aynı değer iki kez gitmesin).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isShippingSaving;

    /// <summary>"Kaydet" basılabilir: kargo yazımı sürmüyor.</summary>
    public bool CanSave => !IsShippingSaving;

    /// <summary><c>decimal(18,2)</c>'nin üst sınırı: sunucunun <c>LicenseShippingSettings.MaxAmount</c>'ı.</summary>
    private const decimal MaxLicenseShippingAmount = 9_999_999_999_999_999.99m;

    private const string LicenseShippingValidationError =
        "Kargo eşiği ve ücreti ya ikisi de boş (kargo kapalı) ya da ikisi de 0'dan büyük olmalı; en çok iki ondalık.";

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

    // Facebook Graph API (2026-06-25). Same UX as YouTube: Connect button
    // pops the browser, status text shows "Bağlı: <Page>" when ready.
    [ObservableProperty] private string _facebookConnectionStatus = "Bağlı değil";
    [ObservableProperty] private bool _isFacebookConnected;
    [ObservableProperty] private bool _isFacebookBusy;
    public bool IsFacebookIdle => !IsFacebookBusy && !IsFacebookConnected && !IsReauthorizePending;

    /// <summary>"Facebook izinlerini güncelle"nin tarayıcı akışı sürüyor: "İptal" görünür. "Facebook'a Bağlan" da
    /// akış bitene dek pasif kalır (aynı anda tek tarayıcı akışı): beklerken "Bağlantıyı Kaldır" basılıp
    /// <see cref="IsFacebookBusy"/> inse de.</summary>
    [ObservableProperty] private bool _isReauthorizePending;

    partial void OnIsReauthorizePendingChanged(bool value) => OnPropertyChanged(nameof(IsFacebookIdle));

    /// <summary>Instagram hesapları yönetilebilir: Facebook bağlı ve o an bir bağlantı işlemi sürmüyor.</summary>
    public bool CanManageInstagram => IsFacebookConnected && !IsFacebookBusy;

    partial void OnIsFacebookBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFacebookIdle));
        OnPropertyChanged(nameof(CanManageInstagram));
    }

    partial void OnIsFacebookConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFacebookIdle));
        OnPropertyChanged(nameof(CanManageInstagram));
    }

    // Instagram resmi Graph API (2026-08-05). Ayrı bir OAuth yok — IG erişimi
    // bağlı Facebook Sayfa'sı üzerinden gidiyor. Bu yüzden burada yalnız durum
    // var, "Bağlan" butonu yok. Kip seçimi de yok: kazıyıcı uzantıdan
    // kaldırıldığı için resmi API tek yol.
    [ObservableProperty] private string _instagramAccountStatus = "Kontrol ediliyor...";

    /// <summary>Bağlı Facebook oturumunda IG izinleri yoksa true — kullanıcı
    /// IG izinleri yapılandırmaya eklenmeden önce bağlanmış demektir ve
    /// yeniden bağlanmadan resmi API sessizce boş döner.</summary>
    [ObservableProperty] private bool _instagramNeedsReconnect;

    /// <summary>Bağlı Sayfalar ve Instagram hesapları, bağlı sırasıyla (birincil önce). Yalnız
    /// <see cref="RefreshInstagramStatusAsync"/> doldurur. Kart satırları iki ve üzeri Sayfada çizer
    /// (<see cref="ShowInstagramPageRows"/>): tek Sayfada durum satırı aynı bilgiyi verir.</summary>
    public ObservableCollection<InstagramPageRow> InstagramPages { get; } = new();

    /// <summary>Hesap satırları görünsün mü: iki ve üzeri Sayfa bağlıyken.</summary>
    public bool ShowInstagramPageRows => InstagramPages.Count >= 2;

    /// <summary>Instagram kartındaki bir işlemin sonucu, kart içi satır (açılır pencere yok); "Tamam" kapatır.</summary>
    [ObservableProperty] private string? _instagramNotice;

    /// <summary>Instagram tazelemelerini sıraya sokar. Kurucu bir tazeleme başlatır, bağlantıyı değiştiren her komut
    /// bir tane daha ister; iç içe koşsalardı bağlantı değişmeden ÖNCE başlamış bir okuma, sonrakinin yazdığı
    /// satırların üstüne eski hâli yazabilirdi.</summary>
    private readonly System.Threading.SemaphoreSlim _instagramRefreshGate = new(1, 1);

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

    /// <summary>Ödeme &amp; Kargo sekmesindeki "WhatsApp Cloud API" bölümü.
    /// Testlerin çoğu bunu vermiyor (sunucuya çıkıyor); null olduğunda bölüm
    /// yalnız görünmez, kayıtlı ayarlar olduğu gibi korunur.</summary>
    public WhatsAppCloudSettingsViewModel? WhatsAppCloud { get; }

    private readonly YouTubeOAuthService? _youTubeOAuth;
    private readonly FacebookOAuthService? _facebookOAuth;
    private readonly Services.Sync.WhatsAppTemplateSyncService? _waTemplateSync;
    private readonly System.Net.Http.IHttpClientFactory? _httpFactory;

    /// <summary>Bir istisnayı sebebini tam göstermeyen sabit bir metne çeviren yollar sebebi buraya yazar (ör. düşen
    /// "Kaldır"). Verilmezse (testler) günlük yazılmaz; uygulamada DI doldurur.</summary>
    private readonly ILogger<SettingsViewModel> _log;

    /// <summary>Facebook sayfa seçim çekmecesi için. Testte verilmiyor —
    /// oradaki senaryolar OAuth bağlama komutunu tetiklemiyor.</summary>
    private readonly Services.Drawers.IDrawerService? _drawers;

    /// <summary>Kargo ayarının kaynağı (§6.2). Verilmezse (eski testler) mod Off sayılır: yerel ayar.</summary>
    private readonly IShippingSettingsProvider? _shipping;

    /// <summary>Mod On'da kargo kısmının yazıcısı (<c>PUT shipping-settings</c>).</summary>
    private readonly Services.Sync.LicenseShippingSettingsWriter? _shippingWriter;

    /// <summary>Kargo alanlarının en son gösterdiği değer ve sürümü (açılışta, stale'de, yazımdan sonra). Kaydet bununla
    /// karşılaştırır — kargo değişmediyse istek gitmez — ve <c>expectedVersion</c> olarak bu sürümü gönderir: arada
    /// başka bilgisayarın yazdığı değer sessizce ezilmesin, <c>stale</c> gelsin.</summary>
    private decimal? _shownThreshold;
    private decimal? _shownFee;
    private int? _shownVersion;

    /// <summary>Kurucunun çağrıldığı bağlam (uygulamada arayüz iş parçacığı). OAuth servisinin kendi iş parçacığından
    /// gelen eksik Sayfa onayı çekmeceyi bunun üzerinden açar; null ise doğrudan.</summary>
    private readonly System.Threading.SynchronizationContext? _uiContext;

    public SettingsViewModel(AppSettings settings, SettingsStore store, ShortcutsTabViewModel shortcutsTab,
        IntakeFormSettingsViewModel intakeForm,
        ShopperAppSettingsViewModel shopperApp,
        YouTubeOAuthService? youTubeOAuth = null,
        FacebookOAuthService? facebookOAuth = null,
        AnimationCatalogClient? catalogClient = null,
        Services.Sync.WhatsAppTemplateSyncService? waTemplateSync = null,
        System.Net.Http.IHttpClientFactory? httpFactory = null,
        Services.Drawers.IDrawerService? drawers = null,
        WhatsAppCloudSettingsViewModel? whatsAppCloud = null,
        IShippingSettingsProvider? shipping = null,
        Services.Sync.LicenseShippingSettingsWriter? shippingWriter = null,
        ILogger<SettingsViewModel>? log = null)
    {
        _uiContext = System.Threading.SynchronizationContext.Current;
        _log = log ?? NullLogger<SettingsViewModel>.Instance;
        _shipping = shipping;
        _shippingWriter = shippingWriter;
        _drawers = drawers;
        WhatsAppCloud = whatsAppCloud;
        _liveSettings = settings;
        _store = store;
        _originalOverlayPort = settings.OverlayPort;
        ShortcutsTab = shortcutsTab;
        IntakeForm = intakeForm;
        ShopperApp = shopperApp;
        _youTubeOAuth = youTubeOAuth;
        _facebookOAuth = facebookOAuth;
        _waTemplateSync = waTemplateSync;
        _httpFactory = httpFactory;

        // Satır sayısı, satırların görünüp görünmeyeceğini belirler: koleksiyon her değiştiğinde bildirilir.
        InstagramPages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowInstagramPageRows));

        LoadFromSettings();
        LoadInstalledPrinters();
        LoadInstalledFonts();
        _ = IntakeForm.LoadAsync();
        _ = ShopperApp.LoadAsync();
        _ = RefreshYouTubeConnectionStatusAsync();
        _ = RefreshFacebookConnectionStatusAsync();
        _ = RefreshInstagramStatusAsync();

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

    /// <summary>
    /// Mirrors <see cref="RefreshYouTubeConnectionStatusAsync"/> for the
    /// Facebook OAuth. Reads the cached page-name from the encrypted token
    /// bundle on disk — no Graph API hit, so it's safe to call on every
    /// dialog open.
    /// </summary>
    public async System.Threading.Tasks.Task RefreshFacebookConnectionStatusAsync()
    {
        if (_facebookOAuth is null)
        {
            FacebookConnectionStatus = "OAuth servisi yapılandırılmamış";
            IsFacebookConnected = false;
            return;
        }

        try
        {
            var name = await _facebookOAuth.GetConnectedPageNameAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(name))
            {
                FacebookConnectionStatus = "Bağlı değil";
                IsFacebookConnected = false;
                return;
            }
            FacebookConnectionStatus = $"Bağlı: {name}";
            IsFacebookConnected = true;
        }
        catch (Exception ex)
        {
            FacebookConnectionStatus = $"Durum okunamadı: {ex.Message}";
            IsFacebookConnected = false;
        }
    }

    /// <summary>
    /// Instagram kartını tazeler: (1) oturumda IG izinleri var mı, (2) bağlı her Sayfaya bir IG profesyonel hesabı
    /// bağlı mı. Graph'a izin için bir, Sayfa başına bir istek gider. Ayarlar açılırken ve bağlantı her değiştiğinde
    /// (bağlan, bağlantıyı kaldır, hesap ekle, hesap kaldır) çalışır; yayın sırasında kendiliğinden çalışmaz.
    ///
    /// <para>Tazelemeler sıraya girer (<see cref="_instagramRefreshGate"/>): kart hep en son istenen tazelemenin
    /// sonucunda kalır. Sonuç yazılmadan hemen önce bağlı Sayfalar yeniden okunur; liste o arada değiştiyse sonuç
    /// atılır ve okuma güncel listeyle yinelenir: süren tazeleme, o sırada değişen bağlantının eski hâlini karta
    /// yazmaz (değişikliği yapan arkadan tazeleme istemese de).</para>
    /// </summary>
    public async System.Threading.Tasks.Task RefreshInstagramStatusAsync()
    {
        await _instagramRefreshGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await ReadInstagramStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            _instagramRefreshGate.Release();
        }
    }

    /// <summary><see cref="RefreshInstagramStatusAsync"/>'in gövdesi; yalnız oradan, sıra alınmışken çağrılır.</summary>
    private async System.Threading.Tasks.Task ReadInstagramStatusAsync()
    {
        if (_facebookOAuth is null)
        {
            ShowInstagramNotConnected();
            return;
        }

        try
        {
            var pages = await _facebookOAuth.GetConnectedPagesAsync().ConfigureAwait(true);
            System.Net.Http.HttpClient? http = null;

            // Her tur: sonda + çözümleme, sonra sonucu yazmadan HEMEN önce bağlı Sayfalar yeniden okunur (bellekten,
            // Graph isteği yok). Sayfa kimliklerinin sırası değiştiyse sonuç atılır ve okuma güncel listeyle baştan
            // yinelenir: süren bir tazeleme, o sırada kaldırılan / eklenen Sayfanın eski hâlini karta yazmaz.
            while (true)
            {
                if (pages.Count == 0)
                {
                    ShowInstagramNotConnected();
                    return;
                }

                http ??= _httpFactory?.CreateClient(InstagramChatHostedService.HttpClientName)
                         ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                // İzin kontrolü kullanıcı token'ı ister (Sayfa token'ıyla /me Sayfa olur). Tek kullanıcı token'ı bağlı
                // bütün Sayfaları kapsar: sonda Sayfa başına değil, tazeleme turu başına BİR kez koşar.
                bool? needsReconnect = null;
                var userToken = await _facebookOAuth.GetUserAccessTokenAsync().ConfigureAwait(true);
                if (!string.IsNullOrEmpty(userToken))
                {
                    var probe = new InstagramPermissionProbe(http, NullLogger<InstagramPermissionProbe>.Instance);
                    // null = bilinmiyor (ağ hatası) → uyarı gösterme, yanlış alarm olmasın.
                    needsReconnect =
                        await probe.HasInstagramPermissionsAsync(userToken!).ConfigureAwait(true) == false;
                }

                // Çözümleyicinin önbelleği tek Sayfalıktır: her Sayfa kendi isteğiyle çözülür.
                var resolver = new InstagramAccountResolver(http, NullLogger<InstagramAccountResolver>.Instance);
                var resolved = new List<ResolvedInstagramPage>(pages.Count);
                foreach (var page in pages)
                {
                    var (account, status) = await resolver.ResolveDetailedAsync(
                        page.PageId, page.PageAccessToken,
                        System.Threading.CancellationToken.None).ConfigureAwait(true);
                    resolved.Add(new ResolvedInstagramPage(page, account, status));
                }

                // Servis her çağrıda yeni liste döner: kimlikler DEĞERLE karşılaştırılır. Kullanıcı token'ı
                // karşılaştırılmaz: bağlantıyı değiştiren her komut kendi tazelemesini ister. Karşılaştırma ile
                // yazım arasında bekleme yoktur.
                var current = await _facebookOAuth.GetConnectedPagesAsync().ConfigureAwait(true);
                if (!SamePageIds(pages, current))
                {
                    pages = current;
                    continue;
                }

                if (needsReconnect is { } value) InstagramNeedsReconnect = value;
                ShowInstagramPages(resolved);
                return;
            }
        }
        catch (Exception ex)
        {
            InstagramAccountStatus = $"Durum okunamadı: {ex.Message}";
            InstagramNeedsReconnect = false;
        }
    }

    /// <summary>İki listedeki Sayfa kimlikleri aynı sırada ve aynı mı (sıralı, <see cref="StringComparison.Ordinal"/>).</summary>
    private static bool SamePageIds(IReadOnlyList<FacebookLinkedPage> a, IReadOnlyList<FacebookLinkedPage> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].PageId, b[i].PageId, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Bağlı bir Sayfa ve hesap çözümlemesinin sonucu; <c>Account</c> yalnız
    /// <see cref="InstagramResolveStatus.Found"/>'da doludur.</summary>
    private readonly record struct ResolvedInstagramPage(
        FacebookLinkedPage Page, InstagramAccount? Account, InstagramResolveStatus Status);

    /// <summary>Facebook bağlı değil (ya da OAuth servisi yok): satır yok, izin uyarısı yok.</summary>
    private void ShowInstagramNotConnected()
    {
        InstagramPages.Clear();
        InstagramAccountStatus = InstagramSettingsTexts.NotConnected;
        InstagramNeedsReconnect = false;
    }

    /// <summary>
    /// Çözümleme sonuçlarını karta yazar: hesap satırları ve durum satırı. İlk Sayfa birincildir. İçinde bekleme
    /// yoktur: satırlar yerel listede kurulur ve koleksiyona tek seferde yerleşir, yarım ya da çift liste görünmez.
    ///
    /// <para>Tek Sayfada durum satırı çoklu hesaptan önceki tam metinlerdir. İki ve üzeri Sayfada durum satırı
    /// hesabı ÇÖZÜLEN Sayfaları sayar ve hesapsız Sayfanın satırı uyarı rengindedir (birincil dahil): iki Sayfa
    /// bağlayan ikisinden de yorum bekler. Çağrısı düşen Sayfanın satırı iki durumda da nötrdür: hesap var mı yok mu
    /// bilinmiyor.</para>
    /// </summary>
    private void ShowInstagramPages(IReadOnlyList<ResolvedInstagramPage> resolved)
    {
        var multiplePages = resolved.Count >= 2;
        var rows = new List<InstagramPageRow>(resolved.Count);
        for (var i = 0; i < resolved.Count; i++)
        {
            var (page, account, status) = resolved[i];
            var text = status switch
            {
                InstagramResolveStatus.Found => InstagramSettingsTexts.AccountLabel(
                    account!.Value.IgUserId, account.Value.Username),
                InstagramResolveStatus.NotLinked => InstagramSettingsTexts.RowNotLinked,
                _ => InstagramSettingsTexts.RowUnreadable,
            };
            rows.Add(new InstagramPageRow(
                page.PageId, page.PageName, text,
                IsPrimary: i == 0,
                CanRemove: i > 0,
                IsWarning: multiplePages && status == InstagramResolveStatus.NotLinked));
        }

        InstagramPages.Clear();
        foreach (var row in rows) InstagramPages.Add(row);

        InstagramAccountStatus = multiplePages
            ? InstagramSettingsTexts.ConnectedAccountCount(
                resolved.Count(r => r.Status == InstagramResolveStatus.Found))
            : resolved[0].Status switch
            {
                InstagramResolveStatus.Found => InstagramSettingsTexts.SinglePageConnected(rows[0].InstagramText),
                InstagramResolveStatus.NotLinked => InstagramSettingsTexts.SinglePageNotLinked,
                _ => InstagramSettingsTexts.SinglePageUnreadable,
            };
    }

    /// <summary>
    /// "Kaldır": bir EK Sayfanın bağlantısını kaldırır. Yazımın ardından satır HEMEN kalkar (tazelemenin Graph
    /// istekleri beklenmez) ve sonuç satırı hesabı adıyla söyler; sonra kart tazelenir. Yazım düşerse bağlantı
    /// değişmemiştir: satır kalır, sonuç satırı yeniden denemeyi söyler, sebep günlüğe Warning olarak düşer. Birincil satırda hiçbir şey yapmaz: birincil
    /// ancak "Bağlantıyı Kaldır" ile, bütün bağlantıyla birlikte kalkar.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task RemoveInstagramAccount(InstagramPageRow? row)
    {
        if (_facebookOAuth is null || row is not { CanRemove: true }) return;

        try
        {
            // Dönüş değeri akışı değiştirmez: false, Sayfa zaten bağlı değil demektir; satır yine kalkmalı.
            await _facebookOAuth.RemoveExtraPageAsync(row.PageId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Sabit metin sebebi söylemez: sebep (dosya / şifreleme hatası; bu yolda Graph çağrısı yok) günlüğe düşer.
            // Satır yalnız Sayfa kimliğini taşır.
            _log.LogWarning(ex, "Instagram account removal failed for Page {PageId}", row.PageId);
            InstagramNotice = InstagramSettingsTexts.RemoveFailed;
            return;
        }

        InstagramPages.Remove(row);
        // Hesabı çözülememiş satırın metni bir ad değil, durumdur ("@" ile başlamaz): sonuç Sayfayı adıyla söyler.
        InstagramNotice = InstagramSettingsTexts.AccountRemoved(
            row.InstagramText.StartsWith('@') ? row.InstagramText : row.PageName);
        await RefreshInstagramStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissInstagramNotice() => InstagramNotice = null;

    /// <summary>"Instagram hesabı ekle" çekmecesi açıkken onun modeli; kapalıyken null. Testler akışı bunun üzerinden
    /// sürer.</summary>
    public InstagramAccountPickerViewModel? ActiveInstagramPicker { get; private set; }

    /// <summary>
    /// "Instagram hesabı ekle" (§8.6.1): saklı Facebook girişiyle, tarayıcı açmadan ve sunucu takası olmadan (TK5)
    /// Sayfaları listeler, çekmecede seçileni ek hesap olarak bağlar ve kartı tazeler. İzin eksikse ya da oturum
    /// yenilenmeliyse listeleme yapmadan "Facebook izinlerini güncelle"ye yönlendirir. Sonuç kart içi satıra yazılır.
    /// Akış arayüz iş parçacığında kalır.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task AddInstagramAccount()
    {
        if (_facebookOAuth is null) return;

        // 0. adım: değer son tazelemede hesaplandı; yeni Graph isteği yok. İzin eksikken Graph Instagram alanını hata
        // vermeden düşürür: listeleme bütün Sayfaları hesapsız gösterir ve kullanıcıyı yanlış yere yollardı.
        if (InstagramNeedsReconnect)
        {
            InstagramNotice = InstagramSettingsTexts.SessionNeedsRenewal;
            return;
        }

        var previousStatus = InstagramAccountStatus;
        InstagramAccountStatus = InstagramSettingsTexts.ReadingPages;
        FacebookPageListing listing;
        try
        {
            listing = await _facebookOAuth.ListAddablePagesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Sabit metin sebebi söylemez: sebep (Graph kodu, HTTP durumu, ağ hatası) günlüğe düşer.
            _log.LogWarning(ex, "Instagram account listing failed");
            RestoreInstagramStatus(previousStatus);
            InstagramNotice = InstagramSettingsTexts.PagesUnreadable;
            return;
        }
        RestoreInstagramStatus(previousStatus);

        if (listing.NeedsReauthorize)
        {
            InstagramNotice = InstagramSettingsTexts.SessionNeedsRenewal;
            return;
        }

        // Instagram'sız aday tek başına çekmece açtırmaz: seçilebilecek bir şey yoktur.
        var picker = new InstagramAccountPickerViewModel(listing.Pages);
        if (!picker.Options.Any(option => option.CanSelect))
        {
            InstagramNotice = InstagramSettingsTexts.NoAddableAccount;
            return;
        }

        if (_drawers is null) return;
        bool confirmed;
        ActiveInstagramPicker = picker;
        try
        {
            confirmed = await _drawers.ShowAsync(InstagramSettingsTexts.AddAccountButton,
                d => Views.Drawers.InstagramAccountAddDrawer.Create(d, picker)).ConfigureAwait(true);
        }
        finally
        {
            ActiveInstagramPicker = null;
        }
        if (!confirmed || picker.Selected is not { CanSelect: true } chosen) return;

        var page = listing.Pages.First(p => string.Equals(p.PageId, chosen.PageId, StringComparison.Ordinal));
        try
        {
            await _facebookOAuth.AddExtraPageAsync(chosen.PageId, chosen.PageName).ConfigureAwait(true);
        }
        catch (FacebookPageLimitException)
        {
            // Metin sebebi tam söyler: günlük yok.
            InstagramNotice = InstagramSettingsTexts.PageLimitReached(FacebookOAuthService.MaxInstagramPages);
            return;
        }
        catch (Exception ex)
        {
            // Satır yalnız Sayfa kimliğini taşır; sebep istisnadadır.
            _log.LogWarning(ex, "Instagram account add failed for Page {PageId}", chosen.PageId);
            InstagramNotice = IsTransientAddFailure(ex)
                ? InstagramSettingsTexts.PagesUnreadable
                : InstagramSettingsTexts.AuthorizationFailed(chosen.PageName);
            return;
        }

        InstagramNotice = InstagramSettingsTexts.AccountAdded(
            InstagramSettingsTexts.AccountLabel(page.IgUserId!, page.IgUsername));
        await RefreshInstagramStatusAsync().ConfigureAwait(true);
    }

    /// <summary>Durum satırı hâlâ "Sayfalar okunuyor..." ise önceki metni geri koyar; o arada bir tazeleme yeni metin
    /// yazdıysa ona dokunmaz.</summary>
    private void RestoreInstagramStatus(string previous)
    {
        if (InstagramAccountStatus == InstagramSettingsTexts.ReadingPages) InstagramAccountStatus = previous;
    }

    /// <summary>
    /// Eklemenin geçici hatası (yeniden denemek çözebilir): Graph'ın 5xx'i ya da kota kodu (<c>InstagramGraphError.Classify</c>'ın
    /// kota kümesi: kod 4 / 17 / 32 / 613 / 80002, alt kod 2446079), Graph dışı ağ hatası, zaman aşımı. Geri kalan her
    /// hata (token türetilemedi, yetki, izin) Sayfa yetkisi sorunudur.
    /// </summary>
    private static bool IsTransientAddFailure(Exception ex) => ex switch
    {
        FacebookGraphException graph => (int?)graph.StatusCode >= 500
            || graph.Code is 4 or 17 or 32 or 613 or 80002
            || graph.Subcode == 2446079,
        System.Net.Http.HttpRequestException => true,
        System.Threading.Tasks.TaskCanceledException => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>Eksik Sayfa onay çekmecesi açıkken sorunun metni; kapalıyken null. Testler soruyu bunun üzerinden
    /// okur.</summary>
    public string? ActiveReauthorizeQuestion { get; private set; }

    /// <summary>Süren "Facebook izinlerini güncelle" akışının iptal kaynağı; akış yokken null. "İptal" bunu tetikler.</summary>
    private System.Threading.CancellationTokenSource? _reauthorizeCts;

    /// <summary>
    /// "Facebook izinlerini güncelle" (§8.6.1): bağlantıyı kaldırmadan tarayıcı onayıyla izinleri ve token'ları
    /// yeniler; ek hesaplar korunur, birincil değişmez. Yeni girişte görünmeyen bağlı Sayfa varsa kaydetmeden önce
    /// çekmecede onay sorulur. Beklerken durum satırı ne yapılacağını söyler, kartın düğmeleri ve "Facebook'a Bağlan"
    /// pasiftir, "İptal" görünür. Sonuç kart içi satıra yazılır; başarıdan sonra kart tazelenir (izin uyarısı ve
    /// eklemenin 0. adımı kalkar). Hiçbir hata yolunda diskteki bağlantı değişmez.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ReauthorizeFacebook()
    {
        if (_facebookOAuth is null) return;

        var previousStatus = InstagramAccountStatus;
        var cts = new System.Threading.CancellationTokenSource();
        _reauthorizeCts = cts;
        var declined = false;
        IsFacebookBusy = true;
        IsReauthorizePending = true;
        InstagramAccountStatus = InstagramSettingsTexts.WaitingForBrowser;
        try
        {
            // Birincil, akış başlarken bağlı listenin ilk girdisidir: eksiklerden biri oysa soruya Facebook yorumları
            // da girer.
            var primaryId = (await _facebookOAuth.GetConnectedPagesAsync().ConfigureAwait(true))
                .FirstOrDefault()?.PageId;
            var result = await _facebookOAuth.ReauthorizeAsync(async (missing, _) =>
            {
                var confirmed = await ConfirmMissingPagesAsync(missing, primaryId).ConfigureAwait(false);
                if (!confirmed) declined = true;
                return confirmed;
            }, cts.Token).ConfigureAwait(true);

            InstagramNotice = InstagramSettingsTexts.PermissionsUpdated(
                result.RefreshedPages, result.MissingPages.Select(page => page.PageName).ToList());
            await RefreshInstagramStatusAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested || declined)
        {
            // Yalnız kullanıcının iptali: "İptal" ya da onayda "Vazgeç". Belirteç iptal edilmemişken gelen
            // TaskCanceledException (HttpClient zaman aşımı) aşağıdaki genel dala düşer.
            InstagramNotice = InstagramSettingsTexts.ReauthorizeCanceled;
        }
        catch (TimeoutException)
        {
            InstagramNotice = InstagramSettingsTexts.ReauthorizeTimedOut;
        }
        catch (FacebookGraphException ex)
        {
            // Ham Graph gövdesi operatöre yazılmaz; sebep günlüğe düşer.
            _log.LogWarning(ex, "Facebook permission update failed");
            InstagramNotice = InstagramSettingsTexts.ReauthorizeGraphFailed(ex.Code, (int?)ex.StatusCode);
        }
        catch (Exception ex)
        {
            // Facebook ekranındaki "İptal" (access_denied), bekleyen başka bir onay, beklerken değişen bağlantı ve ağ
            // hataları buraya düşer.
            _log.LogWarning(ex, "Facebook permission update failed");
            InstagramNotice = InstagramSettingsTexts.ReauthorizeFailed(ex.Message.TrimEnd('.', ' '));
        }
        finally
        {
            IsFacebookBusy = false;
            IsReauthorizePending = false;
            if (ReferenceEquals(_reauthorizeCts, cts)) _reauthorizeCts = null;
            cts.Dispose();
            // O arada bir tazeleme yeni metin yazdıysa ona dokunulmaz.
            if (InstagramAccountStatus == InstagramSettingsTexts.WaitingForBrowser) InstagramAccountStatus = previousStatus;
        }
    }

    /// <summary>"İptal": süren izin güncellemesinin beklemesini keser; akış yoksa etkisizdir.</summary>
    [RelayCommand]
    private void CancelReauthorize()
    {
        try
        {
            _reauthorizeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Akış tam o anda bitti: iptal edilecek bir şey kalmadı.
        }
    }

    /// <summary>
    /// Eksik Sayfa onayı: çekmeceyi kurucunun yakaladığı bağlamda açar (OAuth servisi bunu kendi iş parçacığından
    /// çağırır) ve yanıtı döner. Çekmece servisi yoksa onay verilmemiş sayılır.
    /// </summary>
    private System.Threading.Tasks.Task<bool> ConfirmMissingPagesAsync(
        IReadOnlyList<FacebookLinkedPage> missing, string? primaryId)
    {
        if (_drawers is not { } drawers) return System.Threading.Tasks.Task.FromResult(false);

        var question = InstagramSettingsTexts.MissingPagesQuestion(
            missing.Select(page => page.PageName).ToList(),
            includesPrimary: missing.Any(page => string.Equals(page.PageId, primaryId, StringComparison.Ordinal)));
        if (_uiContext is null) return AskMissingPagesAsync(drawers, question);

        var answer = new System.Threading.Tasks.TaskCompletionSource<System.Threading.Tasks.Task<bool>>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ => answer.SetResult(AskMissingPagesAsync(drawers, question)), null);
        return answer.Task.Unwrap();
    }

    private async System.Threading.Tasks.Task<bool> AskMissingPagesAsync(
        Services.Drawers.IDrawerService drawers, string question)
    {
        ActiveReauthorizeQuestion = question;
        try
        {
            return await drawers.ShowAsync(InstagramSettingsTexts.ReauthorizeButton,
                d => Views.Drawers.MessageDrawer.ForConfirm(d, question)).ConfigureAwait(true);
        }
        finally
        {
            ActiveReauthorizeQuestion = null;
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ConnectFacebook()
    {
        if (_facebookOAuth is null) return;
        try
        {
            IsFacebookBusy = true;
            FacebookConnectionStatus = "Tarayıcıdan onay bekleniyor...";

            // Page picker callback: only invoked when the operator manages >1
            // Page. OAuth dinleyicisinin thread'inden gelebiliyor, o yüzden
            // Dispatcher üzerinden UI'ya geçiyoruz. Faz 3: modal pencere
            // yerine çekmece — InvokeAsync artık Task döndüren bir lambda
            // aldığı için .Task.Unwrap() ile çekmecenin KAPANMASI bekleniyor;
            // düz await sadece lambda'nın BAŞLAMASINI beklerdi.
            await _facebookOAuth.ConnectAsync(async (candidates, ct) =>
            {
                FacebookPageCandidate? chosen = null;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (_drawers is null) return;
                    Views.Drawers.FacebookPagePickerDrawer? view = null;
                    var ok = await _drawers.ShowAsync("Facebook Sayfası Seç",
                        d => view = Views.Drawers.FacebookPagePickerDrawer.Create(d, candidates));
                    if (ok) chosen = view!.Selected;
                }).Task.Unwrap().ConfigureAwait(true);
                return chosen;
            }).ConfigureAwait(true);

            await RefreshFacebookConnectionStatusAsync().ConfigureAwait(true);
            await RefreshInstagramStatusAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            FacebookConnectionStatus = "Bağlantı iptal edildi.";
            IsFacebookConnected = false;
        }
        catch (Exception ex)
        {
            FacebookConnectionStatus = $"Bağlantı başarısız: {ex.Message}";
            IsFacebookConnected = false;
        }
        finally
        {
            IsFacebookBusy = false;
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DisconnectFacebook()
    {
        if (_facebookOAuth is null) return;
        try
        {
            IsFacebookBusy = true;
            await _facebookOAuth.DisconnectAsync().ConfigureAwait(true);
            await RefreshFacebookConnectionStatusAsync().ConfigureAwait(true);
            await RefreshInstagramStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsFacebookBusy = false;
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
        // operator boş bırakmayı = feature off olarak görür. W3-03: kaynak
        // sağlayıcı — mod On'da lisans geneli ayar, mod Off'ta yerel ayar.
        ShowShipping(CurrentShipping());

        // PR-E — Kümülatif kargo "kazandın" template.
        ShippingWonTemplate = _liveSettings.Payment.ShippingWonTemplate;

        // WhatsApp Cloud API — şablon seçimi + yuva eşlemesi.
        WhatsAppCloud?.LoadFrom(_liveSettings.Payment);

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

    private bool Validate() => Validate(licenseWideShipping: false);

    /// <param name="licenseWideShipping">Mod On: kargo alanları sunucunun kuralıyla da doğrulanır (§4.8).</param>
    private bool Validate(bool licenseWideShipping)
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
        // or a watch URL will silently make the ingestor idle forever.
        // Extract logic mirrors YouTubeVideoIdExtractor.TryExtract.
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

        // WhatsApp Cloud API — yarım kalmış yuva eşlemesi kaydedilemez: Meta'ya
        // yanlış sayıda parametre gitmesi demek ve şablon faturalı.
        if (WhatsAppCloud?.ValidationError() is { } waError)
        {
            ValidationError = waError;
            return false;
        }

        // Mod On: sunucunun kuralı (§4.8, LicenseShippingSettings.IsValid) — ya ikisi boş ya ikisi > 0, en çok iki
        // ondalık. Mod Off'ta bugünkü gevşek okuma (geçersiz → boş) değişmez.
        if (licenseWideShipping && !TryParseLicenseShipping(out _, out _))
        {
            ValidationError = LicenseShippingValidationError;
            return false;
        }

        ValidationError = null;
        return true;
    }

    /// <summary>
    /// Kaydet. Kargo dışı alanlar her kipte bugünkü gibi yerel (canlı örnek + diskte atomik Update). Kargo kısmı mod
    /// Off'ta yerel ayara, mod On'da lisans geneli ayara gider (W3-03, §6.2): istemcide doğrulanır, arayüz iş parçacığı
    /// dışında <c>PUT shipping-settings</c> yapılır ve yerel <c>AppSettings.Shipping</c> hiç yazılmaz. Kargo değişmediyse
    /// istek gitmez (kargo dışı bir ayarı kaydetmek bağlantı istemez). Yazılamazsa sayfa açık kalır ve sebep satır içi
    /// şeritte görünür (<see cref="ShippingNotice"/>); <see cref="Saved"/> yalnız her şey yazılınca true.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task SaveAsync()
    {
        Saved = false;

        // Kaynak kaydet anında yeniden okunur: sayfa açıkken kip değişmiş olabilir (disable turu sunucu değerini yerel
        // ayara kopyaladı → artık yerel yazım).
        var shipping = CurrentShipping();
        var licenseWide = shipping.LicenseWide;
        if (!Validate(licenseWide)) return;
        ShippingNotice = null;

        // Mod Off'ta kargo yalnız operatör değiştirdiyse yerel ayara yazılır (§6.2/§8.5): form mod On'da açılıp disable'dan
        // sonra kaydedilirse değişmemiş alanlar, Off turunun yerel ayara kopyaladığı güncel lisans geneli değeri bayat
        // değerle ezerdi. Saf mod Off'ta sonuç aynı: değişmemiş alan zaten yerel değerdir.
        var writeLocalShipping = !licenseWide
            && (ParseOptionalDecimal(FreeShippingThresholdText) != _shownThreshold
                || ParseOptionalDecimal(ShippingFeeText) != _shownFee);

        // Mutate the live AppSettings instance so dependents (LabelPrinter, OverlayHost)
        // pick up the new values immediately. AppSettings is a class with public setters.
        ApplyFormTo(_liveSettings, includeShipping: writeLocalShipping);

        // N04: diske bütün-nesne Save yerine atomik Update — canlı örnekteki
        // sync imleci gibi alanlar açılıştan beri bayatlamış olabilir; yalnız
        // bu ekranın sahip olduğu alanlar diskteki EN GÜNCEL kopyaya yazılır.
        _store.Update(target => ApplyFormTo(target, includeShipping: writeLocalShipping));

        // Faz 2 (2026-05-15): WhatsApp template'leri server'a push'la (mobile
        // preview için). Fire-and-forget — sonuç dialog akışını engellemesin.
        if (_waTemplateSync is not null)
        {
            _ = _waTemplateSync.PushFromSettingsAsync(_liveSettings.Payment);
        }

        OverlayPortChanged = (OverlayPort != _originalOverlayPort);

        if (licenseWide && !await SaveLicenseShippingAsync().ConfigureAwait(true)) return;
        Saved = true;
    }

    [RelayCommand]
    private void DismissShippingNotice() => ShippingNotice = null;

    /// <summary>
    /// Mod On'da kargo kısmını lisans geneli ayara yazar (§4.8). Başarıda ve <c>stale</c>'de alanlar sunucunun satırını
    /// gösterir (önbellek yazıcıda güncellendi); öbür sonuçlarda önbellek değerine döner — form hiçbir zaman yürürlükte
    /// olmayan bir değeri kaydedilmiş gibi göstermez.
    /// </summary>
    /// <returns>Kargo yazıldı ya da değişmemişti.</returns>
    private async System.Threading.Tasks.Task<bool> SaveLicenseShippingAsync()
    {
        TryParseLicenseShipping(out var threshold, out var fee);   // Validate'te geçti
        if (threshold == _shownThreshold && fee == _shownFee) return true;

        if (_shippingWriter is null)
        {
            ShowShipping(CurrentShipping());
            ShippingNotice = Services.Sync.SalesCommandMessages.NotReady;
            return false;
        }

        Services.Sync.ShippingSettingsSaveOutcome outcome;
        IsShippingSaving = true;
        try
        {
            // Beklenen sürüm formun GÖSTERDİĞİ değerin sürümü. Form mod Off'ta yerel değerle açıldıysa (sürümü yok) ve sayfa
            // açıkken kip 1 olduysa 0 gider: sunucuda satır varsa stale döner, alanlar lisans geneli değerle yenilenir.
            // Önbelleğin sürümü gitseydi formun hiç göstermediği lisans geneli değer sessizce ezilirdi.
            outcome = await _shippingWriter
                .SaveAsync(_shownVersion ?? 0, threshold, fee)
                .ConfigureAwait(true);
        }
        finally
        {
            IsShippingSaving = false;
        }

        if (outcome.Current is { } row
            && outcome.Status is Services.Sync.ShippingSettingsSaveStatus.Saved or Services.Sync.ShippingSettingsSaveStatus.Stale)
        {
            ShowShipping(new ShippingSnapshot(
                new ShippingSettings { FreeShippingThreshold = row.FreeShippingThreshold, ShippingFee = row.Fee },
                row.Version, LicenseWide: true));
        }
        else
        {
            ShowShipping(CurrentShipping());
        }
        ShippingNotice = outcome.Message;
        return outcome.Status == Services.Sync.ShippingSettingsSaveStatus.Saved;
    }

    /// <summary>Kargo ayarının şu anki kaynağı; sağlayıcı yoksa (eski testler) yerel ayar.</summary>
    private ShippingSnapshot CurrentShipping() =>
        _shipping?.Current() ?? new ShippingSnapshot(_liveSettings.Shipping, Version: null, LicenseWide: false);

    /// <summary>Kargo alanlarını, çipi ve karşılaştırma değerini verilen anlık görüntüyle doldurur.</summary>
    private void ShowShipping(ShippingSnapshot snapshot)
    {
        _shownThreshold = snapshot.Settings.FreeShippingThreshold;
        _shownFee = snapshot.Settings.ShippingFee;
        _shownVersion = snapshot.Version;
        IsShippingLicenseWide = snapshot.LicenseWide;
        FreeShippingThresholdText = FormatOptionalDecimal(_shownThreshold);
        ShippingFeeText = FormatOptionalDecimal(_shownFee);
    }

    /// <summary>
    /// Mod On'un kargo okuması (§4.8): boş → null; "0" → null (kartın ipucu: "0 yaz = kargo kapalı"); öbür değer
    /// sayı, &gt; 0, en çok iki ondalık ve <c>decimal(18,2)</c>'ye sığmalı. İkisi birlikte boş ya da birlikte dolu.
    /// Türkçe virgül ve nokta kabul edilir (<see cref="ParseOptionalDecimal"/> ile aynı).
    /// </summary>
    private bool TryParseLicenseShipping(out decimal? threshold, out decimal? fee)
    {
        var thresholdOk = TryParseLicenseAmount(FreeShippingThresholdText, out threshold);
        var feeOk = TryParseLicenseAmount(ShippingFeeText, out fee);
        return thresholdOk && feeOk && threshold.HasValue == fee.HasValue;
    }

    private static bool TryParseLicenseAmount(string? raw, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var normalized = raw.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)) return false;
        if (parsed == 0m) return true;
        if (parsed < 0m || parsed > MaxLicenseShippingAmount || decimal.Round(parsed, 2) != parsed) return false;
        value = parsed;
        return true;
    }

    /// <summary>Formdaki değerleri hedef nesneye yazar. İki kez çağrılır: canlı
    /// (DI) örnek anında etki için, diskten taze yüklenen kopya kalıcılık için
    /// (bkz. <c>Save</c>, N04). Bu yüzden idempotent olmalı — yalnız atama.</summary>
    /// <param name="includeShipping">false (mod On): kargo eşiği ve ücreti lisans geneli ayardır, yerel ayar
    /// yazılmaz (§6.2).</param>
    private void ApplyFormTo(AppSettings target, bool includeShipping)
    {
        target.PrinterName          = SelectedPrinter == DefaultPrinterSentinel ? null : SelectedPrinter;
        target.LabelWidthMm         = LabelWidthMm;
        target.LabelHeightMm        = LabelHeightMm;
        target.LabelGapMm           = LabelGapMm;
        target.LabelFontFamily      = LabelFontFamily;
        target.LabelUserFontSize    = LabelUserFontSize;
        target.LabelMessageFontSize = LabelMessageFontSize;
        target.OverlayPort          = OverlayPort;
        target.ChatTheme            = ChatTheme;

        // Phase 4g — Payment
        target.Payment.WhatsAppMessageTemplate = PaymentTemplate;
        target.Payment.Iban                    = Iban;
        target.Payment.AccountHolder           = AccountHolder;
        target.Payment.Papara                  = Papara;

        // Kargo PR A — Shipping. Empty/0/negative → null (feature kapalı).
        if (includeShipping)
        {
            target.Shipping.FreeShippingThreshold = ParseOptionalDecimal(FreeShippingThresholdText);
            target.Shipping.ShippingFee           = ParseOptionalDecimal(ShippingFeeText);
        }

        // PR-E — Kümülatif kargo "kazandın" template.
        target.Payment.ShippingWonTemplate = ShippingWonTemplate ?? string.Empty;

        // WhatsApp Cloud API — şablon seçimi + yuva eşlemesi.
        WhatsAppCloud?.CommitTo(target.Payment);

        // Phase 5c — YouTube. Empty string → null so the hosted service idles
        // instead of attempting to resolve "".
        var trimmedHandle = YouTubeChannelHandle?.Trim();
        target.YouTubeChannelHandle = string.IsNullOrEmpty(trimmedHandle) ? null : trimmedHandle;

        // Phase 5f — Spam filter. The SpamFilter service reads this object on
        // every message via Func<AppSettings>, so changes take effect the
        // moment Save runs — no restart needed.
        target.SpamFilter.Enabled            = SpamFilterEnabled;
        target.SpamFilter.DropShortMessages  = SpamDropShortMessages;
        target.SpamFilter.MinMessageLength   = SpamMinMessageLength;
        target.SpamFilter.DropDuplicates     = SpamDropDuplicates;
        target.SpamFilter.DropAllCaps        = SpamDropAllCaps;
        target.SpamFilter.DropLinks          = SpamDropLinks;
        target.SpamFilter.DropProfanity      = SpamDropProfanity;
        target.SpamFilter.BlockedWords       = (SpamBlockedWordsText ?? string.Empty)
            .Split(new[] { ',', '\n', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 0)
            .ToList();

        // Giveaway animation (Task 20)
        target.GiveawayAnimation.DefaultId = AnimationPicker.SelectedId;
        target.GiveawayAnimation.Volume    = AnimationVolume;
        target.GiveawayAnimation.MutedMode = AnimationMuted;
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
