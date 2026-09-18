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
/// Kural 7: alıcı listesi kampanya oluşturulurken donuyor, gönderim dakikalar
/// (kurtarma devralırsa saatler) sonra olabiliyor. Aradaki geri çekme ya da
/// İYS RET'i gönderimi kesmeli — aksi hâlde iznini geri çekmiş kişiye ticari
/// SMS gider ve bu 6563 ihlalidir.
/// </summary>
public sealed class SmsCampaignIysGateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SmsCampaignIysGateTests(ApiFactory factory) => _factory = factory;

    [Theory]
    // (yerel Status, İYS'nin cevabı, gönderilsin mi)
    [InlineData(IysConsentStatus.Onay, IysConsentStatus.Onay, true)]
    [InlineData(IysConsentStatus.Onay, IysConsentStatus.Ret, false)]   // İYS kesti
    [InlineData(IysConsentStatus.Ret, IysConsentStatus.Onay, false)]   // kişi geri çekti
    [InlineData(IysConsentStatus.Unknown, IysConsentStatus.Onay, false)]
    public void Kapi_iki_alani_da_ONAY_gormeden_acilmaz(
        IysConsentStatus local, IysConsentStatus verified, bool expected)
    {
        var consent = new IysConsent { Status = local, LastVerifiedStatus = verified };
        IysConsentGate.CanSend(consent).Should().Be(expected);
    }

    [Fact]
    public void Dogrulanmamis_kayit_gonderime_kapali()
    {
        // Fail-closed: "kayıt yok" ile "reddetti" ayırt edilemiyor (kural 3).
        var consent = new IysConsent
        {
            Status = IysConsentStatus.Onay,
            LastVerifiedStatus = null,
        };
        IysConsentGate.CanSend(consent).Should().BeFalse();
    }

    [Fact]
    public void Kayit_hic_yoksa_gonderime_kapali()
        => IysConsentGate.CanSend(null).Should().BeFalse();

    [Fact]
    public async Task Onayi_geri_cekilmis_alici_gonderilmez_ve_iade_edilir()
    {
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var campaignId = await SeedCampaignAsync(db, phone);

        // İYS kaydı yok → kapı kapalı.
        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("failed");
        recipient.Error.Should().Be("iys-consent-missing");

        var campaign = await db.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        campaign.RefundedCredits.Should().BeGreaterThan(0,
            "gönderilmeyen mesajın kredisi yayıncıda kalmalı");
    }

    /// <summary>
    /// Kural 7'nin ikinci yüzü: kayıt VAR ve doğrulanmış ONAY ise kapı açılır.
    /// Kapıyı kurup açıldığını da göstermezsek, "hiç göndermiyor" hatası da
    /// testi geçerdi.
    /// </summary>
    [Fact]
    public async Task Dogrulanmis_onayli_alici_gonderilir()
    {
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var campaignId = await SeedCampaignAsync(db, phone);
        await SeedConsentAsync(db, phone, IysConsentStatus.Onay, IysConsentStatus.Onay);

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("sent");
        _factory.Sms.Sent.Should().ContainSingle(m => m.Phone == phone);
    }

    [Fact]
    public async Task Iys_RET_derse_gonderilmez()
    {
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var campaignId = await SeedCampaignAsync(db, phone);
        await SeedConsentAsync(db, phone, IysConsentStatus.Onay, IysConsentStatus.Ret);

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("failed");
        recipient.Error.Should().Be("iys-consent-not-onay");
        _factory.Sms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    /// <summary>
    /// Kapının okuduğu satırı kurar. <c>BrandCode</c> bilerek testteki
    /// yapılandırma değeriyle (varsayılan boş string) aynı: kapı marka bazında
    /// filtreliyor, başka bir markanın satırı bu kampanyayı açmamalı.
    /// </summary>
    private static async Task SeedConsentAsync(
        LicenseDbContext db, string phone,
        IysConsentStatus status, IysConsentStatus? verified)
    {
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = "",
            ChannelType = "MESAJ",
            RecipientType = "BIREYSEL",
            Recipient = phone,
            Status = status,
            LastVerifiedStatus = verified,
            LastVerifiedAt = verified is null ? null : now,
            PushState = verified == IysConsentStatus.Onay
                ? IysPushState.Confirmed
                : IysPushState.Pushed,
            LastLocalEventAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Lisans + kredi + kampanya + tek alıcı. (Plan bu yardımcıyı
    /// <c>SmsCampaignSendJobTests</c>'ten kopyalamayı söylüyordu; o dosya
    /// repoda yok — gönderim işi <c>Controllers/Licenses/SmsCampaignTests</c>
    /// üzerinden HTTP ile kuruluyor. Buradaki kurulum doğrudan DB'ye yazıyor:
    /// kapı sınaması için alıcının telefonunun bilinmesi şart.)
    /// </summary>
    private static async Task<Guid> SeedCampaignAsync(LicenseDbContext db, string phone)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"gate-{Guid.NewGuid():N}@example.com",
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = $"LDK-GATE-{Guid.NewGuid():N}",
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        db.LicenseSmsBalances.Add(new LicenseSmsBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            CreditsRemaining = 99,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.LicenseSmsTransactions.Add(new LicenseSmsTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Amount = 99,
            Kind = "purchase",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            MessageBody = "Kapi testi",
            SegmentsPerMessage = 1,
            RecipientCount = 1,
            ReservedCredits = 1,
            Status = "pending",
            CreatedByCustomerId = customer.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.SmsCampaigns.Add(campaign);

        db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            Phone = phone,
            Status = "pending",
        });

        await db.SaveChangesAsync();
        return campaign.Id;
    }
}
