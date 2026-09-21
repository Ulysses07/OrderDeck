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
/// Görev 3 sözleşmeleri — gönderim işinin hesap kapısı, <c>skipped</c> ve
/// §3.4'ün üç hata sınıfı. Her sınıfın cezalandırdığı özne farklı: Recipient
/// yalnız o alıcıyı, CampaignPause kampanyayı, Account hesabı vurur. Yanlış
/// sınıflandırmanın bedeli kitle kaybı (pending yerine failed) ya da izinsiz
/// gönderim — testler bu ayrımı çiviliyor.
/// </summary>
public sealed class SmsCampaignSendJobErrorClassTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SmsCampaignSendJobErrorClassTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.TenantSms.Clear();
    }

    private static string NewPhone()
        => $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}";

    private sealed record Seed(
        Guid CampaignId, Guid LicenseId, Guid AccountId,
        string[] Phones, string UserCode, string Header, string RawPassword);

    /// <summary>
    /// Lisans + (istenirse) doğrulanmış Netgsm hesabı + kampanya + alıcılar.
    /// <paramref name="consentAll"/> true ise her telefona Onay/Onay İYS satırı
    /// açılır. Şifre ÜRETİLİR (repo public — düz metin yazılmaz) ve
    /// <c>ProtectPassword</c> ile korunmuş saklanır.
    /// </summary>
    private static async Task<Seed> SeedAsync(
        LicenseDbContext db, NetgsmAccountService accounts,
        int recipientCount, bool consentAll = true, bool withAccount = true)
    {
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-EC-{Guid.NewGuid():N}",
            CustomerId = Guid.NewGuid(),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        var userCode = $"user-{Guid.NewGuid():N}";
        var rawPassword = $"p-{Guid.NewGuid():N}";
        var accountId = Guid.NewGuid();

        if (withAccount)
        {
            db.NetgsmAccounts.Add(new NetgsmAccount
            {
                Id = accountId,
                LicenseId = licenseId,
                UserCode = userCode,
                PasswordProtected = accounts.ProtectPassword(rawPassword),
                Header = "ORDERDECK",
                BrandCode = brandCode,
                Status = NetgsmAccountStatus.Verified,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        var phones = Enumerable.Range(0, recipientCount).Select(_ => NewPhone()).ToArray();
        if (consentAll)
        {
            foreach (var p in phones)
                AddConsent(db, brandCode, p, IysConsentStatus.Onay, IysConsentStatus.Onay);
        }

        var campaignId = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = campaignId,
            LicenseId = licenseId,
            MessageBody = "Sinif testi",
            Status = "pending",
            SegmentsPerMessage = 1,
            RecipientCount = phones.Length,
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
        return new Seed(campaignId, licenseId, accountId, phones, userCode, "ORDERDECK", rawPassword);
    }

    private static void AddConsent(
        LicenseDbContext db, string brandCode, string phone,
        IysConsentStatus status, IysConsentStatus? verified)
    {
        var now = DateTimeOffset.UtcNow;
        db.IysConsents.Add(new IysConsent
        {
            Id = Guid.NewGuid(),
            BrandCode = brandCode,
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
    }

    private async Task<List<SmsCampaignRecipient>> RecipientsAsync(Guid campaignId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.SmsCampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).ToListAsync();
    }

    private async Task<SmsCampaign> CampaignAsync(Guid campaignId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
    }

    // A) Sözleşme 5 — kapı elerse alıcı SKIPPED, failed değil.
    [Fact]
    public async Task Kapi_elemesi_skipped_yazar_failed_degil()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 2, consentAll: false);

        // 2. telefona onay VAR ama RET — kapı yine kapalı, sebep farklı.
        var brandCode = (await db.NetgsmAccounts.AsNoTracking()
            .SingleAsync(a => a.Id == seed.AccountId)).BrandCode;
        AddConsent(db, brandCode, seed.Phones[1], IysConsentStatus.Ret, IysConsentStatus.Onay);
        await db.SaveChangesAsync();

        await job.RunAsync(seed.CampaignId);

        var recipients = await RecipientsAsync(seed.CampaignId);
        var noConsent = recipients.Single(r => r.Phone == seed.Phones[0]);
        noConsent.Status.Should().Be("skipped",
            "kapı elemesi arıza değil — sistem doğru çalıştı (§3.3)");
        noConsent.Error.Should().Be("iys-consent-missing");

        var refused = recipients.Single(r => r.Phone == seed.Phones[1]);
        refused.Status.Should().Be("skipped");
        refused.Error.Should().Be("iys-consent-not-onay");

        (await CampaignAsync(seed.CampaignId)).Status.Should().Be("completed");
        _factory.TenantSms.Sent.Should().BeEmpty();
    }

    // B) Kampanya kapısı — Verified hesap YOKSA kampanya hiç başlamaz.
    [Fact]
    public async Task Dogrulanmis_hesap_yoksa_kampanya_duraklar_kitle_pending_kalir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 2, withAccount: false);

        await job.RunAsync(seed.CampaignId);

        (await CampaignAsync(seed.CampaignId)).Status.Should().Be("paused",
            "hesap eksikliği kampanya hatasıdır, alıcı hatası değil — eski "
            + "davranış alıcı başına failed 'iys-brand-missing' yazıp kitleyi harcıyordu");

        (await RecipientsAsync(seed.CampaignId))
            .Should().OnlyContain(r => r.Status == "pending",
                "kurulum doğrulanınca kampanya kaldığı yerden devam edebilmeli");

        _factory.TenantSms.Sent.Should().BeEmpty();
    }

    // C) Sözleşme 13 — hesap sınıfı kod (30) kitleyi harcamaz.
    [Fact]
    public async Task Hesap_sinifi_hata_kitleyi_harcamaz_hesabi_kapatir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 3);
        _factory.TenantSms.FailWith[seed.Phones[1]] = new NetgsmSmsException("30", "gecersiz kimlik");

        await job.RunAsync(seed.CampaignId);

        var recipients = await RecipientsAsync(seed.CampaignId);
        recipients.Single(r => r.Phone == seed.Phones[0]).Status.Should().Be("sent");
        recipients.Single(r => r.Phone == seed.Phones[1]).Status.Should().Be("pending",
            "temiz ret — hiçbir şey gitmedi, alıcı kitleye geri döner; failed "
            + "yazılsaydı şifre düzeltilince geri gelecek kimse kalmazdı (§3.4)");
        recipients.Single(r => r.Phone == seed.Phones[2]).Status.Should().Be("pending",
            "hesap hatası her alıcıda aynen tekrarlanır, denemek anlamsız");

        (await CampaignAsync(seed.CampaignId)).Status.Should().Be("paused");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var account = await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == seed.AccountId);
        account.Status.Should().Be(NetgsmAccountStatus.Failed);
        account.LastError.Should().NotBeNull();
    }

    // D) Sözleşme 4 — CampaignPause sınıfı (bakiye/bilinmeyen kod).
    [Fact]
    public async Task Kampanya_duraklatma_sinifi_hesaba_dokunmaz_kitleyi_korur()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 3);
        _factory.TenantSms.FailWith[seed.Phones[1]] = new NetgsmSmsException("85", "bakiye yetersiz");

        await job.RunAsync(seed.CampaignId);

        var recipients = await RecipientsAsync(seed.CampaignId);
        recipients.Single(r => r.Phone == seed.Phones[0]).Status.Should().Be("sent");
        recipients.Single(r => r.Phone == seed.Phones[1]).Status.Should().Be("pending");
        recipients.Single(r => r.Phone == seed.Phones[2]).Status.Should().Be("pending");

        (await CampaignAsync(seed.CampaignId)).Status.Should().Be("paused");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == seed.AccountId))
            .Status.Should().Be(NetgsmAccountStatus.Verified,
                "bakiye/limit hesabın suçu değil — hesap kapatılırsa yayıncı "
                + "parasını yatırdığında da kilitli kalır");
    }

    // E) Recipient sınıfı (70) — yalnız o alıcı düşer, döngü devam eder.
    [Fact]
    public async Task Alici_sinifi_hata_yalniz_o_aliciyi_dusurur()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 3);
        _factory.TenantSms.FailWith[seed.Phones[1]] = new NetgsmSmsException("70", "gecersiz numara");

        await job.RunAsync(seed.CampaignId);

        var recipients = await RecipientsAsync(seed.CampaignId);
        recipients.Single(r => r.Phone == seed.Phones[0]).Status.Should().Be("sent");
        recipients.Single(r => r.Phone == seed.Phones[1]).Status.Should().Be("failed");
        recipients.Single(r => r.Phone == seed.Phones[2]).Status.Should().Be("sent");

        (await CampaignAsync(seed.CampaignId)).Status.Should().Be("completed");
    }

    // F) Belirsiz hata (ağ) — pending DEĞİL failed: çift ticari SMS riski.
    [Fact]
    public async Task Belirsiz_hata_failed_yazar_ve_donguyu_kesmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 3);
        _factory.TenantSms.FailWith[seed.Phones[1]] = new HttpRequestException("boom");

        await job.RunAsync(seed.CampaignId);

        var recipients = await RecipientsAsync(seed.CampaignId);
        recipients.Single(r => r.Phone == seed.Phones[1]).Status.Should().Be("failed",
            "SMS gitmiş OLABİLİR — pending'e döndürmek aynı kişiye ikinci "
            + "ticari SMS göndermek olurdu (para + 6563)");
        recipients.Single(r => r.Phone == seed.Phones[0]).Status.Should().Be("sent");
        recipients.Single(r => r.Phone == seed.Phones[2]).Status.Should().Be("sent");

        (await CampaignAsync(seed.CampaignId)).Status.Should().Be("completed");
    }

    // G) Sözleşme 10 — şifre çözülemiyorsa hesap Disabled + kampanya paused.
    [Fact]
    public async Task Cozulemeyen_sifre_hesabi_kapatir_kampanyayi_duraklatir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 2);

        // Korunmamış (çözülemez) değer: base64url alfabesinde ama anahtar
        // halkasına ait değil → Unprotect CryptographicException atar.
        var account = await db.NetgsmAccounts.SingleAsync(a => a.Id == seed.AccountId);
        account.PasswordProtected = $"bozuk-{Guid.NewGuid():N}";
        await db.SaveChangesAsync();

        await job.RunAsync(seed.CampaignId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var after = await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == seed.AccountId);
        after.Status.Should().Be(NetgsmAccountStatus.Disabled,
            "anahtar halkası kaybı sessiz bozulma olmaz — kalıcı ve görünür kapanır");
        after.LastError.Should().Be(NetgsmAccountService.UndecryptableMessage);
        after.DisabledAt.Should().BeNull(
            "anahtar halkası kaybı ayrılış değildir — saklama saati başlamaz (§2.4)");

        (await CampaignAsync(seed.CampaignId)).Status.Should().Be("paused");
        (await RecipientsAsync(seed.CampaignId))
            .Should().OnlyContain(r => r.Status == "pending");
        _factory.TenantSms.Sent.Should().BeEmpty();
    }

    // H) jobid saklanır — rapor mutabakatı için ham veri (§3.4 karar 3).
    [Fact]
    public async Task Netgsm_jobid_alici_satirina_yazilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 1);
        _factory.TenantSms.NextJobId = "j-1";

        await job.RunAsync(seed.CampaignId);

        var recipient = (await RecipientsAsync(seed.CampaignId)).Single();
        recipient.Status.Should().Be("sent");
        recipient.ProviderJobId.Should().Be("j-1");
    }

    // I) Kimlik doğru — gönderim hesabın kendi kimlikleriyle gider.
    [Fact]
    public async Task Gonderim_hesabin_kimlikleriyle_gider()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();

        var seed = await SeedAsync(db, accounts, recipientCount: 1);

        await job.RunAsync(seed.CampaignId);

        _factory.TenantSms.Sent.Should().ContainSingle();
        var sent = _factory.TenantSms.Sent[0];
        sent.Creds.UserCode.Should().Be(seed.UserCode);
        sent.Creds.Header.Should().Be(seed.Header);
        sent.Creds.Password.Should().Be(seed.RawPassword,
            "şifre ProtectPassword/TryUnprotectPassword turundan aynen çıkmalı");
        sent.Phone.Should().Be(seed.Phones[0]);
    }
}
