namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF tarafindan kullanilan customer balance DTO'lari.</summary>
public sealed record CustomerBalancePreview(
    Guid WpfCustomerId,
    decimal Balance,
    DateTimeOffset UpdatedAt);

/// <summary>Bakiye düşüm isteği. <paramref name="IdempotencyKey"/> çağrı başına
/// bir kez üretilir: dayanıklılık katmanı bu POST'u yeniden denediğinde gövde
/// (dolayısıyla anahtar) aynı kalır ve sunucu bakiyeyi ikinci kez düşürmez.
/// Anahtar aynı zamanda oluşan ledger satırının kimliğidir.
///
/// <para>R4-03: <paramref name="SaleScope"/> satışın <b>kalıcı</b> kimliği —
/// işin <c>ScopeKey</c>'i. Idempotency anahtarı yerel diskte yaşıyor ve yedek
/// geri yüklenince kayboluyor; kapsam sunucuda kaldığından düşüm sonradan
/// yine tanınabiliyor.</para>
///
/// <para>Ortak bakiye: <paramref name="MemberIds"/> kişinin GUID'li üyeleri
/// (spec 2026-10-11 §6.1-a). null ile boş aynı; havuz kipinde zorunlu
/// (400 <c>member-ids-required</c>), satır kipinde okunmaz. Gövde alanı
/// <c>memberIds</c>; verilmezse <c>"memberIds": null</c> yazılır — sunucu onu
/// yok ile aynı sayar, alanı tanımayan eski sunucu atlar.</para></summary>
public sealed record CustomerBalanceApplyRequest(
    Guid WpfCustomerId,
    decimal Amount,
    decimal ProductTotal,
    Guid? IdempotencyKey = null,
    string? SaleScope = null,
    IReadOnlyList<Guid>? MemberIds = null);

public sealed record CustomerBalanceApplyResponse(
    Guid TransactionId,
    decimal AppliedAmount,
    decimal RemainingBalance);

/// <summary>R4-03: bir kapsamda sunucuda duran, geri alınmamış düşüm. Yerel
/// anahtar kaybolmuş olsa bile satış bununla tanınır.</summary>
/// <param name="PersonPool">Lisans havuz kipinde mi (spec 2026-10-11 §4.3);
/// birleştirme notu (§6.1-d) kullanır. Eski sunucu alanı göndermez → false.</param>
public sealed record CustomerBalanceScope(
    Guid TransactionId,
    decimal AppliedAmount,
    decimal ProductTotal,
    DateTimeOffset CreatedAt,
    bool PersonPool = false);

/// <summary>R6-02: kayıtlı bir düşümün sunucudaki güncel durumu — tekrar
/// paylaşımdan önce "bu düşüm hâlâ geçerli mi?" sorusunun cevabı. Kapsam
/// (SaleScope) sorgusundan farkı: işlem kimliğiyle sorulur, bu yüzden
/// kapsamsız yazılmış eski (033 dönemi) satırlar için de doğru cevap verir.</summary>
public sealed record CustomerBalanceTransactionStatus(
    Guid TransactionId,
    decimal AppliedAmount,
    bool Reversed);

// Panel endpoint'leri ile uyumlu DTO'lar (WPF'in /api/panel/customers/{id}/balance
// kullanması için).
public sealed record CustomerBalanceDto(
    Guid WpfCustomerId,
    Guid LicenseId,
    decimal Balance,
    DateTimeOffset UpdatedAt);

public sealed record CustomerBalanceTransactionDto(
    Guid Id,
    decimal Amount,
    string Kind,
    decimal? OriginalAmount,
    decimal? ShippingDeducted,
    string? Reason,
    Guid? ReversesTransactionId,
    DateTimeOffset CreatedAt);

/// <summary>Panel GET'in kişi üyesi (spec 2026-10-11 §4.6): WPF'in Ayır onayı
/// kullanır. İki kipte de S(a) — silinmişler dahil, <c>WpfCustomerId</c>'nin "N"
/// biçiminin ordinal sırasıyla; bakiye satırı olmayan üyede 0.</summary>
public sealed record CustomerBalanceMemberDto(
    Guid WpfCustomerId,
    decimal Balance,
    bool Purged);

/// <param name="Members">Kişinin üyeleri ve satır bakiyeleri (§4.6). Sunucu
/// S1'den beri gönderir; alanın yokluğu (eski sunucu) <c>null</c> — boş diziyle
/// karışmaz.</param>
public sealed record CustomerBalanceDetailsResponse(
    CustomerBalanceDto Balance,
    CustomerBalanceTransactionDto[] Transactions,
    CustomerBalanceMemberDto[]? Members = null);

public sealed record RefundFullRequest(decimal Amount, string? Reason);

public sealed record RefundNetRequest(
    decimal OriginalAmount,
    decimal ShippingDeducted,
    string? Reason);
