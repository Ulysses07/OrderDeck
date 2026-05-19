using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class R2BroadcastMediaStorage : IBroadcastMediaStorage, IDisposable
{
    // Static init: force SigV4 globally. AmazonS3Config.SignatureVersion="4" is
    // ignored for pre-signed URLs in this SDK version; this static flag is what
    // actually flips presign output from SigV2 (?AWSAccessKeyId=...&Signature=)
    // to SigV4 (?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=...).
    // R2 rejects SigV2 preflight (OPTIONS gets 401 without CORS headers); SigV4
    // preflight returns the expected Access-Control-Allow-Origin.
    static R2BroadcastMediaStorage()
    {
        AWSConfigsS3.UseSignatureVersion4 = true;
    }

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
                SignatureVersion = "4",
                // R2 uses "auto" region; AWSSDK derives the signing region from
                // this. Without it the SigV4 string-to-sign has an empty region
                // and R2 returns SignatureDoesNotMatch on PUT.
                AuthenticationRegion = "auto"
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
