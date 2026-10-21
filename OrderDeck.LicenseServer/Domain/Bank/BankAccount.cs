namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>Obifin'deki banka hesabının yerel görüntüsü; Obifin hesap listesinden tazelenir
/// (<see cref="RefreshedAt"/>). IBAN yalnız hash + maske (spec §7).</summary>
public sealed class BankAccount
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public Guid? BankConnectionId { get; set; }
    public long ObifinAccountId { get; set; }
    public string BankaKodu { get; set; } = "";
    public string IbanMasked { get; set; } = "";
    public string? IbanHash { get; set; }
    public string Currency { get; set; } = "TL";
    public bool Active { get; set; }
    /// <summary>Obifin `GuncellemeTarihi` — bankadan son çekim; gecikme ölçümü.</summary>
    public DateTimeOffset? LastBankSyncAt { get; set; }
    public string? NotificationNote { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
    // KVKK Ö-7: bu hesaba yazılan gelen hareketlerde karşı IBAN'ın niteliği (değer yazılmaz). Maskeli sayısı sıfır değilse o
    // bankanın IBAN'ları özetlenemiyor demektir; Faz 2'deki "tek kişiye bağlı IBAN" koşulu o bankada kurulamaz.
    public int IbanFullCount { get; set; }
    public int IbanMaskedCount { get; set; }
    public int IbanEmptyCount { get; set; }
}
