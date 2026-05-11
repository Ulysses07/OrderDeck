namespace OrderDeck.Core.Payments;

public enum PaymentStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>
/// Kargo PR C: dekont yüklendikten sonra vendor'un kargo davranışı için
/// verdiği karar. Müşteri ürün toplamını ödedi ama kargo ücretini eklemediyse
/// matcher service buradan birini set eder.
/// </summary>
public enum ShipmentDirective
{
    /// <summary>Standart akış — kargo ücreti dahil veya ücretsiz kargo eşiği
    /// aşıldı, karar gerektirmiyor. Default değer (geriye uyumlu).</summary>
    Normal = 0,

    /// <summary>Müşteri kargo ücretini yatırmadı, kargosunu beklemeye al.
    /// Sonraki yayında kümülatif toplam eşiği aşarsa kargolanacak.</summary>
    Hold = 1,

    /// <summary>Müşteri kargo ücretini yatırmadı, kargo şirketi alıcıdan
    /// tahsil edecek. Etikette belirgin işaret + kargocu listesinde not.</summary>
    RecipientPays = 2
}

/// <summary>
/// Müşterinin yayıncıya banka transferi olarak yaptığı ödemenin lokal kaydı.
/// Manuel girilebilir (PR C — DekontEkleDialog) veya gelecekte customer mobile
/// app upload'undan gelebilir. Periyodik olarak <c>PaymentSyncService</c>
/// tarafından LicenseServer'a push'lanır.
///
/// SyncedAt null iken outbox kuyruğunda. Server confirm sonrası dolar.
/// Status değişimi (mobile onay/red) reverse sync ile geri gelir (sonraki faz).
/// </summary>
public sealed record Payment(
    string Id,
    string PayerName,
    decimal Amount,
    long PaidAt,
    string ReferansNo,
    string? PdfHash,
    PaymentStatus Status,
    long CreatedAt,
    long UpdatedAt,
    long? SyncedAt,
    long? ApprovedAt,
    long? RejectedAt,
    string? RejectReason,
    ShipmentDirective ShipmentDirective = ShipmentDirective.Normal);
