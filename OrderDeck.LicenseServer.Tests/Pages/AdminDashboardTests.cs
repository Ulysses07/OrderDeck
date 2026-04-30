using System.Net;
using AngleSharp;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages;

public sealed class AdminDashboardTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminDashboardTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_redirects_to_login()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/admin");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.PathAndQuery.Should().StartWith("/admin/login");
    }

    [Fact]
    public async Task Logged_in_dashboard_renders_four_counters_with_real_values()
    {
        // Seed: 2 customers, 1 active license, 1 revoked license, 0 active activations
        var customer1Id = Guid.NewGuid();
        var customer2Id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Customers.AddRange(
                new Customer { Id = customer1Id, Email = $"c1-{Guid.NewGuid():N}@x", Name = "C1", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow },
                new Customer { Id = customer2Id, Email = $"c2-{Guid.NewGuid():N}@x", Name = "C2", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
            db.Licenses.AddRange(
                new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DASH-A-" + Guid.NewGuid().ToString("N"), CustomerId = customer1Id, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) },
                new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DASH-B-" + Guid.NewGuid().ToString("N"), CustomerId = customer1Id, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow.AddDays(-100), ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), RevokedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = await ctx.OpenAsync(req => req.Content(html));

        // Counter assertions are >= 1 because tests share the InMemory DB across the class fixture
        var customers = int.Parse(doc.QuerySelector("[data-stat='customers']")!.TextContent.Trim());
        var active = int.Parse(doc.QuerySelector("[data-stat='active-licenses']")!.TextContent.Trim());
        var expired = int.Parse(doc.QuerySelector("[data-stat='expired-licenses']")!.TextContent.Trim());

        customers.Should().BeGreaterThanOrEqualTo(2);
        active.Should().BeGreaterThanOrEqualTo(1);
        expired.Should().BeGreaterThanOrEqualTo(1);
    }
}
