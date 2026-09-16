using System.Globalization;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucuya en son BAŞARIYLA gönderilmiş ödeme hesabı değerleri
/// (lisans anahtarı başına bir satır).
/// </summary>
public sealed record PaymentAccountSyncState(
    string LicenseKey,
    string? Iban,
    string? AccountHolder,
    DateTimeOffset SyncedAt);

/// <summary>
/// R10-D04: "sunucuyla hiç karşılaştırılmadı" ile "boş değer başarıyla
/// gönderildi" ayrımı süreç belleğinde yaşayamaz — operatör hesabı boşaltıp
/// senkron turu gelmeden uygulamayı kapatırsa niyet kaybolur ve uzak hesap
/// dolu kalırdı. Satır yokluğu "bilinmiyor" demektir; boşaltma, satırdaki
/// null değerlerle temsil edilir (gerekçe: göç 041 başlığı).
/// </summary>
public sealed class PaymentAccountSyncStateRepository
{
    private readonly IDbConnectionFactory _factory;

    public PaymentAccountSyncStateRepository(IDbConnectionFactory factory) => _factory = factory;

    public PaymentAccountSyncState? Get(string licenseKey)
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingleOrDefault<Row>(
            "SELECT LicenseKey, Iban, AccountHolder, SyncedAt "
          + "FROM PaymentAccountSyncState WHERE LicenseKey = @licenseKey",
            new { licenseKey });
        if (row is null) return null;

        return new PaymentAccountSyncState(
            row.LicenseKey, row.Iban, row.AccountHolder,
            DateTimeOffset.Parse(row.SyncedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

    public void Upsert(string licenseKey, string? iban, string? accountHolder,
        DateTimeOffset syncedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            """
            INSERT INTO PaymentAccountSyncState (LicenseKey, Iban, AccountHolder, SyncedAt)
            VALUES (@licenseKey, @iban, @accountHolder, @syncedAt)
            ON CONFLICT (LicenseKey) DO UPDATE SET
                Iban = excluded.Iban,
                AccountHolder = excluded.AccountHolder,
                SyncedAt = excluded.SyncedAt
            """,
            new { licenseKey, iban, accountHolder, syncedAt = syncedAt.ToString("O") });
    }

    private sealed record Row(string LicenseKey, string? Iban, string? AccountHolder,
        string SyncedAt);
}
