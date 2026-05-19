using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

public sealed class R2BroadcastMediaStorage : IBroadcastMediaStorage, IDisposable
{
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
                // Force SigV4 for pre-signed URLs. Default in this AWSSDK.S3
                // version emits SigV2-style URLs (AWSAccessKeyId=...&Signature=...)
                // which Cloudflare R2 rejects on the CORS OPTIONS preflight:
                // the unsigned preflight gets a 401 before CORS headers are
                // added, so the browser sees no Access-Control-Allow-Origin
                // and fails the upload. SigV4 (X-Amz-Algorithm=AWS4-HMAC-SHA256
                // &X-Amz-Credential=...) is what R2's preflight handler
                // recognizes as a pre-signed request and returns CORS headers
                // for, even on the unauthenticated OPTIONS check.
                SignatureVersion = "4"
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
