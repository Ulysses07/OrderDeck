namespace OrderDeck.LicenseServer.Services.Facebook;

/// <summary>
/// Masaüstü uygulamasının Facebook OAuth'u için sunucu tarafı yapılandırma.
/// Prod'da VPS .env'den bind edilir (<c>OrderDeck__Facebook__AppSecret</c> vb.),
/// dev'de boş kalır ve uçlar 503 döner.
///
/// <para><b>Neden burada:</b> App Secret eskiden <c>FacebookOAuthDefaults</c>
/// içinde derlenip her kuruluma gidiyordu. Kurulumdaki bir binary'den
/// çıkarılabilen sır, sır değildir; Meta da Google'ın aksine App Secret'ı
/// "public client" saymaz. Artık code→token takası yalnız burada yapılır,
/// masaüstü sadece <c>code</c> gönderir.</para>
///
/// <para>WhatsApp'ın kendi Meta app'i AYRI (<c>WhatsAppOptions</c>); bu blok
/// yalnız sohbet/moderasyon app'i içindir, değerleri karıştırma.</para>
/// </summary>
public sealed class FacebookOptions
{
    /// <summary>Graph sürümü — masaüstündeki
    /// <c>FacebookOAuthDefaults.GraphApiVersion</c> ile aynı tutulmalı ki
    /// takas ile sonraki çağrılar aynı sürümde konuşsun.</summary>
    public string GraphApiVersion { get; set; } = "v22.0";

    /// <summary>Graph kök adresi (test için override edilebilir).</summary>
    public string GraphBaseUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>Meta App ID — gizli değil, istemciye <c>config</c> ucundan
    /// bildirilir (yetkilendirme URL'inde zaten açıkta gider).</summary>
    public string AppId { get; set; } = "";

    /// <summary>Meta App Secret. <b>Yalnız sunucuda.</b> İstemciye hiçbir
    /// uçtan dönmez, log'a yazılmaz.</summary>
    public string AppSecret { get; set; } = "";

    /// <summary>Facebook Login for Business "Configuration ID" — izin setini
    /// kodlar. Sunucudan gelmesi, Meta panelinde config değişince yeni bir WPF
    /// sürümü yayınlamak zorunda kalmamayı sağlıyor.</summary>
    public string LoginConfigId { get; set; } = "";

    /// <summary>Meta'ya kayıtlı redirect URI. Loopback DEĞİL: Meta, Live
    /// modda localhost girdisini kabul etmiyor, bu yüzden Caddy'deki
    /// <c>orderdeckapp.com/facebook/callback</c> kuralı isteği masaüstünün
    /// loopback dinleyicisine 302'liyor. Strict Mode tam eşleşme istediği ve
    /// aynı değer hem yetkilendirmede hem takasta gitmek zorunda olduğu için
    /// tek kaynak burasıdır.</summary>
    public string RedirectUri { get; set; } = "https://orderdeckapp.com/facebook/callback";

    /// <summary>HTTP timeout (sn).</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Takas yapılabilir mi. Yapılandırılmamış sunucuda uçlar 503
    /// döner — istemci "sunucu hazır değil" diyebilsin diye sessizce
    /// başarısız olmuyoruz.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}
