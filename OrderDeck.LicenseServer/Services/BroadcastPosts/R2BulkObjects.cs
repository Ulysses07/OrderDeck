using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

/// <summary>Toplu silmede silinemeyen nesne: anahtarı ve hata metni (silme görevinin <c>LastError</c>'ına gider).</summary>
public sealed record MediaDeleteFailure(string Key, string Error);

/// <summary>
/// R2'de önek listeleme ve toplu silme; dekont ve WhatsApp depoları ortak kullanır (yayıncı silmesi, KVKK). İstemci ayarları
/// (<c>RequestChecksumCalculation=WHEN_REQUIRED</c> …) depoların kendi kurucularında; burada yalnız çağrılar.
/// </summary>
public static class R2BulkObjects
{
    /// <summary>Tek <c>DeleteObjects</c> isteğinin üst sınırı (S3 ve R2).</summary>
    public const int MaxKeysPerDelete = 1000;

    /// <summary>Önek altındaki TÜM anahtarlar; <c>ListObjectsV2</c> devam jetonuyla sayfa sayfa. Hata fırlatır: listesi eksik
    /// bir silme "hepsi silindi" sanılmasın.</summary>
    public static async Task<IReadOnlyList<string>> ListKeysAsync(IAmazonS3 s3, string bucket, string prefix, CancellationToken ct)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);
            // AWSSDK v4: boş koleksiyon null gelir.
            foreach (var o in resp.S3Objects ?? []) keys.Add(o.Key);
            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        }
        while (token is not null);
        return keys;
    }

    /// <summary>
    /// Anahtarları <see cref="MaxKeysPerDelete"/>'lik parçalarla siler. Fırlatmaz: silinemeyen her anahtar hata metniyle döner.
    ///
    /// <para>R2, AWS SDK'nın yeni varsayılan sağlama başlığını (CRC32) <c>DeleteObjects</c>'te bir dönem 501 NotImplemented ile
    /// reddetti; bu SDK sürümüyle canlı R2'de toplu silme denenmedi. Toplu istek 501 ya da başka bir istemci hatasıyla (4xx: imza,
    /// sağlama başlığı, istek biçimi) reddedilirse tek nesne silmeye (<c>DeleteObject</c>, üretimde müşteri silmesinin kullandığı
    /// yol) düşülür, kalan parçalar da öyle silinir: ret isteğin biçimine, her yeniden deneme aynı nedenle düşer ve nesneler hiç
    /// silinmezdi. Kesintide (5xx, ağ hatası) ve geçici 4xx'te (408, 429) parçanın her anahtarı silinemedi sayılır: tek tek
    /// denemek kesintiyi bin kez beklemek olurdu, çağıran zaten yeniden dener.</para>
    /// </summary>
    public static async Task<IReadOnlyList<MediaDeleteFailure>> DeleteKeysAsync(IAmazonS3 s3, string bucket,
        IReadOnlyList<string> keys, CancellationToken ct)
    {
        var failures = new List<MediaDeleteFailure>();
        var batchUnsupported = false;
        foreach (var chunk in keys.Chunk(MaxKeysPerDelete))
        {
            if (!batchUnsupported)
            {
                try
                {
                    var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                    {
                        BucketName = bucket,
                        Objects = chunk.Select(k => new KeyVersion { Key = k }).ToList(),
                        // Sessiz kip: yanıt yalnız hataları taşır.
                        Quiet = true,
                    }, ct);
                    failures.AddRange(ToFailures(resp.DeleteErrors));
                    continue;
                }
                catch (DeleteObjectsException ex)
                {
                    // SDK, yanıtta silinemeyen nesne varsa bunu fırlatır; parçanın geri kalanı silindi.
                    failures.AddRange(ToFailures(ex.Response?.DeleteErrors));
                    continue;
                }
                catch (AmazonS3Exception ex) when (BatchRejected(ex.StatusCode))
                {
                    batchUnsupported = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures.AddRange(chunk.Select(k => new MediaDeleteFailure(k, ex.Message)));
                    continue;
                }
            }

            foreach (var key in chunk)
            {
                try
                {
                    await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures.Add(new MediaDeleteFailure(key, ex.Message));
                }
            }
        }
        return failures;
    }

    /// <summary>Toplu isteğin kendisi reddedildi (kesinti değil): 501 ya da geçici olmayan bir 4xx.</summary>
    private static bool BatchRejected(HttpStatusCode status)
        => status == HttpStatusCode.NotImplemented
           || ((int)status is >= 400 and < 500
               && status is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests);

    private static IEnumerable<MediaDeleteFailure> ToFailures(List<DeleteError>? errors)
        => (errors ?? []).Select(e => new MediaDeleteFailure(e.Key, $"{e.Code}: {e.Message}"));
}
