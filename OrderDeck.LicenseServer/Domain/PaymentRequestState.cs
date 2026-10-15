namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ödeme isteği kaydının hâli (<see cref="PaymentRequestRecord.State"/>, yayın ve etiket senkronu spec'i 2026-10-13
/// §3.1, §6.4). Kolon <c>tinyint</c>; değerler kalıcıdır, değişmez.
/// </summary>
public enum PaymentRequestState : byte
{
    /// <summary>Kira canlı: mesaj gönderiliyor. Değeri 0 olmak ZORUNDA: DM6'nın filtreli tekil indeksi
    /// <see cref="PaymentRequestRecord.SendingIndexFilter"/> (<c>[State] = 0</c>) bu değere bakar.</summary>
    Sending = 0,

    /// <summary>Mesaj teslim edildi (<c>finish delivered</c>).</summary>
    Delivered = 1,

    /// <summary>Mesaj gitmiş olabilir, sonuç bilinmiyor: <c>finish unconfirmed</c>, ya da kira teslim kaydı olmadan
    /// doldu ya da aynı kopya aynı kişi×kapsamda yeniden başladı (§6.4-c). Öbür bilgisayarın "zaten istendi"
    /// sorusunda görünür.</summary>
    Unconfirmed = 2,

    /// <summary>Mesaj gönderilmedi (<c>finish abandoned</c>).</summary>
    Abandoned = 3,
}
