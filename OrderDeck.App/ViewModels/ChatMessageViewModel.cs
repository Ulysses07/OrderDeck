using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Chat;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// UI-side wrapper around <see cref="ChatMessage"/> that adds <see cref="IsSenderBlacklisted"/>
/// for red-highlight binding in the live chat panel.
/// </summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessage Message { get; }

    [ObservableProperty] private bool _isSenderBlacklisted;

    public string Platform => Message.Platform;
    public string Username => Message.Username;
    public string Text     => Message.Text;
    public string Display  => Message.DisplayName ?? Message.Username;

    /// <summary>
    /// Drives which moderation items the chat context menu shows. A message
    /// only ever belongs to one platform, so showing the YouTube and Facebook
    /// blocks at the same time just gives the operator dead entries.
    /// </summary>
    public bool IsYouTube => string.Equals(Platform, "youtube", System.StringComparison.OrdinalIgnoreCase);
    public bool IsFacebook => string.Equals(Platform, "facebook", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Satırın sağındaki hesap etiketi: yorumun geldiği Instagram hesabı. <see cref="ChatMessage.SourceAccount"/>
    /// gösterime hazır gelir ve AYNEN kullanılır: "@" kulvar etiketinde hazır gelir (etiketi kulvarı açan hesap
    /// döngüsü kurar; kapı da arayüz de eklemez); burada eklenseydi kullanıcı adı çözülemeyen hesabın Sayfa adı da
    /// "@" alırdı. Alan boşken null: tek Sayfada ve öbür platformlarda etiket çizilmez, satır eskisi gibi kalır.
    /// </summary>
    public string? AccountTag => string.IsNullOrWhiteSpace(Message.SourceAccount) ? null : Message.SourceAccount;

    /// <summary>
    /// Etiketin ipucu: tam hesap adı (uzun ad satırda kırpılır) ve yorumun Meta damgasının yerel saat karşılığı.
    /// Ofset yorumun anındaki yerel ofsettir, sabit bir sayı değil. Etiket yoksa null.
    /// </summary>
    public string? AccountTooltip => AccountTag is { } tag
        ? FormatAccountTooltip(tag, Message.ReceivedAt,
            TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(Message.ReceivedAt)))
        : null;

    /// <summary>İpucu metni; saat <paramref name="unixSeconds"/> anının <paramref name="utcOffset"/> ofsetindeki
    /// karşılığıdır. Ofset parametredir ki saat çevrimi makinenin saat diliminden bağımsız sınanabilsin.</summary>
    internal static string FormatAccountTooltip(string sourceAccount, long unixSeconds, TimeSpan utcOffset) =>
        $"Instagram: {sourceAccount} · "
        + DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(utcOffset).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public ChatMessageViewModel(ChatMessage message, bool isSenderBlacklisted)
    {
        Message = message;
        IsSenderBlacklisted = isSenderBlacklisted;
    }
}
