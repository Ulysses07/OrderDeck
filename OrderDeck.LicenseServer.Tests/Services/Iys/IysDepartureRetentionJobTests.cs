using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// §6 + m.13 saklama takvimi. Paylaşımlı ApiFactory DB'sinde iş bütün
/// tabloyu tarar — assert'ler KENDİ marka/id'leriyle sınırlı tutulmalı.
/// </summary>
public sealed class IysDepartureRetentionJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IysDepartureRetentionJobTests(ApiFactory factory) => _factory = factory;

    private static string NewBrandCode()
        => Random.Shared.Next(100_000, 999_999).ToString();
    private static string NewPhone()
        => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<(Guid LicenseId, Guid AccountId, string BrandCode)>
        SeedDepartedAsync(LicenseDbContext db, NetgsmAccountService accounts,
            DateTimeOffset disabledAt)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Ayrilan",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-{Guid.NewGuid():N}"[..24],
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        });
        var brandCode = NewBrandCode();
        var accountId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = accountId,
            LicenseId = licenseId,
            UserCode = $"user-{Guid.NewGuid():N}"[..32],
            PasswordProtected = accounts.ProtectPassword($"p-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Disabled,
            DisabledAt = disabledAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, accountId, brandCode);
    }

    private static void AddConsent(LicenseDbContext db, string brand, string phone)
    {
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(), BrandCode = brand, ChannelType = "MESAJ",
            RecipientType = "BIREYSEL", Recipient = phone,
            Status = IysConsentStatus.Onay, PushState = IysPushState.Confirmed,
            LastLocalEventAt = now, CreatedAt = now, UpdatedAt = now,
        });
    }

    [Fact]
    public async Task Otuz_gun_dolunca_onaylar_ve_hesap_silinir_ayrilis_kaydi_acilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var disabledAt = DateTimeOffset.UtcNow.AddDays(-31);
        var (licenseId, accountId, brand) = await SeedDepartedAsync(db, accounts, disabledAt);
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeFalse();
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId)).Should().BeFalse();
        var dep = await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand);
        dep.LicenseId.Should().Be(licenseId);
        dep.DepartedAt.Should().BeCloseTo(disabledAt, TimeSpan.FromSeconds(1),
            "m.13 3 yıl ayrılış ANINDAN sayılır, silme anından değil");
        dep.PurgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Otuz_gun_dolmadan_hicbir_sey_silinmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (_, accountId, brand) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow.AddDays(-29));
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeTrue();
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId)).Should().BeTrue();
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == brand)).Should().BeFalse();
    }

    [Fact]
    public async Task Marka_baska_canli_hesapta_yasiyorsa_onaylar_kalir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (_, accountId, brand) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow.AddDays(-31));
        // Aynı markayı taşıyan CANLI ikinci hesap (başka lisans).
        var (_, aliveAccountId, _) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow); // seed Disabled açar, aşağıda düzeltilir
        var alive = await db.NetgsmAccounts.SingleAsync(a => a.Id == aliveAccountId);
        alive.BrandCode = brand;
        alive.Status = NetgsmAccountStatus.Verified;
        alive.DisabledAt = null;
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand))
            .Should().BeTrue("marka canlı hesapta yaşıyor — onaylar ONUN");
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId))
            .Should().BeFalse("ayrılan hesabın satırı yine silinir");
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == brand))
            .Should().BeFalse("marka ölmedi — imha takvimi açılmaz");
    }

    [Fact]
    public async Task Yetim_marka_onaylari_silinir_ve_ayrilis_kaydi_acilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var brand = NewBrandCode();
        var licenseId = Guid.NewGuid();
        var phone = NewPhone();
        AddConsent(db, brand, phone);
        // LicenseId geri kazanımı için olay izi (hesap yok, olay var).
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            Recipient = phone, OccurredAt = DateTimeOffset.UtcNow.AddDays(-5),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeFalse();
        var dep = await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand);
        dep.LicenseId.Should().Be(licenseId, "olay izinden geri kazanılır");
        dep.PurgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Uc_yil_dolunca_donem_ispati_ve_kampanyalar_imha_edilir_yeni_donem_yasar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var departedAt = DateTimeOffset.UtcNow - (IysDepartureRetentionJob.ProofRetention
            + TimeSpan.FromDays(1));
        var (licenseId, accountId, brand) = await SeedDepartedAsync(db, accounts, departedAt);
        // 30-gün fazı bu hesabı işlemesin: hesabı elle kaldırıp takvim kaydını
        // doğrudan açıyoruz (Faz 3'ü tek başına test etmek için).
        db.NetgsmAccounts.Remove(await db.NetgsmAccounts.SingleAsync(a => a.Id == accountId));
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            DepartedAt = departedAt,
            ConsentsDeletedAt = departedAt.AddDays(30),
        });
        var phone = NewPhone();
        // Dönem İÇİ olay (ayrılıştan önce) → imha edilecek.
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            Recipient = phone, OccurredAt = departedAt.AddDays(-10),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        // Dönem DIŞI olay (ayrılıştan sonra — markayı devralan yeni dönem) → yaşar.
        var newEraEventId = Guid.NewGuid();
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = newEraEventId, LicenseId = Guid.NewGuid(), BrandCode = brand,
            Recipient = NewPhone(), OccurredAt = departedAt.AddDays(10),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        // Dönem içi kampanya + alıcısı → imha edilecek.
        var campaignId = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId, LicenseId = licenseId, MessageBody = "Eski donem",
            Status = "sent", SegmentsPerMessage = 1, RecipientCount = 1,
            CreatedAt = departedAt.AddDays(-20),
        });
        db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Phone = phone, Status = "sent",
        });
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsentEvents.AnyAsync(
                e => e.BrandCode == brand && e.OccurredAt <= departedAt))
            .Should().BeFalse("dönem ispatı m.13 süresi dolunca imha edilir");
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == newEraEventId))
            .Should().BeTrue("yeni dönemin ispatı ESKİ ayrılışın imhasına karışmaz");
        (await vdb.SmsCampaigns.AnyAsync(c => c.Id == campaignId)).Should().BeFalse();
        (await vdb.SmsCampaignRecipients.AnyAsync(r => r.CampaignId == campaignId))
            .Should().BeFalse();
        (await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand))
            .PurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Uc_yil_dolmadan_ispat_yasar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var brand = NewBrandCode();
        var licenseId = Guid.NewGuid();
        var departedAt = DateTimeOffset.UtcNow.AddYears(-2);
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            DepartedAt = departedAt, ConsentsDeletedAt = departedAt.AddDays(30),
        });
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = brand,
            Recipient = NewPhone(), OccurredAt = departedAt.AddDays(-1),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsentEvents.AnyAsync(e => e.BrandCode == brand))
            .Should().BeTrue("m.13: 3 yıl dolmadan ispat İMHA EDİLEMEZ");
        (await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand))
            .PurgedAt.Should().BeNull();
    }
}
