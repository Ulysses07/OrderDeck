namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Netgsm REST API kimlik bilgileri. Prod'da VPS .env'den bind edilir
/// (<c>Netgsm__UserCode</c> vb.); dev'de boş kalır ve Sms:Provider=log olur.
/// </summary>
public sealed class NetgsmOptions
{
    public string UserCode { get; set; } = "";   // Netgsm abone no
    public string Password { get; set; } = "";    // Netgsm API şifresi
    public string Header { get; set; } = "";       // Onaylı gönderici başlığı (sender ID)
    public string BaseUrl { get; set; } = "https://api.netgsm.com.tr";

    /// <summary>Hizmet mesajı (OTP) İYS filtresi. "0" = ticari değil, İYS
    /// kontrolü yok. Boş bırakılırsa payload'a eklenmez (başlık tipi belirler).
    /// OTP başlığı "bilgilendirme" onaylıysa "0" güvenlidir.</summary>
    public string? IysFilter { get; set; }

    /// <summary>Ticari ileti (toplu kampanya) İYS filtresi. B2C "11", B2B "12".
    /// Kampanya alıcıları tüketici → varsayılan "11". Netgsm bu filtreyle
    /// alıcıyı İYS onay/ret kaydına göre eler (2026-06-16'da 11 ile code 00
    /// kanıtlandı).</summary>
    public string CommercialIysFilter { get; set; } = "11";

    /// <summary>Mesaj encoding. Türkçe karakter için "TR" (mesaj 70 haneye düşer).
    /// Boş = GSM-7 (160 hane). OTP mesajı Türkçe karaktersiz olduğundan boş kalır.</summary>
    public string? Encoding { get; set; }

    /// <summary>HTTP timeout (sn). Netgsm asılı kalırsa forgot-password isteğini
    /// bloklamasın diye kısa tutulur.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
