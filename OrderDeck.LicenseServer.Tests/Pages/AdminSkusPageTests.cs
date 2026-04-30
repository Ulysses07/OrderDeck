using System.Net;
using AngleSharp;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages;

public sealed class AdminSkusPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminSkusPageTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Index_lists_seeded_skus()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/skus");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
        var rows = doc.QuerySelectorAll("table[data-table='skus'] tbody tr");
        rows.Length.Should().BeGreaterThanOrEqualTo(2);   // STD + PRO seed minimum
        html.Should().Contain("STD");
        html.Should().Contain("PRO");
    }
}
