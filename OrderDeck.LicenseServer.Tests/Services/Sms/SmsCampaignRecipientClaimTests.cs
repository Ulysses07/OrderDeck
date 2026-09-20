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
/// Görev 16 — alıcı başına atomik talep. Gönderim işi bekleyen alıcıların
/// TAMAMINI baştan belleğe alıyor; işçi A 1. alıcıya SMS gönderip sonucu henüz
/// diske indirmemişken kampanya devralınırsa (duraklat+devam ettir ya da bayat
/// kira), işçi B aynı alıcıyı hâlâ <c>pending</c> görür ve aynı kişiye ikinci
/// ticari SMS gider. Tur başı sahiplik yoklaması bunu kapatmaz: o kontrol
/// gönderimden ÖNCE koşuyor, yarış ise gönderim ile kayıt ARASINDA — klasik
/// TOCTOU.
///
/// <para>Buradaki testler yaşam döngüsünün <c>pending → sending → sent|failed</c>
/// hâline gelmesini ve <c>sending</c> talebinin eşzamanlılık jetonuyla
/// korunmasını çiviliyor.</para>
/// </summary>
public sealed class SmsCampaignRecipientClaimTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SmsCampaignRecipientClaimTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.TenantSms.Clear();
    }

    private static string NewPhone()
        => $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}";

    /// <summary>
    /// Talep gönderimden ÖNCE diske inmeli. Sıralama ters olursa çökme anında
    /// hiçbir koruma kalmaz: satır <c>pending</c> durur, devralan işçi aynı
    /// kişiye ikinci kez gönderir.
    /// </summary>
    [Fact]
    public async Task Alici_gonderimden_once_sending_olarak_talep_edilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(
            db, scope.ServiceProvider.GetRequiredService<NetgsmAccountService>(),
            recipientCount: 1);

        // Gözlem AYRI scope'tan: işçinin kendi bağlamından okursak izlenen
        // (henüz kaydedilmemiş olabilecek) kopyayı görürüz, DİSKTEKİNİ değil.
        // Assertion kancanın içinde YAPILMIYOR — orada atılan istisnayı işin
        // catch'i yutar ve alıcıyı "failed" yazar, yani test yalan söylerdi.
        string? statusDuringSend = null;
        DateTimeOffset? claimedAtDuringSend = null;
        _factory.TenantSms.OnSent = _ =>
        {
            using var s2 = _factory.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var r = db2.SmsCampaignRecipients.AsNoTracking()
                .Single(x => x.CampaignId == campaignId);
            statusDuringSend = r.Status;
            claimedAtDuringSend = r.ClaimedAt;
        };

        try { await job.RunAsync(campaignId); }
        finally { _factory.TenantSms.OnSent = null; }

        statusDuringSend.Should().Be("sending",
            "fiziksel gönderim yapıldığı anda satır zaten talep edilmiş olmalı");
        claimedAtDuringSend.Should().NotBeNull("talep damgası CAS'ın kendisi");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var after = await vdb.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        after.Status.Should().Be("sent", "talep sonucu ezmemeli");
    }

    /// <summary>
    /// Görevin kalbi: talep yazımı CAS'tan geçmeli. Başka bir işçi satırı
    /// kapmışsa bizim yazımımız çakışma almalı ve gönderim HİÇ yapılmamalı.
    /// </summary>
    [Fact]
    public async Task Baskasinin_talep_ettigi_alici_gonderilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, phones) = await SeedAsync(
            db, scope.ServiceProvider.GetRequiredService<NetgsmAccountService>(),
            recipientCount: 2);

        // 1. alıcının gönderimi sırasında rakip işçi 2. alıcıyı kapıyor.
        // Kampanyaya DOKUNMUYOR bilerek: kampanya sahipliği kaybolsaydı koşu
        // zaten tur başı yoklamada dururdu ve alıcı jetonunu hiç sınamazdık.
        string? stolenPhone = null;
        _factory.TenantSms.OnSent = msg =>
        {
            if (stolenPhone is not null) return;
            using var rival = _factory.Services.CreateScope();
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var other = rdb.SmsCampaignRecipients
                .Single(r => r.CampaignId == campaignId && r.Phone != msg.Phone);
            other.Status = "sending";
            other.ClaimedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            rdb.SaveChanges();
            stolenPhone = other.Phone;
        };

        try { await job.RunAsync(campaignId); }
        finally { _factory.TenantSms.OnSent = null; }

        stolenPhone.Should().NotBeNull("kanca koşmadıysa test hiçbir şey kanıtlamaz");
        _factory.TenantSms.Sent.Should().HaveCount(1,
            "2 olursa aynı alıcıya iki işçi birden göndermiş demektir");
        _factory.TenantSms.Sent.Should().NotContain(m => m.Phone == stolenPhone);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stolen = await vdb.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId && r.Phone == stolenPhone);
        stolen.Status.Should().Be("sending", "satır rakibin, sonucu biz yazamayız");
        stolen.SentAt.Should().BeNull();

        var sentPhone = phones.Single(p => p != stolenPhone);
        (await vdb.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId && r.Phone == sentPhone))
            .Status.Should().Be("sent");
    }

    /// <summary>
    /// <c>sending</c>'de takılı kalan alıcı BİLİNÇLİ olarak kurtarılmaz.
    /// <c>pending</c>'e döndürmek gitmiş olabilecek bir SMS'i ikinci kez
    /// göndermek olurdu; <c>failed</c> saymak ise gitmiş olabilecek bir
    /// SMS'i arıza gibi göstermek. İkisi de yanlış yönde hata.
    /// </summary>
    [Fact]
    public async Task Sending_kalan_alici_tekrar_gonderilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, phones) = await SeedAsync(
            db, scope.ServiceProvider.GetRequiredService<NetgsmAccountService>(),
            recipientCount: 2);

        // Önceki koşu 1. alıcıya gönderim yaptı ama sonucu yazamadan öldü;
        // 2. alıcı düzgünce "failed" kapandı.
        var recipients = await db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaignId).ToListAsync();
        var stuck = recipients.Single(r => r.Phone == phones[0]);
        stuck.Status = "sending";
        stuck.ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var failed = recipients.Single(r => r.Phone == phones[1]);
        failed.Status = "failed";
        failed.Error = "provider-rejected";
        await db.SaveChangesAsync();

        await job.RunAsync(campaignId);

        _factory.TenantSms.Sent.Should().BeEmpty(
            "gitmiş OLABİLECEK bir mesaj ikinci kez gönderilemez");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await vdb.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId && r.Phone == phones[0]))
            .Status.Should().Be("sending", "belirsiz satır olduğu gibi kalır");

        var campaign = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("completed");
    }

    /// <summary>
    /// Lisans + doğrulanmış Netgsm hesabı + onaylı alıcılı kampanya.
    /// Kapı (İYS) açık tohumlanıyor: burada sınanan şey talep mekaniği,
    /// izin kapısı değil. Şifre gerçek koruma kalıbıyla yazılır — gönderim
    /// kapısı artık şifreyi çözüyor, düz metin seed hesabı kapatırdı.
    /// </summary>
    private static async Task<(Guid CampaignId, Guid LicenseId, string[] Phones)> SeedAsync(
        LicenseDbContext db, NetgsmAccountService accounts, int recipientCount)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"claim-{Guid.NewGuid():N}@example.com",
            Name = "Talep",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-CLAIM-{Guid.NewGuid():N}",
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
            UserCode = Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString(),
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var phones = Enumerable.Range(0, recipientCount).Select(_ => NewPhone()).ToArray();
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
                LastVerifiedAt = DateTimeOffset.UtcNow,
                PushState = IysPushState.Confirmed,
                LastLocalEventAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var campaignId = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId,
            LicenseId = licenseId,
            MessageBody = "Talep testi",
            Status = "pending",
            SegmentsPerMessage = 1,
            RecipientCount = phones.Length,
            CreatedByCustomerId = customerId,
            CreatedAt = DateTimeOffset.UtcNow,
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
        return (campaignId, licenseId, phones);
    }
}
