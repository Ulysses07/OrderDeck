using FluentAssertions;
using Hangfire;
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
/// §2.5 — kurulum kapatıldığında devam eden kampanya da durur. Durmazsa
/// admin'in "kapat" düğmesi yalan söyler: hesap kapalıyken bin SMS daha gider
/// ve bunların İYS izni artık doğrulanamaz durumdadır.
/// </summary>
public sealed class SmsCampaignPauseTests : IClassFixture<HookedApiFactory>
{
    private readonly HookedApiFactory _factory;

    public SmsCampaignPauseTests(HookedApiFactory factory)
    {
        _factory = factory;
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;
        _factory.Sms.OnSent = null;
        _factory.Hook.Reset();
    }

    private static string NewUserCode()
        => Random.Shared.NextInt64(8_500_000_000, 8_599_999_999).ToString();

    private static string NewPhone()
        => $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}";

    /// <summary>Onaylı iki alıcılı bir kampanya tohumlar.</summary>
    private static async Task<(Guid CampaignId, Guid AccountId, string[] Phones)> SeedAsync(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"pz-{Guid.NewGuid():N}@x",
            Name = "Pz",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        });

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = $"LDK-PZ-{Guid.NewGuid():N}",
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });

        var brandCode = Random.Shared.Next(100_000, 999_999).ToString();
        var accountId = Guid.NewGuid();
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = accountId,
            LicenseId = licenseId,
            UserCode = NewUserCode(),
            PasswordProtected = $"pw-{Guid.NewGuid():N}",
            Header = "ORDERDECK",
            BrandCode = brandCode,
            Status = NetgsmAccountStatus.Verified,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        db.LicenseSmsBalances.Add(new LicenseSmsBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CreditsRemaining = 99,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.LicenseSmsTransactions.Add(new LicenseSmsTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Amount = 99,
            Kind = "purchase",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var phones = new[] { NewPhone(), NewPhone() };
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
            MessageBody = "Kampanya",
            Status = "pending",
            SegmentsPerMessage = 1,
            RecipientCount = phones.Length,
            ReservedCredits = phones.Length,
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
        return (campaignId, accountId, phones);
    }

    [Fact]
    public async Task Gonderim_ortasinda_duraklatilan_kampanya_kalan_aliciya_gitmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

        // İlk gönderimden hemen sonra, AYRI bir scope'tan duraklat — admin'in
        // yaptığı tam olarak bu: job koşarken başka bir istek durumu yazıyor.
        _factory.Sms.OnSent = _ =>
        {
            using var s2 = _factory.Services.CreateScope();
            var db2 = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var c = db2.SmsCampaigns.Single(x => x.Id == campaignId);
            if (c.Status == "sending") { c.Status = "paused"; db2.SaveChanges(); }
        };

        try { await job.RunAsync(campaignId); }
        finally { _factory.Sms.OnSent = null; }

        _factory.Sms.Sent.Should().HaveCount(1, "duraklatma ikinci alıcıyı durdurmalı");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("paused", "job duraklatmayı ezmemeli");
        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0,
            "kalan alıcılar hâlâ pending — rezervasyon onların karşılığı, iade edilirse "
            + "kampanya devam ettirildiğinde kredi iki kez harcanmış olur");

        var recipients = await vdb.SmsCampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).ToListAsync();
        recipients.Count(r => r.Status == "sent").Should().Be(1);
        recipients.Count(r => r.Status == "pending").Should().Be(1);
    }

    [Fact]
    public async Task Gercek_kapatma_yolu_kosan_isi_durdurur()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, accountId, _) = await SeedAsync(db);

        // Bir öncekinin aksine burada durum ELLE yazılmıyor: Görev 8'in
        // kesin-ret dalının ve Görev 12'nin kapatma düğmesinin ORTAK
        // çağırdığı gerçek metot koşuyor — hem de gönderim işi kampanyayı
        // üstlenmiş ve ClaimedAt'i her alıcıda tazelerken. Yani bu, iki
        // yazıcının aynı satırda buluştuğu tek testtir; metodun retry
        // döngüsünün var olma sebebi bu senaryo.
        var pausedCount = -1;
        _factory.Sms.OnSent = _ =>
        {
            if (pausedCount >= 0) return; // yalnız ilk gönderimde kapat
            using var closer = _factory.Services.CreateScope();
            var accounts = closer.ServiceProvider.GetRequiredService<NetgsmAccountService>();
            pausedCount = accounts.CloseAccountAndPauseCampaignsAsync(
                    accountId, NetgsmAccountStatus.Disabled,
                    "Yönetici tarafından kapatıldı.")
                .GetAwaiter().GetResult();
        };

        try { await job.RunAsync(campaignId); }
        finally { _factory.Sms.OnSent = null; }

        pausedCount.Should().Be(1, "koşan kampanya kapatma yazımında yakalanmalı");
        _factory.Sms.Sent.Should().HaveCount(1, "kapatma ikinci alıcıyı durdurmalı");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        campaign.Status.Should().Be("paused", "job kapatmayı ezmemeli");
        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0);

        (await vdb.NetgsmAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId))
            .Status.Should().Be(NetgsmAccountStatus.Disabled);

        // Duraklatma artık ClaimedAt jetonunu da ilerlettiği için, GÖNDERİLMİŞ
        // ilk alıcının sonuç yazımı çakışmayla karşılaşır. O çakışma yanlış
        // ele alınırsa (kampanyayı yeniden yazmaya çalışmak ya da istisnayı
        // dışarı bırakmak) alıcı "pending" kalır: SMS gitmiş ama kayıtta
        // gitmemiş görünür, kampanya devam ettirildiğinde AYNI KİŞİYE ikinci
        // kez gider. Bu iki satır, `SaveRecipientResultAsync`'in kampanyayı
        // yazımdan düşürüp yeniden kaydetme davranışını kilitliyor.
        var recipients = await vdb.SmsCampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).ToListAsync();

        recipients.Count(r => r.Status == "sent").Should().Be(1);
        recipients.Count(r => r.Status == "pending").Should().Be(1);
    }

    /// <summary>
    /// Bulgu 1 — duraklatma, kampanyayı ZATEN OKUMUŞ bir işçiyi de geçersiz
    /// kılmalı. Durum yazımı tek başına yetmez: işçi elindeki kopyayla
    /// `sending` + kendi `ClaimedAt`'ini yazınca duraklatma sessizce geri
    /// alınır ve admin'in kapatma düğmesi yalan söyler.
    ///
    /// <para><c>futureStamp</c> vakası, ileri damgalı bir kampanyada da
    /// üstlenmenin düştüğünü gösterir — ama damganın <b>değerini</b>
    /// kanıtlamaz: üstlenme yazımı zaten çakıştığı için atanan damga diske
    /// hiç inmez ve aşağıdaki <c>BeAfter</c> duraklatmanın damgasını ölçer.
    /// Monotonluk iddiası
    /// <see cref="Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez"/>
    /// testine aittir; ikisini birbirine karıştırma.</para>
    /// </summary>
    [Theory]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    [InlineData("sending", false)]
    public async Task Kapatmadan_once_okunan_kampanya_sonradan_ustlenilemez(
        string status, bool futureStamp)
    {
        using var worker = _factory.Services.CreateScope();
        var db = worker.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, accountId, _) = await SeedAsync(db);

        var stale = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);
        stale.Status = status;
        stale.ClaimedAt = futureStamp
            ? DateTimeOffset.UtcNow.AddHours(1)
            : status == "sending"
                ? DateTimeOffset.UtcNow - SmsCampaignSendJob.ClaimLease - TimeSpan.FromHours(1)
                : null;

        await db.SaveChangesAsync();
        var previous = stale.ClaimedAt;

        // `worker` scope'u kampanyayı İZLEMEYE devam ediyor — gerçek işçinin
        // kapatma anındaki hâli bu.
        using (var closer = _factory.Services.CreateScope())
        {
            var accounts = closer.ServiceProvider.GetRequiredService<NetgsmAccountService>();

            (await accounts.CloseAccountAndPauseCampaignsAsync(
                accountId, NetgsmAccountStatus.Disabled, "Yönetici tarafından kapatıldı."))
                .Should().Be(1);
        }

        await worker.ServiceProvider
            .GetRequiredService<SmsCampaignSendJob>()
            .RunAsync(campaignId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        campaign.Status.Should().Be("paused");
        campaign.ClaimedAt.Should().NotBeNull();

        if (previous.HasValue)
            campaign.ClaimedAt!.Value.Should().BeAfter(previous.Value);

        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0);
        _factory.Sms.Sent.Should().BeEmpty();

        (await vdb.SmsCampaignRecipients.CountAsync(
            r => r.CampaignId == campaignId && r.Status == "pending"))
            .Should().Be(2);
    }

    /// <summary>
    /// Üstlenme damgasının monotonluğu — ÇAKIŞMASIZ yolda. Yukarıdaki teori
    /// bunu kanıtlayamaz: orada üstlenme yazımı zaten çakışmayla düşüyor, yani
    /// damganın DEĞERİ hiç diske inmiyor ve <c>BeAfter</c> aslında
    /// duraklatmanın damgasını ölçüyor. Burada karşı yazıcı YOK: kampanya
    /// bir saat ileri damgalı doğuyor, iş onu sorunsuz üstleniyor ve damganın
    /// kendi değeri diske iniyor. Ham <c>UtcNow</c> ataması jetonu bir saat
    /// GERİ alır; bunu yalnız bu test görür.
    /// </summary>
    [Fact]
    public async Task Ileri_tarihli_damgali_kampanya_ustlenilince_jeton_geri_gitmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

        // İleri damga uydurma değil: duraklatma `max(UtcNow, önceki + 1 tick)`
        // yazıyor, yani saat geri atlayan bir makinede jeton gerçekten
        // "gelecekte" kalabiliyor. Bir saat, saat çözünürlüğünden bağımsız
        // olsun diye seçildi — testin flaky olmaması bu farka dayanıyor.
        var campaign = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);
        campaign.ClaimedAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var previous = campaign.ClaimedAt!.Value;

        await job.RunAsync(campaignId);

        _factory.Sms.Sent.Should().HaveCount(2, "karşı yazıcı yok, koşu bitmeli");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var after = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);

        after.Status.Should().Be("completed");
        after.ClaimedAt.Should().NotBeNull();
        after.ClaimedAt!.Value.Should().BeAfter(previous,
            "üstlenme damgası ileri damgayı GERİ alırsa, duraklatmadan önce "
            + "kampanyayı okumuş bayat bir işçi sahipliği yeniden kazanır");
    }

    /// <summary>
    /// Üstlenme çakışmasının <c>return</c>'ü — alıcı listesi BOŞken. Döngü
    /// başındaki sahiplik yoklaması buradaki tek koruma DEĞİL, hiç koruma
    /// değil: gönderilecek alıcı kalmadığında döngü bir kez bile dönmez ve
    /// koşu doğrudan tamamlama + iade bloğuna gider. Üstlenemediğimiz bir
    /// kampanyanın iadesini yazmak krediyi yoktan var eder.
    /// </summary>
    [Fact]
    public async Task Ustlenme_cakismasi_kampanyayi_tamamlamaz_ve_iade_yazmaz()
    {
        using var worker = _factory.Services.CreateScope();
        var db = worker.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, _, _) = await SeedAsync(db);

        // İki alıcı da sonuçlanmış ("failed") ama iadesi HENÜZ yazılmamış:
        // `owed - RefundedCredits = 2 - 0 = 2`. Yani tamamlama bloğuna
        // ulaşılırsa para gerçekten hareket eder.
        var campaign = await db.SmsCampaigns.SingleAsync(c => c.Id == campaignId);
        var licenseId = campaign.LicenseId;
        campaign.RefundedCredits = 0;
        foreach (var r in await db.SmsCampaignRecipients
                     .Where(r => r.CampaignId == campaignId).ToListAsync())
        {
            r.Status = "failed";
            r.Error = "provider-rejected";
        }
        await db.SaveChangesAsync();

        var creditsBefore = (await db.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId)).CreditsRemaining;

        // İşçi kampanyayı ZATEN okudu (yukarıdaki `campaign` bu scope'ta
        // izleniyor, jeton özgün değeri null). Şimdi başkası jetonu ilerletiyor
        // — durumu değiştirmiyor, çünkü test edilen şey durum kapısı değil
        // üstlenme CAS'ı.
        using (var rival = _factory.Services.CreateScope())
        {
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var c = await rdb.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
            c.ClaimedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await rdb.SaveChangesAsync();
        }

        await worker.ServiceProvider
            .GetRequiredService<SmsCampaignSendJob>()
            .RunAsync(campaignId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);
        after.Status.Should().Be("pending", "üstlenme düştüyse koşu hiç başlamamıştır");
        after.CompletedAt.Should().BeNull();
        after.RefundedCredits.Should().Be(0);

        (await vdb.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(creditsBefore,
                "üstlenemediğimiz kampanyanın iadesini yazmak krediyi yoktan var eder");

        (await vdb.LicenseSmsTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0, "bu koşu hiç sahip olmadı, ledger'a dokunmamalı");
    }

    [Fact]
    public async Task Paused_kampanya_hic_ustlenilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

        var c = await db.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
        c.Status = "paused";
        await db.SaveChangesAsync();

        await job.RunAsync(campaignId);

        _factory.Sms.Sent.Should().BeEmpty();
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId))
            .Status.Should().Be("paused");
    }

    [Fact]
    public async Task Paused_kampanya_kurtarma_isiyle_diriltilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, _, _) = await SeedAsync(db);

        // Hem bayat claim hem eski CreatedAt: kurtarma işinin İKİ yakalama
        // koşulunu da tetikleyebilecek en kötü hâl.
        var c = await db.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
        c.Status = "paused";
        c.ClaimedAt = DateTimeOffset.UtcNow - SmsCampaignSendJob.ClaimLease - TimeSpan.FromHours(1);
        c.CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        await db.SaveChangesAsync();

        var enqueued = new List<Guid>();
        var jobs = new RecordingBackgroundJobClient(enqueued);
        var recovery = new SmsCampaignRecoveryJob(
            db, jobs,
            scope.ServiceProvider.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<SmsCampaignRecoveryJob>>());

        await recovery.RunAsync();

        enqueued.Should().NotContain(campaignId,
            "duraklatılmış kampanyayı diriltmek admin'in kapatma kararını geri alır");
    }

    [Fact]
    public async Task Diriltilen_kampanya_iadeyi_ikinci_kez_yapmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, _, _) = await SeedAsync(db);

        // Tamamlanmış ve iadesi YAPILMIŞ bir kampanyayı, bayat bir "duraklat"
        // yazımının + devam ettirmenin dirilteceği hâle kur: durum yine
        // "pending", ama her iki alıcı da sonuçlanmış ve RefundedCredits dolu.
        // Job bu kampanyayı üstlenip hiç alıcı bulamayacak ve doğrudan
        // tamamlamaya gidecek — iade orada ikinci kez yazılırsa kredi
        // yoktan var edilir.
        var campaign = await db.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
        var licenseId = campaign.LicenseId;
        campaign.Status = "pending";
        campaign.ClaimedAt = null;
        campaign.RefundedCredits = 2; // 2 failed × 1 segment — zaten ödendi
        foreach (var r in await db.SmsCampaignRecipients
                     .Where(r => r.CampaignId == campaignId).ToListAsync())
        {
            r.Status = "failed";
            r.Error = "boom";
        }
        await db.SaveChangesAsync();

        var creditsBefore = (await db.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId)).CreditsRemaining;

        await job.RunAsync(campaignId);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(x => x.Id == campaignId);
        after.Status.Should().Be("completed");
        after.RefundedCredits.Should().Be(2, "borç zaten ödenmişti, artmamalı");

        (await vdb.LicenseSmsBalances.AsNoTracking().SingleAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(creditsBefore,
                "iade idempotent değilse aynı krediler ikinci kez bakiyeye eklenir");

        (await vdb.LicenseSmsTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0, "bu koşu yeni bir iade işlemi yazmamalı");
    }

    [Fact]
    public async Task Devam_ettirilip_yeniden_ustlenilen_kampanyaya_eski_isci_gondermez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
        var (campaignId, accountId, _) = await SeedAsync(db);

        // OnSent alıcı 1'in SendAsync'i içinde koşar; oradan yalnız KANCAYI
        // kuruyoruz. Araya girme, alıcı 1'in sonucu diske indikten sonra
        // çalışsın ki işçi A gerçekten döngünün ikinci turuna girsin —
        // test etmek istediğimiz yoklama orada.
        _factory.Sms.OnSent = _ =>
        {
            _factory.Sms.OnSent = null;
            _factory.Hook.AfterSave = InterleaveAsync;
        };

        // Kapat → devam ettir → BAŞKA bir işçi üstlensin. Üçü de ayrı
        // scope'ta: işçi A'nın DbContext'i hiçbirini görmüyor, elindeki
        // `campaign` nesnesi bayatlıyor.
        async Task InterleaveAsync()
        {
            _factory.Hook.AfterSave = null;

            using var other = _factory.Services.CreateScope();
            var odb = other.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var accounts = other.ServiceProvider.GetRequiredService<NetgsmAccountService>();
            var licenseId = (await odb.NetgsmAccounts.AsNoTracking()
                .SingleAsync(a => a.Id == accountId)).LicenseId;

            // 1) Admin kapatması — kampanya paused, ClaimedAt ileri damgalanır.
            await accounts.CloseAccountAndPauseCampaignsAsync(
                accountId, NetgsmAccountStatus.Disabled, "Yönetici kapattı.");

            // 2) Yayıncı kimlikleri düzeltti, PUT doğrulandı — paused → pending.
            await accounts.StageResumePausedCampaignsAsync(licenseId);
            await odb.SaveChangesAsync();

            // 3) Kurtarma süpürmesi yeni bir işçiye verdi: taze ClaimedAt.
            var c = await odb.SmsCampaigns.SingleAsync(x => x.Id == campaignId);
            c.Status = "sending";
            c.ClaimedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await odb.SaveChangesAsync();
        }

        try { await job.RunAsync(campaignId); }
        finally
        {
            _factory.Sms.OnSent = null;
            _factory.Hook.Reset();
        }

        _factory.Sms.Sent.Should().HaveCount(1,
            "işçi A sahipliğini kaybetti; ikinci alıcı artık YENİ işçinin işi. "
            + "2 olursa aynı kişiye iki ticari ileti gitmiş demektir");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaign = await vdb.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId);
        campaign.Status.Should().Be("sending", "yeni sahibin durumu ezilmemeli");
        campaign.CompletedAt.Should().BeNull();
        campaign.RefundedCredits.Should().Be(0,
            "sahipliği kaybeden işçi iade YAPMAZ — kalan alıcı yeni işçide");

        // Alıcı 1'in sonucu KAYBOLMAMALI: SMS gerçekten gitti, kaydı da inmiş
        // olmalı. Araya girme `OnSent`'in içinde yapılsaydı bu satır YİNE
        // geçerdi — `SaveRecipientResultAsync` çakışmada kampanyayı detach
        // edip yeniden kaydediyor, yani alıcı satırı her hâlükârda iniyor.
        // Fark davranışta değil, KAPSAMDA: `OnSent` ile sahiplik kaybı alıcı
        // 1'in yazımında yakalanır, koşu oracıkta `return` eder ve döngü
        // ikinci tura HİÇ girmez — yani Adım 5c'nin öldürmek istediği
        // `current.ClaimedAt` karşılaştırmasına sıra gelmez. `AfterSave` ile
        // alıcı 1 temiz kapanır, işçi A ikinci tura girer ve tek kapı o
        // karşılaştırma olur.
        (await vdb.SmsCampaignRecipients.AsNoTracking()
            .CountAsync(r => r.CampaignId == campaignId && r.Status == "sent"))
            .Should().Be(1);
    }
}

/// <summary>Hangfire'a gerçekten iş atmadan, atılan kampanya kimliklerini
/// toplayan minimal <see cref="IBackgroundJobClient"/>.</summary>
internal sealed class RecordingBackgroundJobClient : IBackgroundJobClient
{
    private readonly List<Guid> _ids;
    public RecordingBackgroundJobClient(List<Guid> ids) => _ids = ids;

    public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
    {
        if (job.Args.Count > 0 && job.Args[0] is Guid id) _ids.Add(id);
        return Guid.NewGuid().ToString("N");
    }

    public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => true;
}
