using System.Net.Http.Json;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public static class CustomerAuthHelper
{
    public static async Task<(HttpClient client, Guid customerId, string jwt)> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var email = $"backup-test-{Guid.NewGuid():N}@test.com";
        var password = "TestPass1234!";

        var client = factory.CreateClient();

        // Register
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, name = "Backup Test", password });
        reg.EnsureSuccessStatusCode();

        // Force-confirm via DB (skip email click for tests)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Email == email);
            customer.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Login → JWT
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var loginBody = await login.Content.ReadFromJsonAsync<LoginResp>();
        var jwt = loginBody!.Token;

        // Resolve customerId
        Guid customerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            customerId = (await db.Customers.FirstAsync(c => c.Email == email)).Id;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, customerId, jwt);
    }

    private sealed record LoginResp(string Token, DateTimeOffset ExpiresAt);
}
