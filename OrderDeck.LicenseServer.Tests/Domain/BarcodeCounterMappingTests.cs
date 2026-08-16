using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class BarcodeCounterMappingTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Sayac_lisans_basina_tek_satirdir()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();

        db.BarcodeCounters.Add(new BarcodeCounter { LicenseId = licenseId, Next = 1 });
        await db.SaveChangesAsync();

        var row = await db.BarcodeCounters.FindAsync(licenseId);
        row!.Next.Should().Be(1);
    }
}
