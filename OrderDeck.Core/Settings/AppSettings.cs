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

    /// <summary>Phase 4g: WhatsApp ödeme isteme yapılandırması.</summary>
    public PaymentSettings Payment { get; set; } = new();

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
}

/// <summary>Phase 4g: WhatsApp ödeme istemleri için Settings bloğu.</summary>
public sealed class PaymentSettings
{
    public string WhatsAppMessageTemplate { get; set; } =
        "Merhaba {ad}, {tarih} yayınımızdan toplam {tutar} TL ödemeniz bekleniyor.\n\n" +
        "IBAN: {iban}\nHesap Sahibi: {hesap_sahibi}\nPapara: {papara}\n\nTeşekkürler!";

    public string Iban { get; set; } = "";
    public string AccountHolder { get; set; } = "";
    public string Papara { get; set; } = "";
}
