using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OrderDeck.Core.Payments;

/// <summary>
/// Kirasız ödeme kaydının (<c>payment-requests/backfill</c>) belirlenimli Id'si (spec 2026-10-13 §6.4 "Ödeme geçmişi"):
/// yerel <c>PaymentJob</c>'un Id'si ile teslim ya da deneme sayısından türetilir, yineleme zararsızdır — aynı iş aynı
/// sayıda aynı Id'yi verir, sunucu onu <c>unchanged</c> sayar (geri yüklenmiş kopyanın baştan gönderimi dahil, §8.7).
///
/// <para><b>İki ayrı ad uzayı:</b> teslim edilmiş kayıt <see cref="Delivered"/> (teslim sayısıyla), sonucu bilinmeyen
/// gönderim <see cref="Unconfirmed"/> (deneme sayısıyla). Tek uzay olsaydı belirsiz denemenin ardından gelen teslim
/// (<c>MarkDelivered</c> deneme sayısını teslim sayısına eşitler) belirsiz kaydın Id'sini başka içerikle yeniden kullanır,
/// sunucu onu <c>content-conflict</c> sayıp teslimi hiç yazmazdı.</para>
///
/// <para>Türetme: SHA-256("orderdeck-payment-record|{uzay}|{iş Id'si}|{sayı}") özetinin ilk 16 baytı. Sayı biçimi
/// kültürden bağımsız. DEĞİŞTİRİLMEZ: sunucuya gitmiş kayıtların Id'leri bu metne bağlı; farklı bir türetme aynı işi
/// ikinci kez yazdırırdı.</para>
/// </summary>
public static class PaymentRecordIds
{
    /// <summary>İşin <paramref name="deliveries"/>'inci teslimi.</summary>
    public static Guid Delivered(string jobId, int deliveries) => Derive("delivered", jobId, deliveries);

    /// <summary>İşin <paramref name="sendAttempts"/>'inci, sonucu bilinmeyen gönderim denemesi.</summary>
    public static Guid Unconfirmed(string jobId, int sendAttempts) => Derive("unconfirmed", jobId, sendAttempts);

    private static Guid Derive(string space, string jobId, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var text = "orderdeck-payment-record|" + space + "|" + jobId + "|"
                   + count.ToString(CultureInfo.InvariantCulture);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }
}
