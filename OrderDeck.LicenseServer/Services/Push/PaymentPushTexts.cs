namespace OrderDeck.LicenseServer.Services.Push;

/// <summary>Shopper'a giden ödeme kararı bildirimlerinin sabit gövdeleri. KVKK K-5: bildirim gövdesi Google FCM'ye gider;
/// tutar ve serbest metin ret gerekçesi YAZILMAZ, ayrıntı uygulamada.</summary>
public static class PaymentPushTexts
{
    public const string PaymentApprovedPushBody = "Dekontun onaylandı.";
    public const string PaymentRejectedPushBody = "Dekontun reddedildi. Ayrıntıyı uygulamada görebilirsin.";
}
