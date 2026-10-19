namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ortak bakiye denetiminin taban çizgisi (spec 2026-10-11 §3.1, §7.3, §8.1): S1 öncesinden kalan eksi satırlar,
/// D1 bozulmaları (<c>Balance ≠ Σ Amount</c>) ve kopya satırlar, yakalandıkları andaki <see cref="Balance"/> ve
/// <see cref="LedgerSum"/> ile. Günlük denetim yalnız tabana göre YENİ sapmada uyarır. Yalnız
/// <c>person-balance verify --capture-baseline --apply</c> ve <c>--mark-reviewed --apply</c> yazar.
///
/// <para><see cref="WpfCustomerId"/> = <see cref="Guid.Empty"/> satırı lisansın "taban alındı" işaretidir: yakalama
/// anında hiç sapması olmayan lisans da işaret alır, uyarılar onun için de açılır (plan "Spec ↔ kod farkları" 7).
/// Projeksiyona FK yok — işaret satırı ve silinen projeksiyonlar için.</para>
/// </summary>
public sealed class PersonBalanceBaseline
{
    public Guid LicenseId { get; set; }

    /// <summary>Satırın asıl ya da kopya kaydı; <see cref="Guid.Empty"/> = lisans işareti.</summary>
    public Guid WpfCustomerId { get; set; }

    /// <summary>Yakalama anındaki <c>CustomerBalance.Balance</c> (satır yoksa 0).</summary>
    public decimal Balance { get; set; }

    /// <summary>Yakalama anında satıra bağlı hareketlerin toplamı.</summary>
    public decimal LedgerSum { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Sahip satırı inceledi ("düzelt" ya da "olduğu gibi kalsın"); S2'nin <c>enable</c>'ı hepsini dolu ister.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }
}
