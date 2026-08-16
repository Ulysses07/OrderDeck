using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Lisans sayacından barkod numarası ayırır.
///
/// <para><b>KAYDETMEZ.</b> İzlenen <see cref="BarcodeCounter"/> satırını
/// değiştirir ve biter; <c>SaveChanges</c> çağırmak ÇAĞIRANIN işi. Sebep:
/// sayaç ile varyant tek iş biriminde işlenmeli. Ayırıcı kendi kaydını
/// yapsaydı, ondan sonra gelen bir doğrulama hatası ya da benzersizlik
/// çakışması sayacı ilerlemiş, varyantı yazılmamış bırakırdı — numaralar
/// sessizce delinirdi. Bu kural teste bağlandı:
/// <c>Ayirma_kaydetmez_sayaci_cagiran_isler</c>.</para>
///
/// <para><b>Atlama:</b> panelden elle yazılmış bir barkod, sayacın sırada
/// olduğu değeri kapmış olabilir. Numaralar tek tek değil, tek sorguda
/// aralık olarak sorulur; çakışanlar atlanır. Döngü sonlanır çünkü her
/// turda <c>Next</c> en az 1 ilerler.</para>
///
/// <para><b>DİKKAT — görülmeyen satırlar:</b> "alınmış" sorgusu yalnız
/// KAYDEDİLMİŞ varyantları görür. Aynı istek içinde iki ayrı ayırma yapılıp
/// arada kaydedilmezse ikincisi birincinin numaralarını görmez. Bugün her
/// istek tek ayırma yapıyor; bu kalıp bozulursa burası da bozulur.</para>
/// </summary>
public sealed class BarcodeAllocator
{
    private readonly LicenseDbContext _db;

    public BarcodeAllocator(LicenseDbContext db) => _db = db;

    /// <summary>
    /// Barkod numarasının hane sayısı. Sabit burada duruyor çünkü numara
    /// uzayını TANIMLAYAN yer burası: aynı sayıyı bilen ikinci bir yer daha
    /// var (<c>PanelBroadcastCodesController</c>, "10 haneli saf sayı yayın
    /// kodu olamaz" bekçisi). Ayrı ayrı yazılsalardı hane genişliği
    /// değiştiğinde bekçi sessizce yanlış uzayı korurdu.
    /// </summary>
    public const int Digits = 10;

    private static readonly string PadFormat =
        "D" + Digits.ToString(CultureInfo.InvariantCulture);

    /// <summary><see cref="Digits"/> hane, soldan sıfır dolgulu, kültürden bağımsız.</summary>
    public static string Format(long n) =>
        n.ToString(PadFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="count"/> adet benzersiz barkod ayırır ve geri döner;
    /// sayacı günceller ama <c>SaveChanges</c> çağırmaz — bu çağıranın işi.
    /// </summary>
    /// <remarks>
    /// <b>Görünmez satır uyarısı:</b> "alınmış numara" sorgusu yalnızca
    /// <i>kaydedilmiş</i> (<c>SaveChanges</c> çağrılmış) varyantları görür.
    /// Aynı istek içinde iki kez çağrılıp arada kayıt yapılmazsa ikinci çağrı
    /// birincinin ayırdığı numaraları çakışmış saymaz — çift tahsis riski oluşur.
    /// Ayrıntı için sınıf düzeyindeki "DİKKAT — görülmeyen satırlar" paragrafına bakın.
    /// </remarks>
    public async Task<IReadOnlyList<string>> AllocateAsync(
        Guid licenseId, int count, CancellationToken ct)
    {
        if (count <= 0) return Array.Empty<string>();

        var counter = await _db.BarcodeCounters
            .FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct);

        if (counter is null)
        {
            counter = new BarcodeCounter { LicenseId = licenseId, Next = 1 };
            _db.BarcodeCounters.Add(counter);
        }

        var result = new List<string>(count);
        while (result.Count < count)
        {
            var need = count - result.Count;
            var candidates = new List<string>(need);
            for (var i = 0; i < need; i++)
                candidates.Add(Format(counter.Next + i));

            var taken = await _db.ProductVariants
                .AsNoTracking()
                .Where(v => v.LicenseId == licenseId
                            && candidates.Contains(v.Barcode))
                .Select(v => v.Barcode)
                .ToListAsync(ct);

            var takenSet = new HashSet<string>(taken, StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                counter.Next++;
                if (!takenSet.Contains(candidate)) result.Add(candidate);
            }
        }

        return result;
    }
}
