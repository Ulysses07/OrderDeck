using System.Globalization;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucuya gönderilmiş ödeme hesabı değerleri (lisans anahtarı başına bir
/// satır). <paramref name="PendingSince"/> doluysa değerler DOĞRULANMAMIŞTIR:
/// gönderim yapıldı, karşılığı görülmedi (R11-D02).
///
/// <paramref name="InstallationId"/> satırı YAZAN ayar dosyasının kimliğidir
/// (R12-D03). Satır yedekle taşınır, ayar dosyası taşınmaz; damga tutmuyorsa
/// satır bu ayar dosyası için karşılaştırma tabanı sayılamaz. NULL = damgasız
/// (göç 044 öncesi yazılmış).
/// </summary>
public sealed record PaymentAccountSyncState(
    string LicenseKey,
    string? Iban,
    string? AccountHolder,
    DateTimeOffset SyncedAt,
    DateTimeOffset? PendingSince,
    string? InstallationId);

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
            "SELECT LicenseKey, Iban, AccountHolder, SyncedAt, PendingSince, InstallationId "
          + "FROM PaymentAccountSyncState WHERE LicenseKey = @licenseKey",
            new { licenseKey });
        if (row is null) return null;

        return new PaymentAccountSyncState(
            row.LicenseKey, row.Iban, row.AccountHolder,
            Parse(row.SyncedAt)!.Value, Parse(row.PendingSince), row.InstallationId);
    }

    /// <summary>
    /// R11-D02: gönderimden ÖNCE çağrılır — "şu değerleri yolladım, sonucunu
    /// henüz bilmiyorum". Yanıtı kaybolan bir gönderim de böylece iz bırakır;
    /// sonraki tur karşılaştırma yapmadan yeniden gönderir. Satırın önceki
    /// DOĞRULANMIŞ değerlerinin üzerine yazması sorun değil: belirsiz durumda
    /// karar zaten "gönder", karşılaştırma yapılmıyor.
    /// </summary>
    public void MarkPending(string licenseKey, string? iban, string? accountHolder,
        DateTimeOffset attemptedAt, string installationId)
    {
        using var conn = _factory.Open();
        conn.Execute(
            """
            INSERT INTO PaymentAccountSyncState
                (LicenseKey, Iban, AccountHolder, SyncedAt, PendingSince, InstallationId)
            VALUES (@licenseKey, @iban, @accountHolder, @attemptedAt, @attemptedAt, @installationId)
            ON CONFLICT (LicenseKey) DO UPDATE SET
                Iban = excluded.Iban,
                AccountHolder = excluded.AccountHolder,
                PendingSince = COALESCE(PaymentAccountSyncState.PendingSince, excluded.PendingSince),
                InstallationId = excluded.InstallationId
            """,
            new { licenseKey, iban, accountHolder, attemptedAt = attemptedAt.ToString("O"), installationId });
    }

    /// <summary>Gönderim doğrulandı: değerler kesinleşir, belirsizlik kalkar.</summary>
    public void Upsert(string licenseKey, string? iban, string? accountHolder,
        DateTimeOffset syncedAt, string installationId)
    {
        using var conn = _factory.Open();
        conn.Execute(
            """
            INSERT INTO PaymentAccountSyncState
                (LicenseKey, Iban, AccountHolder, SyncedAt, PendingSince, InstallationId)
            VALUES (@licenseKey, @iban, @accountHolder, @syncedAt, NULL, @installationId)
            ON CONFLICT (LicenseKey) DO UPDATE SET
                Iban = excluded.Iban,
                AccountHolder = excluded.AccountHolder,
                SyncedAt = excluded.SyncedAt,
                PendingSince = NULL,
                InstallationId = excluded.InstallationId
            """,
            new { licenseKey, iban, accountHolder, syncedAt = syncedAt.ToString("O"), installationId });
    }

    /// <summary>
    /// R12-D03: satırın DEĞERLERİ doğru ama damgası başka bir ayar dosyasına
    /// ait (ya da hiç yok). Yerel yapılandırma satırla örtüştüğü için sunucuya
    /// gidecek bir şey yok; satır yalnız bu ayar dosyası adına sahiplenilir ki
    /// bundan SONRAKİ bilinçli boşaltma niyet sayılabilsin.
    /// </summary>
    public void Adopt(string licenseKey, string installationId)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE PaymentAccountSyncState SET InstallationId = @installationId "
          + "WHERE LicenseKey = @licenseKey",
            new { licenseKey, installationId });
    }

    private static DateTimeOffset? Parse(string? value) =>
        value is null ? null
        : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private sealed record Row(string LicenseKey, string? Iban, string? AccountHolder,
        string SyncedAt, string? PendingSince, string? InstallationId);
}
