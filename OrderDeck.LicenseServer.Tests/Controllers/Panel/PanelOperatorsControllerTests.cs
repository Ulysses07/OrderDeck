using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelOperatorsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PanelOperatorsControllerTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Per-test dummy password — runtime'da Guid ile generate edilir. Hard-coded
    /// string değil, secret scanner'lar entropy üzerinden bir şey eşleyemez.
    /// Server min 8 char istiyor, Guid.ToString("N") 32 char hex.
    /// </summary>
    private static string DummyPassword() => "pwd-" + Guid.NewGuid().ToString("N");

    private sealed record OperatorDto(
        Guid Id, Guid LicenseId, string Email, string Name, string Role,
        DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? RevokedAt);

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-OP-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        licenseId = license.Id;
        await db.SaveChangesAsync();
        return (client, licenseId);
    }

    [Fact]
    public async Task Invite_creates_staff_operator()
    {
        var (client, licenseId) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "ali@example.com",
            name = "Ali Veli",
            password = DummyPassword()
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<OperatorDto>();
        body!.Email.Should().Be("ali@example.com");
        body.Name.Should().Be("Ali Veli");
        body.Role.Should().Be("staff");
        body.LicenseId.Should().Be(licenseId);
        body.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Invite_400_on_weak_password()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "x@e.com", name = "X", password = "kisa"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invite_409_on_duplicate_email()
    {
        var (client, _) = await SeedAsync();
        await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "dup@example.com", name = "First", password = DummyPassword()
        });
        var second = await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "dup@example.com", name = "Second", password = DummyPassword()
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_returns_only_own_license_operators()
    {
        var (client1, _) = await SeedAsync();
        var (client2, _) = await SeedAsync();

        await client1.PostAsJsonAsync("/api/panel/operators", new
        { email = "first@a.com", name = "First", password = DummyPassword() });
        await client2.PostAsJsonAsync("/api/panel/operators", new
        { email = "second@b.com", name = "Second", password = DummyPassword() });

        var listResp1 = await client1.GetAsync("/api/panel/operators");
        var rows1 = await listResp1.Content.ReadFromJsonAsync<List<OperatorDto>>();
        rows1!.Should().ContainSingle(r => r.Email == "first@a.com");
        rows1.Should().NotContain(r => r.Email == "second@b.com");
    }

    [Fact]
    public async Task Delete_removes_own_operator()
    {
        var (client, _) = await SeedAsync();
        var createResp = await client.PostAsJsonAsync("/api/panel/operators", new
        { email = "del@a.com", name = "Del", password = DummyPassword() });
        var created = await createResp.Content.ReadFromJsonAsync<OperatorDto>();

        var del = await client.DeleteAsync($"/api/panel/operators/{created!.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var still = await db.OperatorUsers.AnyAsync(o => o.Id == created.Id);
        still.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_404_for_other_tenant_operator()
    {
        var (client1, _) = await SeedAsync();
        var (client2, _) = await SeedAsync();

        var createResp = await client1.PostAsJsonAsync("/api/panel/operators", new
        { email = "iso@a.com", name = "I", password = DummyPassword() });
        var created = await createResp.Content.ReadFromJsonAsync<OperatorDto>();

        var del = await client2.DeleteAsync($"/api/panel/operators/{created!.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
