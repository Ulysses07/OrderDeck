using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// Komutların işlem kapsamlı uygulama kilidi (<c>sp_getapplock</c>, spec 2026-10-13 §4.6 adım 5). Satır jetonunun
/// koruyamadığı yarışlar için: henüz var olmayan bir satırı iki komutun birden açması (aynı müşteriye eşzamanlı iki
/// <c>attach-shipment</c> → tek dosya, §10.1-6) ya da bir kümeyi seçip ona göre yazan iki komut. Kilit isteğin
/// işlemine aittir (<c>@LockOwner = 'Transaction'</c>): commit ya da geri alma onu bırakır; elle bırakma yok, istisnada
/// sızmaz.
///
/// <para><b>Zaman aşımı 409 <c>stale</c>.</b> Kilidi bekleyen komut <see cref="DefaultTimeout"/> içinde alamazsa (ya da
/// kilitlenme kurbanı seçilirse) hiçbir şey yazmadan reddedilir; istemci güncel satırları uygulayıp yeniden dener.
/// Sınırsız beklemek isteği ve bağlantıyı tutardı.</para>
///
/// <para>InMemory'de (testler) işlem ve kilit yok: yardımcı hiçbir şey yapmaz. Kilidin etkisi gerçek SQL Server'da
/// sınanır.</para>
/// </summary>
public static class SalesCommandLock
{
    /// <summary>Kilidi bekleme sınırı.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary><c>sp_getapplock</c>'un <c>@Resource</c> sınırı (nvarchar(255)).</summary>
    public const int MaxResourceLength = 255;

    /// <summary>
    /// Bir yayının kilit adı: o yayında bir kodun planını kurup ona göre fiyat yazan komutlar (<c>set-code-price</c>)
    /// bunu alır. Anahtar koda değil yayına bağlıdır: kod harf duyarsız eşleşir (<c>LabelRules.SameCode</c>) ve kod
    /// başına bir kilit adı ancak bir harf katlamasıyla kurulabilirdi — <c>sp_getapplock</c> adları ikili karşılaştırır,
    /// iki yazımı aynı ürün sayan katlama kilitte ayrı ad üretmemeli. Birden çok yayın kilidi alan komut sıralı alır
    /// (<see cref="Guid"/> sırası), iki komut birbirini kilitlenmeye sokmaz.
    /// </summary>
    public static string SessionResource(Guid licenseId, Guid sessionId) =>
        $"sales-session:{licenseId:N}:{sessionId:N}";

    /// <summary>
    /// Bir müşterinin kargo dosyalarının kilit adı (§4.6 <c>ship:{lisans}:{asıl müşteri}</c>): müşterinin havuzunu
    /// okuyup ona göre dosya açan ya da karar veren komutlar (<c>attach-shipment</c>, <c>shipment-decision</c>) bunu
    /// alır. Müşteri asıl kaydın Id'sidir (<c>CustomerIdResolver</c>); GUID'siz yerel Id ham değeriyle girer
    /// (<c>Orders.CustomerId</c> en çok 64 karakter, ad sınırın altında kalır).
    /// </summary>
    public static string ShipmentResource(Guid licenseId, string customerId) =>
        $"ship:{licenseId:N}:{customerId}";

    /// <summary>
    /// Kilidi açık işlemde alır. İşlem yoksa (ilişkisel sağlayıcıda) programlama hatasıdır: kilit bir şeyi korumazdı.
    /// </summary>
    /// <exception cref="SalesCommandRejection">Zaman aşımı, iptal ya da kilitlenme: 409 <c>stale</c>.</exception>
    public static async Task AcquireAsync(
        LicenseDbContext db, string resource, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        if (resource.Length > MaxResourceLength)
            throw new ArgumentException($"Kilit adı en çok {MaxResourceLength} karakter.", nameof(resource));
        if (!db.Database.IsRelational()) return;

        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Komut kilidi yalnız açık bir işlem içinde alınır.");

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = @timeout;
            SELECT @result;
            """;
        var resourceParameter = command.CreateParameter();
        resourceParameter.ParameterName = "@resource";
        resourceParameter.DbType = DbType.String;
        resourceParameter.Size = MaxResourceLength;
        resourceParameter.Value = resource;
        command.Parameters.Add(resourceParameter);
        var timeoutParameter = command.CreateParameter();
        timeoutParameter.ParameterName = "@timeout";
        timeoutParameter.DbType = DbType.Int32;
        timeoutParameter.Value = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        command.Parameters.Add(timeoutParameter);

        var raw = await command.ExecuteScalarAsync(ct);
        if (raw is not int code)
            throw new InvalidOperationException(
                "sp_getapplock geçersiz bir değer döndü: " + Convert.ToString(raw, CultureInfo.InvariantCulture));

        // 0: hemen, 1: bekledikten sonra verildi. -1 zaman aşımı, -2 iptal, -3 kilitlenme kurbanı: yarış, yeniden
        // denenebilir. -999: parametre ya da çağrı hatası.
        switch (code)
        {
            case >= 0:
                return;
            case -1 or -2 or -3:
                throw SalesCommandRejection.Stale(
                    "Aynı kayıtlar üzerinde başka bir işlem sürüyor; güncel satırlarla yeniden deneyin.");
            default:
                throw new InvalidOperationException(
                    "sp_getapplock başarısız: " + code.ToString(CultureInfo.InvariantCulture));
        }
    }
}
