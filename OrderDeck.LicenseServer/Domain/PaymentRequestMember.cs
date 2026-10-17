namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ödeme isteği kaydının bir üyesi — spec 2026-10-13 §3.1 (S3), §6.4-d/e. Canlı kira ve önceki istek aramaları
/// PersonKey'e ek olarak üye kümesi kesişimiyle de yapılır: grup senkronu gecikse bile iki bilgisayar aynı kişiyi
/// bulur. <see cref="LicenseId"/> ve <see cref="ScopeKey"/> kayıttan kopyalanır ki kesişim indeksi
/// <c>(LicenseId, WpfCustomerId, ScopeKey)</c> kayıt tablosuna gitmeden çalışsın.
///
/// <para>Licenses'a FK yoktur (kayıt üzerinden cascade; SQL Server çoklu cascade yolunu reddeder).</para>
/// </summary>
public sealed class PaymentRequestMember
{
    public Guid RecordId { get; set; }
    public PaymentRequestRecord Record { get; set; } = null!;

    /// <summary>Üyenin WPF Id'si: GUID'li üyede asıl kayda çözülmüş "N" hex, GUID'siz üyede ham yerel Id.</summary>
    public string WpfCustomerId { get; set; } = "";

    public Guid LicenseId { get; set; }

    /// <summary>Kaydın <see cref="PaymentRequestRecord.ScopeKey"/>'i.</summary>
    public string ScopeKey { get; set; } = "";
}
