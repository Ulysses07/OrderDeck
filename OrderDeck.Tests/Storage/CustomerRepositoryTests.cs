using System;
using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class CustomerRepositoryTests
{
    private static CustomerRepository CreateRepository()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new CustomerRepository(db);
    }

    private static Customer NewCustomer(string id = "c1") =>
        new(id, "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 1000, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null);

    [Fact]
    public void Insert_then_FindByPlatformAndUsername_returns_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(NewCustomer());

        var found = repo.FindByPlatformAndUsername("instagram", "@ayse_y");
        found.Should().NotBeNull();
        found!.Id.Should().Be("c1");
        found.IsBlacklisted.Should().BeFalse();
        found.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void FindByPlatformAndUsername_returns_null_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.FindByPlatformAndUsername("instagram", "@nonexistent").Should().BeNull();
    }

    [Fact]
    public void IncrementLabelStats_adds_count_and_amount_and_lastSeen()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());

        repo.IncrementLabelStats("c1", labelDelta: 2, amountDelta: 250m, lastSeenAt: 5000);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y");
        fresh!.TotalLabelsPrinted.Should().Be(2);
        fresh.TotalAmount.Should().Be(250m);
        fresh.LastSeenAt.Should().Be(5000);
    }

    [Fact]
    public void UpdateBlacklist_sets_flag_reason_and_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());

        repo.UpdateBlacklist("c1", isBlacklisted: true, reason: "Ödemedi", blacklistedAt: 9000);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        fresh.IsBlacklisted.Should().BeTrue();
        fresh.BlacklistReason.Should().Be("Ödemedi");
        fresh.BlacklistedAt.Should().Be(9000);
    }

    [Fact]
    public void UpdateBlacklist_can_clear_flag_and_reason()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());
        repo.UpdateBlacklist("c1", isBlacklisted: true, reason: "test", blacklistedAt: 9000);

        repo.UpdateBlacklist("c1", isBlacklisted: false, reason: null, blacklistedAt: null);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        fresh.IsBlacklisted.Should().BeFalse();
        fresh.BlacklistReason.Should().BeNull();
        fresh.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void GetBlacklisted_returns_only_blacklisted_newest_first()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(NewCustomer("c1"));
        repo.Insert(NewCustomer("c2") with { Username = "@b" });
        repo.Insert(NewCustomer("c3") with { Username = "@c" });

        repo.UpdateBlacklist("c1", true, "r1", 1000);
        repo.UpdateBlacklist("c3", true, "r3", 3000);

        var list = repo.GetBlacklisted();
        list.Should().HaveCount(2);
        list[0].Id.Should().Be("c3");
        list[1].Id.Should().Be("c1");
    }

    [Fact]
    public void UpdateNotes_sets_notes_or_normalizes_whitespace_to_null()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var c = new Customer("c-1", "instagram", "@ali", "Ali", null,
            FirstSeenAt: 100, LastSeenAt: 100,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null);
        repo.Insert(c);

        repo.UpdateNotes("c-1", "VIP müşteri");
        repo.GetById("c-1")!.Notes.Should().Be("VIP müşteri");

        repo.UpdateNotes("c-1", "   ");
        repo.GetById("c-1")!.Notes.Should().BeNull();

        repo.UpdateNotes("c-1", null);
        repo.GetById("c-1")!.Notes.Should().BeNull();
    }

    [Fact]
    public void Search_returns_matching_customers_ordered_by_last_seen()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(new Customer("c-1", "instagram", "@ali",     "Ali", null,
            100, 200, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("c-2", "instagram", "@alican",  "Alican", null,
            100, 300, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("c-3", "tiktok",    "@veli",    "Veli", null,
            100, 400, false, null, null, 0, 0m, null, null, null));

        var results = repo.Search("ali", limit: 50);
        results.Select(c => c.Id).Should().Equal(new[] { "c-2", "c-1" });

        repo.Search("ALI", limit: 50).Select(c => c.Id)
            .Should().Equal(new[] { "c-2", "c-1" });

        repo.Search("xyz", limit: 50).Should().BeEmpty();

        repo.Search("ali", limit: 1).Should().HaveCount(1);
    }

    [Fact]
    public void UpsertFromIntakeForm_creates_new_customer_with_form_platform()
    {
        var repo = CreateRepository();
        var now = 1714521600L;

        var customer = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Canlı", "Atatürk Cad. No:12", null, now);

        customer.Platform.Should().Be("form");
        customer.Username.Should().Be("bilalcanli");
        customer.DisplayName.Should().Be("Bilal Canlı");
        customer.Address.Should().Be("Atatürk Cad. No:12");
        customer.FirstSeenAt.Should().Be(now);
        customer.LastSeenAt.Should().Be(now);
    }

    [Fact]
    public void UpsertFromIntakeForm_updates_existing_customer_by_platform_username()
    {
        var repo = CreateRepository();
        var firstNow = 1714521600L;
        var secondNow = 1714608000L;

        var first = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Eski", "Eski Adres", null, firstNow);
        var second = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Yeni", "Yeni Adres", null, secondNow);

        second.Id.Should().Be(first.Id);    // same row
        second.DisplayName.Should().Be("Bilal Yeni");
        second.Address.Should().Be("Yeni Adres");
        second.FirstSeenAt.Should().Be(firstNow);
        second.LastSeenAt.Should().Be(secondNow);
    }

    [Fact]
    public void UpsertFromIntakeForm_treats_form_platform_as_distinct_from_instagram()
    {
        var repo = CreateRepository();
        var now = 1714521600L;

        // Same username, different platform — distinct customers
        repo.UpsertFromIntakeForm("bilalcanli", "Bilal F", "Form Adres", null, now);
        // Mevcut Insert API ile Instagram customer create
        repo.Insert(new Customer(
            Id: Guid.NewGuid().ToString("N"),
            Platform: "instagram",
            Username: "bilalcanli",
            DisplayName: "Bilal IG",
            AvatarUrl: null, FirstSeenAt: now, LastSeenAt: now,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null));

        var allByUsername = repo.Search("bilalcanli", limit: 10);
        allByUsername.Should().HaveCount(2);
        allByUsername.Should().Contain(c => c.Platform == "form");
        allByUsername.Should().Contain(c => c.Platform == "instagram");
    }

    [Fact]
    public void UpdatePhone_PersistsE164ValueAndCanBeReadBack()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var c = new Customer("id1", "twitch", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null);
        repo.Insert(c);

        repo.UpdatePhone("id1", "+905551234567");

        var loaded = repo.GetById("id1");
        loaded!.Phone.Should().Be("+905551234567");
    }

    [Fact]
    public void UpdatePhone_OnNonExistentId_DoesNotThrow()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        Action act = () => repo.UpdatePhone("nonexistent-id", "+905551234567");
        act.Should().NotThrow();
    }

    // ── Kargo PR F: RecipientPaysActive ─────────────────────────────────

    [Fact]
    public void Insert_default_RecipientPaysActive_is_false()
    {
        var repo = CreateRepository();
        repo.Insert(NewCustomer());

        var loaded = repo.GetById("c1");
        loaded!.RecipientPaysActive.Should().BeFalse();
    }

    [Fact]
    public void SetRecipientPaysActive_flips_flag_true_then_false()
    {
        var repo = CreateRepository();
        repo.Insert(NewCustomer());

        repo.SetRecipientPaysActive("c1", true);
        repo.GetById("c1")!.RecipientPaysActive.Should().BeTrue();

        repo.SetRecipientPaysActive("c1", false);
        repo.GetById("c1")!.RecipientPaysActive.Should().BeFalse();
    }

    [Fact]
    public void SetRecipientPaysActive_on_unknown_id_does_not_throw()
    {
        var repo = CreateRepository();
        Action act = () => repo.SetRecipientPaysActive("nonexistent", true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Insert_with_RecipientPaysActive_true_persists_flag()
    {
        var repo = CreateRepository();
        var c = NewCustomer() with { RecipientPaysActive = true };
        repo.Insert(c);

        repo.GetById("c1")!.RecipientPaysActive.Should().BeTrue();
    }

    [Fact]
    public void UpsertPersonFromIntake_creates_row_per_platform_sharing_one_group()
    {
        var repo = CreateRepository();

        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "@sibel_s", null), ("youtube", "sibelgelibolu", null), ("tiktok", "sibel.tt", null) },
            fullName: "Sibel S", address: "İstanbul", phone: "+905551112233",
            email: "sibel@example.com", tckn: "12345678901",
            whatsAppConsent: true, smsConsent: false, nowUnix: 5000);

        groupId.Should().NotBeNullOrEmpty();

        // Handle normalize: baştaki '@' atılır.
        var ig = repo.FindByPlatformAndUsername("instagram", "sibel_s");
        var yt = repo.FindByPlatformAndUsername("youtube", "sibelgelibolu");
        var tt = repo.FindByPlatformAndUsername("tiktok", "sibel.tt");

        ig.Should().NotBeNull();
        yt.Should().NotBeNull();
        tt.Should().NotBeNull();

        // Hepsi aynı grupta.
        ig!.GroupId.Should().Be(groupId);
        yt!.GroupId.Should().Be(groupId);
        tt!.GroupId.Should().Be(groupId);

        // İletişim/izin bilgisi tüm satırlara yazıldı; DisplayName Ad Soyad'a set edildi.
        ig.Email.Should().Be("sibel@example.com");
        ig.Tckn.Should().Be("12345678901");
        ig.Address.Should().Be("İstanbul");
        ig.WhatsAppConsent.Should().BeTrue();
        ig.SmsConsent.Should().BeFalse();
        ig.DisplayName.Should().Be("Sibel S");
    }

    [Fact]
    public void UpsertPersonFromIntake_reuses_existing_group_when_identity_already_grouped()
    {
        var repo = CreateRepository();

        // İlk kayıt: Instagram + YouTube tek grupta.
        var g1 = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "sibel_s", null), ("youtube", "sibelgelibolu", null) },
            "Sibel S", "İstanbul", null, null, null, false, false, 5000);

        // İkinci kayıt: aynı Instagram + yeni Facebook → grup yeniden kullanılmalı (merge).
        var g2 = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "sibel_s", null), ("facebook", "sibel.fb", null) },
            "Sibel S", "İstanbul", null, null, null, false, false, 6000);

        g2.Should().Be(g1);
        repo.FindByPlatformAndUsername("facebook", "sibel.fb")!.GroupId.Should().Be(g1);
    }

    [Fact]
    public void UpsertPersonFromIntake_merges_into_existing_shopper_row_case_insensitive()
    {
        var repo = CreateRepository();

        // Alışverişten otomatik kaydedilmiş numarasız müşteri (chat casing farklı).
        repo.Insert(new Customer("shop1", "instagram", "SibelVIP", "SibelVIP", null,
            100, 100, false, null, null, 3, 250m, null, null, null));

        // Form: aynı kişi küçük harfle kaydoluyor.
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "sibelvip", null) },
            "Sibel Yılmaz", "İzmir", "+905551112233", null, null, true, false, 5000);

        // AYRI satır AÇILMAMALI — mevcut satır güncellenmeli (geçmiş korunur).
        var all = repo.GetAll().Where(c => c.Platform == "instagram").ToList();
        all.Should().HaveCount(1);
        var c = all[0];
        c.Id.Should().Be("shop1");
        c.Phone.Should().Be("+905551112233");
        c.Address.Should().Be("İzmir");
        c.TotalAmount.Should().Be(250m);          // alışveriş geçmişi korundu
        c.GroupId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void UpsertPersonFromIntake_youtube_merges_into_channelId_row_via_handle()
    {
        var repo = CreateRepository();

        // Chat'ten kaydedilmiş YouTube müşterisi: Username=channelId, DisplayName=@handle.
        repo.Insert(new Customer("yt1", "youtube", "UCabc123channel", "@sibelgelibolu", null,
            100, 100, false, null, null, 2, 180m, null, null, null));

        // Form: müşteri @handle'ını yazıyor (channelId'yi bilmez).
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("youtube", "SibelGelibolu", null) },   // farklı casing + @ yok
            "Sibel G", "Ankara", "+905559998877", null, null, false, true, 5000);

        // channelId satırına birleşmeli, AYRI (youtube, handle) satırı açılmamalı.
        var yts = repo.GetAll().Where(c => c.Platform == "youtube").ToList();
        yts.Should().HaveCount(1);
        var c = yts[0];
        c.Id.Should().Be("yt1");
        c.Username.Should().Be("UCabc123channel");  // channelId korundu (chat eşleşmesi sürsün)
        c.Phone.Should().Be("+905559998877");
        c.TotalAmount.Should().Be(180m);            // geçmiş korundu
    }

    [Fact]
    public void CountAll_and_CountRegistered_reflect_phone_presence()
    {
        var repo = CreateRepository();
        // 2 chat-only (telefonsuz) + 1 kayıtlı (telefonlu)
        repo.Insert(new Customer("a", "instagram", "u1", "U1", null, 1, 1, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("b", "youtube", "UCx", "@u2", null, 1, 1, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("c", "instagram", "u3", "U3", null, 1, 1, false, null, null, 0, 0m, null, "Adres", "+905551112233"));

        repo.CountAll().Should().Be(3);
        repo.CountRegistered().Should().Be(1);
    }

    [Fact]
    public void MergeIntoGroup_assigns_shared_group_to_ungrouped_customers()
    {
        var repo = CreateRepository();
        repo.Insert(new Customer("a", "instagram", "u1", "U1", null, 1, 1, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("b", "youtube", "UCx", "@u2", null, 1, 1, false, null, null, 0, 0m, null, null, null));

        var groupId = repo.MergeIntoGroup(new[] { "a", "b" });

        groupId.Should().NotBeNullOrWhiteSpace();
        repo.GetById("a")!.GroupId.Should().Be(groupId);
        repo.GetById("b")!.GroupId.Should().Be(groupId);
    }

    [Fact]
    public void MergeIntoGroup_preserves_existing_group_and_pulls_all_members()
    {
        var repo = CreateRepository();
        // "a" ve "b" zaten g1 grubunda; "c" gru+psuz. c'yi a ile birleştirince
        // hepsi g1'e toplanmalı (b dahil, geride üye kalmamalı).
        repo.Insert(new Customer("a", "instagram", "u1", "U1", null, 1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("b", "youtube", "UCx", "@u2", null, 1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("c", "tiktok", "u3", "U3", null, 1, 1, false, null, null, 0, 0m, null, null, null));

        var groupId = repo.MergeIntoGroup(new[] { "a", "c" });

        groupId.Should().Be("g1");
        repo.GetById("a")!.GroupId.Should().Be("g1");
        repo.GetById("b")!.GroupId.Should().Be("g1");
        repo.GetById("c")!.GroupId.Should().Be("g1");
    }

    [Fact]
    public void MergeIntoGroup_throws_when_fewer_than_two()
    {
        var repo = CreateRepository();
        repo.Insert(new Customer("a", "instagram", "u1", "U1", null, 1, 1, false, null, null, 0, 0m, null, null, null));

        var act = () => repo.MergeIntoGroup(new[] { "a" });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnmergeGroup_clears_group_for_all_members()
    {
        var repo = CreateRepository();
        repo.Insert(new Customer("a", "instagram", "u1", "U1", null, 1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("b", "youtube", "UCx", "@u2", null, 1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));

        repo.UnmergeGroup("g1");

        repo.GetById("a")!.GroupId.Should().BeNull();
        repo.GetById("b")!.GroupId.Should().BeNull();
    }

    [Fact]
    public void GetGroupMembers_returns_all_rows_in_group_only()
    {
        var repo = CreateRepository();
        repo.Insert(new Customer("a", "instagram", "u1", "U1", null, 1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("b", "youtube", "UCx", "@handle", null, 1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("c", "tiktok", "u3", "U3", null, 1, 1, false, null, null, 0, 0m, null, null, null));

        var members = repo.GetGroupMembers("g1");

        members.Should().HaveCount(2);
        members.Select(m => m.Platform).Should().BeEquivalentTo(new[] { "instagram", "youtube" });
    }

    [Fact]
    public void BackfillFullNameForIdentities_fills_empty_fullname_across_group_only()
    {
        var repo = CreateRepository();
        // Grup: IG (chat takma adlı, FullName boş) + YouTube. FullName ikisinde de boş.
        repo.Insert(new Customer("ig1", "instagram", "musaa.sevinc", "musaa.sevinc", null,
            100, 100, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("yt1", "youtube", "UCabc", "@musa", null,
            100, 100, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));

        var updated = repo.BackfillFullNameForIdentities(
            new[] { ("instagram", "musaa.sevinc") }, "Musa Sevinç");

        updated.Should().Be(2); // eşleşen satırın tüm grubu
        repo.GetById("ig1")!.FullName.Should().Be("Musa Sevinç");
        repo.GetById("yt1")!.FullName.Should().Be("Musa Sevinç");
        repo.GetById("ig1")!.DisplayName.Should().Be("musaa.sevinc"); // dokunulmadı
    }

    [Fact]
    public void BackfillFullNameForIdentities_does_not_overwrite_existing_fullname()
    {
        var repo = CreateRepository();
        repo.Insert(new Customer("ig1", "instagram", "u", "u", null,
            100, 100, false, null, null, 0, 0m, null, null, null, FullName: "Zaten Var"));

        var updated = repo.BackfillFullNameForIdentities(
            new[] { ("instagram", "u") }, "Yeni İsim");

        updated.Should().Be(0);
        repo.GetById("ig1")!.FullName.Should().Be("Zaten Var");
    }

    [Fact]
    public void UpsertPersonFromIntake_links_by_phone_when_usernames_differ()
    {
        var repo = CreateRepository();
        // Önceden IG'den kaydolmuş, telefonlu, gruplu müşteri.
        repo.Insert(new Customer("ig1", "instagram", "ayse", "Ayşe Y", null,
            1, 1, false, null, null, 0, 0m, null, "Adr", "+905551112233", GroupId: "g1"));

        // Aynı kişi FB'den FARKLI kullanıcı adıyla ama AYNI telefonla kaydoluyor.
        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("facebook", "ayse.fb", null) },
            "Ayşe Yılmaz", "Adr", "+905551112233", null, null, false, true, 5000);

        groupId.Should().Be("g1"); // mevcut grup telefonla bulundu, korundu
        repo.GetById("ig1")!.GroupId.Should().Be("g1");
        var members = repo.GetGroupMembers("g1");
        members.Select(m => m.Platform).Should().Contain(new[] { "instagram", "facebook" });
    }

    [Fact]
    public void UpsertPersonFromIntake_phone_merge_propagates_blacklist()
    {
        var repo = CreateRepository();
        // Kara listeli, telefonlu, tekil (grupsuz) mevcut müşteri.
        repo.Insert(new Customer("bad1", "instagram", "kotu", "Kötü", null,
            1, 1, true, "dolandırıcı", null, 0, 0m, 999, "Adr", "+905550001122"));

        // Aynı telefonla FB'den yeni kayıt → aynı gruba çekilir + kara liste yayılır.
        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("facebook", "kotu.fb", null) },
            "Kötü Kişi", "Adr", "+905550001122", null, null, false, true, 5000);

        var members = repo.GetGroupMembers(groupId);
        members.Should().HaveCount(2);
        members.Should().OnlyContain(m => m.IsBlacklisted);
    }

    [Fact]
    public void UpsertPersonFromIntake_stores_real_fullname_without_overwriting_chat_displayname()
    {
        var repo = CreateRepository();
        // Chat'ten gelmiş IG satırı: DisplayName = IG takma adı (gerçek isim değil).
        repo.Insert(new Customer("ig1", "instagram", "musaa.sevinc", "musaa.sevinc", null,
            100, 100, false, null, null, 0, 0m, null, null, null));

        // Form: gerçek Ad Soyad farklı.
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "musaa.sevinc", null) },
            "Musa Sevinç", "Adres", "+905076313815", "e@x.com", null, true, true, 5000);

        var c = repo.GetById("ig1")!;
        c.DisplayName.Should().Be("musaa.sevinc"); // chat takma adı korundu (chat eşleşmesi sürsün)
        c.FullName.Should().Be("Musa Sevinç");     // gerçek isim ayrı kolonda saklandı
        c.Phone.Should().Be("+905076313815");
    }
}
