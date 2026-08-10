namespace OrderDeck.App.Startup;

/// <summary>
/// Yarım kalmış yayın oturumu bulunduğunda operatörün verdiği karar.
///
/// Ayrı dosyada: hem gate (View) hem StartupFlow (karar makinesi) bunu
/// kullanıyor ve ikisinin birbirini tanıması gerekmiyor.
/// </summary>
public enum SessionRecoveryChoice
{
    /// <summary>Seçim yapılmadan kapandı — uygulama açılmasın.</summary>
    Exit = 0,
    /// <summary>Oturuma devam et, sayaçlar kaldığı yerden işlesin.</summary>
    Continue,
    /// <summary>Oturumu kapat, yeni bir yayına temiz başla.</summary>
    EndSession
}
