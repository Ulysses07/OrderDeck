namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>Netgsm hata sınıfı — §3.4'ün üç sınıfı. Sınıf, hatanın KİMİ
/// cezalandıracağını belirler: tek alıcıyı mı, kampanyayı mı, hesabı mı.</summary>
public enum NetgsmErrorClass
{
    /// <summary>Yalnız bu alıcı geçersiz (kod 70) → alıcı <c>failed</c>, döngü devam.</summary>
    Recipient,
    /// <summary>Hesap düzeyinde arıza (şifre/başlık/İYS yetkisi) → kampanya
    /// <c>paused</c> + hesap <c>Failed</c>; kalan alıcılar <c>pending</c> korunur.</summary>
    Account,
    /// <summary>Bakiye/limit/bilinmeyen → kampanya <c>paused</c>, alıcılar
    /// <c>pending</c> korunur. Varsayılan sınıf: bilinmeyen kodu alıcıya
    /// <c>failed</c> yazmak bakiye bittiğinde tüm kitleyi tek turda tüketirdi.</summary>
    CampaignPause,
}

/// <summary>
/// Netgsm'in gönderimi TEMİZ REDDETMESİ: yanıt alındı ve iş kabul edilmedi,
/// yani hiçbir SMS gitmedi. <b>Bu garanti sınıfın sözleşmesidir</b> — çağıran
/// (SmsCampaignSendJob) buna dayanarak alıcıyı <c>pending</c>'e geri döndürür.
/// Ağ hatası / timeout bu istisnaya SARILMAZ: istek Netgsm'e ulaşmış ve mesaj
/// gitmiş olabilir; onlar ham istisna olarak çıkar ve alıcı <c>failed</c> yazılır
/// (belirsizlikte çift ticari SMS riskine karşı fail-closed).
/// </summary>
public sealed class NetgsmSmsException : Exception
{
    /// <summary>Netgsm REST v2 hata kodu ("30", "70"…). Gövdeden kod
    /// çıkarılamadıysa null — null da <see cref="NetgsmErrorClass.CampaignPause"/>.</summary>
    public string? Code { get; }

    public NetgsmSmsException(string? code, string message) : base(message) => Code = code;

    /// <summary>Plan dokümanındaki kod tablosu. 30/40/50/51 = hesap kimliği/
    /// başlığı/İYS yetkisi — alıcıdan bağımsız, her alıcıda aynen tekrarlanır.
    /// 70 = parametre/alıcı hatası — yalnız o alıcıyı ilgilendirir.</summary>
    public NetgsmErrorClass Classify() => Code switch
    {
        "30" or "40" or "50" or "51" => NetgsmErrorClass.Account,
        "70" => NetgsmErrorClass.Recipient,
        _ => NetgsmErrorClass.CampaignPause,
    };
}
