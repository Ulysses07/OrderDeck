namespace OrderDeck.LicenseServer.Services.Push;

/// <summary>
/// FCM provider config. <c>OrderDeck:Push:Fcm</c> bind edilir.
/// </summary>
public sealed class FcmOptions
{
    /// <summary>Firebase service account JSON dosyasının absolute path'i.
    /// Deploy edilen ortamda /etc/orderdeck/firebase-service-account.json gibi
    /// secure bir konumda tutulur. Boş ise FCM sender boot etmez (Program.cs
    /// throw eder).</summary>
    public string ServiceAccountJsonPath { get; set; } = "";

    /// <summary>True ise FCM gönderim batch'leri (multicast yerine SendEachAsync).
    /// Batch çağrı 500'e kadar token destekler — yayıncının 5-10 device'ı
    /// var, batch açıkça gerekli değil ama büyütülebilirlik için tutuldu.</summary>
    public bool UseBatchSend { get; set; } = true;
}
