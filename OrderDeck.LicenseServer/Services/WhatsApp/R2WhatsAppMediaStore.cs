using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// WhatsApp medyasını Cloudflare R2'ye yazar. Yayın medyası ve dekontlarla
/// <b>aynı bucket</b>'ı paylaşır; ayrım nesne anahtarındaki <c>wa/</c> önekiyle
/// yapılır (bkz. <see cref="WhatsAppMediaDownloader"/>).
///
/// <para><b>R2 tuzakları</b> (mevcut iki depoda da aynısı geçerli): R2
/// <c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c> desteklemediği için
/// <c>DisablePayloadSigning</c> + <c>UseChunkEncoding=false</c> gerekiyor,
/// ve CRC32 checksum'ı desteklemediği için AWSSDK v4'ün
/// <c>RequestChecksumCalculation</c> varsayılanı WHEN_REQUIRED'a çekiliyor.
/// (SigV4 için gereken statik <c>UseSignatureVersion4</c> bayrağı AWSSDK v4'te
/// kaldırıldı — v4 zaten her zaman SigV4 imzalıyor.)</para>
/// </summary>
public sealed class R2WhatsAppMediaStore : IWhatsAppMediaStore, IDisposable
{
    private readonly R2Options _opt;
    private readonly AmazonS3Client _s3;

    public R2WhatsAppMediaStore(R2Options opt)
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
                AuthenticationRegion = "auto",
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }

    public async Task<string> SaveAsync(
        string objectKey, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(bytes);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opt.BucketName,
            Key = objectKey,
            InputStream = ms,
            ContentType = contentType,
            AutoCloseStream = false,
            DisablePayloadSigning = true,
            UseChunkEncoding = false,
        }, ct);
        return objectKey;
    }

    public Task<string> CreateDownloadUrlAsync(
        string objectKey, TimeSpan? expiry = null, CancellationToken ct = default)
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

    public void Dispose() => _s3.Dispose();
}
