using System.Globalization;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucuya gönderilmiş ödeme hesabı değerleri (lisans anahtarı başına bir
/// satır). <paramref name="PendingSince"/> doluysa değerler DOĞRULANMAMIŞTIR:
/// gönderim yapıldı, karşılığı görülmedi (R11-D02).
/// </summary>
public sealed record PaymentAccountSyncState(
    string LicenseKey,
    string? Iban,
    string? AccountHolder,
    DateTimeOffset SyncedAt,
    DateTimeOffset? PendingSince);

/// <summary>
/// R10-D04: "sunucuyla hiç karşılaştırılmadı" ile "boş değer başarıyla
/// gönderildi" ayrımı süreç belleğinde yaşayamaz — operatör hesabı boşaltıp
/// senkron turu gelmeden uygulamayı kapatırsa niyet kaybolur ve uzak hesap
/// dolu kalırdı. Satır yokluğu "bilinmiyor" demektir; boşaltma, satırdaki
/// null değerlerle temsil edilir (gerekçe: göç 041 başlığı).
///
/// R11-D02: üçüncü bir durum var — satır VAR ama <c>PendingSince</c> dolu:
/// gönderim yapıldı, sonucu görülmedi. Bu "hiç denenmedi" ile aynı sayılamaz;
/// yoksa yanıtı kaybolan bir gönderimden sonra operatörün boşaltma niyeti
/// sessizce atlanır (gerekçe: göç 043 başlığı).
/// </summary>
public sealed class PaymentAccountSyncStateRepository
{
    private readonly IDbConnectionFactory _factory;

    public PaymentAccountSyncStateRepository(IDbConnectionFactory factory) => _factory = factory;

    public PaymentAccountSyncState? Get(string licenseKey)
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingleOrDefault<Row>(
            "SELECT LicenseKey, Iban, AccountHolder, SyncedAt, PendingSince "
          + "FROM PaymentAccountSyncState WHERE LicenseKey = @licenseKey",
            new { licenseKey });
        if (row is null) return null;

        return new PaymentAccountSyncState(
            row.LicenseKey, row.Iban, row.AccountHolder,
            Parse(row.SyncedAt)!.Value, Parse(row.PendingSince));
    }

    /// <summary>
    /// R11-D02: gönderimden ÖNCE çağrılır — "şu değerleri yolladım, sonucunu
    /// henüz bilmiyorum". Yanıtı kaybolan bir gönderim de böylece iz bırakır;
    /// sonraki tur karşılaştırma yapmadan yeniden gönderir. Satırın önceki
    /// DOĞRULANMIŞ değerlerinin üzerine yazması sorun değil: belirsiz durumda
    /// karar zaten "gönder", karşılaştırma yapılmıyor.
    /// </summary>
    public void MarkPending(string licenseKey, string? iban, string? accountHolder,
        DateTimeOffset attemptedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            """
            INSERT INTO PaymentAccountSyncState (LicenseKey, Iban, AccountHolder, SyncedAt, PendingSince)
            VALUES (@licenseKey, @iban, @accountHolder, @attemptedAt, @attemptedAt)
            ON CONFLICT (LicenseKey) DO UPDATE SET
                Iban = excluded.Iban,
                AccountHolder = excluded.AccountHolder,
                PendingSince = COALESCE(PaymentAccountSyncState.PendingSince, excluded.PendingSince)
            """,
            new { licenseKey, iban, accountHolder, attemptedAt = attemptedAt.ToString("O") });
    }

    /// <summary>Gönderim doğrulandı: değerler kesinleşir, belirsizlik kalkar.</summary>
    public void Upsert(string licenseKey, string? iban, string? accountHolder,
        DateTimeOffset syncedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            """
            INSERT INTO PaymentAccountSyncState (LicenseKey, Iban, AccountHolder, SyncedAt, PendingSince)
            VALUES (@licenseKey, @iban, @accountHolder, @syncedAt, NULL)
            ON CONFLICT (LicenseKey) DO UPDATE SET
                Iban = excluded.Iban,
                AccountHolder = excluded.AccountHolder,
                SyncedAt = excluded.SyncedAt,
                PendingSince = NULL
            """,
            new { licenseKey, iban, accountHolder, syncedAt = syncedAt.ToString("O") });
    }

    private static DateTimeOffset? Parse(string? value) =>
        value is null ? null
        : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private sealed record Row(string LicenseKey, string? Iban, string? AccountHolder,
        string SyncedAt, string? PendingSince);
}
