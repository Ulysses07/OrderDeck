namespace OrderDeck.Core.Settings;

public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";

    // Printing
    public string? PrinterName { get; set; }
    public int LabelWidthMm  { get; set; } = 60;
    public int LabelHeightMm { get; set; } = 30;
    public int LabelGapMm    { get; set; } = 5;
    public string LabelFontFamily { get; set; } = "Arial";
    public int   LabelUserFontSize  { get; set; } = 14;
    public int   LabelMessageFontSize { get; set; } = 12;

    // Shortcuts (Phase 3b-1)
    public bool UseCustomShortcuts { get; set; } = false;

    /// <summary>Custom kısayol profili: command id → chord string. Null = henüz custom yok.</summary>
    public System.Collections.Generic.Dictionary<string, string>? CustomShortcuts { get; set; }

    /// <summary>Phase 4f: last intake form submission cursor (max SubmittedAt synced).</summary>
    public DateTimeOffset? LastIntakeFormSync { get; set; }

    /// <summary>Payment sync (PR B): server'dan UpdatedAt cursor — bu tarihten
    /// sonra güncellenen mobile onay/red sonuçlarını çekiyor. İlk run'da null.</summary>
    public DateTimeOffset? LastPaymentReverseSync { get; set; }

    /// <summary>Shipment sync (PR-D, 2026-05-13): kümülatif kargo reverse-sync
    /// cursor. WPF authoritative olduğu için pull nadiren çalışır, ama
    /// cursor advance edilir.</summary>
    public DateTimeOffset? LastShipmentReverseSync { get; set; }

    /// <summary>Phase 4g: WhatsApp ödeme isteme yapılandırması.</summary>
    public PaymentSettings Payment { get; set; } = new();

    /// <summary>Kargo eşik + ücreti — PR A (2026-05-11). Default null/null
    /// → feature kapalı, davranış değişmez. PR B'de Label.IsShippingFee
    /// + otomatik label; PR C'de dekont eşleştirme modal'ı tüketecek.</summary>
    public ShippingSettings Shipping { get; set; } = new();

    /// <summary>Phase 5c: YouTube Live chat scraper. Empty/null disables the scraper.
    /// Accepted values: "@handle", "handle", or any URL containing @handle. The
    /// hosted service resolves the handle to the active live video each time the
    /// user goes live; offline state is detected and the service idles.</summary>
    public string? YouTubeChannelHandle { get; set; }

    /// <summary>Phase 5d: YouTube OAuth 2.0 Client ID (Desktop application
    /// type). Bundled with the installer in production; for development the
    /// operator drops the value into settings.json by hand. Used by
    /// <c>YouTubeOAuthService</c> to start the consent flow when the user
    /// clicks "Connect YouTube" in Settings. Null/empty = moderation features
    /// disabled (read-only InnerTube scraper still works).</summary>
    public string? YouTubeOAuthClientId { get; set; }

    /// <summary>OAuth 2.0 Client Secret paired with <see cref="YouTubeOAuthClientId"/>.
    /// Stored as plain text per Google's desktop-app guidance: a desktop
    /// secret is not actually secret because it ships in every binary anyway,
    /// so encryption only adds friction without raising the bar for an
    /// attacker with file-system access.</summary>
    public string? YouTubeOAuthClientSecret { get; set; }

    /// <summary>Spam/troll filter rules applied to inbound chat messages
    /// before they reach the bus. Disabled rules pass everything through.</summary>
    public OrderDeck.Core.Chat.SpamFilterSettings SpamFilter { get; set; } = new();

    /// <summary>Giveaway animation settings (wheel plugin, volume, mute).</summary>
    public GiveawayAnimationSettings GiveawayAnimation { get; set; } = new();

    /// <summary>True when the operator has completed the first-run setup
    /// wizard (license activation, YouTube handle, printer, Chrome extension
    /// install). Default false → wizard runs once on first launch after
    /// install. Persisted in settings.json so a clean app restart doesn't
    /// re-prompt. Pre-installer existing users will see the wizard once on
    /// their next update — acceptable since they can skip every optional
    /// step in seconds.</summary>
    public bool HasCompletedFirstRun { get; set; } = false;
}

/// <summary>Phase 4g: WhatsApp ödeme istemleri için Settings bloğu.</summary>
public sealed class PaymentSettings
{
    public string WhatsAppMessageTemplate { get; set; } =
        "Merhaba {ad}, {tarih} yayınımızdan toplam {tutar} TL ödemeniz bekleniyor.\n\n" +
        "Ürün toplamı: {urun_toplami} TL\n" +
        "{kargo}\n\n" +
        "IBAN: {iban}\nHesap Sahibi: {hesap_sahibi}\nPapara: {papara}\n\nTeşekkürler!";

    public string Iban { get; set; } = "";
    public string AccountHolder { get; set; } = "";
    public string Papara { get; set; } = "";

    /// <summary>
    /// Kümülatif kargo PR-E (2026-05-12): Müşteri ücretsiz kargo eşiğini
    /// aştıktan sonra vendor "Evet, kargolansın" dediğinde gönderilecek
    /// tebrik mesajı şablonu. Placeholder'lar: {ad}, {kumulatif_tutar}, {tarih}.
    ///
    /// Boş bırakılırsa WhatsApp mesajı oluşturulmaz — sessiz akış.
    /// </summary>
    public string ShippingWonTemplate { get; set; } =
        "Merhaba {ad}, toplam {kumulatif_tutar} TL alımınız ile ücretsiz " +
        "kargo hakkı kazandınız! 🎁 Siparişiniz en kısa sürede kargoya " +
        "verilecek. Teşekkürler!";
}

/// <summary>
/// Kargo eşik + ücreti — işletme bazlı sabit ayar (yayın bazında değişmez).
/// Müşteri toplamı <c>FreeShippingThreshold</c>'ı aşarsa ücretsiz kargo;
/// altında kalırsa <c>ShippingFee</c> kadar kargo satırı eklenir (PR B+ ile
/// otomatik label oluşturma + dekont eşleştirme).
///
/// Her iki alan da null bırakılırsa "kargo feature kapalı" anlamına gelir —
/// PR B-E henüz yokken default davranış kapalı.
/// </summary>
public sealed class ShippingSettings
{
    /// <summary>Üzerinde kargo ücretsiz olan minimum sipariş tutarı (TL).
    /// Null = feature kapalı.</summary>
    public decimal? FreeShippingThreshold { get; set; }

    /// <summary>Eşik altında uygulanan sabit kargo ücreti (TL).
    /// Null = feature kapalı.</summary>
    public decimal? ShippingFee { get; set; }

    /// <summary>İki alan da pozitif değer içeriyor ve feature aktif mi?</summary>
    public bool IsEnabled =>
        FreeShippingThreshold is > 0 && ShippingFee is > 0;
}

public sealed class GiveawayAnimationSettings
{
    /// <summary>Plugin id from OrderDeck.Overlay/wwwroot/animations/manifest.json.</summary>
    public string DefaultId { get; set; } = "wheel";

    /// <summary>0.0 - 1.0 master volume. Plugins route audio via AudioController which respects this.</summary>
    public double Volume { get; set; } = 0.7;

    /// <summary>When true, all plugin audio is silenced regardless of Volume.</summary>
    public bool MutedMode { get; set; } = false;
}
