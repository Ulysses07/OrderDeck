namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Giden WhatsApp gönderimlerinin tekrarını engelleyen rezervasyon satırı.
///
/// <para><b>Neden var:</b> HttpClient dayanıklılık katmanı 5xx/ağ hatasında
/// POST'u da yeniden deniyor; gönderim idempotent değil ve gerçek para /
/// gerçek müşteri demek. Operatörün tek tıkı, kimse farkına varmadan 2-3
/// faturalı mesaja dönüşebilir.</para>
///
/// <para><b>Nasıl çalışır:</b> satır Graph çağrısından <b>ÖNCE</b> yazılır,
/// sonuç tamamlanınca aynı satıra işlenir — böylece hem tamamlanmış (sonucu
/// aynen tekrar döneriz) hem de hâlâ uçuşta olan tekrarlar yakalanır.</para>
/// </summary>
public sealed class WaSendAttempt
{
    /// <summary>İstemcinin ürettiği idempotency anahtarı — PK.</summary>
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License? License { get; set; }

    /// <summary>"pending" (rezerve edildi, sonuç yok) | "done" (sonuç işlendi).</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Tamamlanmış gönderimin sonucu; "pending" iken null.</summary>
    public bool? Ok { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Yazılan <see cref="WaMessage"/> satırının id'si — tekrar
    /// isteğe birebir aynı yanıtı dönebilmek için saklanır.</summary>
    public Guid? MessageId { get; set; }

    /// <summary>Rezervasyonun yazıldığı an. Bayat "pending" tespiti bununla yapılır.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
