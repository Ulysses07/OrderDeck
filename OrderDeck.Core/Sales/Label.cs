namespace OrderDeck.Core.Sales;

/// <summary>
/// A queued or printed label. Created when the user double-clicks a chat message in the
/// MainShell; persisted to SQLite. PrintedAt = null means it's still in the queue.
/// </summary>
public sealed record Label(
    string Id,
    string SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    long? PrintedAt,
    long? CancelledAt = null,
    string? CancelReason = null,
    /// <summary>True when this label originated as a backup (created via
    /// <c>LabelService.AddBackup</c>). Drives the small "Y" stamp on the
    /// printed sticker so the picker can spot a re-routed sale without
    /// confusing it with the original line. Stays true after the backup is
    /// confirmed (<see cref="IsTentativeBackup"/> flips to false) — the
    /// physical paper already has the Y mark.</summary>
    bool IsBackupPromoted = false,
    /// <summary>The Label.Id this row is a standby for. Null for normal sales.
    /// Set when a backup is added; preserved after promotion for audit so
    /// reports can show "this sale replaced label X".</summary>
    string? ParentLabelId = null,
    /// <summary>1 while the backup hasn't been confirmed: the sticker may have
    /// been printed and set aside, but the sale isn't real yet, so it must be
    /// excluded from session revenue and customer aggregates. Flipped to 0 by
    /// <c>LabelService.ConfirmBackup</c> when the operator promotes the
    /// backup after the original buyer cancels.</summary>
    bool IsTentativeBackup = false,
    /// <summary>Human-readable name shown on the queue row UI. For YouTube,
    /// <see cref="Username"/> is the opaque channel ID (UCxxx...) used for
    /// stable customer linking — DisplayName carries the pretty label
    /// ("Ayşe Yılmaz") that the operator wants to see. Nullable; UI falls
    /// back to Username when absent.</summary>
    string? DisplayName = null,
    /// <summary>Kargo PR B (2026-05-11): true ise bu label normal ürün satışı
    /// değil, müşterinin sipariş toplamı kargo eşiğinin altında kaldığı için
    /// otomatik veya operatör tarafından eklenen kargo ücreti satırıdır.
    /// LabelService.AddShippingFee() bayrağı true setler. PR E print
    /// template + Excel rapor ayrımı yapar; PR C dekont eşleştirme
    /// hesaplamasında bu label'ları "kargo dahil mi?" check'i için kullanır.</summary>
    bool IsShippingFee = false,
    /// <summary>Kümülatif kargo PR-B (2026-05-12): label hangi açık Shipment
    /// dosyasına bağlı. Null = henüz hiçbir Shipment'a attach edilmemiş (eski
    /// satırlar veya PR-C öncesi yeni satırlar). ShipmentService attach
    /// edince doldurulur, Shipped olunca FK korunur (audit için).</summary>
    string? ShipmentId = null);
