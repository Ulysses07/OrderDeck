using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Iys;

/// <summary>
/// §6 + m.13 saklama takvimi. <see cref="ApiFactory"/> bu SINIFA özel bir
/// InMemory DB açar (bkz. ApiFactory.cs — <c>_dbName</c> kurucuda üretilir);
/// "paylaşımlı" burada BAŞKA test sınıflarının artığı değil, bu sınıftaki
/// [Fact]'lerin xunit tarafından SIRALI koşulup aynı DB'yi paylaşması demek —
/// bu yüzden assert'ler KENDİ marka/id'leriyle sınırlı tutulmalı.
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
            DateTimeOffset? disabledAt)
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

    private static void AddConsent(LicenseDbContext db, string brand, string phone,
        DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
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
        dep.ConsentsDeletedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5),
            "30. gün temizliğinin gerçekleştiği an — koşu anı");
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
    public async Task Sistem_kapanisli_hesaba_DisabledAt_bos_hic_dokunulmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        // §2.4 — anahtar halkası kaybı gibi SİSTEM kaynaklı kapanış DisabledAt'i
        // hiç damgalamaz; 30 günlük sayaç bu satır için hiç başlamamış olur.
        var (_, accountId, brand) = await SeedDepartedAsync(db, accounts, disabledAt: null);
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand))
            .Should().BeTrue("DisabledAt boşsa saklama sayacı hiç başlamamıştır");
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId)).Should().BeTrue();
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == brand)).Should().BeFalse();
    }

    [Fact]
    public async Task Marka_sistem_kapanisli_sahipte_yasiyorsa_onaylar_kalir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (_, accountId, brand) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow.AddDays(-31));
        // Aynı markayı taşıyan SİSTEM KAPANIŞLI ikinci hesap (§2.4): Status
        // Disabled ama DisabledAt boş — ayrılışla kapanmadı, sahipliği sürüyor.
        var (_, sysClosedAccountId, _) = await SeedDepartedAsync(db, accounts, disabledAt: null);
        var sysClosed = await db.NetgsmAccounts.SingleAsync(a => a.Id == sysClosedAccountId);
        sysClosed.BrandCode = brand;
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand))
            .Should().BeTrue("sistem kapanışlı satır DisabledAt=null olsa da markanın SAHİBİ kalır");
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == accountId))
            .Should().BeFalse("ayrılan hesabın satırı yine silinir");
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == brand))
            .Should().BeFalse("marka ölmedi — imha takvimi açılmaz");
    }

    [Fact]
    public async Task Marka_penceresi_dolmamis_ayrilmis_kardeste_onaylar_kalir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var (_, aId, brand) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow.AddDays(-31));
        // Aynı markayı taşıyan B: AYRILDI ama kendi 30 günlük penceresi HENÜZ
        // dolmadı — marka henüz ölmedi, A'nın günü B'nin penceresini kısaltmamalı.
        var (_, bId, _) = await SeedDepartedAsync(db, accounts,
            DateTimeOffset.UtcNow.AddDays(-20));
        var b = await db.NetgsmAccounts.SingleAsync(a => a.Id == bId);
        b.BrandCode = brand;
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand))
            .Should().BeTrue("B'nin kendi penceresi dolmadı — marka henüz ölmedi");
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == aId))
            .Should().BeFalse("A'nın hesap satırı yine silinir");
        (await vdb.NetgsmAccounts.AnyAsync(a => a.Id == bId))
            .Should().BeTrue("B kendi 30 günü dolmadan silinmez");
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
        // I-1: 30 günlük ödemsiz süre yetim markaya da uygulanır — son onay
        // hareketini eskitmezsek bu satır grace testiyle çakışır.
        AddConsent(db, brand, phone, DateTimeOffset.UtcNow.AddDays(-31));
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
        dep.DepartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5),
            "gerçek ayrılış anı bilinmiyor — tespit anı kullanılır");
        dep.PurgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Ikinci_yetimlesme_yeni_donem_satiri_acar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var brand = NewBrandCode();
        // Birinci dönemin İMHA EDİLMEMİŞ takvim kaydı (A ayrıldı, markayı B
        // devraldı, şimdi B de yetim düştü — bu SATIRI EZMEMELİ).
        var firstDepartureId = Guid.NewGuid();
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = firstDepartureId, LicenseId = null, BrandCode = brand,
            DepartedAt = DateTimeOffset.UtcNow.AddDays(-100),
            ConsentsDeletedAt = DateTimeOffset.UtcNow.AddDays(-70),
        });
        // I-1: 30 günlük ödemsiz süre burada da geçerli — eskitmezsek İkinci
        // yetimleşme testi grace penceresine takılıp yanlış sebeple kırılır.
        AddConsent(db, brand, NewPhone(), DateTimeOffset.UtcNow.AddDays(-31));
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        var before = DateTimeOffset.UtcNow;
        await job.RunAsync(CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand)).Should().BeFalse();
        var departures = await vdb.NetgsmDepartures.Where(d => d.BrandCode == brand)
            .OrderBy(d => d.DepartedAt).ToListAsync();
        departures.Should().HaveCount(2,
            "ikinci yetimleşme KENDİ döneminin takvimini açar — birinci dönemin " +
            "imha randevusunu EZMEMELİ, yoksa o dönemin ispatı asla imha edilmez");
        departures[0].Id.Should().Be(firstDepartureId, "eski dönem kaydı olduğu gibi kalmalı");
        var newest = departures[1];
        // DepartedAt, koşu başındaki `now`. Assert anına göre sabit tolerans yükte
        // aşılıyordu; koşuyu önce/sonra damgasıyla sınırlamak yükten bağımsız.
        newest.DepartedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        newest.PurgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Yetim_marka_30_gun_dolmadan_silinmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var brand = NewBrandCode();
        // Taze yetim: hesabı hiç yok ama son onay hareketi BUGÜN — broadcaster
        // BrandCode'u az önce değiştirmiş ya da cascade silme az önce olmuş
        // olabilir. Aydınlatma metnindeki 30 günlük ödemsiz süre burada da işler.
        AddConsent(db, brand, NewPhone());
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == brand))
            .Should().BeTrue("30 günlük ödemsiz süre dolmadan yetim marka silinemez");
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == brand))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Bos_BrandCode_onay_satirlari_atlanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var phone = NewPhone();
        // #472-#473 dağıtımı arasında BrandCode hiç yazılmadan kaydedilmiş
        // eski bir onay satırını simüle ediyor — otomatik silinmemeli.
        AddConsent(db, "", phone);
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsents.AnyAsync(c => c.BrandCode == "" && c.Recipient == phone))
            .Should().BeTrue("boş marka kodu elle karar bekler, otomatik silinmez");
        (await vdb.NetgsmDepartures.AnyAsync(d => d.BrandCode == ""))
            .Should().BeFalse("boş marka için imha takvimi AÇILMAMALI");
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
            Status = "completed", SegmentsPerMessage = 1, RecipientCount = 1,
            CreatedAt = departedAt.AddDays(-20),
        });
        db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Phone = phone, Status = "sent",
        });
        // Dönem SONRASI (yeni dönem) kampanya + alıcısı — AYNI lisans ama
        // ayrılıştan SONRA oluşturulmuş → imha edilmemeli.
        var newEraCampaignId = Guid.NewGuid();
        db.SmsCampaigns.Add(new SmsCampaign
        {
            Id = newEraCampaignId, LicenseId = licenseId, MessageBody = "Yeni donem",
            Status = "completed", SegmentsPerMessage = 1, RecipientCount = 1,
            CreatedAt = departedAt.AddDays(5),
        });
        db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
        {
            Id = Guid.NewGuid(), CampaignId = newEraCampaignId, Phone = NewPhone(), Status = "sent",
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
        (await vdb.SmsCampaigns.AnyAsync(c => c.Id == newEraCampaignId))
            .Should().BeTrue("ayrılıştan sonraki kampanya yeni dönemindir — imha edilmez");
        (await vdb.SmsCampaignRecipients.AnyAsync(r => r.CampaignId == newEraCampaignId))
            .Should().BeTrue("yeni dönem kampanyasının alıcısı da hayatta kalmalı");
        (await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand))
            .PurgedAt.Should().NotBeNull();
        // Kalıcı imha kaydı (KVKK Ö-13): lisans ve tablo başına, imhayla aynı kayıtta.
        var records = await vdb.DataDestructionRecords
            .Where(r => r.LicenseId == licenseId).ToListAsync();
        records.Should().ContainSingle(r => r.Target == "IysConsentEvents")
            .Which.RowCount.Should().Be(1);
        records.Should().ContainSingle(r => r.Target == "SmsCampaigns")
            .Which.RowCount.Should().Be(1);
        records.Should().ContainSingle(r => r.Target == "SmsCampaignRecipients")
            .Which.RowCount.Should().Be(1);
        records.Should().OnlyContain(r => r.Action == DataDestruction.Actions.Deleted
            && r.Trigger == DataDestruction.Triggers.Retention
            && r.Actor == DataDestruction.SystemActor
            && r.Criterion.Contains("m.13"));
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

    [Fact]
    public async Task Uc_yil_lisanssiz_ayrilista_yalniz_olaylar_imha_edilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var brand = NewBrandCode();
        var departedAt = DateTimeOffset.UtcNow - (IysDepartureRetentionJob.ProofRetention
            + TimeSpan.FromDays(1));
        // LicenseId null — Faz 2'nin geri kazanamadığı ya da lisansı zaten
        // KVKK ile silinmiş bir ayrılış. Kampanya adımı atlanmalı, PATLAMAMALI.
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = Guid.NewGuid(), LicenseId = null, BrandCode = brand,
            DepartedAt = departedAt, ConsentsDeletedAt = departedAt.AddDays(30),
        });
        var eventId = Guid.NewGuid();
        db.IysConsentEvents.Add(new IysConsentEvent
        {
            Id = eventId, LicenseId = null, BrandCode = brand,
            Recipient = NewPhone(), OccurredAt = departedAt.AddDays(-1),
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
        });
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        var act = async () => await job.RunAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("lisanssız ayrılışta kampanya adımı atlanmalı, patlamamalı");

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == eventId)).Should().BeFalse();
        (await vdb.NetgsmDepartures.SingleAsync(d => d.BrandCode == brand))
            .PurgedAt.Should().NotBeNull();
    }

    // ── Markasız yayıncı (sahip kararı 10, 2026-10-23) ────────────────────
    // Doğrulanmış markası olmayan yayıncıda toplayıcı olayı BrandCode=null
    // yazar (ErrorCode no-brand ya da invalid-phone) ve kayıt satırı açmaz.
    // Olaylar LİSANSA bağlıdır, lisansla kaskad gitmez (FK yok) ve lisansın
    // sonraki doğrulamasında yeniden oynatılır — lisans yaşadıkça dönem
    // bitmez. Ayrılış = lisansın silinmesi (yayıncı platformdan ayrıldı).

    private static async Task<Guid> SeedLicenseAsync(LicenseDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cu-{Guid.NewGuid():N}@t.test",
            Name = "Markasiz",
            PasswordHash = $"h-{Guid.NewGuid():N}",
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
        await db.SaveChangesAsync();
        return licenseId;
    }

    /// <summary>Toplayıcının markasız yayıncı için yazdığı olayın biçimi.</summary>
    private static IysConsentEvent NoBrandEvent(Guid? licenseId, DateTimeOffset at,
        string errorCode = "no-brand")
        => new()
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = null,
            Recipient = TestPhone.NewE164(), OccurredAt = at,
            EventType = IysConsentEventType.LocalConsent, Status = IysConsentStatus.Onay,
            SourceTable = "Shopper", SourceId = Guid.NewGuid(), ErrorCode = errorCode,
        };

    [Fact]
    public async Task Markasiz_yayincinin_lisansi_silinince_ispat_takvimi_acilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Lisans satırı YOK: KVKK silmesi lisansı sildi, olaylar (FK'sız) kaldı.
        var licenseId = Guid.NewGuid();
        db.IysConsentEvents.Add(NoBrandEvent(licenseId, DateTimeOffset.UtcNow.AddDays(-10)));
        db.IysConsentEvents.Add(NoBrandEvent(licenseId, DateTimeOffset.UtcNow.AddDays(-3),
            errorCode: "invalid-phone"));
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        var before = DateTimeOffset.UtcNow;
        await job.RunAsync(CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var dep = await vdb.NetgsmDepartures.SingleAsync(d => d.LicenseId == licenseId);
        dep.BrandCode.Should().BeNull("markasız dönem: ispat markaya değil lisansa bağlı");
        dep.DepartedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "silinme anı bilinmiyor — tespit anı (Faz 2'nin yetim markadaki kuralı)");
        dep.PurgedAt.Should().BeNull();
        (await vdb.IysConsentEvents.CountAsync(e => e.LicenseId == licenseId))
            .Should().Be(2, "m.13: takvim açılır ama 3 yıl dolmadan ispat imha edilmez");
    }

    [Fact]
    public async Task Markasiz_yayincinin_lisansi_yasarken_takvim_acilmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = await SeedLicenseAsync(db);
        // Eski olay bile: lisans yaşıyor, RET'ler bir sonraki doğrulamada yeniden
        // oynatılır (StageReplayNoBrandEventsAsync) — dönem henüz bitmedi.
        db.IysConsentEvents.Add(NoBrandEvent(licenseId, DateTimeOffset.UtcNow.AddYears(-4)));
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.NetgsmDepartures.AnyAsync(d => d.LicenseId == licenseId))
            .Should().BeFalse("lisans yaşıyor — ayrılış yok");
        (await vdb.IysConsentEvents.AnyAsync(e => e.LicenseId == licenseId)).Should().BeTrue();
    }

    [Fact]
    public async Task Markasiz_ayrilis_ikinci_kosuda_yeniden_acilmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = Guid.NewGuid();
        db.IysConsentEvents.Add(NoBrandEvent(licenseId, DateTimeOffset.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.NetgsmDepartures.CountAsync(d => d.LicenseId == licenseId))
            .Should().Be(1, "olaylar 3 yıl yaşar; her gece yeni randevu açılırsa saat her gün ileri kayar");
    }

    [Fact]
    public async Task Markasiz_donem_uc_yil_dolunca_yalniz_o_lisansin_markasiz_olaylari_imha_edilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = Guid.NewGuid();
        var departedAt = DateTimeOffset.UtcNow - (IysDepartureRetentionJob.ProofRetention
            + TimeSpan.FromDays(1));
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = null,
            DepartedAt = departedAt, ConsentsDeletedAt = departedAt,
        });
        // Dönem içi, o lisansın markasız olayları → imha.
        var noBrand = NoBrandEvent(licenseId, departedAt.AddDays(-10));
        var badPhone = NoBrandEvent(licenseId, departedAt.AddDays(-5), errorCode: "invalid-phone");
        // Başka lisansın markasız olayı → yaşar. `e.BrandCode == dep.BrandCode`
        // null'da IS NULL'a döner ve TÜM lisansların markasız olaylarını seçerdi.
        var otherLicense = NoBrandEvent(Guid.NewGuid(), departedAt.AddDays(-10));
        // Aynı lisansın MARKALI olayı → yaşar: marka döneminin kendi takvimi var.
        var branded = NoBrandEvent(licenseId, departedAt.AddDays(-10));
        branded.BrandCode = NewBrandCode();
        branded.ErrorCode = null;
        // Sahipsiz eski olay (LicenseId de BrandCode da null) → yaşar: kimin olduğu bilinmiyor.
        var ownerless = NoBrandEvent(null, departedAt.AddDays(-10));
        db.IysConsentEvents.AddRange(noBrand, badPhone, otherLicense, branded, ownerless);
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == noBrand.Id || e.Id == badPhone.Id))
            .Should().BeFalse("markasız dönemin ispatı m.13 süresi dolunca imha edilir");
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == otherLicense.Id))
            .Should().BeTrue("başka lisansın ispatı bu imhaya karışmaz");
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == branded.Id))
            .Should().BeTrue("markalı olay marka döneminin takvimine bağlı");
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == ownerless.Id))
            .Should().BeTrue("sahipsiz olay otomatik imha edilmez");
        var departures = await vdb.NetgsmDepartures
            .Where(d => d.LicenseId == licenseId).ToListAsync();
        departures.Should().ContainSingle("dönem içi olaylar mevcut randevuya giriyor — yeni randevu açılmaz")
            .Which.PurgedAt.Should().NotBeNull();
        // İmha kaydı marka yoluyla aynı: lisans ve tablo başına.
        var record = await vdb.DataDestructionRecords
            .SingleAsync(r => r.LicenseId == licenseId && r.Target == "IysConsentEvents");
        record.RowCount.Should().Be(2);
        record.Action.Should().Be(DataDestruction.Actions.Deleted);
        record.Trigger.Should().Be(DataDestruction.Triggers.Retention);
        record.Actor.Should().Be(DataDestruction.SystemActor);
        record.Criterion.Should().Contain("m.13");
    }

    [Fact]
    public async Task Markasiz_donem_uc_yil_dolmadan_ispat_yasar()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var licenseId = Guid.NewGuid();
        var departedAt = DateTimeOffset.UtcNow.AddYears(-2);
        db.NetgsmDepartures.Add(new NetgsmDeparture
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, BrandCode = null,
            DepartedAt = departedAt, ConsentsDeletedAt = departedAt,
        });
        var ev = NoBrandEvent(licenseId, departedAt.AddDays(-1));
        db.IysConsentEvents.Add(ev);
        await db.SaveChangesAsync();

        var job = new IysDepartureRetentionJob(db,
            NullLogger<IysDepartureRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.IysConsentEvents.AnyAsync(e => e.Id == ev.Id))
            .Should().BeTrue("m.13: 3 yıl dolmadan ispat İMHA EDİLEMEZ");
        (await vdb.NetgsmDepartures.SingleAsync(d => d.LicenseId == licenseId))
            .PurgedAt.Should().BeNull();
    }

    [Fact]
    public void Job_DI_kapsamindan_cozulur()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IysDepartureRetentionJob>()
            .Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IysMirrorImportJob>()
            .Should().NotBeNull("Görev 8'in işi de aynı yerde kayıtlı olmalı");
        scope.ServiceProvider.GetRequiredService<IysMirrorSyncJob>()
            .Should().NotBeNull("günlük eşitleme işi de aynı yerde kayıtlı olmalı");
    }
}
