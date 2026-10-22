namespace OrderDeck.LicenseServer.Domain.Bank;

public enum BankTransactionDirection { Incoming = 0, Outgoing = 1 }

/// <summary>Obifin'den çekilen tek hareket. (LicenseId, ObifinId) tekil → idempotent yazım.
/// Karşı IBAN yalnız özet + maske (spec §7); karşı tarafın VKN/TCKN'si yazılmaz (KVKK Ö-2); ham JSON ve açıklama süreli (§5).</summary>
public sealed class BankTransaction
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public long ObifinId { get; set; }
    public Guid? BankAccountId { get; set; }
    public long ObifinAccountId { get; set; }
    public string BankaKodu { get; set; } = "";
    public BankTransactionDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TL";
    public DateTimeOffset OccurredAt { get; set; }
    public string? Description { get; set; }
    public string? TransactionCode { get; set; }
    public string? CommonType { get; set; }
    public string? BankReference { get; set; }
    public string? CounterpartyIbanHash { get; set; }
    public string? CounterpartyIbanMasked { get; set; }
    /// <summary>Özet mod-97 geçerli bir IBAN'dan üretildi (Görev 11 sonrası çekim). Yalnız bu hareketler IBAN hafızası öğretir
    /// (<c>PaymentMatchReconciler.LearnIbanAsync</c>, KVKK Ö-7): öncesinde çekilen hareketin özeti maskeli IBAN'dan üretilmiş
    /// olabilir. Göç eski satırlarda false bırakır.</summary>
    public bool CounterpartyIbanHashValid { get; set; }
    public string? CounterpartyName { get; set; }
    /// <summary>YAZILMAZ (KVKK Ö-2, 2026-10). Sütun genişlet/daralt kuralıyla bir sonraki sürümde düşürülür; eski değerleri
    /// <c>bank-data-cleanup</c> boşaltır. Yeni kod bu alanı okumamalı.</summary>
    public string? CounterpartyTaxIdHash { get; set; }
    public string? RawJson { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset? DescriptionPurgedAt { get; set; }
}
