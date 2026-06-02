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
}
