using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// CampaignPause duraklatma yazımı kampanya çakışmasıyla düşse bile alıcının
/// <c>pending</c> dönüşü KAYBOLMAMALI. Eski davranış kampanyayı Detach edip
/// dönüyordu; alıcı diskte <c>sending</c> kalıyor ve devam ettirilen
/// kampanyada sessizce atlanıyordu (kitleden düşme — 2026-09-21 denetim P2).
///
/// Gerçek SQL Server ŞART: InMemory'de transaction yok — SaveChanges kampanya
/// çakışmasında patlasa bile alıcı yazımı KISMEN diske iner ve test sahte
/// yeşil yanar. Gerçek SQL tüm SaveChanges'i geri alır; delik yalnız burada
/// görünür (aynı ders: IysConsentUniqueIndexTests).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SmsCampaignPauseRaceRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public SmsCampaignPauseRaceRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Duraklatma_yarisi_kaybedilse_de_alici_pending_e_doner()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var (campaignId, phones) = await SeedAsync(db, accounts);
        _factory.TenantSms.FailWith[phones[1]] = new NetgsmSmsException("85", "bakiye yetersiz");

        // Rakip yazım: gönderim düşerken (sonuç yazılmadan ÖNCE) kampanya
        // damgası başka bir bağlamdan ilerletilir — job'un pending+paused
        // SaveChanges'i kampanya üstünde DbUpdateConcurrencyException alır.
        var rivalRan = false;
        _factory.TenantSms.OnFailing = _ =>
        {
            using var rival = _factory.Services.CreateScope();
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var c = rdb.SmsCampaigns.Single(x => x.Id == campaignId);
            c.Status = "paused";
            c.ClaimedAt = (c.ClaimedAt ?? DateTimeOffset.UtcNow).AddTicks(1);
            rdb.SaveChanges();
            rivalRan = true;
        };

        await job.RunAsync(campaignId);

        rivalRan.Should().BeTrue("rakip yazım koşmadıysa test hiçbir şey kanıtlamaz");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var recipients = await vdb.SmsCampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).ToListAsync();

        // Job'un alıcı sorgusunda OrderBy yok — SQL hangi alıcıyı önce
        // verirse o işlenir. Düşen alıcı İLK işlendiyse kampanya duraklar
        // ve diğerine hiç sıra gelmez ("pending" kalır); SON işlendiyse
        // ilki "sent" olur. İkisi de doğru — asıl değişmez şu: kimse
        // "sending"de MAHSUR kalmaz.
        recipients.Single(r => r.Phone == phones[0]).Status
            .Should().BeOneOf(new[] { "sent", "pending" },
                "işleme sırası belirsiz — ya gönderildi ya hiç sıra gelmedi; "
                + "'sending'de kalmak tek yanlış sonuç");
        recipients.Single(r => r.Phone == phones[1]).Status.Should().Be("pending",
            "temiz ret — hiçbir şey gitmedi; duraklatma yarışı kaybedilse de "
            + "alıcı 'sending'de mahsur kalırsa kitleden sessizce düşer");

        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId))
            .Status.Should().Be("paused",
                "rakibin kararı korunur — kim yazarsa yazsın sonuç duraklatma");
    }

    /// <summary>Lisans + doğrulanmış Netgsm hesabı + 2 alıcılı kampanya +
    /// her telefona Onay/Onay İYS satırı. Kimlikler ÜRETİLİR (repo public).
    /// Kolon uzunlukları gerçek SQL'de kesilmesin diye kırpılır.</summary>
    private static async Task<(Guid CampaignId, string[] Phones)> SeedAsync(
        LicenseDbContext db, NetgsmAccountService accounts)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"pr-{customerId:N}@x",
            Name = "Yaris",
            PasswordHash = "h",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-PR-{Guid.NewGuid():N}",
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}"[..32],
            PasswordProtected = accounts.ProtectPassword($"p-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var phones = Enumerable.Range(0, 2)
            .Select(_ => $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}")
            .ToArray();
        var now = DateTimeOffset.UtcNow;
        foreach (var p in phones)
        {
            db.IysConsents.Add(new IysConsent
            {
                Id = Guid.NewGuid(),
                BrandCode = brandCode,
                ChannelType = "MESAJ",
                RecipientType = "BIREYSEL",
                Recipient = p,
                Status = IysConsentStatus.Onay,
                LastVerifiedStatus = IysConsentStatus.Onay,
                LastVerifiedAt = now,
                PushState = IysPushState.Confirmed,
                LastLocalEventAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var campaignId = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId,
            LicenseId = licenseId,
            MessageBody = "Yaris testi",
            Status = "pending",
            SegmentsPerMessage = 1,
            RecipientCount = phones.Length,
            CreatedAt = now,
        });
        foreach (var p in phones)
        {
            db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Phone = p,
                Status = "pending",
            });
        }

        await db.SaveChangesAsync();
        return (campaignId, phones);
    }
}
