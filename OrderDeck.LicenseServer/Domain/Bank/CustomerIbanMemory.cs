namespace OrderDeck.LicenseServer.Domain.Bank;

public enum IbanMemorySource { ManualMatch = 0, HumanApproval = 1 }

/// <summary>Müşteri ↔ gönderen IBAN hafızası. Geri alma satırı SİLER (spec §3): yanlış öğrenip
/// sessizce tekrarlamayı önlemek için aktif tabloda iptal bayrağı tutulmaz.</summary>
public sealed class CustomerIbanMemory
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public Guid WpfCustomerId { get; set; }
    public string IbanHash { get; set; } = "";
    public string IbanMasked { get; set; } = "";
    public IbanMemorySource LearnedFrom { get; set; }
    public Guid? SourceBankTransactionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
