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
