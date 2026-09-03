namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

/// <summary>
/// Kayıt formundaki "hesabını bağla" girişlerinin yapılandırması.
/// VPS .env: <c>IntakeLogin__GoogleClientId</c>, <c>IntakeLogin__GoogleClientSecret</c>,
/// <c>IntakeLogin__YouTubeEnabled</c>, <c>IntakeLogin__FacebookEnabled</c>.
///
/// YouTube bayrağı Google'ın <c>youtube.readonly</c> kapsam onayına kilitli:
/// kod karanlıkta yatar, onay gelince bayrak açılır — deploy gerekmez, restart yeter.
/// Facebook app'i (masaüstüyle aynı, <c>OrderDeck:Facebook</c>) <c>public_profile</c>
/// için review istemez; o bayrak hemen açılabilir.
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
}
