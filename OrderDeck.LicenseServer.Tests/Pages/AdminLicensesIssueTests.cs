using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages;

public sealed class AdminLicensesIssueTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminLicensesIssueTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_issue_form_lists_skus()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/licenses/issue");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("STD");
        html.Should().Contain("PRO");
    }

    [Fact]
    public async Task Post_issue_creates_license_audit_and_redirects()
    {
        var custEmail = $"issue-{Guid.NewGuid():N}@x";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Email = custEmail, Name = "I", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var getResp = await client.GetAsync("/admin/licenses/issue");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.CustomerEmail"] = custEmail,
            ["Input.SkuCode"] = "STD"
        });
        var postResp = await client.PostAsync("/admin/licenses/issue", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResp.Headers.Location!.ToString().ToLowerInvariant().Should().Contain("/admin/licenses/ldk-");

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = await db2.Customers.FirstAsync(c => c.Email == custEmail);
        var license = await db2.Licenses.FirstAsync(l => l.CustomerId == customer.Id);
        license.SkuCode.Should().Be("STD");

        var audit = await db2.AuditLogs
            .Where(a => a.EventType == "license.issue" && a.TargetId == license.LicenseKey)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
        audit!.Details.Should().Contain(custEmail);
    }

    [Fact]
    public async Task Post_issue_with_unknown_customer_shows_error()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var getResp = await client.GetAsync("/admin/licenses/issue");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.CustomerEmail"] = "nope@x.com",
            ["Input.SkuCode"] = "STD"
        });
        var postResp = await client.PostAsync("/admin/licenses/issue", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await postResp.Content.ReadAsStringAsync();
        html.Should().Contain("Email yok");
    }
}
