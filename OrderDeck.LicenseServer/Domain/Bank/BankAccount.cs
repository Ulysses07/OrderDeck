namespace OrderDeck.LicenseServer.Domain.Bank;

public sealed class BankAccount
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
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
}
