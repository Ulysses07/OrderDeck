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
        balances.Should().ContainSingle().Which.Balance.Should().Be(80);
        balances[0].WpfCustomerId.Should().Be(canonical.Id);
    }

    [Fact]
    public async Task Asil_kayitta_bakiye_yoksa_kopyanin_bakiye_satiri_aynen_tasinir()
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
        balances.Should().ContainSingle();
        balances[0].Id.Should().Be(balanceId, "satır silinip yeniden oluşturulmaz, aynı satır repoint edilir");
        balances[0].WpfCustomerId.Should().Be(canonical.Id);
        balances[0].Balance.Should().Be(45m, "asıl kayıtta bakiye yoktu — tutar değişmeden taşınır");
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
            Id = shopperId, FullName = "Ayse Gul",
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
    public async Task Siparisin_SyncVersion_u_tasinirken_artar()
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

        (await db.Orders.SingleAsync(o => o.Id == orderId)).SyncVersion.Should().Be(6);
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
        (await db.Orders.SingleAsync(o => o.Id == orderId)).SyncVersion.Should().Be(3, "dokunulmamalı — kopya=asıl yozlaşmış çağrı");
    }
}
