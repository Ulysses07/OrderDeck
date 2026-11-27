namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>
/// Bir IBAN'ın karar durumu (banka havalesi spec'i 2026-10-10 §2.5): lisans ve özet başına tek satır. Kişiye bağ taşımaz,
/// ama özet kişisel veri sayılır: o IBAN'dan son havaleden (<see cref="LastTransferAt"/>) 12 ay sonra silinir (S4).
/// S2a'da hiçbir kod okumaz ve yazmaz.
/// </summary>
public sealed class BankIbanState
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>64 hex özet; ham IBAN yazılamaz (CHECK).</summary>
    public string IbanHash { get; set; } = "";

    /// <summary>IBAN iki farklı kişi için onaylandı. Çiftlerden biri silinse de kalkmaz.</summary>
    public bool Shared { get; set; }

    /// <summary>IBAN önerisine "Hayır" dendi ya da bu IBAN'dan gelen bir onay geri alındı. Aynı IBAN'a verilen yeni "Evet"
    /// kaldırır.</summary>
    public bool Disputed { get; set; }

    /// <summary>Bu IBAN'dan gelen son havalenin işlem tarihi (ileri tarih şimdiye kırpılır, yalnız ileri gider). Sütun
    /// şart: hareketler 180. günde silinir, son havale sonradan bulunamaz.</summary>
    public DateTimeOffset LastTransferAt { get; set; }

    /// <summary>Eşzamanlılık jetonu: "Evet" ile "Hayır" aynı satırda yarışır. SaveChanges merkezî damgalar.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
