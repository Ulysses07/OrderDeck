using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// Markasız dönem (sahip kararı 10) gerçek SQL Server'da. InMemory C# null
/// anlamını kullanır; burada iki şey ancak ilişkisel sağlayıcıda kanıtlanır:
/// (1) markasız randevu imhası <c>BrandCode IS NULL</c> olaylardan YALNIZ kendi
/// lisansınınkileri seçer (eşitlik yazılsaydı parametre null'da IS NULL olur,
/// bütün lisansları seçerdi), (2) lisans başına en yeni olayın gruplanması
/// SQL'e çevrilir ve nullable sütun göçü uygulanır (fabrika açılışı Migrate).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IysDepartureRetentionRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public IysDepartureRetentionRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static IysConsentEvent NoBrandEvent(Guid licenseId, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = null,
        Recipient = TestPhone.NewE164(), OccurredAt = at,
        EventType = IysConsentEventType.LocalRevoke, Status = IysConsentStatus.Ret,
        SourceTable = "Shopper", SourceId = Guid.NewGuid(), ErrorCode = "no-brand",
    };

    [Fact]
    public async Task Markasiz_donem_SQL_Serverda_yalniz_kendi_lisansini_imha_eder_ve_silinen_lisansa_takvim_acar()
    {
        var expiredAt = DateTimeOffset.UtcNow - (IysDepartureRetentionJob.ProofRetention
            + TimeSpan.FromDays(1));
        var expiredLicense = Guid.NewGuid();
        var freshlyDeletedLicense = Guid.NewGuid();
        var expired = NoBrandEvent(expiredLicense, expiredAt.AddDays(-3));
        var fresh = NoBrandEvent(freshlyDeletedLicense, expiredAt.AddDays(-3));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.NetgsmDepartures.Add(new NetgsmDeparture
            {
                Id = Guid.NewGuid(), LicenseId = expiredLicense, BrandCode = null,
                DepartedAt = expiredAt, ConsentsDeletedAt = expiredAt,
            });
            db.IysConsentEvents.AddRange(expired, fresh);
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IysDepartureRetentionJob>()
                .RunAsync(CancellationToken.None);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.IysConsentEvents.AnyAsync(e => e.Id == expired.Id))
                .Should().BeFalse("süresi dolan markasız randevunun ispatı imha edilir");
            (await db.IysConsentEvents.AnyAsync(e => e.Id == fresh.Id))
                .Should().BeTrue("başka lisansın markasız olayı aynı tarihli olsa da bu imhaya karışmaz");
            (await db.NetgsmDepartures.SingleAsync(d => d.LicenseId == expiredLicense))
                .PurgedAt.Should().NotBeNull();
            var opened = await db.NetgsmDepartures.SingleAsync(d => d.LicenseId == freshlyDeletedLicense);
            opened.BrandCode.Should().BeNull();
            opened.PurgedAt.Should().BeNull();
            (await db.DataDestructionRecords.SingleAsync(
                    r => r.LicenseId == expiredLicense && r.Target == "IysConsentEvents"))
                .RowCount.Should().Be(1);
        }
    }
}
