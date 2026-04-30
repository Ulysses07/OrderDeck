using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Customers;

public class AdminCustomersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminCustomersTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Create_customer_returns_201()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email = $"new-{Guid.NewGuid():N}@example.com",
            name = "New Customer",
            initialPassword = "initpw1234",
            autoConfirm = true
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_customer_with_existing_email_returns_409()
    {
        var client = await AdminClientAsync();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "First", initialPassword = "pw12345678"
        });

        var resp = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Dup", initialPassword = "pw12345678"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_returns_customers_descending_by_created()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/admin/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmEmail_marks_customer_confirmed()
    {
        var client = await AdminClientAsync();
        var email = $"conf-{Guid.NewGuid():N}@example.com";
        var createResp = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Conf", initialPassword = "pw12345678", autoConfirm = false
        });
        var createBody = await createResp.Content.ReadFromJsonAsync<IdBody>();

        var resp = await client.PostAsync($"/api/v1/admin/customers/{createBody!.id}/confirm-email", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = await db.Customers.FirstAsync(c => c.Email == email);
        c.EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task List_without_admin_token_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/admin/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record IdBody(Guid id);
}
