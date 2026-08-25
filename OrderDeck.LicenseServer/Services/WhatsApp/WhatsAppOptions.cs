namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// WhatsApp Cloud API yapılandırması. Prod'da VPS .env'den bind edilir
/// (<c>OrderDeck__WhatsApp__AppSecret</c> vb.); dev'de boş kalır ve
/// <c>OrderDeck:WhatsApp:Provider=log</c> olur (gerçek API çağrısı yok).
///
/// <para><b>Çok-tenant:</b> uygulama geneli değerler (tek Meta app) burada:
/// <see cref="GraphApiVersion"/>, <see cref="VerifyToken"/>, <see cref="AppSecret"/>.
/// Her tenant'ın gönderim kimlikleri (Phone Number ID + Access Token) ileride
/// DB'de tenant başına şifreli saklanır; şimdilik <see cref="DefaultPhoneNumberId"/>
/// + <see cref="DefaultAccessToken"/> "kendi numaran = ilk tenant" için config'ten
/// gelir (uçtan test).</para>
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>Graph API sürümü (Meta eski sürümleri ~2 yıl sonra deprecate eder → pinli).
    /// 2026-08-25 itibarıyla en yeni: v26.0 (29.07.2026 çıktı; v27.0 yok — Graph
    /// "Unknown path components" döndürüyor). Webhook alanları da Meta tarafında
    /// v26.0'a abone; ikisini aynı sürümde tutuyoruz — payload biçimi ile çağrı
    /// sürümü ayrışırsa hata sessiz olur (alan kaybolur, istisna atılmaz).
    /// Prod'da <c>OrderDeck__WhatsApp__GraphApiVersion</c> ile geri alınabilir.</summary>
    public string GraphApiVersion { get; set; } = "v26.0";

    /// <summary>Webhook GET doğrulaması: Meta'nın gönderdiği <c>hub.verify_token</c>
    /// bununla eşleşmeli. Kendi belirlediğin rastgele string.</summary>
    public string VerifyToken { get; set; } = "";

    /// <summary>Webhook POST imza doğrulaması için Meta App Secret. Gelen
    /// <c>X-Hub-Signature-256</c> = HMAC-SHA256(appSecret, rawBody) ile doğrulanır.</summary>
    public string AppSecret { get; set; } = "";

    /// <summary>Meta App ID. Embedded Signup'ta <c>oauth/access_token</c> çağrısının
    /// <c>client_id</c>'si. WhatsApp app'i Facebook chat app'inden AYRI — bu değeri
    /// oradan kopyalama.</summary>
    public string AppId { get; set; } = "";

    /// <summary>Facebook Login for Business "Configuration ID" — panelin JS SDK'ya
    /// verdiği <c>config_id</c>. Sunucu bunu kullanmaz, yalnız panele bildirir;
    /// burada durmasının sebebi tek bir WhatsApp yapılandırma bloğu olması.</summary>
    public string EmbeddedSignupConfigId { get; set; } = "";

    /// <summary>Varsayılan/ilk tenant (kendi numaran) Phone Number ID — çok-tenant
    /// DB'ye geçene kadar config'ten. Graph POST hedefi: <c>/{PhoneNumberId}/messages</c>.</summary>
    public string DefaultPhoneNumberId { get; set; } = "";

    /// <summary>Varsayılan/ilk tenant kalıcı Access Token (System User).</summary>
    public string DefaultAccessToken { get; set; } = "";

    /// <summary>HTTP timeout (sn). Gönderim inline await edildiğinde asılı kalmasın.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Graph API kök adresi (test için override edilebilir).</summary>
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";
}
