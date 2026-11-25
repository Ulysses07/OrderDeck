namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>
/// Havale kararının denetim satırı (banka havalesi spec'i 2026-10-10 §2.6, §11.1): yalnız eklenir. Ad, IBAN, açıklama ve
/// not YAZILMAZ; <see cref="DetailsJson"/> yalnız Id, sürüm, tutar, <c>memoryId</c> ve <c>LearnedFrom</c> taşır. Tek FK
/// lisansadır (kaskad: lisans silinince yetim kalmasın); öbür bağlar FK'SIZ Guid'dir, hareket 180. günde silinir, olay
/// kalır. S2a'da hiçbir kod okumaz ve yazmaz.
/// </summary>
public sealed class BankDecisionEvent
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Banka hareketi (<see cref="BankTransaction"/>).</summary>
    public Guid? TxId { get; set; }

    /// <summary>Beklenti, yani ödeme isteği kaydı (<see cref="PaymentRequestRecord"/>).</summary>
    public Guid? ExpectationId { get; set; }

    public Guid? PaymentId { get; set; }

    /// <summary>"Aynı ödeme" bağının öbür ucu (spec §4.4).</summary>
    public Guid? RelatedPaymentId { get; set; }

    /// <summary>Kargo adımı (spec §4.6).</summary>
    public Guid? StepId { get; set; }

    public BankDecisionActorKind ActorKind { get; set; }

    /// <summary>Personel kararında personel (<see cref="OperatorUser"/>).</summary>
    public Guid? OperatorId { get; set; }

    public BankDecisionSurface Surface { get; set; }

    /// <summary>Olayın adı. Enum değil dize: geri alınan imaj bilmediği adı düşmeden okur. S2a ad tanımlamaz, her PR kendi
    /// sabitini ekler.</summary>
    public string Action { get; set; } = "";

    /// <summary>Gerekçe kodu (ör. <see cref="BankDismissReason"/> ya da <see cref="BankRevertReason"/> adı).</summary>
    public string? ReasonCode { get; set; }

    /// <summary>İstemcinin ürettiği istek kimliği (spec §3): yinelenen istek aynı sonucu alır. Tekil değildir, hakem satır
    /// jetonudur; bir istek birden çok olay yazabilir.</summary>
    public Guid? RequestId { get; set; }

    public int RuleVersion { get; set; }

    /// <summary>İlk değerlendirmede: bayrak dışındaki bütün otomatik onay koşulları tuttu mu (ölçüm, spec §11.4).</summary>
    public bool? WouldAutoApprove { get; set; }

    /// <summary>İlk değerlendirmede: varsayılan seçimin sipariş kümesi kargoya sunucuda bağlanabilir mi (ölçüm, spec
    /// §11.4).</summary>
    public bool? OrderSetReliable { get; set; }

    /// <summary>Müşteri (<see cref="WpfCustomerProjection"/>); ilk değerlendirmede kuralın seçeceği.</summary>
    public Guid? WpfCustomerId { get; set; }

    /// <summary>Tutmayan koşullar, <see cref="BankAskReasons"/> biçiminde.</summary>
    public string? AskReasons { get; set; }

    public string? DetailsJson { get; set; }
}
