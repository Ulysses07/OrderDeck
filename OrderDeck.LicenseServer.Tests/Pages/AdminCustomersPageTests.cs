using System.Net;
using AngleSharp;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminCustomersPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminCustomersPageTests(ApiFactory factory) => _factory = factory;

    private async Task<Guid> SeedCustomerAsync(string email, string name = "Test Customer")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer { Id = Guid.NewGuid(), Email = email, Name = name, PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    [Fact]
    public async Task Index_lists_customers()
    {
        var email = $"list-{Guid.NewGuid():N}@x";
        await SeedCustomerAsync(email);
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var resp = await client.GetAsync("/admin/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(email);
    }

    [Fact]
    public async Task Index_search_filters_to_matching_email()
    {
        var unique = "find-" + Guid.NewGuid().ToString("N");
        await SeedCustomerAsync(unique + "@x");
        await SeedCustomerAsync($"other-{Guid.NewGuid():N}@x");
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var resp = await client.GetAsync($"/admin/customers?search={unique}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
        var rows = doc.QuerySelectorAll("table[data-table='customers'] tbody tr");
        rows.Length.Should().Be(1);
    }

    [Fact]
    public async Task Detail_shows_customer_with_licenses()
    {
        var custId = await SeedCustomerAsync($"detail-{Guid.NewGuid():N}@x", "Detail Customer");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DET-" + Guid.NewGuid().ToString("N"), CustomerId = custId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
            await db.SaveChangesAsync();
        }

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync($"/admin/customers/{custId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Detail Customer");
        html.Should().Contain("LDK-DET-");
    }

    [Fact]
    public async Task ConfirmEmail_sets_EmailConfirmedAt_and_writes_audit()
    {
        var custId = await SeedCustomerAsync($"confirm-{Guid.NewGuid():N}@x");
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        // GET detail to grab anti-forgery token
        var getResp = await client.GetAsync($"/admin/customers/{custId}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });
        var postResp = await client.PostAsync($"/admin/customers/{custId}?handler=ConfirmEmail", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = await db.Customers.FirstAsync(c => c.Id == custId);
        customer.EmailConfirmedAt.Should().NotBeNull();

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "customer.confirm-email" && a.TargetId == custId.ToString())
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
    }
}
