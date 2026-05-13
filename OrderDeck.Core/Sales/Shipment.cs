namespace OrderDeck.Core.Sales;

/// <summary>
/// Kümülatif kargo dosyası — bir müşterinin biriken (henüz kargolanmamış)
/// Label'larını gruplar. Yeni alım yapıldıkça mevcut açık Shipment'a Label
/// eklenir; <see cref="ShipmentStatus.Shipped"/>'e döndüğünde kapanır ve
/// sonraki alım yeni Shipment açar.
///
/// CumulativeAmount denormalize edilmiş alandır — bağlı Label.Price toplamı.
/// ShipmentService attach/detach sırasında bu invariant'ı korur.
///
/// Bkz: docs/superpowers/specs/2026-05-12-cumulative-shipping-trigger-design.md
/// </summary>
public sealed record Shipment(
    string Id,
    string CustomerId,
    ShipmentStatus Status,
    long CreatedAt,
    long? HeldAt,
    long? ShippedAt,
    decimal CumulativeAmount,
    /// <summary>PR-D (2026-05-13): LicenseServer'a son sync zamanı.
    /// Null = outbox'ta, henüz push edilmedi. Service push sonrası
    /// MarkSynced ile doldurur. Local mutation (AttachLabels, ApplyDecision)
    /// sonra null'a döndürülür → bir sonraki tick'te yeniden push.</summary>
    long? SyncedAt = null);

/// <summary>
/// Shipment yaşam döngüsü. Geçişler:
///   Pending → Held (vendor "beklet" seçti)
///   Pending → RecipientPays (vendor "alıcı ödemeli" seçti)
///   Pending → Shipped (vendor doğrudan kargolansın dedi)
///   Held → Shipped (kümülatif eşik aşıldı, vendor onayladı)
///   Held → RecipientPays (vendor fikir değiştirdi)
///   RecipientPays → Shipped (etiket basıldı, kargocuya verildi)
///   Shipped → (terminal, kapalı)
/// </summary>
public enum ShipmentStatus
{
    /// <summary>Yeni oluştu, henüz vendor karar vermedi.</summary>
    Pending,

    /// <summary>Vendor "beklet" dedi, kümülatif eşik aşılmasını bekliyor.</summary>
    Held,

    /// <summary>Vendor "alıcı ödemeli" seçti; kargocu personele ayrı listede gözükür.</summary>
    RecipientPays,

    /// <summary>Kargocuya verildi, terminal state. Yeni alım için yeni Shipment açılır.</summary>
    Shipped
}
