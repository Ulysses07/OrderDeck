using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Backups;

/// <summary>
/// Tight-quota fixture: caps each customer to 1 MB so the second 700 KB upload
/// trips 507 InsufficientStorage. Default ApiFactory disables the quota
/// (PerCustomerQuotaMb=0), so this lives in its own fixture rather than
/// reconfiguring the shared one mid-test.
/// </summary>
public sealed class TightQuotaApiFactory : ApiFactory
{
    protected override IDictionary<string, string?> ExtraConfig =>
        new Dictionary<string, string?>
        {
            ["Backup:PerCustomerQuotaMb"] = "1"  // 1 MB cap
        };
}

public class BackupQuotaTests : IClassFixture<TightQuotaApiFactory>
{
    private readonly TightQuotaApiFactory _factory;
    public BackupQuotaTests(TightQuotaApiFactory factory) => _factory = factory;

    private static byte[] Payload(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Second_upload_that_would_blow_quota_returns_507()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // First upload: 700 KB — fits under the 1 MB cap.
        var p1 = Payload(700 * 1024);
        var c1 = new ByteArrayContent(p1);
        c1.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha(p1));
        var r1 = await client.PostAsync("/api/v1/me/backups", c1);
        r1.EnsureSuccessStatusCode();

        // Second upload: another 700 KB — pushes total to ~1.4 MB, must reject.
        client.DefaultRequestHeaders.Remove("X-Backup-Sha256");
        var p2 = Payload(700 * 1024);
        var c2 = new ByteArrayContent(p2);
        c2.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha(p2));
        var r2 = await client.PostAsync("/api/v1/me/backups", c2);

        r2.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);
        var body = await r2.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body!["error"].Should().Contain("quota");
    }

    [Fact]
    public async Task Small_uploads_under_quota_succeed()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // Three 200 KB uploads — total 600 KB, well under 1 MB. All must succeed.
        for (var i = 0; i < 3; i++)
        {
            var p = Payload(200 * 1024);
            var c = new ByteArrayContent(p);
            c.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            client.DefaultRequestHeaders.Remove("X-Backup-Sha256");
            client.DefaultRequestHeaders.Add("X-Backup-Sha256", Sha(p));
            var r = await client.PostAsync("/api/v1/me/backups", c);
            r.EnsureSuccessStatusCode();
        }
    }
}
