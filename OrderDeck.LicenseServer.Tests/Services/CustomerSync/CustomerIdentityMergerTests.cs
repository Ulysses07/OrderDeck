using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

public sealed class CustomerIdentityMergerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public CustomerIdentityMergerTests(ApiFactory factory) => _factory = factory;

    // ── yardımcılar ─────────────────────────────────────────────────────
    // Her test kendi License + WpfCustomerProjection çiftini üretir — ApiFactory
    // IClassFixture olduğu için InMemory veritabanı sınıftaki TÜM testler
    // arasında paylaşılır; üretilmiş Guid'ler testleri birbirinden izole eder.

    private static async Task<License> SeedLicenseAsync(LicenseDbContext db, Guid customerId)
    {
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = $"LDK-MRG-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license;
    }

    private static WpfCustomerProjection NewProjection(Guid licenseId, string username, string platform = "tiktok")
        => new() { Id = Guid.NewGuid(), LicenseId = licenseId, Platform = platform, Username = username, UpdatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Siparis_kargo_baglanti_ve_bakiye_asil_kayda_tasinir_bakiyeler_toplanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = $"LDK-MRG-{Guid.NewGuid():N}", CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        var canonical = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "tiktok", Username = "ayse", UpdatedAt = DateTimeOffset.UtcNow };
        var copy = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "tiktok", Username = "Ayse", UpdatedAt = DateTimeOffset.UtcNow };
        db.WpfCustomerProjections.AddRange(canonical, copy);
        db.Orders.Add(new Order { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = copy.Id.ToString("N"), Platform = "tiktok", Username = "Ayse", MessageText = "A1", Price = 100, AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.Shipments.Add(new Shipment { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = copy.Id.ToString("N"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.CustomerBalances.Add(new CustomerBalance { Id = Guid.NewGuid(), LicenseId = license.Id, WpfCustomerId = canonical.Id, Balance = 50, UpdatedAt = DateTimeOffset.UtcNow });
        db.CustomerBalances.Add(new CustomerBalance { Id = Guid.NewGuid(), LicenseId = license.Id, WpfCustomerId = copy.Id, Balance = 30, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        var counts = await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        counts.Orders.Should().Be(1);
        counts.Shipments.Should().Be(1);
        (await db.Orders.SingleAsync(o => o.LicenseId == license.Id)).CustomerId.Should().Be(canonical.Id.ToString("N"));
        (await db.Shipments.SingleAsync(s => s.LicenseId == license.Id)).CustomerId.Should().Be(canonical.Id.ToString("N"));
        var balances = await db.CustomerBalances.Where(b => b.LicenseId == license.Id).ToListAsync();
        balances.Should().HaveCount(2, "kopyanın bakiye satırı silinmez — tutarı taşınır, satırın kendisi 0'lanır");
        balances.Single(b => b.WpfCustomerId == canonical.Id).Balance.Should().Be(80);
        balances.Single(b => b.WpfCustomerId == copy.Id).Balance.Should().Be(0m, "kopyanın tutarı asıl kayda taşındı");
    }

    [Fact]
    public async Task Asil_kayitta_bakiye_yoksa_kopya_icin_yeni_satir_acilir_kopyanin_satiri_sifirlanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);
        var balanceId = Guid.NewGuid();
        db.CustomerBalances.Add(new CustomerBalance { Id = balanceId, LicenseId = license.Id, WpfCustomerId = copy.Id, Balance = 45m, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        var counts = await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        counts.Balances.Should().Be(1);
        var balances = await db.CustomerBalances.Where(b => b.LicenseId == license.Id).ToListAsync();
        balances.Should().HaveCount(2, "kopyanın satırı silinmez — asıl kayıt için YENİ bir satır açılır");
        var canonicalBal = balances.Single(b => b.WpfCustomerId == canonical.Id);
        canonicalBal.Id.Should().NotBe(balanceId, "asıl kayıt için yeni satır açıldı, kopyanınki repoint edilmedi");
        canonicalBal.Balance.Should().Be(45m, "asıl kayıtta bakiye yoktu — tutar değişmeden yeni satıra geçer");
        var copyBal = balances.Single(b => b.Id == balanceId);
        copyBal.WpfCustomerId.Should().Be(copy.Id, "kopyanın satırı kendi WpfCustomerId'sinde kalır, taşınmaz");
        copyBal.Balance.Should().Be(0m, "tutar asıl kayda taşındı — kopyanınki sıfırlanır");
    }

    [Fact]
    public async Task Kopyanin_bakiyesi_sifirsa_asil_kayda_hicbir_sey_eklenmez_sayilmaz_satiri_yalniz_damgalanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);
        var balanceId = Guid.NewGuid();
        var originalUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.CustomerBalances.Add(new CustomerBalance { Id = balanceId, LicenseId = license.Id, WpfCustomerId = copy.Id, Balance = 0m, UpdatedAt = originalUpdatedAt });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        var counts = await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        counts.Balances.Should().Be(0, "kopyanın bakiyesi zaten 0 — sayılmaz");
        var balances = await db.CustomerBalances.AsNoTracking().Where(b => b.LicenseId == license.Id).ToListAsync();
        balances.Should().ContainSingle("asıl kayıt için satır AÇILMAMALI — eklenecek tutar yok");
        var copyBal = balances.Single();
        copyBal.Id.Should().Be(balanceId);
        copyBal.WpfCustomerId.Should().Be(copy.Id);
        copyBal.Balance.Should().Be(0m);
        // Ortak bakiye M12 (spec 2026-10-11 §4.12): sıfır bakiyeli kopya satırı da damgalanır. Damgasız, bu satırı
        // okuyan eşzamanlı bir grup geri alması çakışmadan commit eder ve parayı kopyaya (görünmez) yazardı.
        copyBal.UpdatedAt.Should().BeAfter(originalUpdatedAt, "jeton ilerlemeli — eşzamanlı geri alma çakışsın");
    }

    [Fact]
    public async Task Bakiye_hareketi_sohbet_iban_hafizasi_ve_odeme_eslesmesi_asil_kayda_tasinir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);

        db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, WpfCustomerId = copy.Id,
            Amount = 25m, Kind = "manual-adjustment", CreatedByCustomerId = customerId, CreatedAt = DateTimeOffset.UtcNow,
        });

        var shopperId = Guid.NewGuid();
        db.Shoppers.Add(new Shopper
        {
            Id = shopperId, FullName = "Ornek Musteri",
            Phone = $"+905{Random.Shared.Next(10000000, 99999999)}",
            PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-",
        });
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = license.Id,
            Platform = "tiktok", Username = "Ayse", WpfCustomerId = copy.Id, JoinedAt = DateTimeOffset.UtcNow,
        });

        db.WaConversations.Add(new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            CustomerPhone = $"905{Random.Shared.Next(100000000, 999999999)}",
            PhoneNumberId = "pnid-test", Status = "open", WpfCustomerId = copy.Id, CreatedAt = DateTimeOffset.UtcNow,
        });

        db.CustomerIbanMemories.Add(new CustomerIbanMemory
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, WpfCustomerId = copy.Id,
            IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", IbanMasked = "TR..",
            LearnedFrom = IbanMemorySource.ManualMatch, CreatedAt = DateTimeOffset.UtcNow,
        });

        var bankTx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000),
            ObifinAccountId = 1, BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming,
            Amount = 100m, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow,
        };
        db.BankTransactions.Add(bankTx);
        var staleUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.PaymentMatches.Add(new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, BankTransactionId = bankTx.Id,
            ProposedWpfCustomerId = copy.Id, ActualWpfCustomerId = copy.Id,
            Layer = PaymentMatchLayer.NameAmount, Status = PaymentMatchStatus.ConfirmedByHuman,
            CreatedAt = staleUpdatedAt, UpdatedAt = staleUpdatedAt,
        });

        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        var counts = await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        counts.BalanceTransactions.Should().Be(1);
        counts.Links.Should().Be(1);
        counts.WaConversations.Should().Be(1);
        counts.IbanMemories.Should().Be(1);
        counts.PaymentMatches.Should().Be(1);
        counts.Orders.Should().Be(0);
        counts.Shipments.Should().Be(0);
        counts.Balances.Should().Be(0);

        (await db.CustomerBalanceTransactions.SingleAsync(t => t.LicenseId == license.Id)).WpfCustomerId.Should().Be(canonical.Id);
        (await db.ShopperBroadcasterLinks.SingleAsync(l => l.LicenseId == license.Id)).WpfCustomerId.Should().Be(canonical.Id);
        (await db.WaConversations.SingleAsync(c => c.LicenseId == license.Id)).WpfCustomerId.Should().Be(canonical.Id);
        (await db.CustomerIbanMemories.SingleAsync(m => m.LicenseId == license.Id)).WpfCustomerId.Should().Be(canonical.Id);

        var match = await db.PaymentMatches.SingleAsync(m => m.LicenseId == license.Id);
        match.ProposedWpfCustomerId.Should().Be(canonical.Id);
        match.ActualWpfCustomerId.Should().Be(canonical.Id);
        match.UpdatedAt.Should().BeAfter(staleUpdatedAt, "PaymentMatch.UpdatedAt eşzamanlılık jetonu — taşımada ilerlemeli");
    }

    [Fact]
    public async Task Sms_kampanya_alicisi_denetim_kaydi_oldugu_icin_degistirilmez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);

        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, MessageBody = "Kampanya",
            SegmentsPerMessage = 1, RecipientCount = 1, CreatedByCustomerId = customerId, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.SmsCampaigns.Add(campaign);
        var recipientId = Guid.NewGuid();
        db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
        {
            Id = recipientId, CampaignId = campaign.Id, WpfCustomerId = copy.Id,
            Phone = $"+905{Random.Shared.Next(10000000, 99999999)}", Status = "sent",
        });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        var recipient = await db.SmsCampaignRecipients.SingleAsync(r => r.Id == recipientId);
        recipient.WpfCustomerId.Should().Be(copy.Id, "gönderim anının denetim kaydı — asıl kayda taşınmaz");
    }

    [Fact]
    public async Task Baska_lisansin_ayni_kimlige_rastlayan_satirlari_dokunulmadan_kalir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);

        var (_, otherCustomerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var otherLicense = await SeedLicenseAsync(db, otherCustomerId);

        // Başka lisansın siparişi — CustomerId string'i yalnızca test kurgusuyla
        // copy'nin hex'iyle aynı (gerçekte GUID çakışması olası değil; amaç
        // LicenseId filtresinin sorguda gerçekten var olduğunu kanıtlamak).
        var otherOrderId = Guid.NewGuid();
        db.Orders.Add(new Order
        {
            Id = otherOrderId, LicenseId = otherLicense.Id, CustomerId = copy.Id.ToString("N"),
            Platform = "tiktok", Username = "baska-biri", MessageText = "B1", Price = 10,
            AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Başka lisansın bakiyesi — WpfCustomerId aynı sebeple kasıtlı olarak eşit.
        var otherBalanceId = Guid.NewGuid();
        db.CustomerBalances.Add(new CustomerBalance
        {
            Id = otherBalanceId, LicenseId = otherLicense.Id, WpfCustomerId = copy.Id,
            Balance = 15m, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        var counts = await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        counts.Orders.Should().Be(0);
        counts.Balances.Should().Be(0);
        (await db.Orders.SingleAsync(o => o.Id == otherOrderId)).CustomerId.Should().Be(copy.Id.ToString("N"));
        (await db.CustomerBalances.SingleAsync(b => b.Id == otherBalanceId)).WpfCustomerId.Should().Be(copy.Id);
    }

    [Fact]
    public async Task Siparisin_SyncVersion_u_tasimada_degismez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);
        var orderId = Guid.NewGuid();
        db.Orders.Add(new Order
        {
            Id = orderId, LicenseId = license.Id, CustomerId = copy.Id.ToString("N"),
            Platform = "tiktok", Username = "Ayse", MessageText = "A1", Price = 100,
            AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SyncVersion = 5,
        });
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        await merger.RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();

        (await db.Orders.SingleAsync(o => o.Id == orderId)).SyncVersion.Should().Be(5,
            "EF jetonu WHERE'e zaten giriyor; artış yalnız eşzamanlı orders/sync partilerini 409'a çevirirdi");
    }

    [Fact]
    public async Task Kopya_ve_asil_ayniysa_hicbir_seye_dokunmadan_sifir_sayac_doner()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var same = NewProjection(license.Id, "ayse");
        db.WpfCustomerProjections.Add(same);
        var orderId = Guid.NewGuid();
        db.Orders.Add(new Order
        {
            Id = orderId, LicenseId = license.Id, CustomerId = same.Id.ToString("N"),
            Platform = "tiktok", Username = "ayse", MessageText = "A1", Price = 10,
            AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SyncVersion = 3,
        });
        // Guard'ın asıl koruduğu durum: fromId/toId aynı olunca fromBal/toBal
        // AYNI satıra düşer — guard olmasaydı bu satır kendi üzerine toplanıp
        // SİLİNİRDİ (CustomerBalance merge dalı). Burada varlığı ve tutarı
        // değişmeden kaldığını doğrulamadan guard'ın gerçek etkisi kanıtlanmaz.
        var balanceId = Guid.NewGuid();
        db.CustomerBalances.Add(new CustomerBalance
        {
            Id = balanceId, LicenseId = license.Id, WpfCustomerId = same.Id,
            Balance = 70m, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Payments.Add(Receipt(license.Id, same.Id, PaymentStatus.Pending, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var merger = scope.ServiceProvider.GetRequiredService<CustomerIdentityMerger>();
        var counts = await merger.RepointReferencesAsync(license.Id, same.Id, same.Id, default);
        await db.SaveChangesAsync();

        counts.Orders.Should().Be(0);
        counts.Shipments.Should().Be(0);
        counts.Links.Should().Be(0);
        counts.BalanceTransactions.Should().Be(0);
        counts.Balances.Should().Be(0);
        counts.IbanMemories.Should().Be(0);
        counts.PaymentMatches.Should().Be(0);
        counts.WaConversations.Should().Be(0);
        counts.Payments.Should().Be(0);
        (await db.Orders.SingleAsync(o => o.Id == orderId)).SyncVersion.Should().Be(3, "dokunulmamalı — kopya=asıl yozlaşmış çağrı");
        var balance = await db.CustomerBalances.SingleAsync(b => b.Id == balanceId);
        balance.WpfCustomerId.Should().Be(same.Id);
        balance.Balance.Should().Be(70m, "guard olmasaydı bu satır kendi üzerine toplanıp silinirdi");
    }

    // ── Banka havalesi onayı S2a (spec 2026-10-10 §9): çift IBAN bağı ve dekontun müşterisi ──
    // InMemory tekil indeks uygulamaz: aynı özetin iki müşteride bağı burada tohumlanabilir (gerçek indeksle, S3b
    // sonrası şemada: CustomerIdentityMergerRelationalTests).

    private async Task<(License License, WpfCustomerProjection Canonical, WpfCustomerProjection Copy)> SeedPairAsync(
        LicenseDbContext db)
    {
        var (_, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var license = await SeedLicenseAsync(db, customerId);
        var canonical = NewProjection(license.Id, "ayse");
        var copy = NewProjection(license.Id, "Ayse");
        db.WpfCustomerProjections.AddRange(canonical, copy);
        await db.SaveChangesAsync();
        return (license, canonical, copy);
    }

    private static string NewIbanHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private static CustomerIbanMemory Bond(
        Guid licenseId, Guid customerId, string ibanHash, DateTimeOffset createdAt,
        IbanMemorySource learnedFrom = IbanMemorySource.HumanApproval, DateTimeOffset? lastConfirmedAt = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = customerId, IbanHash = ibanHash, IbanMasked = "TR..",
        LearnedFrom = learnedFrom, CreatedAt = createdAt, LastConfirmedAt = lastConfirmedAt,
    };

    private static Payment Receipt(Guid licenseId, Guid customerId, PaymentStatus status, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = customerId, ReferansNo = $"r-{Guid.NewGuid():N}",
        Amount = 10m, PaidAt = at, Status = status, CreatedAt = at, UpdatedAt = at,
    };

    /// <summary>Tohumu kaydeder, izleyiciyi boşaltıp birleştirir. Boş izleyici üretimdeki gibidir: birleştirici bağları ve
    /// dekontları kendi okumalı, tohumun izlenen satırlarına dayanmamalı.</summary>
    private static async Task<CustomerIdentityMerger.RepointCounts> SaveAndMergeAsync(
        LicenseDbContext db, Guid licenseId, Guid copyId, Guid canonicalId)
    {
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var counts = await new CustomerIdentityMerger(db).RepointReferencesAsync(licenseId, copyId, canonicalId, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return counts;
    }

    [Theory]
    [InlineData(IbanMemorySource.BankConfirm, IbanMemorySource.HumanApproval, true)]   // kopyanınki kalır (asıl kayıttaki daha eski olsa da)
    [InlineData(IbanMemorySource.HumanApproval, IbanMemorySource.BankConfirm, false)]  // asıl kayıttaki kalır
    [InlineData(IbanMemorySource.BankConfirm, IbanMemorySource.BankConfirm, false)]    // eski CreatedAt (asıl kayıttaki) kalır
    [InlineData(IbanMemorySource.ManualMatch, IbanMemorySource.HumanApproval, false)]
    public async Task Iki_kayitta_ayni_IBAN_tek_satira_iner(
        IbanMemorySource copySource, IbanMemorySource canonicalSource, bool copyKept)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (license, canonical, copy) = await SeedPairAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var hash = NewIbanHash();
        var canonicalBond = Bond(license.Id, canonical.Id, hash, t0, canonicalSource);
        var copyBond = Bond(license.Id, copy.Id, hash, t0.AddDays(1), copySource);
        db.CustomerIbanMemories.AddRange(canonicalBond, copyBond);

        var counts = await SaveAndMergeAsync(db, license.Id, copy.Id, canonical.Id);

        counts.IbanMemories.Should().Be(1, "silinen ikiz de taşınan bağ sayılır");
        var kept = await db.CustomerIbanMemories.SingleAsync(m => m.LicenseId == license.Id);
        kept.Id.Should().Be(copyKept ? copyBond.Id : canonicalBond.Id);
        kept.WpfCustomerId.Should().Be(canonical.Id);
        kept.LearnedFrom.Should().Be(copyKept ? copySource : canonicalSource);
        (await db.DataDestructionRecords.AnyAsync(r => r.LicenseId == license.Id)).Should()
            .BeFalse("bağ kalan satırda yaşar: tekilleştirme imha değil");
    }

    /// <summary>Asıl kaydın satırı yalnız orada durduğu için kazanmaz: kural iki satıra da aynı uygulanır.</summary>
    [Theory]
    [InlineData(IbanMemorySource.HumanApproval, -1, false, true)]  // kopyanınki daha eski: Id'si büyük olsa da kalır
    [InlineData(IbanMemorySource.HumanApproval, 0, true, true)]    // aynı an: küçük Id kopyanınki
    [InlineData(IbanMemorySource.HumanApproval, 0, false, false)]  // aynı an: küçük Id asıl kayıttaki
    [InlineData(IbanMemorySource.BankConfirm, -1, true, false)]    // asıl kayıttaki BankConfirm: kopyanınki eski ve küçük Id'li olsa da düşer
    public async Task Kalan_bag_kaynaga_sonra_yasa_sonra_Id_ye_gore_secilir(
        IbanMemorySource canonicalSource, int copyDayOffset, bool copyHasSmallerId, bool copyKept)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (license, canonical, copy) = await SeedPairAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var hash = NewIbanHash();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(i => i.ToString("N"), StringComparer.Ordinal).ToArray();
        var canonicalBond = Bond(license.Id, canonical.Id, hash, t0, canonicalSource);
        var copyBond = Bond(license.Id, copy.Id, hash, t0.AddDays(copyDayOffset));
        canonicalBond.Id = ids[copyHasSmallerId ? 1 : 0];
        copyBond.Id = ids[copyHasSmallerId ? 0 : 1];
        db.CustomerIbanMemories.AddRange(canonicalBond, copyBond);

        await SaveAndMergeAsync(db, license.Id, copy.Id, canonical.Id);

        var kept = await db.CustomerIbanMemories.SingleAsync(m => m.LicenseId == license.Id);
        kept.Id.Should().Be(copyKept ? copyBond.Id : canonicalBond.Id);
        kept.WpfCustomerId.Should().Be(canonical.Id);
    }

    [Theory]
    [InlineData(10, 2, 2)]         // düşenin doğrulaması daha yeni
    [InlineData(2, 10, 2)]         // kalanınki daha yeni
    [InlineData(null, 2, 2)]       // kalan P1 öncesi (boş): başlangıcı 30 günlük CreatedAt'ti
    [InlineData(null, 40, null)]   // düşeninki kalanın başlangıcından (CreatedAt) eski
    public async Task Son_dogrulama_ikisinin_en_yenisi_olur(int? keptDaysAgo, int droppedDaysAgo, int? expectedDaysAgo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (license, canonical, copy) = await SeedPairAsync(db);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? DaysAgo(int? days) => days is { } d ? now.AddDays(-d) : null;
        var hash = NewIbanHash();
        var kept = Bond(license.Id, canonical.Id, hash, now.AddDays(-30), lastConfirmedAt: DaysAgo(keptDaysAgo));
        db.CustomerIbanMemories.AddRange(kept,
            Bond(license.Id, copy.Id, hash, now.AddDays(-29), lastConfirmedAt: DaysAgo(droppedDaysAgo)));

        await SaveAndMergeAsync(db, license.Id, copy.Id, canonical.Id);

        var row = await db.CustomerIbanMemories.SingleAsync(m => m.LicenseId == license.Id);
        row.Id.Should().Be(kept.Id);
        row.LastConfirmedAt.Should().Be(DaysAgo(expectedDaysAgo));
        row.CreatedAt.Should().Be(now.AddDays(-30));
    }

    [Fact]
    public async Task Dusenin_bos_son_dogrulamasi_kalani_degistirmez()
    {
        // Düşen P1 öncesi (doğrulaması boş) ve daha yeni öğrenilmiş: öğrenme anı doğrulama sayılmaz. Hata yönü erken silme.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (license, canonical, copy) = await SeedPairAsync(db);
        var now = DateTimeOffset.UtcNow;
        var hash = NewIbanHash();
        var kept = Bond(license.Id, canonical.Id, hash, now.AddDays(-30), lastConfirmedAt: now.AddDays(-10));
        db.CustomerIbanMemories.AddRange(kept, Bond(license.Id, copy.Id, hash, now.AddDays(-1)));

        await SaveAndMergeAsync(db, license.Id, copy.Id, canonical.Id);

        var row = await db.CustomerIbanMemories.SingleAsync(m => m.LicenseId == license.Id);
        row.Id.Should().Be(kept.Id);
        row.LastConfirmedAt.Should().Be(now.AddDays(-10));
    }

    [Fact]
    public async Task Farkli_IBAN_tasinir_baska_lisansin_satirina_dokunulmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (license, canonical, copy) = await SeedPairAsync(db);
        var (otherLicense, _, _) = await SeedPairAsync(db);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var (hashA, hashB) = (NewIbanHash(), NewIbanHash());
        var canonicalBond = Bond(license.Id, canonical.Id, hashA, t0);
        var copyBond = Bond(license.Id, copy.Id, hashB, t0);
        // Aynı özet, aynı lisansın üçüncü müşterisinde (S3b sonrası ortak IBAN): ikiz değil, kalır.
        var third = Guid.NewGuid();
        var thirdBond = Bond(license.Id, third, hashB, t0.AddDays(-1));
        // Müşteri Id'leri yalnız test kurgusuyla aynı: LicenseId koşulu hem taşınanda hem ikiz aramasında olmalı.
        var otherCopyBond = Bond(otherLicense.Id, copy.Id, hashB, t0.AddDays(-1));
        var otherCanonicalBond = Bond(otherLicense.Id, canonical.Id, hashB, t0.AddDays(-1));
        db.CustomerIbanMemories.AddRange(canonicalBond, copyBond, thirdBond, otherCopyBond, otherCanonicalBond);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Bağlam öbür satırları izlerken de ikiz sayılmamalılar (karar izleyiciden verilir).
        await db.CustomerIbanMemories
            .Where(m => m.LicenseId == otherLicense.Id || m.WpfCustomerId == third).LoadAsync();

        var counts = await new CustomerIdentityMerger(db).RepointReferencesAsync(license.Id, copy.Id, canonical.Id, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        counts.IbanMemories.Should().Be(1);
        (await db.CustomerIbanMemories.Where(m => m.LicenseId == license.Id || m.LicenseId == otherLicense.Id)
                .ToDictionaryAsync(m => m.Id, m => m.WpfCustomerId))
            .Should().BeEquivalentTo(new Dictionary<Guid, Guid>
            {
                [canonicalBond.Id] = canonical.Id, [copyBond.Id] = canonical.Id, [thirdBond.Id] = third,
                [otherCopyBond.Id] = copy.Id, [otherCanonicalBond.Id] = canonical.Id,
            });
    }

    [Fact]
    public async Task Dekontun_musterisi_asil_kayda_cevrilir_UpdatedAt_ve_Status_degismez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (license, canonical, copy) = await SeedPairAsync(db);
        var (otherLicense, _, _) = await SeedPairAsync(db);
        var at = DateTimeOffset.UtcNow.AddDays(-5);
        var third = Guid.NewGuid();
        var pending = Receipt(license.Id, copy.Id, PaymentStatus.Pending, at);
        var approved = Receipt(license.Id, copy.Id, PaymentStatus.Approved, at);
        var thirds = Receipt(license.Id, third, PaymentStatus.Pending, at);
        var foreign = Receipt(otherLicense.Id, copy.Id, PaymentStatus.Pending, at); // Id yalnız test kurgusuyla aynı
        db.Payments.AddRange(pending, approved, thirds, foreign);

        var counts = await SaveAndMergeAsync(db, license.Id, copy.Id, canonical.Id);

        counts.Payments.Should().Be(2);
        var rows = await db.Payments.Where(p => p.LicenseId == license.Id || p.LicenseId == otherLicense.Id)
            .ToDictionaryAsync(p => p.Id);
        rows.Should().HaveCount(4);
        rows[pending.Id].WpfCustomerId.Should().Be(canonical.Id);
        rows[pending.Id].Status.Should().Be(PaymentStatus.Pending);
        rows[approved.Id].WpfCustomerId.Should().Be(canonical.Id);
        rows[approved.Id].Status.Should().Be(PaymentStatus.Approved);
        rows[thirds.Id].WpfCustomerId.Should().Be(third, "başka müşterinin dekontu");
        rows[foreign.Id].WpfCustomerId.Should().Be(copy.Id, "başka lisansın dekontu");
        rows.Values.Should().OnlyContain(p => p.UpdatedAt == at,
            "payments/since imleci UpdatedAt'tir: ilerlese dekontlar bütün bilgisayarlara yeniden inerdi");
    }
}
