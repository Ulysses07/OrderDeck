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

    /// <summary>Bu bağı doğrulayan en yeni havalenin İŞLEM tarihi (elle eşleme ya da dekont onayıyla doğrulanmış; ileri tarih
    /// şimdiye kırpılır). Doğrulama anı süreyi uzatmaz; eski bir havalenin geç doğrulanması saklamanın başlangıcını geri
    /// çekmez. Saklama: bundan (yoksa <see cref="CreatedAt"/>'ten — P1 öncesi satırlar) 12 ay (KVKK K-6; politika "son
    /// doğrulanmış ödemeden 12 ay", <c>BankDataRetentionJob</c>).</summary>
    public DateTimeOffset? LastConfirmedAt { get; set; }
}
