namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

/// <summary>
/// Kayıt formundaki "hesabını bağla" girişlerinin yapılandırması.
/// VPS .env: <c>IntakeLogin__GoogleClientId</c>, <c>IntakeLogin__GoogleClientSecret</c>,
/// <c>IntakeLogin__FacebookAppId</c>, <c>IntakeLogin__FacebookAppSecret</c>,
/// <c>IntakeLogin__YouTubeEnabled</c>, <c>IntakeLogin__FacebookEnabled</c>.
///
/// YouTube bayrağı Google'ın <c>youtube.readonly</c> kapsam onayına kilitli:
/// kod karanlıkta yatar, onay gelince bayrak açılır — deploy gerekmez, restart yeter.
///
/// Facebook app'i masaüstününkinden (<c>OrderDeck:Facebook</c>) AYRI: o app
/// "Facebook Login for Business" tipinde ve yalnız <c>public_profile</c> isteyen
/// klasik dialog'u "supported permission" hatasıyla reddediyor (sahada görüldü,
/// 2026-09-04). Bu yüzden form için Consumer tipinde ayrı app açıldı
/// (OrderDeck Kayit, 1090037693602616) — klasik login'de <c>public_profile</c>
/// otomatik erişimli, review istemez.
/// </summary>
public sealed class IntakeLoginOptions
{
    public const string SectionName = "IntakeLogin";

    /// <summary>Google OAuth istemcisi — WPF'in kullandığı Cloud projesinde
    /// AYRI bir "Web application" client oluşturulur (masaüstü client'ı
    /// redirect URI kabul etmez).</summary>
    public string? GoogleClientId { get; set; }

    /// <summary>Yalnız sunucuda; log'a ve istemciye asla çıkmaz.</summary>
    public string? GoogleClientSecret { get; set; }

    /// <summary>Form için AYRI Consumer app'in kimliği (OrderDeck Kayit).
    /// Masaüstünün <c>OrderDeck__Facebook__AppId</c>'si DEĞİL.</summary>
    public string? FacebookAppId { get; set; }

    /// <summary>Yalnız sunucuda; log'a ve istemciye asla çıkmaz.</summary>
    public string? FacebookAppSecret { get; set; }

    /// <summary>İki sağlayıcı için ortak dönüş adresi. Google Cloud Console'da
    /// "Authorized redirect URIs"e, Meta app'inde "Valid OAuth Redirect URIs"e
    /// BİREBİR bu değer yazılmalı.</summary>
    public string RedirectUri { get; set; } = "https://orderdeckapp.com/musteri-kayit/baglanti-donusu";

    public bool YouTubeEnabled { get; set; }
    public bool FacebookEnabled { get; set; }

    /// <summary>Bayrak açık AMA kimlik bilgisi eksikse buton yine gizli kalır —
    /// yarım yapılandırma müşteriye kırık link olarak yansımasın.</summary>
    public bool YouTubeLoginReady =>
        YouTubeEnabled
        && !string.IsNullOrWhiteSpace(GoogleClientId)
        && !string.IsNullOrWhiteSpace(GoogleClientSecret);

    /// <inheritdoc cref="YouTubeLoginReady"/>
    public bool FacebookLoginReady =>
        FacebookEnabled
        && !string.IsNullOrWhiteSpace(FacebookAppId)
        && !string.IsNullOrWhiteSpace(FacebookAppSecret);
}
