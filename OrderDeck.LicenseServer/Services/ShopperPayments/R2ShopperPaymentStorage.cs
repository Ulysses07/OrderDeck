using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Services.ShopperPayments;

/// <summary>
/// Cloudflare R2 direct-upload storage for shopper-submitted PDF dekonts.
/// Server receives bytes via multipart, parses, then PutObjectAsync.
/// Mevcut R2BroadcastMediaStorage'tan FARK: pre-signed PUT URL üretmiyor;
/// server doğrudan upload ediyor (parse-then-store akışı için gerekli).
/// </summary>
public sealed class R2ShopperPaymentStorage : IShopperPaymentStorage, IDisposable
{
    // Static init: force SigV4 globally. AmazonS3Config.SignatureVersion="4" is
    // ignored for pre-signed URLs in this SDK version; this static flag is what
    // actually flips presign output from SigV2 to SigV4.
    // R2 rejects SigV2 with SignatureDoesNotMatch.
    static R2ShopperPaymentStorage()
    {
        AWSConfigsS3.UseSignatureVersion4 = true;
    }

    private readonly R2Options _opt;
    private readonly AmazonS3Client _s3;

    public R2ShopperPaymentStorage(R2Options opt)
    {
        _opt = opt;
        if (!_opt.IsConfigured)
            throw new InvalidOperationException(
                "R2 options not configured (AccountId/AccessKeyId/SecretAccessKey/BucketName all required).");

        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(_opt.AccessKeyId, _opt.SecretAccessKey),
            new AmazonS3Config
            {
                ServiceURL = _opt.ServiceUrl,
                ForcePathStyle = true,
                SignatureVersion = "4",
                AuthenticationRegion = "auto",
            });
    }

    public async Task<string> UploadAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(bytes);
        var req = new PutObjectRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
            InputStream = ms,
            ContentType = contentType,
            AutoCloseStream = false,
            // Cloudflare R2 does NOT support `STREAMING-AWS4-HMAC-SHA256-PAYLOAD`
            // (chunked streaming signature). The AWS SDK defaults to it for
            // stream uploads. Switch to UNSIGNED-PAYLOAD so R2 accepts the
            // request. We're already on HTTPS so the unsigned body isn't a
            // material security concern (the request itself is still SigV4-
            // signed via headers).
            DisablePayloadSigning = true,
            UseChunkEncoding = false,
        };
        await _s3.PutObjectAsync(req, ct);
        return objectKey;
    }

    public Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry ?? TimeSpan.FromMinutes(15)),
        });
        return Task.FromResult(url);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        await _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
        }, ct);
    }

    public void Dispose() => _s3.Dispose();
}
