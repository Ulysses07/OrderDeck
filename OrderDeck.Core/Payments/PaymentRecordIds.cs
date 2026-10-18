using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OrderDeck.Core.Payments;

/// <summary>
/// Kirasız ödeme kaydının (<c>payment-requests/backfill</c>) belirlenimli Id'si (spec 2026-10-13 §6.4 "Ödeme geçmişi"):
/// yerel <c>PaymentJob</c>'un Id'si ile teslim ya da deneme sayısından türetilir, yineleme zararsızdır — aynı iş aynı
/// sayıda aynı Id'yi verir; içerik aynıysa sunucu onu <c>unchanged</c> sayar (geri yüklenmiş kopyanın baştan gönderimi
/// dahil, §8.7), teslimden sonra değiştiyse (iptal, revizyon, birleştirme) <c>content-conflict</c> ile düşer ve ilk kayıt
/// kalır.
///
/// <para><b>İki ayrı ad uzayı:</b> teslim edilmiş kayıt <see cref="Delivered"/> (teslim sayısıyla), sonucu bilinmeyen
/// gönderim <see cref="Unconfirmed"/> (teslim ve deneme sayısıyla). Tek uzay olsaydı belirsiz denemenin ardından gelen teslim
/// (<c>MarkDelivered</c> deneme sayısını teslim sayısına eşitler) belirsiz kaydın Id'sini başka içerikle yeniden kullanır,
/// sunucu onu <c>content-conflict</c> sayıp teslimi hiç yazmazdı.</para>
///
/// <para><b>Belirsiz kayıtta teslim sayısı da Id'ye girer:</b> deneme sayısı tek başına tekdüze değil — <c>MarkDelivered</c>
/// onu yeni teslim sayısına geri çeker. D=0'da iki belirsiz deneme (2) ve bir teslimden (D=1, deneme 1) sonra sonucu
/// yazılamayan deneme (D=1, deneme 2) yine "2" üretir, aynı Id başka içerikle giderdi. (teslim, deneme) çifti her belirsiz
/// gönderimde yenidir: teslimler arasında deneme sayısı yalnız artar (başlamamış denemenin geri alınması kayıt bırakmaz).</para>
///
/// <para><b>Hangi uzay:</b> yalnız işin satırı belirler (<c>HasUnconfirmedSend</c> = deneme &gt; teslim;
/// <c>PaymentHistoryRecords.FromJob</c>) — kaydı kuran yolun ayrıca bildiği sonuç değil. Teslim yazılmış iş (Cloud API'nin
/// <c>in_progress</c>'i dahil: <c>SendPending</c> yerelde teslim yazar) <see cref="Delivered"/>'dır.</para>
///
/// <para><b>Kalan risk (geri yükleme, spec §8.7):</b> kopya kimliği türetmeye girmez. Geri yükleme işin sayaçlarını yedek
/// anına geri çeker; yedekten sonra yapılmış bir teslimin (iş, sayı) çifti geri yüklenmiş kopyanın sonraki teslimine yeniden
/// verilir ve o teslimin kaydı sunucuya yazılmaz (<c>content-conflict</c> ya da <c>unchanged</c>). Düzeltme ödeme
/// geçmişinin baştan gönderimiyle birlikte tasarlanmalı; sahip kararı bekliyor.</para>
///
/// <para>Türetme: SHA-256("orderdeck-payment-record|{uzay}|{iş Id'si}|{sayı}") özetinin ilk 16 baytı; belirsiz kayıtta
/// sayı "{teslim}.{deneme}". Sayı biçimi kültürden bağımsız. DEĞİŞTİRİLMEZ: sunucuya gitmiş kayıtların Id'leri bu metne
/// bağlı; farklı bir türetme aynı işi ikinci kez yazdırırdı.</para>
/// </summary>
public static class PaymentRecordIds
{
    /// <summary>İşin <paramref name="deliveries"/>'inci teslimi.</summary>
    public static Guid Delivered(string jobId, int deliveries)
        => Derive("delivered", jobId, deliveries.ToString(CultureInfo.InvariantCulture));

    /// <summary>İşin <paramref name="deliveries"/> teslimden sonraki <paramref name="sendAttempts"/>'inci, sonucu bilinmeyen
    /// gönderim denemesi.</summary>
    public static Guid Unconfirmed(string jobId, int deliveries, int sendAttempts)
        => Derive("unconfirmed", jobId,
            deliveries.ToString(CultureInfo.InvariantCulture) + "." + sendAttempts.ToString(CultureInfo.InvariantCulture));

    private static Guid Derive(string space, string jobId, string count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var text = "orderdeck-payment-record|" + space + "|" + jobId + "|" + count;
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }
}
