using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Admin;

public class AdminBackupSummaryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminBackupSummaryTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetSummary_AsAdmin_RendersAggregates()
    {
        var (customerId, backupId) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);

        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await client.GetAsync($"/Admin/Customers/{customerId}/backups/{backupId}/summary");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        html.Should().Contain("Toplam Ciro");
        html.Should().Contain("150,00 TL");  // sample seed total
        html.Should().Contain("alice");      // top customer
    }

    [Fact]
    public async Task GetSummary_NonExistentBackup_Returns404()
    {
        var (customerId, _) = await BackupSeedHelper.SeedSampleBackupAsync(_factory);
        var client = await _factory.CreateLoggedInAdminClientAsync();
        var resp = await client.GetAsync(
            $"/Admin/Customers/{customerId}/backups/{Guid.NewGuid()}/summary");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
