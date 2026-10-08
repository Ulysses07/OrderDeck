using System;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Customers;

public class CustomerServiceTests
{
    private static CustomerService MakeSvc(InMemorySqlite db, IClock clock,
        out CustomerRepository customers, out SessionRepository sessions, out LabelRepository labels)
    {
        customers = new CustomerRepository(db);
        sessions = new SessionRepository(db);
        labels = new LabelRepository(db);
        return new CustomerService(customers, sessions, labels, clock);
    }

    [Fact]
    public void GetOrCreate_creates_customer_with_zero_aggregates()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        var customer = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        customer.TotalLabelsPrinted.Should().Be(0);
        customer.TotalAmount.Should().Be(0m);
        customer.IsBlacklisted.Should().BeFalse();
        customer.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void GetOrCreate_returns_existing_on_second_call()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        var first  = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);
        var second = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public void RecordPrintedLabels_bumps_aggregates()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 5000L);
        var svc = MakeSvc(db, clock, out var repo, out _, out _);
        var c = svc.GetOrCreate("instagram", "@a", null, null);

        svc.RecordPrintedLabels(c.Id, labelCount: 3, amount: 450m);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@a")!;
        fresh.TotalLabelsPrinted.Should().Be(3);
        fresh.TotalAmount.Should().Be(450m);
    }

    [Fact]
    public void AddToBlacklist_flips_flag_with_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 7000L);
        var svc = MakeSvc(db, clock, out var repo, out _, out _);
        var c = svc.GetOrCreate("instagram", "@bad", null, null);

        svc.AddToBlacklist(c.Id, "Ödemedi 3 kez");

        var fresh = repo.GetById(c.Id)!;
        fresh.IsBlacklisted.Should().BeTrue();
        fresh.BlacklistReason.Should().Be("Ödemedi 3 kez");
        fresh.BlacklistedAt.Should().Be(7000L);
    }

    [Fact]
    public void RemoveFromBlacklist_clears_flag_reason_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 7000L);
        var svc = MakeSvc(db, clock, out var repo, out _, out _);
        var c = svc.GetOrCreate("instagram", "@bad", null, null);
        svc.AddToBlacklist(c.Id, "test");

        svc.RemoveFromBlacklist(c.Id);

        var fresh = repo.GetById(c.Id)!;
        fresh.IsBlacklisted.Should().BeFalse();
        fresh.BlacklistReason.Should().BeNull();
        fresh.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void EnsureBlacklistedManual_creates_then_blacklists_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 9000L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        var c = svc.EnsureBlacklistedManual("tiktok", "@spammer", "Spam");

        c.Platform.Should().Be("tiktok");
        c.Username.Should().Be("spammer", "elle kara liste de kimliği \"@\"sız açar");
        c.IsBlacklisted.Should().BeTrue();
        c.BlacklistReason.Should().Be("Spam");
        c.BlacklistedAt.Should().Be(9000L);
    }

    [Fact]
    public void EnsureBlacklistedManual_blacklists_existing_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 9000L);
        var svc = MakeSvc(db, clock, out _, out _, out _);
        var existing = svc.GetOrCreate("instagram", "@a", null, null);

        var blacklisted = svc.EnsureBlacklistedManual("instagram", "@a", "Reason");

        blacklisted.Id.Should().Be(existing.Id);
        blacklisted.IsBlacklisted.Should().BeTrue();
    }

    // --- Phase 4g Task 6: GetLastStreamShoppers ---

    [Fact]
    public void GetLastStreamShoppers_NoEndedSession_ReturnsEmpty()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        svc.GetLastStreamShoppers().Should().BeEmpty();
    }

    [Fact]
    public void GetLastStreamShoppers_HydratesCustomersFromLatestEndedSession()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1L);
        var svc = MakeSvc(db, clock, out var customers, out var sessions, out var labels);
        var telefon = TestPhone.NewE164();

        customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, telefon));

        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice",
            "Apple aldım", "APPLE", 50m, AddedAt: 110, PrintedAt: 120));
        sessions.End("s1", 200);

        var result = svc.GetLastStreamShoppers();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
        result[0].Phone.Should().Be(telefon);
    }

    [Fact]
    public void GetLastStreamShoppers_UnprintedLabelsExcluded()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1L);
        var svc = MakeSvc(db, clock, out var customers, out var sessions, out var labels);

        customers.Insert(new Customer("c1", "twitch", "bob", "Bob", null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "bob",
            "Apple", "APPLE", 50m, AddedAt: 110, PrintedAt: null));
        sessions.End("s1", 200);

        svc.GetLastStreamShoppers().Should().BeEmpty();
    }

    [Fact]
    public void Blacklisting_one_grouped_identity_blacklists_the_whole_group()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        var svc = MakeSvc(db, clock, out var customers, out _, out _);

        // Kişi iki platformda kayıtlı (tek grup).
        customers.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "sibel_ig", null), ("facebook", "sibel_fb", null) },
            "Sibel", "İstanbul", null, null, null, false, false, 1000,
            formId: Guid.NewGuid(), submittedAtMs: 1_000_000);

        // Instagram kimliğinden kara listeye al → grup yayılımı.
        var ig = customers.FindByPlatformAndUsername("instagram", "sibel_ig")!;
        svc.AddToBlacklist(ig.Id, "spam");

        // Diğer platform da kara listede olmalı (çapraz-platform açığı kapandı).
        customers.FindByPlatformAndUsername("facebook", "sibel_fb")!.IsBlacklisted.Should().BeTrue();

        // Grup un-blacklist → hepsi temizlenir.
        svc.RemoveFromBlacklist(ig.Id);
        customers.FindByPlatformAndUsername("facebook", "sibel_fb")!.IsBlacklisted.Should().BeFalse();
    }

    [Fact]
    public void YouTube_chat_channelId_is_adopted_into_group_and_inherits_blacklist()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        var svc = MakeSvc(db, clock, out var customers, out _, out _);

        // Form: kişi Instagram + YouTube @handle bildirdi (grup).
        customers.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "sibel_ig", null), ("youtube", "SibelGelibolu", null) },
            "Sibel", "İstanbul", null, null, null, false, false, 1000,
            formId: Guid.NewGuid(), submittedAtMs: 1_000_000);

        // Instagram'dan kara listeye al → tüm grup.
        var ig = customers.FindByPlatformAndUsername("instagram", "sibel_ig")!;
        svc.AddToBlacklist(ig.Id, "spam");

        // YouTube chat mesajı channelId ile gelir, DisplayName = @handle (farklı case).
        var yt = svc.GetOrCreate("youtube", "UCabc123channel", "@sibelgelibolu", null);

        // channelId satırı gruba adopte edildi ve kara listeyi devraldı.
        yt.GroupId.Should().NotBeNullOrEmpty();
        yt.IsBlacklisted.Should().BeTrue();
    }

    // ── Instagram "@" bölünmesi (2026-10-08) ─────────────────────────────────
    // 2026-08-05'ten bu sürüme dek Instagram API yolu kullanıcı adını "@ad" diye yazdı;
    // form ve eski eklenti "@"sız yazar. Kimlik "@"sız tutulur, eski "@ad" satırı yine bulunur.

    private static Customer SeedChatRow(CustomerRepository customers, string platform, string username)
    {
        var c = new Customer(Guid.NewGuid().ToString("N"), platform, username, username, null,
            100, 100, false, null, null, 0, 0m, null, null, null);
        customers.Insert(c);
        return c;
    }

    [Theory]
    [InlineData("instagram")]
    [InlineData("tiktok")]
    [InlineData("facebook")]
    public void Sohbetten_acilan_musteri_bastaki_at_isareti_olmadan_saklanir(string platform)
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var svc = MakeSvc(db, Mock.Of<IClock>(c => c.UnixNow() == 1234L), out var customers, out _, out _);

        var c = svc.GetOrCreate(platform, "@yeni_kisi", "yeni_kisi", null);

        c.Username.Should().Be("yeni_kisi");
        customers.CountAll().Should().Be(1);
    }

    [Fact]
    public void YouTube_kullanici_adina_dokunulmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var svc = MakeSvc(db, Mock.Of<IClock>(c => c.UnixNow() == 1234L), out _, out _, out _);

        svc.GetOrCreate("youtube", "@kanal_adi", null, null).Username.Should().Be("@kanal_adi");
    }

    [Fact]
    public void Yalniz_eski_at_satiri_olan_musteri_yeni_satir_acmadan_bulunur()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var svc = MakeSvc(db, Mock.Of<IClock>(c => c.UnixNow() == 1234L), out var customers, out _, out _);
        var eski = SeedChatRow(customers, "instagram", "@musteri_a");

        svc.GetOrCreate("instagram", "musteri_a", "musteri_a", null).Id.Should().Be(eski.Id);
        svc.Find("instagram", "musteri_a")!.Id.Should().Be(eski.Id);
        customers.CountAll().Should().Be(1, "bölünme yeni satırla sürmemeli");
    }

    [Fact]
    public void Iki_yazim_da_varsa_her_arama_kendi_yazimini_bulur()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var svc = MakeSvc(db, Mock.Of<IClock>(c => c.UnixNow() == 1234L), out var customers, out _, out _);
        var duz = SeedChatRow(customers, "instagram", "musteri_b");
        var atli = SeedChatRow(customers, "instagram", "@musteri_b");

        // Yeni yorum (artık "@"sız) form/eski satıra gider; eski etiketlerin "@ad"ı kendi satırını bulur.
        svc.GetOrCreate("instagram", "musteri_b", null, null).Id.Should().Be(duz.Id);
        svc.GetOrCreate("instagram", "@musteri_b", null, null).Id.Should().Be(duz.Id,
            "sohbet yolu adı \"@\"sız arar — iki kayıt varken yeni etiket formlu satıra gider");
        customers.FindByPlatformAndUsername("instagram", "@musteri_b")!.Id.Should().Be(atli.Id);
        customers.FindByPlatformAndUsername("instagram", "musteri_b")!.Id.Should().Be(duz.Id);
    }

    [Fact]
    public void Eski_at_yazimi_aramasi_yalniz_duz_satir_varsa_onu_bulur()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        MakeSvc(db, Mock.Of<IClock>(c => c.UnixNow() == 1234L), out var customers, out _, out _);
        var duz = SeedChatRow(customers, "instagram", "musteri_c");

        customers.FindByPlatformAndUsername("instagram", "@musteri_c")!.Id.Should().Be(duz.Id);
    }

    [Fact]
    public void YouTube_aramasi_at_yazimlarini_birbirine_eslemez()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        MakeSvc(db, Mock.Of<IClock>(c => c.UnixNow() == 1234L), out var customers, out _, out _);
        SeedChatRow(customers, "youtube", "@kanal_b");

        customers.FindByPlatformAndUsername("youtube", "kanal_b").Should().BeNull();
    }

    [Fact]
    public void Form_yalniz_eski_at_satiri_olan_musteriye_baglanir()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        MakeSvc(db, Mock.Of<IClock>(c => c.UnixNow() == 1234L), out var customers, out _, out _);
        var eski = SeedChatRow(customers, "instagram", "@musteri_d");
        var phone = TestPhone.NewE164();

        customers.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "musteri_d", null) },
            "Deneme Kisi", "Deneme adres", phone, null, null, false, false, 1000,
            formId: Guid.NewGuid(), submittedAtMs: 1_000_000);

        customers.CountAll().Should().Be(1, "form sohbet satırına bağlanmalı, ayrı satır açmamalı");
        customers.GetById(eski.Id)!.Phone.Should().Be(phone);
    }
}
