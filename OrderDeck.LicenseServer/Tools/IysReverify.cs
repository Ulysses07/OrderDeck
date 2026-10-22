using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll iys-reverify [--brand &lt;kod&gt;] [--apply]
///
/// <para><b>Tek seferlik onarım (2026-10-22).</b> <c>NetgsmIysClient.SearchAsync</c> tek alıcılı <c>/iys/search</c>
/// yanıtını (Netgsm orada <c>query</c>'yi dizi değil NESNE döndürüyor) okuyamıyordu: İYS'nin ONAY olarak tuttuğu kayıtlar
/// Unknown sayıldı, doğrulama takvimi tükendi (<c>Failed</c>, <c>iys-not-confirmed ...</c>) ve kurtarma son tarih geçince
/// <c>Expired</c> (<c>push-deadline-passed</c>) yazdı. Okuyucu düzeltmesi deploy edildikten SONRA koşulur; önce kuru koşu.</para>
///
/// <para><b>--apply YENİDEN İTMEZ.</b> Hedef kayıtları <c>Pushed</c>'a döndürür ve randevuyu şimdiye çeker
/// (<c>NextVerifyAt = şimdi</c>, <c>VerifyAttempts = 0</c>, <c>LastError = null</c>). Son tarihi geçmiş bir onayı eski
/// consentDate ile İYS'ye yeniden göndermek H467 riskidir; gerek de yok — bu kayıtlar İYS'ye zaten bir kez kuyruğa alındı
/// (<c>LastPushedAt</c> dolu), soru yalnız İYS'nin onları ne olarak tuttuğu. Doğrulama işi (5 dakikada bir) düzeltilmiş
/// okuyucuyla sorar: ONAY derse kayıt <c>Confirmed</c> olur; demezse takvim tükenince yeniden <c>Failed</c>, kurtarma son
/// tarih geçmişse <c>Expired</c> yazar ve push işi <c>Expired</c>'ı taramaz — döngü sonludur
/// (<c>IysConsentVerifyJobTests.Son_tarihi_gecmis_Pushed_kayit_dogrulanamazsa_yeniden_itilmeden_Expired_da_biter</c>).</para>
///
/// <para><b>Hedef:</b> <c>Status = Onay</c>, <c>PushState ∈ {Expired, Failed}</c>, <c>LastError</c> =
/// <c>push-deadline-passed</c> ya da <c>iys-not-confirmed</c> ile başlıyor, <c>LastPushedAt</c> dolu. Son koşul
/// görevdeki tanımdan bir daraltma: hiç kuyruğa alınmamış kaydı "Bildirildi" diye göstermemek için — İYS'nin onu ONAY
/// tutması için önce bizim itmiş olmamız gerekir. RET beyanları, ağ/kod hatasıyla düşenler ve doğrulanmış kayıtlar
/// dışarıda kalır.</para>
///
/// <para><b>Tek UPDATE.</b> Uygulama, kuru koşudaki süzgeçle aynı <c>WHERE</c>'li tek bir <c>ExecuteUpdate</c>'tir:
/// arada yeni bir yerel onay satırı <c>Pending</c>'e çektiyse satır artık eşleşmez ve ezilmez. Kurtarma işi aynı anda
/// bir <c>Failed</c> satırı okuyup yazarsa o satır yeniden <c>Expired</c>/<c>Pending</c> olabilir — komut idempotent,
/// kuru koşuyu tekrarlayıp sıfır değilse yeniden uygula.</para>
///
/// <para><b>İz:</b> <c>IysConsentEvent</c> yazılmaz (olay türlerinin hiçbiri elle sıfırlama değil; ekle-only ispat
/// tablosunu anlamsız satırla doldurmayız) ve denetim satırı yazılmaz (<c>AuditLogEntry</c> bir yönetici kimliği ister,
/// CLI'de yok; depodaki hiçbir CLI yazmıyor). İz konsol çıktısıdır — tutanağa alın. Doğrulama işinin her sorgusu zaten
/// <c>SearchResult</c> olayı yazar.</para>
///
/// <para>Argümanlar katı (<see cref="BankDataCleanup"/> kalıbı): tanınmayan, yinelenen ya da değeri eksik argüman → 2,
/// yapılandırmaya ve veritabanına dokunmadan; yazım hatalı <c>--apply</c> kuru koşuya dönüşmez. Çıktıda kişisel veri
/// yok — yalnız marka kodu, durum ve sayılar.</para>
/// <para>Çıkış kodları: 0 tamam; 2 kullanım/yapılandırma.</para>
/// </summary>
public static class IysReverify
{
    public const string DeadlinePassed = "push-deadline-passed";
    public const string NotConfirmedPrefix = "iys-not-confirmed";

    private const string Usage = "Kullanım: iys-reverify [--brand <marka kodu>] [--apply]";

    public sealed record Options(string? Brand, bool Apply);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var options))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var conn = config.GetConnectionString("LicenseDb");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("ConnectionStrings:LicenseDb bulunamadı.");
            return 2;
        }
        var dbOptions = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(dbOptions);
        return await RunAsync(db, options, Console.Out, CancellationToken.None);
    }

    /// <summary>Komutun gövdesi: argümanlar ayrıştırılmış, bağlantı kurulmuş. Testler gerçek SQL Server'a buradan girer
    /// (<c>ExecuteUpdate</c> InMemory'de çalışmaz).</summary>
    public static async Task<int> RunAsync(LicenseDbContext db, Options options, TextWriter output, CancellationToken ct)
    {
        var scope = options.Brand ?? "hepsi";
        output.WriteLine($"İYS yeniden doğrulama — {(options.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} (marka: {scope})");

        var rows = await Targets(db, options.Brand)
            .GroupBy(c => new { c.BrandCode, c.PushState, c.LastError })
            .Select(g => new { g.Key.BrandCode, g.Key.PushState, g.Key.LastError, Count = g.Count() })
            .ToListAsync(ct);
        foreach (var r in rows.OrderBy(r => r.BrandCode, StringComparer.Ordinal)
                     .ThenBy(r => r.PushState).ThenBy(r => r.LastError, StringComparer.Ordinal))
            output.WriteLine($"  marka={r.BrandCode} durum={r.PushState} hata={r.LastError}: {r.Count}");
        var total = rows.Sum(r => r.Count);
        output.WriteLine($"Hedef: {total} kayıt.");

        // Bilgi: düzeltilmiş doğrulama işi bunları CLI'siz, sıradaki randevularında okur (en geç 24 sa).
        var pushedUnknown = await Scoped(db, options.Brand)
            .CountAsync(c => c.Status == IysConsentStatus.Onay
                             && c.PushState == IysPushState.Pushed
                             && c.LastVerifiedStatus == IysConsentStatus.Unknown, ct);
        if (pushedUnknown > 0)
            output.WriteLine($"Bilgi: {pushedUnknown} kayıt Bildirildi (Pushed) ve son İYS cevabı Unknown — komut onlara "
                             + "dokunmaz; düzeltilmiş doğrulama işi sıradaki randevularında okur.");

        if (!options.Apply)
        {
            output.WriteLine("KURU ÇALIŞTIRMA — hiçbir şey yazılmadı. Uygulamak için aynı komutu --apply ile çalıştırın.");
            return 0;
        }
        if (total == 0)
        {
            output.WriteLine("Uygulanacak kayıt yok.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = await Targets(db, options.Brand).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.PushState, IysPushState.Pushed)
            .SetProperty(c => c.NextVerifyAt, now)
            .SetProperty(c => c.VerifyAttempts, 0)
            .SetProperty(c => c.LastError, (string?)null)
            .SetProperty(c => c.UpdatedAt, now), ct);

        output.WriteLine($"UYGULANDI — {updated} kayıt Bildirildi'ye (Pushed) döndü, doğrulama randevusu şimdi. "
                         + "İYS'ye yeniden itilmedi.");
        if (updated != total)
            output.WriteLine($"Not: sayımdan sonra {total - updated} kayıt başka bir iş tarafından değişti ve atlandı.");
        output.WriteLine("Doğrulama işi 5 dakikada bir koşar: İYS ONAY derse kayıt 'İYS ile eşleşti'ye geçer; demezse takvim "
                         + "tükenince yeniden Düştü / Süresi Doldu olur. Birkaç dakika sonra kuru koşuyu tekrarlayın.");
        return 0;
    }

    /// <summary>Onarımın hedef kümesi — kuru koşu ve uygulama AYNI süzgeci kullanır. Bkz. sınıf dokümanı.</summary>
    public static IQueryable<IysConsent> Targets(LicenseDbContext db, string? brand)
        => Scoped(db, brand).Where(c =>
            c.Status == IysConsentStatus.Onay
            && (c.PushState == IysPushState.Expired || c.PushState == IysPushState.Failed)
            && c.LastPushedAt != null
            && c.LastError != null
            && (c.LastError == DeadlinePassed || c.LastError.StartsWith(NotConfirmedPrefix)));

    private static IQueryable<IysConsent> Scoped(LicenseDbContext db, string? brand)
        => brand is null ? db.IysConsents : db.IysConsents.Where(c => c.BrandCode == brand);

    /// <summary>args[0] komut adıdır. Bkz. sınıf dokümanı: katı ayrıştırma. Marka kodu yalnız rakam (Netgsm marka kodu)
    /// ve sütun uzunluğunu (16) aşamaz: <c>--brand --apply</c> marka "--apply" sanılıp kuru koşuya dönüşmez.</summary>
    public static bool TryParseArgs(string[] args, out Options options)
    {
        string? brand = null;
        var apply = false;
        options = new Options(null, false);
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--brand" when brand is null:
                    if (i + 1 >= args.Length) return false;
                    var value = args[i + 1];
                    if (value.Length is 0 or > 16 || !value.All(char.IsAsciiDigit)) return false;
                    brand = value;
                    i++;
                    break;
                default:
                    return false;
            }
        }
        options = new Options(brand, apply);
        return true;
    }
}
