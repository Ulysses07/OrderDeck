using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class CustomerBackupEntityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public CustomerBackupEntityTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void Model_HasCustomerBackupEntity_WithRequiredProps()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var entityType = db.Model.FindEntityType(typeof(CustomerBackup));
        entityType.Should().NotBeNull();

        entityType!.FindProperty(nameof(CustomerBackup.BlobPath))!
            .GetMaxLength().Should().Be(500);
        entityType.FindProperty(nameof(CustomerBackup.ChecksumSha256))!
            .GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(CustomerBackup.IsMonthlyMilestone))!
            .IsNullable.Should().BeFalse();
    }

    [Fact]
    public void Index_OnCustomerIdAndCreatedAt_Exists()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var entityType = db.Model.FindEntityType(typeof(CustomerBackup));
        var indexes = entityType!.GetIndexes();
        indexes.Should().Contain(i =>
            i.GetDatabaseName() == "IX_CustomerBackups_CustomerId_CreatedAt_DESC");
    }
}
