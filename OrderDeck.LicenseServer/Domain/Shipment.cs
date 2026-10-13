namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Kümülatif kargo durumu (server-side, PR-D, 2026-05-13). WPF App'in lokal
/// SQLite Shipment'larının server-side replikası. WPF push'lar, mobile Panel
/// app cross-session "bekleyen kargo dosyaları"nı buradan okur.
///
/// LicenseId üzerinden tenant izolasyonu — yayıncı sadece kendi lisansının
/// Shipment'larını görür. CustomerId WPF lokal Customer.Id (her iki tarafta
/// aynı: GUID hex string).
/// </summary>
public enum ShipmentStatus
{
    /// <summary>Yeni oluştu, vendor henüz karar vermedi.</summary>
    Pending = 0,
    /// <summary>"Beklet" — kümülatif eşik aşılmasını bekliyor.</summary>
    Held = 1,
    /// <summary>"Alıcı ödemeli" — sticky, etikette kırmızı yazı.</summary>
    RecipientPays = 2,
    /// <summary>Kargolandı — terminal, yeni alım yeni Shipment açar.</summary>
    Shipped = 3
}

public sealed class Shipment
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>WPF lokal Customer.Id (hex GUID string). Server tarafında
    /// foreign key değil çünkü Customer sync ayrı bir akış — WPF customer'ı
    /// silinse bile audit için bağlı olduğu Shipment kalmalı.</summary>
    public string CustomerId { get; set; } = "";

    public ShipmentStatus Status { get; set; } = ShipmentStatus.Pending;

    /// <summary>Bağlı Label.Price toplamının denormalize cache'i (WPF
    /// authoritative). Server doğrudan değiştirmez; WPF push'ta gelen değeri
    /// kabul eder.</summary>
    public decimal CumulativeAmount { get; set; }

    /// <summary>WPF tarafında üretilen oluşturma zamanı (yayıncı saatinde).
    /// Server saatiyle farklı olabilir; sıralama için WPF zamanı authoritative.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Vendor "beklet" dediği an. Held state'e ilk geçişte set,
    /// sonraki kararlarda korunur.</summary>
    public DateTimeOffset? HeldAt { get; set; }

    /// <summary>Kargoya verildiği an. Shipped state'e geçişte set.</summary>
    public DateTimeOffset? ShippedAt { get; set; }

    /// <summary>Server tarafı son değişiklik zamanı — reverse-sync cursor
    /// için kullanılır (Payment ile aynı pattern).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    // ── Yayın ve etiket senkronu (spec 2026-10-13 §3.1, §7).

    /// <summary>SQL Server rowversion — değişiklik akışının imleci (§4.4).
    /// Eşzamanlılık jetonu DEĞİL; bkz. <see cref="WpfCustomerProjection.ChangeSeq"/>.</summary>
    public long ChangeSeq { get; set; }

    /// <summary>Komutların beklenen sürümü. Kolon DEFAULT 0: S1 öncesi imaj
    /// bu kolonu tanımaz, açtığı dosya 0 doğar.</summary>
    public int Version { get; set; }

    /// <summary>Doluysa tutar bağlı siparişlerden türetilir (DM7).</summary>
    public DateTimeOffset? AmountDerivedAt { get; set; }

    /// <summary>Dosyaya sunucu komutu dokundu; açılış penceresindeki
    /// <c>shipments/sync</c> bu dosyayı değiştirmez (§7.1).</summary>
    public DateTimeOffset? CommandedAt { get; set; }

    /// <summary>Mod Off'ta dosyanın tek yazıcısı olan kopya (§3.1, §4.10).</summary>
    public Guid? OriginReplicaId { get; set; }
}
