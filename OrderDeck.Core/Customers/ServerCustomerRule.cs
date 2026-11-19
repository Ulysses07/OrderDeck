using System.Diagnostics.CodeAnalysis;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Sunucunun bir müşteri satırını kabul edip etmeyeceği — TEK tanım. Sınırlar sunucunun kabul sınırlarının
/// (<c>LicensesWpfCustomersSyncController.IsAcceptable</c>, kolon sınırları) AYNISI. Burada tekrarlanıyor, çünkü tek
/// amaç "sunucu bunu reddeder mi?" sorusunu göndermeden önce cevaplamak. İki kullanıcı aynı tanımı kullanır:
/// <list type="bullet">
/// <item>Müşteri senkronu (<c>WpfCustomerProjectionSyncService</c>). Sunucu böyle satırı yazmaz: bugün yazılmadan
///   sayılır (<c>LicensesWpfCustomersSyncController</c>, <c>invalid</c> kolu). Eski sunucu partinin TAMAMINI 400'le
///   reddederdi; imleç ilerlemediği için 2026-08-14'te Username'i boş tek kayıt 565 müşteriyi 12 gün bekletti. İki
///   durumda da satır sunucuya hiç ulaşmaz; bozuk satır bu yüzden istemcide de elenir.</item>
/// <item>Ödeme sahibi kuralı (<c>PaymentOwnerRule.IsOwnerCandidate</c>). Elenen satır sunucuya hiç ulaşmaz; sahip
///   olsaydı düşüm çapası hiç yazılamaz, kişide kalıcı <c>customer-not-synced</c> olurdu (ortak bakiye §6.1-a, M14).</item>
/// </list>
/// <para>Sunucudaki doğrulama aynen kalmalı: sınır orada. Sunucu sınırı bir gün gevşerse buradaki eleme fazladan satır
/// atlar (hata değil, yalnız gecikme); sıkılaşırsa sunucu satırı yazmadan sayar ve bu sabitler güncellenmeli. Değerler
/// bu yüzden kaynağıyla birlikte anılıyor.</para>
/// </summary>
public static class ServerCustomerRule
{
    /// <summary>Sunucunun Platform kolon sınırı.</summary>
    public const int MaxPlatformLength = 32;

    /// <summary>Sunucunun Username kolon sınırı.</summary>
    public const int MaxUsernameLength = 128;

    /// <summary>Satır sunucuya gönderilebilir mi: Platform ve Username boş/boşluk değil ve sınırları içinde.
    /// <c>[NotNullWhen(true)]</c>: "kabul edilebilir" demek aynı zamanda "boş değil" demek; çağıranın null akışı
    /// <c>true</c> kolunda iki değeri null değil bilir.</summary>
    public static bool IsServerAcceptable(
        [NotNullWhen(true)] string? platform,
        [NotNullWhen(true)] string? username)
        => !string.IsNullOrWhiteSpace(platform) && platform.Length <= MaxPlatformLength
        && !string.IsNullOrWhiteSpace(username) && username.Length <= MaxUsernameLength;
}
