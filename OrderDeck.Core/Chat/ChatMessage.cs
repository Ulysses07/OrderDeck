using System.Collections.Generic;

namespace OrderDeck.Core.Chat;

/// <param name="SourceAccount">Mesajın geldiği hesabın gösterime hazır etiketi: <c>@kullanıcıadı</c>,
/// kullanıcı adı çözülemediyse Sayfa adı ("@"sız). Yalnız Instagram birleştirme kapısı doldurur (iki ve
/// üzeri Sayfa bağlıyken); öbür bütün mesajlarda null. Kalıcılaşmaz: overlay'e, veritabanına ve siparişe
/// taşınmaz.</param>
public sealed record ChatMessage(
    string Id,
    string Platform,
    string? ExternalId,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string Text,
    long ReceivedAt,
    IReadOnlyList<string> Badges,
    string? SourceAccount = null);
