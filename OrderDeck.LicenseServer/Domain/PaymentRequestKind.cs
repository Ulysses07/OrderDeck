namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ödeme isteği kaydının türü (<see cref="PaymentRequestRecord.Kind"/>, yayın ve etiket senkronu spec'i 2026-10-13
/// §3.1, §6.4). Kolon <c>tinyint</c>; değerler kalıcıdır, değişmez.
/// </summary>
public enum PaymentRequestKind : byte
{
    /// <summary>Mod On'da <c>payment-requests/begin</c>'in açtığı kira: kişi×kapsamda bilgisayarlar arası tek
    /// eşzamanlı istek (DM6). <see cref="PaymentRequestState.Sending"/> doğar; <c>finish</c> ya da süre dolumu
    /// kapatır.</summary>
    Lease = 0,

    /// <summary>Kirasız, sonradan yazılan kayıt (<c>payment-requests/backfill</c>): Ws'nin ödeme geçmişi geri
    /// doldurması ve mod Off'ta teslimden sonra kuyruğa alınan kayıt (§6.4 "Ödeme geçmişi"). Hiç
    /// <see cref="PaymentRequestState.Sending"/> olmaz; <see cref="PaymentRequestRecord.ProductTotal"/> ve
    /// <see cref="PaymentRequestRecord.TotalBeforeBalance"/> taşımaz.</summary>
    Record = 1,
}
