using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// §2.5 son halkası — kurulum geri açılıp doğrulandığında duraklatılmış
/// kampanya devam eder. Etmezse kredisi rezerve edilmiş, alıcıları "pending"
/// bir kampanya sonsuza dek asılı kalır: yayıncı ne gönderim görür ne iade.
/// </summary>
public sealed class NetgsmAccountResumeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public NetgsmAccountResumeTests(ApiFactory factory) => _factory = factory;

    private static async Task<(Guid LicenseId, Guid PausedId, Guid CompletedId)> SeedAsync(
        LicenseDbContext db)
    {
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-RS-{Guid.NewGuid():N}",
            CustomerId = Guid.NewGuid(),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        Guid Add(string status, DateTimeOffset? claimedAt)
        {
            var id = Guid.NewGuid();
            db.SmsCampaigns.Add(new SmsCampaign
            {
                Id = id,
                LicenseId = licenseId,
                MessageBody = "Kampanya",
                Status = status,
                ClaimedAt = claimedAt,
                SegmentsPerMessage = 1,
                RecipientCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
            return id;
        }

        var pausedId = Add("paused", DateTimeOffset.UtcNow.AddHours(-1));
        var completedId = Add("completed", null);
        await db.SaveChangesAsync();
        return (licenseId, pausedId, completedId);
    }

    [Fact]
    public async Task Devam_ettirme_paused_kampanyayi_pending_yapar_ve_jetonu_ilerletir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (licenseId, pausedId, completedId) = await SeedAsync(db);

        var previous = (await db.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == pausedId)).ClaimedAt!.Value;

        // Metot kaydetmiyor — çağıranın SaveChanges'ine biniyor. Gerçek
        // çağıran (panel PUT'u) hesabın Verified yazımıyla aynı kayıtta
        // birleştiriyor; test o rolü üstleniyor.
        var resumed = await svc.StageResumePausedCampaignsAsync(licenseId, CancellationToken.None);
        await db.SaveChangesAsync();

        resumed.Should().Be(1);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var paused = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == pausedId);
        paused.Status.Should().Be("pending");
        paused.ClaimedAt.Should().NotBeNull();
        paused.ClaimedAt!.Value.Should().BeAfter(previous,
            "jeton monoton artmalı; null'a çekmek zinciri koparır ve "
            + "duraklatmada bir tick ileri itilmiş damganın GERİSİNDE "
            + "bir değer üretebilir");
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == completedId))
            .Status.Should().Be("completed");
    }

    [Fact]
    public async Task Devam_ettirme_baska_lisansa_dokunmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (licenseId, _, _) = await SeedAsync(db);
        var (_, otherPausedId, _) = await SeedAsync(db);

        await svc.StageResumePausedCampaignsAsync(licenseId, CancellationToken.None);
        await db.SaveChangesAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == otherPausedId))
            .Status.Should().Be("paused");
    }

    [Fact]
    public async Task Duraklatilmis_kampanya_yokken_sifir_doner()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-RS-{Guid.NewGuid():N}",
            CustomerId = Guid.NewGuid(),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();

        (await svc.StageResumePausedCampaignsAsync(licenseId, CancellationToken.None))
            .Should().Be(0);
    }
}
