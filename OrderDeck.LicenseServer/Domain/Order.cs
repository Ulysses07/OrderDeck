namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// WPF lokal "Label" entity'sinin server-side replikası. Mobile Panel
/// Siparişler ekranında yayın bazlı sipariş listesini bu tablodan okur.
///
/// "Label" yerine "Order" adı seçildi — server semantically siparişle
/// uğraşıyor, label sadece WPF'te fiziksel etiket karşılığı. LicenseId
/// üzerinden tenant izolasyonu; SessionId opsiyonel olabilir (eski/orphan
/// label'lar için).
///
/// WPF authoritative.
/// </summary>
public sealed class Order
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid? SessionId { get; set; }
    public StreamSession? Session { get; set; }

    /// <summary>WPF lokal CustomerId (hex GUID string). Server tarafı Customer
    /// entity sync edilmiyor — sadece display name + username yeterli.</summary>
    public string CustomerId { get; set; } = "";

    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }

    public string MessageText { get; set; } = "";
    public string? Code { get; set; }
    public decimal Price { get; set; }

    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? PrintedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>True ise label normal ürün satışı değil, kargo ücreti satırıdır.</summary>
    public bool IsShippingFee { get; set; }

    /// <summary>True ise backup-promoted ("Y" damga); audit için.</summary>
    public bool IsBackupPromoted { get; set; }

    /// <summary>True ise tentative backup (henüz onaylanmamış yedek); cumulative
    /// hesaplarında dışarda bırakılır.</summary>
    public bool IsTentativeBackup { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
