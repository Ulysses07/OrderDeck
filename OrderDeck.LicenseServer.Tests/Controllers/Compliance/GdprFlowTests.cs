using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Compliance;

public class GdprFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public GdprFlowTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Export_returns_zip_with_expected_entries()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        var resp = await client.GetAsync("/api/v1/me/export");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.Name).ToList();

        entryNames.Should().Contain(new[]
        {
            "customer.json", "licenses.json", "activations.json",
            "email-logs.json", "backups.json", "audit-log.json", "_export-info.json"
        });

        // customer.json must contain the customer's email but NOT the password hash.
        var customerJson = await ReadEntryAsync(zip, "customer.json");
        customerJson.Should().Contain(customerId.ToString());
        customerJson.Should().NotContain("PasswordHash", because: "password hashes must never leave the server");
    }

    [Fact]
    public async Task Export_without_auth_returns_401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/me/export");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Purge_via_admin_anonymises_customer_and_deletes_data()
    {
        var (custClient, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        // Upload a backup so purge has something to clean up.
        var payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        custClient.DefaultRequestHeaders.Add("X-Backup-Sha256", sha);
        var upload = await custClient.PostAsync("/api/v1/me/backups", content);
        upload.EnsureSuccessStatusCode();

        // Snapshot the original email — needed for the confirmation field.
        string originalEmail;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            originalEmail = (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId)).Email;
        }

        // Mint an admin token.
        string adminToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var admin = await db.AdminUsers.FirstOrDefaultAsync();
            if (admin is null)
            {
                admin = new OrderDeck.LicenseServer.Domain.AdminUser
                {
                    Id = Guid.NewGuid(),
                    Username = "purgetest-admin",
                    PasswordHash = hasher.Hash("x"),
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.AdminUsers.Add(admin);
                await db.SaveChangesAsync();
            }
            adminToken = jwt.IssueAdminToken(admin.Id, admin.Username).Token;
        }

        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var purge = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge",
            new { confirmEmail = originalEmail });
        purge.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Customer row exists but anonymised; backups/licenses/activations gone.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var anon = await verifyDb.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId);
        anon.Email.Should().StartWith("purged-");
        anon.Email.Should().EndWith("@deleted.invalid");
        anon.Name.Should().Be("[Deleted]");
        anon.PasswordHash.Should().Be("PURGED");

        var remainingBackups = await verifyDb.CustomerBackups.CountAsync(b => b.CustomerId == customerId);
        remainingBackups.Should().Be(0);

        // AuditLog must record the purge — the row stays even though customer data is gone.
        var purgeAudit = await verifyDb.AuditLogs.CountAsync(
            a => a.EventType == "customer.purged" && a.TargetId == customerId.ToString());
        purgeAudit.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Purge_with_wrong_confirmation_email_returns_400()
    {
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        string adminToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
            var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
            var admin = new OrderDeck.LicenseServer.Domain.AdminUser
            {
                Id = Guid.NewGuid(),
                Username = $"a-{Guid.NewGuid():N}",
                PasswordHash = hasher.Hash("x"),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.AdminUsers.Add(admin);
            await db.SaveChangesAsync();
            adminToken = jwt.IssueAdminToken(admin.Id, admin.Username).Token;
        }

        using var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var purge = await adminClient.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge",
            new { confirmEmail = "wrong@example.com" });
        purge.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"entry {name} missing");
        await using var s = entry.Open();
        using var sr = new StreamReader(s);
        return await sr.ReadToEndAsync();
    }
}
