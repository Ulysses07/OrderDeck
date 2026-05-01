using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// AWS S3 / B2 / Wasabi / MinIO replication for encrypted backup blobs.
/// Uses the path-style endpoint (works against MinIO and most S3-clones)
/// and forces the regional service URL from config rather than auto-detecting.
/// </summary>
public sealed class S3BackupSink : IS3BackupSink, IDisposable
{
    private readonly S3BackupOptions _opt;
    private readonly BackupOptions _backupOpt;
    private readonly AmazonS3Client _client;
    private readonly ILogger<S3BackupSink> _log;

    public bool IsEnabled => true;

    public S3BackupSink(IOptions<BackupOptions> backupOpt, ILogger<S3BackupSink> log)
    {
        _backupOpt = backupOpt.Value;
        _opt = _backupOpt.S3;
        _log = log;

        if (string.IsNullOrWhiteSpace(_opt.ServiceUrl) ||
            string.IsNullOrWhiteSpace(_opt.AccessKey) ||
            string.IsNullOrWhiteSpace(_opt.SecretKey) ||
            string.IsNullOrWhiteSpace(_opt.Bucket))
        {
            throw new InvalidOperationException(
                "Backup:S3 is enabled but ServiceUrl/AccessKey/SecretKey/Bucket are not all set.");
        }

        _client = new AmazonS3Client(
            _opt.AccessKey,
            _opt.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = _opt.ServiceUrl,
                ForcePathStyle = true,         // MinIO + B2 + most S3-clones require this
                UseHttp = _opt.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            });
    }

    public async Task<bool> UploadAsync(string localPath, Guid customerId, CancellationToken ct = default)
    {
        var key = BuildKey(localPath, customerId);
        try
        {
            await using var stream = File.OpenRead(localPath);
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _opt.Bucket,
                Key = key,
                InputStream = stream,
                ContentType = "application/octet-stream",
                // Server-side metadata for forensics — does NOT include the
                // master key id (we don't have key versioning yet, see Phase 5b).
                Metadata = { ["x-orderdeck-customer-id"] = customerId.ToString() }
            }, ct);
            _log.LogInformation("S3 upload ok: {Key} ({Size} bytes)", key, new FileInfo(localPath).Length);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "S3 upload failed for {Key}", key);
            if (!_opt.BestEffort) throw;
            return false;
        }
    }

    public async Task DeleteAsync(string remoteKey, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_opt.Bucket, remoteKey, ct);
            _log.LogInformation("S3 delete ok: {Key}", remoteKey);
        }
        catch (Exception ex)
        {
            // Always swallow delete errors — local retention is the source of truth.
            _log.LogWarning(ex, "S3 delete failed for {Key}", remoteKey);
        }
    }

    /// <summary>Mirror the on-disk layout: {Prefix}{customerId}/{filename}.bin
    /// so a side-by-side listing matches local storage and we can reverse-map
    /// a key back to a file in disaster recovery.</summary>
    private string BuildKey(string localPath, Guid customerId)
    {
        var fileName = Path.GetFileName(localPath);
        var prefix = _opt.Prefix.TrimEnd('/') + "/";
        return $"{prefix}{customerId}/{fileName}";
    }

    public void Dispose() => _client.Dispose();
}
