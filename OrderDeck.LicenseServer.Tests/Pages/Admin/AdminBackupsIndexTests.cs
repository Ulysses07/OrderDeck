using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public class AdminBackupsIndexTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminBackupsIndexTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetIndex_WithoutAuth_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var resp = await client.GetAsync($"/Admin/Customers/{Guid.NewGuid()}/backups");
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetIndex_AsAdmin_ListsCustomerBackups()
    {
        // Seed customer + 2 backups
        Guid customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var c = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"admin-list-{Guid.NewGuid():N}@test.com",
                Name = "T", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(c);
            db.CustomerBackups.AddRange(
                new CustomerBackup
                {
                    Id = Guid.NewGuid(), CustomerId = c.Id,
                    BlobPath = "/tmp/fake1", SizeBytes = 1024,
                    ChecksumSha256 = new string('a', 64),
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
                },
                new CustomerBackup
                {
                    Id = Guid.NewGuid(), CustomerId = c.Id,
                    BlobPath = "/tmp/fake2", SizeBytes = 2048,
                    ChecksumSha256 = new string('b', 64),
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsMonthlyMilestone = true
                });
            await db.SaveChangesAsync();
            customerId = c.Id;
        }

        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        html.Should().Contain("Yedekler");
        html.Should().Contain("MB");
    }
}
