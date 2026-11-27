namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>
/// Havalenin karar katmanındaki durumu (banka havalesi spec'i 2026-10-10 §3). <see cref="PaymentMatchStatus"/>'tan ayrıdır:
/// o, eşleştiricinin isabetidir ve değer kümesi büyümez. String saklanır, ad değişmez. Geri alma ayrı bir durum değildir;
/// satır <see cref="Unmatched"/>'e döner.
/// </summary>
public enum PaymentMatchDecision
{
    /// <summary>Karar katmanına girmemiş. Sütunun DB varsayılanı da budur: bu sütunu bilmeyen imajın INSERT'i ona
    /// düşer.</summary>
    None = 0,

    /// <summary>Dışlanan kod (ör. POS) ya da TL/TRY olmayan hareket.</summary>
    Excluded = 1,

    /// <summary>Öneri ya da aday var, otomatik kural tutmadı: insana sorulur.</summary>
    Awaiting = 2,

    /// <summary>Öneri ve aday yok, "Hayır" dendi, onay geri alındı ya da açıklaması değerlendirilmeden silindi.</summary>
    Unmatched = 3,

    /// <summary>İnsan onayladı.</summary>
    Approved = 4,

    /// <summary>Otomatik onay kuralıyla (§5) onaylandı.</summary>
    AutoApproved = 5,

    /// <summary>Onaylı bir dekonta bağlandı (§4.4).</summary>
    ClosedByReceipt = 6,

    /// <summary>Gerekçeyle "müşteri ödemesi değil" dendi (<see cref="BankDismissReason"/>).</summary>
    Dismissed = 7,
}
