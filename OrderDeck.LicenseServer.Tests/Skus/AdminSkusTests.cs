using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Skus;

public class AdminSkusTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminSkusTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task List_returns_seeded_skus()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/api/v1/admin/skus");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var skus = await resp.Content.ReadFromJsonAsync<List<SkuBody>>();
        skus.Should().NotBeNull();
        skus!.Should().Contain(s => s.code == "STD");
        skus.Should().Contain(s => s.code == "PRO");
    }

    private sealed record SkuBody(string code, string displayName, int defaultDurationDays, int defaultActivationSlots);
}
