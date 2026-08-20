namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Facebook/Instagram entegrasyonunun derleme zamanı sabitleri.
///
/// <para><b>Burada artık kimlik bilgisi YOK.</b> App ID, Login for Business
/// config id ve redirect URI lisans sunucusundan geliyor
/// (<c>GET /api/v1/facebook/oauth/config</c>), App Secret ise masaüstüne hiç
/// inmiyor — code→token takasını sunucu yapıyor. Eskiden App Secret ve config
/// id burada boş duruyor ve "üretim makinesinde doldurulacak" deniyordu; o
/// adım yayın hattına hiç eklenmedi, dolayısıyla her kurulumda Facebook
/// bağlantısı "App ID eksik" diye patlıyordu. Sabit yerine sunucu seçilmesinin
/// asıl sebebi de bu: Meta panelinde config veya izin seti değişince yeni bir
/// masaüstü sürümü yayınlamak gerekmiyor.</para>
/// </summary>
internal static class FacebookOAuthDefaults
{
    /// <summary>Graph API version pinned for the entire integration. Bump
    /// in one place when a new version becomes the default — Meta deprecates
    /// versions after ~2 years. Sunucudaki <c>OrderDeck:Facebook:GraphApiVersion</c>
    /// ile aynı tutulmalı: OAuth diyaloğunu bu değer, takası o kurar.</summary>
    public const string GraphApiVersion = "v22.0";
}
