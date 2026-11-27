namespace OrderDeck.LicenseServer.Domain;

/// <summary>Beklentiyi neyin kapattığı (banka havalesi spec'i 2026-10-10 §2.2). String saklanır, ad değişmez; tablonun
/// CHECK'i de <c>Payment</c> adına bakar.</summary>
public enum PaymentAllocationKind
{
    /// <summary>Ödeme. <see cref="PaymentExpectationAllocation.PaymentId"/> yalnız bunda doludur.</summary>
    Payment = 0,

    /// <summary>"Kalanı sil" (S5).</summary>
    Waived = 1,

    /// <summary>"İptal" (S5).</summary>
    Cancelled = 2,

    /// <summary>Banka dışı kapanış: Elden, Papara (S5).</summary>
    OffBank = 3,
}

/// <summary>
/// Bir beklentinin, yani ödeme isteği kaydının kapatılan tutarı (banka havalesi spec'i 2026-10-10 §2.2). Bir ödeme birden
/// çok beklentiyi, bir beklenti birden çok ödemeyi kapatabilir. Geri alma satırı silmez, <see cref="ReversedAt"/> yazar.
///
/// <para>Tek FK ödemeyedir (kaskad). Licenses'a FK yoktur: ikinci kaskad yolunu SQL Server reddeder (1785). Ödemesiz satır
/// bu yüzden lisans silmesinde kaskadla gitmez, onu yayıncı silmesi açıkça siler (S4). S2a'da hiçbir kod okumaz ve
/// yazmaz.</para>
/// </summary>
public sealed class PaymentExpectationAllocation
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public Guid? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    /// <summary>Kapatılan istek (<see cref="PaymentRequestRecord.Id"/>), FK'sız.</summary>
    public Guid ExpectationId { get; set; }

    public PaymentAllocationKind Kind { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
}
