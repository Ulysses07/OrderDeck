using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

public class DbContextTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public DbContextTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void Database_seeded_with_two_skus()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var skus = db.Skus.OrderBy(s => s.Code).ToList();
        skus.Should().HaveCount(2);
        skus[0].Code.Should().Be("PRO");
        skus[0].DefaultActivationSlots.Should().Be(3);
        skus[1].Code.Should().Be("STD");
        skus[1].DefaultActivationSlots.Should().Be(1);
    }
}
