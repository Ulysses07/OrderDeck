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
    public async Task Onayi_olmayan_alici_gonderilmez_ve_skipped_yazilir()
    {
        _factory.TenantSms.Clear();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, _) = await SeedCampaignAsync(db, accounts, phone);

        // İYS kaydı yok → kapı kapalı. §3.3: eleme "skipped"tır — sistem
        // doğru çalıştı, arıza ("failed") değil.
        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("skipped");
        recipient.Error.Should().Be("iys-consent-missing");

        var campaign = await db.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("completed", "eleme kampanyayı asılı bırakmamalı");
    }

    /// <summary>
    /// Kural 7'nin ikinci yüzü: kayıt VAR ve doğrulanmış ONAY ise kapı açılır.
    /// Kapıyı kurup açıldığını da göstermezsek, "hiç göndermiyor" hatası da
    /// testi geçerdi.
    /// </summary>
    [Fact]
    public async Task Dogrulanmis_onayli_alici_gonderilir()
    {
        _factory.TenantSms.Clear();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, brandCode) = await SeedCampaignAsync(db, accounts, phone);
        await SeedConsentAsync(db, brandCode, phone, IysConsentStatus.Onay, IysConsentStatus.Onay);

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("sent");
        _factory.TenantSms.Sent.Should().ContainSingle(m => m.Phone == phone);
    }

    [Fact]
    public async Task Iys_RET_derse_gonderilmez()
    {
        _factory.TenantSms.Clear();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, brandCode) = await SeedCampaignAsync(db, accounts, phone);
        await SeedConsentAsync(db, brandCode, phone, IysConsentStatus.Onay, IysConsentStatus.Ret);

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("skipped");
        recipient.Error.Should().Be("iys-consent-not-onay");
        _factory.TenantSms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    [Fact]
    public async Task Baska_yayincinin_onayi_bu_kampanyanin_kapisini_ACMAZ()
    {
        // Spec sözleşme #4. İzin marka başına: kişi B yayıncısına onay verdiyse
        // A'nın kampanyası o onayı kullanamaz. Kullanırsa kişiye hiç izin
        // vermediği bir yayıncıdan ticari SMS gider — 6563 ihlali.
        _factory.TenantSms.Clear();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, _) = await SeedCampaignAsync(db, accounts, phone);

        // Onay BAŞKA bir markaya ait — kampanyanın lisansıyla ilgisi yok.
        var otherBrand = Random.Shared.Next(100000, 999999).ToString();
        await SeedConsentAsync(db, otherBrand, phone, IysConsentStatus.Onay, IysConsentStatus.Onay);

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("skipped");
        recipient.Error.Should().Be("iys-consent-missing");
        _factory.TenantSms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    [Fact]
    public async Task Dogrulanmis_Netgsm_hesabi_olmayan_lisans_hic_gonderemez()
    {
        // Fail-closed: marka çözülemiyorsa hiçbir onay geçerli sayılamaz.
        // §3.2: bu artık alıcı hatası değil KAMPANYA hatası — kitle
        // harcanmaz, kampanya duraklar; kurulum doğrulanınca devam eder.
        _factory.TenantSms.Clear();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, brandCode) = await SeedCampaignAsync(db, accounts, phone);
        await SeedConsentAsync(db, brandCode, phone, IysConsentStatus.Onay, IysConsentStatus.Onay);

        // Hesabı doğrulanmamış hâle getir: marka artık çözülmemeli.
        var account = await db.NetgsmAccounts.SingleAsync(a => a.BrandCode == brandCode);
        account.Status = NetgsmAccountStatus.Disabled;
        await db.SaveChangesAsync();

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("pending",
            "kurulumunu bitirmemiş yayıncının kitlesi harcanmamalı — "
            + "kampanya devam ettirildiğinde alıcı hâlâ gönderilebilir olmalı");
        _factory.TenantSms.Sent.Should().NotContain(m => m.Phone == phone);

        var campaign = await db.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("paused",
            "hesap yokluğu kampanya düzeyinde bir engel; alıcı başına hata değil");
    }

    [Fact]
    public async Task Baska_kanalin_onayi_SMS_kapisini_ACMAZ()
    {
        // Tekil indeks aynı marka+telefon için kanal başına ayrı satıra izin
        // verir. E-posta için verilen onay SMS göndermeye yetki vermez; kapı
        // kanalı süzmezse kişi hiç izin vermediği kanaldan ticari ileti alır.
        _factory.TenantSms.Clear();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, brandCode) = await SeedCampaignAsync(db, accounts, phone);

        // Marka ve numara DOĞRU, kanal yanlış.
        await SeedConsentAsync(db, brandCode, phone,
            IysConsentStatus.Onay, IysConsentStatus.Onay, channelType: "EPOSTA");

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("skipped");
        recipient.Error.Should().Be("iys-consent-missing");
        _factory.TenantSms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    [Fact]
    public async Task Baska_alici_tipinin_onayi_SMS_kapisini_ACMAZ()
    {
        // Kanal testinin ikiz maddesi: tekil indeks alıcı tipini de taşır,
        // yani aynı marka+kanal+numara için BIREYSEL ve TACIR ayrı satırlardır.
        // Tüzel kişi sıfatıyla verilen onay, aynı numaranın bireysel hattına
        // ticari ileti göndermeye yetki vermez (6563 farklı rejim uygular).
        // Süzgeç olmasaydı bu satır kapıyı açardı — ve iki satır birden
        // doğsaydı ToDictionaryAsync çift anahtarla patlardı.
        _factory.TenantSms.Clear();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var phone = $"+90555{Random.Shared.Next(1000000, 9999999)}";
        var (campaignId, brandCode) = await SeedCampaignAsync(db, accounts, phone);

        // Marka, numara ve kanal DOĞRU; yalnız alıcı tipi yanlış.
        await SeedConsentAsync(db, brandCode, phone,
            IysConsentStatus.Onay, IysConsentStatus.Onay, recipientType: "TACIR");

        await job.RunAsync(campaignId);

        var recipient = await db.SmsCampaignRecipients.AsNoTracking()
            .SingleAsync(r => r.CampaignId == campaignId);
        recipient.Status.Should().Be("skipped");
        recipient.Error.Should().Be("iys-consent-missing");
        _factory.TenantSms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    /// <summary>
    /// Kapının okuduğu satırı kurar. <c>brandCode</c> kampanyanın lisansına ait
    /// markadır — başka bir markanın satırı bu kampanyayı AÇMAMALI.
    /// </summary>
    private static async Task SeedConsentAsync(
        LicenseDbContext db, string brandCode, string phone,
        IysConsentStatus status, IysConsentStatus? verified,
        string channelType = "MESAJ", string recipientType = "BIREYSEL")
    {
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = brandCode,
            ChannelType = channelType,
            RecipientType = recipientType,
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
    /// Lisans + doğrulanmış Netgsm hesabı + kampanya + tek alıcı.
    /// Marka kodu lisansa özel üretiliyor: kapı artık kampanyanın lisansından
    /// markayı çözüyor, global yapılandırmadan DEĞİL.
    ///
    /// <para>(Plan bu yardımcıyı <c>SmsCampaignSendJobTests</c>'ten kopyalamayı
    /// söylüyordu; o dosya repoda yok — gönderim işi
    /// <c>Controllers/Licenses/SmsCampaignTests</c> üzerinden HTTP ile
    /// kuruluyor. Buradaki kurulum doğrudan DB'ye yazıyor: kapı sınaması için
    /// alıcının telefonunun bilinmesi şart.)</para>
    /// </summary>
    private static async Task<(Guid CampaignId, string BrandCode)> SeedCampaignAsync(
        LicenseDbContext db, NetgsmAccountService accounts, string phone)
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

        var brandCode = Random.Shared.Next(100000, 999999).ToString();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            UserCode = $"user-{Guid.NewGuid():N}",
            PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            MessageBody = "Kapi testi",
            SegmentsPerMessage = 1,
            RecipientCount = 1,
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
        return (campaign.Id, brandCode);
    }
}
