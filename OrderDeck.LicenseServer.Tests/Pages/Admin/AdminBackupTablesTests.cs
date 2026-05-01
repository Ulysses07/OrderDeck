using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public class AdminBackupTablesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminBackupTablesTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetCustomers_RendersTable_WithSeedData()
    {
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups/{backupId}/customers");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("alice");
        html.Should().Contain("+905551111111");
    }

    [Fact]
    public async Task GetSessions_RendersTable_WithAggregates()
    {
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups/{backupId}/sessions");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Yayın #1");
        html.Should().Contain("150,00 TL");
    }

    [Fact]
    public async Task GetCustomers_WithSearchFilter_FiltersRows()
    {
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await client.GetAsync(
            $"/Admin/Customers/{customerId}/backups/{backupId}/customers?search=alice");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("alice");
    }
}
