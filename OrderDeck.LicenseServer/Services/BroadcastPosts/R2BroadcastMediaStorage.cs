using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class R2BroadcastMediaStorage : IBroadcastMediaStorage, IDisposable
{
    // SigV4 notu: AWSSDK v3'te presign çıktısını SigV2'den SigV4'e çevirmek için
    // `AWSConfigsS3.UseSignatureVersion4` static flag'i şarttı (R2 SigV2 preflight'ı
    // 401'liyor, CORS başlığı dönmüyordu). AWSSDK **v4** her zaman SigV4 imzalıyor
    // ve hem o flag'i hem `AmazonS3Config.SignatureVersion`'ı kaldırdı — bu yüzden
    // static ctor artık yok. Davranış aynı, ayar gereksiz.
    private readonly R2Options _opt;
    private readonly AmazonS3Client _client;
    private readonly ILogger<R2BroadcastMediaStorage> _log;

    public R2BroadcastMediaStorage(R2Options opt, ILogger<R2BroadcastMediaStorage> log)
    {
        _opt = opt;
        _log = log;
        if (!_opt.IsConfigured)
            throw new InvalidOperationException(
                "R2 options not configured (AccountId/AccessKeyId/SecretAccessKey/BucketName all required).");

        _client = new AmazonS3Client(
            _opt.AccessKeyId, _opt.SecretAccessKey,
            new AmazonS3Config
            {
                ServiceURL = _opt.ServiceUrl,
                ForcePathStyle = true,
                // R2 uses "auto" region; AWSSDK derives the signing region from
                // this. Without it the SigV4 string-to-sign has an empty region
                // and R2 returns SignatureDoesNotMatch on PUT.
                AuthenticationRegion = "auto",
                // AWSSDK v4 varsayılanı WHEN_SUPPORTED: her PUT'a
                // x-amz-checksum-crc32 ekliyor. R2 CRC32'yi desteklemiyor
                // ("Header 'x-amz-checksum-algorithm' with value 'CRC32' not
                // implemented") ve presign edilen URL'e de bu başlık imzaya
                // giriyor. WHEN_REQUIRED = v3'teki davranış.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }

    public Task<string> CreateUploadUrlAsync(string objectKey, string contentType, long sizeBytes, CancellationToken ct = default)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(10),
            ContentType = contentType
        };
        return Task.FromResult(_client.GetPreSignedURL(req));
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5)
        };
        return Task.FromResult(_client.GetPreSignedURL(req));
    }

    public async Task<MediaObjectInfo?> HeadAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            var resp = await _client.GetObjectMetadataAsync(_opt.BucketName, objectKey, ct);
            return new MediaObjectInfo(resp.ContentLength, resp.Headers.ContentType ?? "");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Önek altındaki tüm anahtarları döner. S3/R2 tek yanıtta en çok 1000 anahtar
    /// veriyor; <c>IsTruncated</c> doğru olduğu sürece <c>ContinuationToken</c> ile
    /// devam edilir. Sayfalama atlanırsa 1000'inci anahtardan sonrası GÖRÜNMEZ olur
    /// ve mutabakat işi yetimleri sessizce kaçırır.
    ///
    /// İstisna bilerek YUTULMUYOR (DeleteAsync'in aksine): eksik bir liste
    /// "yetim yok" gibi görünür, çağıran iş hatayı lisans başına yakalayıp
    /// loglayabilsin diye yukarı bırakılıyor.
    /// </summary>
    public async Task<IReadOnlyList<MediaObjectListing>> ListAsync(
        string prefix, CancellationToken ct = default)
    {
        var results = new List<MediaObjectListing>();
        string? token = null;

        do
        {
            var resp = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _opt.BucketName,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct);

            foreach (var o in resp.S3Objects ?? [])
                results.Add(new MediaObjectListing(o.Key, ToUtc(o.LastModified)));

            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        }
        while (token is not null);

        return results;
    }

    /// <summary>
    /// S3 zaman damgası UTC'dir ama SDK <c>DateTime.Kind</c>'ı Unspecified
    /// bırakabiliyor; doğrudan DateTimeOffset'e çevirmek sunucunun YEREL saat
    /// farkını uygular ve ödemsiz süre kıyasını saatlerce kaydırır. Kind
    /// açıkça UTC'ye sabitleniyor. Damga yoksa "şimdi" varsayılır — bilinmeyen
    /// yaş en taze kabul edilir, yani nesne silinmez (güvenli taraf).
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime? value)
        => value is null
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_opt.BucketName, objectKey, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "R2 delete failed for {Key} (swallowed)", objectKey);
        }
    }

    public void Dispose() => _client.Dispose();
}
