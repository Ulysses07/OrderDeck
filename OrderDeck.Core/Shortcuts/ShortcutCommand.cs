using System.Collections.Generic;

namespace OrderDeck.Core.Shortcuts;

/// <summary>
/// Bilinen komut ID'lerinin canonical listesi. Yeni komut eklemek için:
/// 1) yeni const ekle
/// 2) <see cref="DisplayNames"/>'e ekle
/// 3) <c>ShortcutRegistry.BuildDefaults</c>'a ekle
/// 4) <c>ShortcutBinder.GetCommand</c>'a yeni vaka ekle
/// </summary>
public static class ShortcutCommand
{
    public const string Print            = "print";
    public const string DeleteSelected   = "delete-selected";
    public const string ClearQueue       = "clear-queue";
    public const string StartStream      = "start-stream";
    public const string EndStream        = "end-stream";
    public const string StartGiveaway    = "start-giveaway";
    public const string OpenShortcutHelp = "open-shortcut-help";
    public const string OpenSettings     = "open-settings";
    public const string OpenHistory      = "open-history";
    public const string OpenBlacklist    = "open-blacklist";
    public const string OpenCustomers    = "open-customers";

    /// <summary>UI'da gösterilecek Türkçe başlıklar.</summary>
    public static IReadOnlyDictionary<string, string> DisplayNames { get; } = new Dictionary<string, string>
    {
        [Print]            = "Yazdır",
        [DeleteSelected]   = "Seçili etiketi sil",
        [ClearQueue]       = "Kuyruğu temizle",
        [StartStream]      = "Yayını başlat",
        [EndStream]        = "Yayını bitir",
        [StartGiveaway]    = "Çekiliş başlat",
        [OpenShortcutHelp] = "Kısayol yardımı",
        [OpenSettings]     = "Ayarlar",
        [OpenHistory]      = "Yayın geçmişi",
        [OpenBlacklist]    = "Kara liste",
        [OpenCustomers]    = "Müşteriler",
    };
}
