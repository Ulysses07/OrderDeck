namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bilgisayarlar arası ödeme isteği kaydı — yayın ve etiket senkronu spec'i 2026-10-13 §3.1 (S3 satırları), §6.4.
/// Yerel <c>PaymentPersonGate</c>'in ve <c>PaymentJob</c> teslim kaydının sunucu ikizi. İki tür
/// (<see cref="PaymentRequestKind"/>): mod On'da <c>begin</c>'in açtığı <b>kira</b> ve kirasız, sonradan yazılan
/// <b>kayıt</b> (ödeme geçmişi geri doldurması, mod Off).
///
/// <para><b>DM6.</b> Kişi×kapsamda aynı anda en çok bir <see cref="PaymentRequestState.Sending"/> kayıt olur. Yarışın
/// hakemi filtreli tekil indekstir (<see cref="SendingIndexName"/>): <c>(LicenseId, PersonKeyHash, ScopeKey)</c>
/// WHERE <see cref="SendingIndexFilter"/>. İndeks metnin değil özetin üzerindedir; SQL karşılaştırması harf ve sondaki
/// boşluk farkını yutabildiği için iki ayrı kişi aksi hâlde birbirini engellerdi (§2).</para>
///
/// <para><b>KVKK (§9).</b> Kişisel metin taşımaz: yalnız Id, tutar ve makine adı. <see cref="PersonKey"/> bir Id
/// sınıflamasıdır (<c>g:</c> GroupId, <c>c:</c> asıl kayıt Id'si, <c>r:</c> GUID'siz yerel Id), ad değildir.</para>
///
/// <para>Lisans silinince kayıt, üyeleri ve siparişleri cascade ile gider. Üye ve sipariş satırlarında Licenses'a FK
/// yoktur (SQL Server çoklu cascade yolunu reddeder), Orders'a da yoktur: geçmiş sipariş sunucuda olmayabilir.</para>
/// </summary>
public sealed class PaymentRequestRecord
{
    /// <summary>DM6'nın filtreli tekil indeksinin adı. Tekil ihlal (2601) bu adla tanınır ve 500 yerine 409
    /// <c>in-progress-elsewhere</c>'e çevrilir (§6.4-h); ad bu yüzden sabittir.</summary>
    public const string SendingIndexName = "UX_PaymentRequestRecords_Sending";

    /// <summary>DM6 indeksinin süzgeci: yalnız <see cref="PaymentRequestState.Sending"/> (0) satırlar.</summary>
    public const string SendingIndexFilter = "[State] = 0";

    /// <summary><see cref="PersonKey"/>'in harmanı: bayt bayt (ordinal) karşılaştırma. Metin yalnız bellekte ordinal
    /// yeniden denetim içindir; indeks ve kilit <see cref="PersonKeyHash"/> üzerindedir.</summary>
    public const string PersonKeyCollation = "Latin1_General_100_BIN2";

    /// <summary><see cref="PersonKey"/> kolonunun uzunluğu: önek (2) + en çok 64 karakterlik GroupId ya da ham Id.</summary>
    public const int PersonKeyMaxLength = 80;

    /// <summary>Kapsam anahtarı uzunluğu; defterdeki <see cref="CustomerBalanceTransaction.SaleScope"/> ile aynı.</summary>
    public const int ScopeKeyMaxLength = CustomerBalanceTransaction.SaleScopeMaxLength;

    /// <summary>WPF müşteri Id'si uzunluğu (<see cref="Order.CustomerId"/> ile aynı).</summary>
    public const int WpfCustomerIdMaxLength = 64;

    /// <summary><see cref="MachineName"/> uzunluğu (<see cref="WpfReplica.MachineName"/> ile aynı).</summary>
    public const int MachineNameMaxLength = 128;

    /// <summary>SHA-256 özetinin bayt uzunluğu (<c>binary(32)</c>).</summary>
    public const int HashLength = 32;

    /// <summary>İstemcinin <c>recordId</c>'si; birincil anahtar. Aynı Id ve aynı içerik yeniden oynatmadır (§6.4-a).</summary>
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Sunucunun kişi anahtarı (§2): <c>g:{GroupId}</c>, <c>c:{asıl kayıt hex}</c> ya da <c>r:{ham Id}</c>.
    /// BIN2 harmanında saklanır.</summary>
    public string PersonKey { get; set; } = "";

    /// <summary><see cref="PersonKey"/>'in SHA-256 özeti; DM6 indeksinin ve kilidin anahtarı.</summary>
    public byte[] PersonKeyHash { get; set; } = [];

    /// <summary>Kapsam (<c>session:{id}</c> ya da <c>cumulative</c>). Varsayılan harmanda: SaleScope ile aynı
    /// karşılaştırma anlamı.</summary>
    public string ScopeKey { get; set; } = "";

    /// <summary>İsteğin sahip satırı: GUID'li sahipte kanonik "N" hex, GUID'siz sahipte ham yerel Id.</summary>
    public string OwnerWpfCustomerId { get; set; } = "";

    /// <summary>Kaydı yazan kopya.</summary>
    public Guid ReplicaId { get; set; }

    /// <summary>Kaydı yazan bilgisayarın adı; öbür bilgisayarın sorusunda gösterilir ("PC-A'dan istendi").</summary>
    public string? MachineName { get; set; }

    public PaymentRequestKind Kind { get; set; }

    public PaymentRequestState State { get; set; }

    /// <summary>İstemcinin ürün toplamı (<c>begin</c>). <see cref="PaymentRequestKind.Record"/> taşımaz.</summary>
    public decimal? ProductTotal { get; set; }

    /// <summary>Kargo dahil, bakiyeden önceki tutar (<c>begin</c>). <see cref="PaymentRequestKind.Record"/>
    /// taşımaz.</summary>
    public decimal? TotalBeforeBalance { get; set; }

    /// <summary>İstenen net tutar (<c>finish</c> ya da kayıt).</summary>
    public decimal? NetAmount { get; set; }

    /// <summary>Bakiyeden düşülen tutar (<c>finish</c> ya da kayıt).</summary>
    public decimal? AppliedAmount { get; set; }

    /// <summary>Tutarın tamamı bakiyeden karşılandı.</summary>
    public bool? FullyFromBalance { get; set; }

    /// <summary>Tutarın hesaplandığı lisans kargo ayarı sürümü (<see cref="LicenseShippingSettings.Version"/>).</summary>
    public int? ShippingSettingsVersion { get; set; }

    /// <summary>Kanonik isteğin SHA-256 özeti: yeniden oynatma ile <c>content-conflict</c>'i ayırır (§6.4-a).</summary>
    public byte[] RequestHash { get; set; } = [];

    /// <summary>Kaydın başladığı an (sunucu saati; geri doldurulan kayıtta istemcinin bildirdiği zaman).</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Kiranın dolduğu an (<see cref="PaymentRequestKind.Lease"/>); sonrası <c>Unconfirmed</c>.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Kaydın kapandığı an (sunucu saati); okuyucular uzak kayıtları buna göre sıralar (§6.4).</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Eşzamanlılık jetonu: her yazım bir artırır (<c>finish</c> ile süre dolumu yarışı).</summary>
    public int Version { get; set; }

    /// <summary>İsteğin bütün üyeleri (GUID'siz olanlar dahil, ham Id).</summary>
    public List<PaymentRequestMember> Members { get; set; } = [];

    /// <summary>İsteğin kapsadığı siparişler (<c>orderIds</c>).</summary>
    public List<PaymentRequestRecordOrder> RecordOrders { get; set; } = [];
}
