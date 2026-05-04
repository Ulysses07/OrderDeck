using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Services.Email;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Backup;

/// <summary>
/// Higher-level test that exercises the Hangfire-job glue:
/// <list type="bullet">
///   <item>FindLatestBlob → RunAsync flow.</item>
///   <item>"No blobs yet" silent success.</item>
///   <item>Failure path triggers an email when Admin:AlertEmail is set.</item>
///   <item>Failure path stays silent when Admin:AlertEmail is empty.</item>
/// </list>
/// </summary>
public class BackupRestoreDrillJobTests : IDisposable
{
    private readonly string _root;
    private readonly string _testKey =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly RecordingEmailSender _email = new();

    public BackupRestoreDrillJobTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "orderdeck-drill-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private (BackupRestoreDrillJob job, BackupStorageService svc) BuildJob(string? alertEmail)
    {
        var opts = new BackupOptions
        {
            MasterKeyHex = _testKey,
            ActiveKeyVersion = 0,
            StorageRoot = _root,
        };
        var svc = new BackupStorageService(
            Options.Create(opts),
            NullLogger<BackupStorageService>.Instance);

        var configValues = new Dictionary<string, string?>();
        if (alertEmail is not null) configValues["Admin:AlertEmail"] = alertEmail;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var job = new BackupRestoreDrillJob(
            svc,
            Options.Create(opts),
            _email,
            config,
            NullLogger<BackupRestoreDrillJob>.Instance);
        return (job, svc);
    }

    private async Task WriteValidBlobAsync(BackupStorageService svc)
    {
        var dbPath = Path.Combine(_root, "fixture.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath};Pooling=false"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE T (Id INTEGER PRIMARY KEY); INSERT INTO T VALUES (1)";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var zipPath = Path.Combine(_root, "fixture.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(dbPath, "fixture.db");
        }
        var plaintext = await File.ReadAllBytesAsync(zipPath);
        var (envelope, _) = svc.Encrypt(plaintext);

        var custDir = Path.Combine(_root, "cust");
        Directory.CreateDirectory(custDir);
        await File.WriteAllBytesAsync(Path.Combine(custDir, "blob.bin"), envelope);
    }

    private async Task WriteTamperedBlobAsync(BackupStorageService svc)
    {
        await WriteValidBlobAsync(svc);
        var path = Path.Combine(_root, "cust", "blob.bin");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length - 1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);
    }

    [Fact]
    public async Task RunAsync_with_no_blobs_finishes_silently_and_does_not_email()
    {
        var (job, _) = BuildJob(alertEmail: "ops@example.com");

        await job.RunAsync(CancellationToken.None);

        _email.Sent.Should().BeEmpty(
            "fresh deployments shouldn't page anyone every Monday");
    }

    [Fact]
    public async Task RunAsync_with_passing_drill_does_not_email()
    {
        var (job, svc) = BuildJob(alertEmail: "ops@example.com");
        await WriteValidBlobAsync(svc);

        await job.RunAsync(CancellationToken.None);

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_with_failing_drill_sends_alert_when_email_configured()
    {
        var (job, svc) = BuildJob(alertEmail: "ops@example.com");
        await WriteTamperedBlobAsync(svc);

        await job.RunAsync(CancellationToken.None);

        _email.Sent.Should().HaveCount(1);
        var sent = _email.Sent[0];
        sent.ToEmail.Should().Be("ops@example.com");
        sent.Subject.Should().Contain("FAILED");
        sent.PlainBody.Should().Contain("[FAIL] Decrypt");
    }

    [Fact]
    public async Task RunAsync_with_failing_drill_stays_silent_when_email_unconfigured()
    {
        var (job, svc) = BuildJob(alertEmail: null);
        await WriteTamperedBlobAsync(svc);

        await job.RunAsync(CancellationToken.None);

        _email.Sent.Should().BeEmpty(
            "Admin:AlertEmail unset means log-only — no recipient to mail to");
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public sealed record Send(string ToEmail, string ToName, string Subject, string HtmlBody, string PlainBody);
        public List<Send> Sent { get; } = new();

        public Task SendAsync(string toEmail, string toName, string subject,
            string htmlBody, string plainBody, CancellationToken ct = default)
        {
            Sent.Add(new Send(toEmail, toName, subject, htmlBody, plainBody));
            return Task.CompletedTask;
        }
    }
}
