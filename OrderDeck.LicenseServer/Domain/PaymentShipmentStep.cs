namespace OrderDeck.LicenseServer.Domain;

/// <summary>Kargo adımının hâli (banka havalesi spec'i 2026-10-10 §4.6). String saklanır, ad değişmez; tablonun indeks
/// süzgeçleri de bu adlara bakar.</summary>
public enum PaymentShipmentStepState
{
    /// <summary>Koşucunun bağlamasını bekliyor.</summary>
    Waiting = 0,

    /// <summary>Bağlanmadan önce karar bekliyor.</summary>
    NeedsDecision = 1,

    /// <summary>Bitti.</summary>
    Bound = 2,

    /// <summary>Bağlandı, karar bekliyor.</summary>
    BoundNeedsDecision = 3,

    /// <summary>Onay geri alındı; koşucu adımın kurduğu bağı çözecek.</summary>
    Reverting = 4,

    /// <summary>Geri alma bitti.</summary>
    Closed = 5,
}

/// <summary>Adımın neden beklediği (banka havalesi spec'i 2026-10-10 §4.6.1). String saklanır, ad değişmez.</summary>
public enum PaymentShipmentStepReason
{
    /// <summary>Siparişin yayını devredilmedi; bağ devirde kurulur.</summary>
    SessionLive = 0,

    /// <summary>Satış senkronu kapalı; iş masaüstünde "Elle gir" ile yapılır.</summary>
    ModeOff = 1,

    /// <summary>Tutar seçilen isteklerin kalanından küçük.</summary>
    Shortage = 2,

    /// <summary>Kişi alıcı ödemeli.</summary>
    RecipientPays = 3,

    /// <summary>Hangi siparişlerin ödendiği bilinmiyor.</summary>
    OrderSetUnknown = 4,

    /// <summary>Sipariş kümesi istekten ya da onaydan sonra değişti.</summary>
    OrdersChanged = 5,

    /// <summary>Kümede bağlanabilecek sipariş kalmadı.</summary>
    NothingToBind = 6,

    /// <summary>Bağlandıktan sonra müşteri satırının havuzu kargo eşiğine ulaştı.</summary>
    Threshold = 7,

    /// <summary>Deneme sınırı aşıldı; saatte bir yeniden denenir.</summary>
    RetryExhausted = 8,
}

/// <summary>Masaüstünün kargo kararı (<c>decide</c>; banka havalesi spec'i 2026-10-10 §4.6.2). String saklanır, ad
/// değişmez.</summary>
public enum PaymentShipmentStepDecision
{
    /// <summary><c>normal</c></summary>
    Normal = 0,

    /// <summary><c>hold</c></summary>
    Hold = 1,

    /// <summary><c>ship-now</c></summary>
    ShipNow = 2,

    /// <summary><c>recipient-pays</c></summary>
    RecipientPays = 3,
}

/// <summary>
/// Onaylanan ödemenin siparişlerini kargo dosyasına bağlayan kalıcı adım: ödeme × müşteri satırı başına bir (banka havalesi
/// spec'i 2026-10-10 §4.6). Onayla aynı SaveChanges'te yazılır, bağı commit'ten sonra koşucu kurar. Geri alınan dekont aynı
/// ödemeyle yeniden onaylanabilir; tekillik bu yüzden yalnız kapanmamış ve çözülmeyen adımdadır.
///
/// <para>Tek FK ödemeyedir (kaskad); Licenses'a FK yoktur (ikinci kaskad yolunu SQL Server reddeder). S2a'da hiçbir kod
/// okumaz ve yazmaz.</para>
/// </summary>
public sealed class PaymentShipmentStep
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;
    public Guid LicenseId { get; set; }

    /// <summary>Müşteri satırı, <see cref="Order.CustomerId"/> biçiminde (GUID'siz yerel müşteri düşmesin). BIN2:
    /// karşılaştırma ordinal.</summary>
    public string CustomerKey { get; set; } = "";

    public PaymentShipmentStepState State { get; set; }
    public PaymentShipmentStepReason? Reason { get; set; }

    /// <summary>Açılacak dosya için önerilen Id (<c>attach-shipment</c> komutundaki gibi), FK'sız.</summary>
    public Guid? ProposedShipmentId { get; set; }

    /// <summary>Yayın devredilmeden verilmiş karar: koşucu devirde uygular, yeniden sormaz.</summary>
    public PaymentShipmentStepDecision? Decision { get; set; }

    /// <summary>Son karar isteğinin kimliği: yinelenen istek aynı sonucu alır.</summary>
    public Guid? LastRequestId { get; set; }

    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Eşzamanlılık jetonu: her bağlama, çözme ve karar bir artırır; geri alınmış ödeme bağlı kalamaz.</summary>
    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><see cref="State"/>'in son değiştiği an. Bir günü geçen adımın uyarısı bununla hesaplanır (spec §14):
    /// <see cref="NextAttemptAt"/> saatte bir ilerler.</summary>
    public DateTimeOffset StateChangedAt { get; set; }

    public List<PaymentShipmentStepOrder> Orders { get; set; } = [];
}

/// <summary>
/// Adımın onay anında donmuş bir siparişi (banka havalesi spec'i 2026-10-10 §4.6.3). Orders'a FK yoktur, gerekçe
/// <see cref="PaymentRequestRecordOrder"/> ile aynı.
/// </summary>
public sealed class PaymentShipmentStepOrder
{
    public Guid StepId { get; set; }
    public PaymentShipmentStep Step { get; set; } = null!;

    /// <summary>Siparişin (etiketin) Id'si.</summary>
    public Guid OrderId { get; set; }

    public Guid LicenseId { get; set; }

    /// <summary>Siparişi kümeye getiren istek kaydı (<see cref="PaymentRequestRecord"/>), FK'sız.</summary>
    public Guid RecordId { get; set; }

    /// <summary>Onay anındaki fiyat: sonradan düzeltilen siparişin ödendiği fiyat budur.</summary>
    public decimal PriceAtApproval { get; set; }

    /// <summary>Adımın kurduğu bağın dosyası. Geri alma yalnız adımın kurduğu bağı çözer: siparişin bağı bununla ve
    /// <see cref="BoundSetAt"/> ile aynıysa.</summary>
    public Guid? BoundShipmentId { get; set; }

    public DateTimeOffset? BoundSetAt { get; set; }
}
