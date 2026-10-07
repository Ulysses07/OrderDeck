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

    /// <summary>Operatör kartta gördüğü ismi yazıyor; isim Username'de değil
    /// DisplayName (chat takma adı) ya da FullName (kayıt formu) kolonunda
    /// duruyor. Arama yalnız Username'e bakarsa müşteri "yok" görünür.</summary>
    [Fact]
    public void Search_finds_customers_by_display_name_and_full_name()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(new Customer("c-1", "instagram", "dnmalc", "Deneme Alıcı", null,
            100, 200, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("c-2", "instagram", "ayse_o", null, null,
            100, 300, false, null, null, 0, 0m, null, null, null,
            FullName: "Örnek Müşteri"));

        repo.Search("Deneme Alıcı", limit: 50).Select(c => c.Id).Should().Equal("c-1");
        repo.Search("Örnek Müşteri", limit: 50).Select(c => c.Id).Should().Equal("c-2");
        // Kullanıcı adıyla arama bozulmamalı.
        repo.Search("dnmalc", limit: 50).Select(c => c.Id).Should().Equal("c-1");
    }

    [Fact]
    public void Search_is_turkish_case_insensitive_and_word_order_agnostic()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        // Kurgusal sözcükler; sınanan harfler (Ş/ş, I/ı) korunuyor.
        repo.Insert(new Customer("c-1", "instagram", "u1", "Şemsiye Işıma", null,
            100, 200, false, null, null, 0, 0m, null, null, null));

        // SQLite LOWER() yalnız ASCII küçültür → "şemsiye" eskiden eşleşmiyordu.
        repo.Search("şemsiye", limit: 50).Select(c => c.Id).Should().Equal("c-1");
        // Türkçe i ailesi: "ışıma" / "isima" değil ama "IŞIMA" aynı harf sayılmalı.
        repo.Search("IŞIMA", limit: 50).Select(c => c.Id).Should().Equal("c-1");
        // Sözcük sırası ve fazladan boşluk sonucu değiştirmemeli.
        repo.Search("ışıma   şemsiye", limit: 50).Select(c => c.Id).Should().Equal("c-1");
        repo.Search("şemsiye defter", limit: 50).Should().BeEmpty();
    }

    /// <summary>Operatör numarayı kayıttakinden farklı biçimde yazıyor
    /// (başında 0 olan boşluklu ulusal yazılış ↔ kanonik +90'lı biçim); iki taraf
    /// da rakamlara indirgenmezse hiçbir zaman eşleşmez.</summary>
    [Fact]
    public void Search_finds_customers_by_phone_in_any_format()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        // Numaralar her koşuda üretilir; biçimli sorgular kayıttaki numaradan türetilir.
        // Parça: sona yakın dört hane (son ek değil). Arama baştaki sıfırları attığı için
        // "0" ile başlayan parça dört haneden kısa kalıp hiç aranmazdı — böyle numara alınmaz.
        string telefon;
        do telefon = TestPhone.NewE164(); while (telefon[^6] == '0');
        var ulusal = telefon[3..];                  // 10 hane, "+90" atılmış
        var parca = ulusal[^6..^2];
        // İkinci numara parçayı taşımamalı — taşısaydı parça araması iki müşteriyi de bulurdu.
        var baskaTelefon = TestPhone.NewE164();
        while (baskaTelefon[3..].Contains(parca)) baskaTelefon = TestPhone.NewE164();

        repo.Insert(new Customer("c-1", "instagram", "u1", "Deneme Alıcı", null,
            100, 200, false, null, null, 0, 0m, null, null, telefon));
        repo.Insert(new Customer("c-2", "instagram", "u2", "Veli", null,
            100, 300, false, null, null, 0, 0m, null, null, baskaTelefon));

        repo.Search(telefon, limit: 50).Select(c => c.Id).Should().Equal("c-1");
        repo.Search($"0{ulusal[..3]} {ulusal[3..6]} {ulusal[6..8]} {ulusal[8..]}", limit: 50)
            .Select(c => c.Id).Should().Equal("c-1");
        repo.Search(ulusal, limit: 50).Select(c => c.Id).Should().Equal("c-1");
        // Numaranın son parçası da yeter (operatör sadece sonunu hatırlıyor).
        repo.Search(parca, limit: 50).Select(c => c.Id).Should().Equal("c-1");
        // Çok kısa girdi tüm listeyi getirmemeli.
        repo.Search("55", limit: 50).Should().BeEmpty();
    }

    [Fact]
    public void UpsertFromIntakeForm_creates_new_customer_with_form_platform()
    {
        var repo = CreateRepository();
        var now = 1714521600L;

        var customer = repo.UpsertFromIntakeForm("ornekmusteri", "Örnek Müşteri", "Atatürk Cad. No:12", null, now, submittedAtMs: now * 1000);

        customer.Platform.Should().Be("form");
        customer.Username.Should().Be("ornekmusteri");
        customer.DisplayName.Should().Be("Örnek Müşteri");
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

        var first = repo.UpsertFromIntakeForm("ornekmusteri", "Bilal Eski", "Eski Adres", null, firstNow, submittedAtMs: firstNow * 1000);
        var second = repo.UpsertFromIntakeForm("ornekmusteri", "Bilal Yeni", "Yeni Adres", null, secondNow, submittedAtMs: secondNow * 1000);

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
        repo.UpsertFromIntakeForm("ornekmusteri", "Bilal F", "Form Adres", null, now, submittedAtMs: now * 1000);
        // Mevcut Insert API ile Instagram customer create
        repo.Insert(new Customer(
            Id: Guid.NewGuid().ToString("N"),
            Platform: "instagram",
            Username: "ornekmusteri",
            DisplayName: "Bilal IG",
            AvatarUrl: null, FirstSeenAt: now, LastSeenAt: now,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null));

        var allByUsername = repo.Search("ornekmusteri", limit: 10);
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
        var telefon = TestPhone.NewE164();

        repo.UpdatePhone("id1", telefon);

        var loaded = repo.GetById("id1");
        loaded!.Phone.Should().Be(telefon);
    }

    // ── R12-D02 (2026-09-16 denetimi): telefon yazısı da silme kapısına tabi ──
    //
    // "Karar, uygulanan yazının WHERE'inde yaşar" sözleşmesi intake ve backfill
    // yollarında vardı; elle telefon girişi o kapıya sahip değildi. Silme
    // kararı indikten SONRA gelen bir Save boşaltılmış satırı yeniden
    // dolduruyordu ve sonraki boş ingest onu temizlemiyordu.

    [Fact]
    public void UpdatePhone_silinmis_satira_yazmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "twitch", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));
        repo.RecordPurge("twitch", "alice", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var written = repo.UpdatePhone("id1", TestPhone.NewE164());

        written.Should().Be(0, "çağıran hiç satır değişmediğini görebilmeli");
        repo.GetById("id1")!.Phone.Should().BeNull();
    }

    [Fact]
    public void UpdatePhone_silme_oncesi_yazi_normal_calisir()
    {
        // Karşı kontrol: kapı, silinmemiş satırdaki meşru yazıyı engellemiyor.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "twitch", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));
        var telefon = TestPhone.NewE164();

        var written = repo.UpdatePhone("id1", telefon);

        written.Should().Be(1);
        repo.GetById("id1")!.Phone.Should().Be(telefon);

        // …ve sonradan inen silme onu yine temizliyor (R11-D01 korunuyor).
        repo.RecordPurge("twitch", "alice", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        repo.GetById("id1")!.Phone.Should().BeNull();
    }

    [Fact]
    public void UpdatePhone_OnNonExistentId_DoesNotThrow()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        Action act = () => repo.UpdatePhone("nonexistent-id", TestPhone.NewE164());
        act.Should().NotThrow();
    }

    // ── N03 (2026-09-10 denetimi): UpdatePhone delta imlecini ilerletmeli ──

    /// <summary>İmleci "bu satır senkronlandı" konumuna taşır — sync servisinin
    /// AdvanceWatermark'ı ile aynı davranış (partinin son satırının SyncSeq'i).</summary>
    private static long CursorAfter(CustomerRepository repo, string id)
        => repo.GetById(id)!.SyncSeq;

    /// <summary>Gönderimin delta sorgusu (C6, <see cref="CustomerSyncRepository.GetForPush"/>) — üretim
    /// çağıranı kalmayan eski <c>GetUpdatedSince</c>'in yerine (C7 incelemesi M-9).</summary>
    private static IReadOnlyList<CustomerSyncRow> Delta(InMemorySqlite db, long cursor, int max = 100)
        => new CustomerSyncRepository(db).GetForPush(cursor, max);

    [Fact]
    public void UpdatePhone_satiri_delta_sorgusuna_dusurur()
    {
        // İmleç satırı çoktan geçmiş: telefon güncellenince satır yeniden
        // seçilmeli; yoksa numara sunucuya hiç senkronlanmaz.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "twitch", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));
        var cursor = CursorAfter(repo, "id1");
        Delta(db, cursor).Should().BeEmpty("imleç satırı zaten geçti");
        var telefon = TestPhone.NewE164();

        repo.UpdatePhone("id1", telefon);

        var delta = Delta(db, cursor);
        delta.Should().ContainSingle().Which.Fields.Phone.Should().Be(telefon);
    }

    [Fact]
    public void UpdatePhone_ayni_saniyede_ikinci_guncelleme_de_secilir()
    {
        // İlk güncelleme senkronlandıktan sonra (imleç satırın yeni konumunda)
        // aynı saniye içindeki ikinci güncelleme de delta'ya düşmeli — imleç
        // saatten bağımsız olduğu için saniye çözünürlüğü hiç devreye girmiyor.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "twitch", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));
        // Aynı üretilen gövde, farklı son hane: iki numara kesin farklı.
        var govde = TestPhone.NewNational()[..9];
        var ilkTelefon = $"+90{govde}1";
        var ikinciTelefon = $"+90{govde}2";
        repo.UpdatePhone("id1", ilkTelefon);
        var cursor = CursorAfter(repo, "id1");
        Delta(db, cursor)
            .Should().BeEmpty("ilk güncelleme senkronlandı, imleç satırın üzerinde");

        repo.UpdatePhone("id1", ikinciTelefon);

        var delta = Delta(db, cursor);
        delta.Should().ContainSingle().Which.Fields.Phone.Should().Be(ikinciTelefon);
    }

    // ── N03-k (2026-09-11 denetimi): intake upsert'leri de imleci ilerletmeli ──

    [Fact]
    public void UpsertFromIntakeForm_guncelleme_satiri_delta_sorgusuna_dusurur()
    {
        // Satır zaten senkronlanmış (imleç üzerinde) ve form güncellemesi aynı
        // saniyede geliyor: LastSeenAt = @now düz yazımı satırı imlecin ARKASINDA
        // bırakır → telefon/adres sunucuya hiç gitmez. MAX(LastSeenAt+1, @now)
        // ile satır kesin olarak imlecin önüne düşmeli.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "form", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));
        var cursor = CursorAfter(repo, "id1");
        Delta(db, cursor).Should().BeEmpty("imleç satırı zaten geçti");
        var telefon = TestPhone.NewE164();

        repo.UpsertFromIntakeForm("alice", "Örnek Müşteri", "İzmir", telefon, nowUnix: 1000, submittedAtMs: 1_000_000);

        var delta = Delta(db, cursor);
        delta.Should().ContainSingle().Which.Fields.Phone.Should().Be(telefon);
    }

    [Fact]
    public void UpsertFromIntakeForm_donen_musteri_yazilan_LastSeenAt_ile_ayni()
    {
        // Dönen nesnenin LastSeenAt'i DB'ye yazılanla aynı olmalı — çağıran bu
        // değeri imleç/karşılaştırma için kullanabilir.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "form", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));

        var returned = repo.UpsertFromIntakeForm("alice", "Örnek Müşteri", "İzmir", null, nowUnix: 1000, submittedAtMs: 1_000_000);

        returned.LastSeenAt.Should().Be(repo.GetById("id1")!.LastSeenAt);
    }

    [Fact]
    public void UpsertPersonFromIntake_guncelleme_satiri_delta_sorgusuna_dusurur()
    {
        // Aynı hata çoklu-platform intake upsert'inde de var: mevcut satır
        // güncellenirken LastSeenAt = @now düz yazılıyor.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "instagram", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));
        var cursor = CursorAfter(repo, "id1");
        Delta(db, cursor).Should().BeEmpty("imleç satırı zaten geçti");
        var telefon = TestPhone.NewE164();

        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "alice", null) },
            "Örnek Müşteri", "İzmir", telefon, null, null, false, false, nowUnix: 1000, formId: Guid.NewGuid(), submittedAtMs: 1_000_000);

        var delta = Delta(db, cursor);
        delta.Should().ContainSingle().Which.Fields.Phone.Should().Be(telefon);
    }

    // ── N03-g (2026-09-12 denetimi): imleç saatten bağımsız olmalı ──────

    [Fact]
    public void GetForPush_ileri_zamanli_baska_satir_imleci_tasisa_da_guncelleme_kaybolmaz()
    {
        // Denetimin kontrollü deneyi: imleç GENEL, LastSeenAt artışı SATIRA
        // ÖZEL. Saat kaymış/ileri zamanlı TEK satır imleci 60 sn öne taşırsa,
        // BAŞKA bir satırın MAX(LastSeenAt+1, now) artışı imlece asla
        // yetişemez → o güncelleme sunucuya HİÇ gitmez ve bir daha denenmez.
        // SyncSeq saatten bağımsız olduğu için bu sınıf yapısal olarak imkânsız.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        repo.Insert(new Customer("id1", "twitch", "alice", "Alice", null,
            now - 10, now - 10, false, null, null, 0, 0m, null, null, null));
        // Saat kayması / ileri zamanlı veri: bu satır 60 sn ileride ve SON
        // senkronlanan satır — imleç onun üzerinde kalıyor.
        repo.Insert(new Customer("skew", "twitch", "bob", "Bob", null,
            now, now + 60, false, null, null, 0, 0m, null, null, null));

        var batch = Delta(db, 0);
        batch.Should().HaveCount(2);
        var cursor = batch[^1].SyncSeq;
        var telefon = TestPhone.NewE164();

        repo.UpdatePhone("id1", telefon);

        var delta = Delta(db, cursor);
        delta.Should().ContainSingle("ileri zamanlı satır imleci taşısa da güncelleme kaybolmamalı")
            .Which.Fields.Phone.Should().Be(telefon);
    }

    [Fact]
    public void GetForPush_saat_geri_alinirsa_bile_guncelleme_secilir()
    {
        // Saat geri alındığında LastSeenAt geri gidebilir (iş zamanı öyle
        // olmalı — "en son ne zaman görüldü" kullanıcıya gösteriliyor), ama
        // senkron sırası asla geri gitmemeli.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "twitch", "alice", "Alice", null,
            5_000_000_000, 5_000_000_000, false, null, null, 0, 0m, null, null, null));
        var cursor = CursorAfter(repo, "id1");

        // UpdatePhone MAX(LastSeenAt+1, now) yazar; LastSeenAt zaten çok ileride
        // olduğu için iş zamanı pratikte ilerlemese de satır delta'ya düşmeli.
        var telefon = TestPhone.NewE164();
        repo.UpdatePhone("id1", telefon);

        Delta(db, cursor).Should().ContainSingle()
            .Which.Fields.Phone.Should().Be(telefon);
    }

    [Fact]
    public void GetForPush_ayni_saniyedeki_satirlar_sayfa_sinirinda_atlanmaz()
    {
        // F07: aynı saniyeye BatchSize'dan fazla satır düştüğünde yalnız-zaman
        // imleci sayfa sınırındaki satırları sonsuza dek atlıyordu. SyncSeq
        // benzersiz olduğu için "aynı değere sahip iki satır" hâli yok.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        for (var i = 0; i < 25; i++)
        {
            repo.Insert(new Customer($"id{i:D2}", "twitch", $"u{i}", null, null,
                1000, 1000, false, null, null, 0, 0m, null, null, null));
        }

        var seen = new List<string>();
        long cursor = 0;
        while (true)
        {
            var page = Delta(db, cursor, 10);
            if (page.Count == 0) break;
            seen.AddRange(page.Select(c => c.Id));
            cursor = page[^1].SyncSeq;
        }

        seen.Should().HaveCount(25).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void SyncSeq_yalnizca_projeksiyona_giden_alanlar_degisince_artar()
    {
        // Etiket basımı sıcak yol: TotalAmount/TotalLabelsPrinted/LastSeenAt
        // projeksiyonun İÇERİĞİNİ değiştirmiyor. Eskiden imleç LastSeenAt
        // olduğu için her etiket satırı yeniden gönderiyordu.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(new Customer("id1", "twitch", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null));
        var cursor = CursorAfter(repo, "id1");

        repo.IncrementLabelStats("id1", 1, 250m, lastSeenAt: 2000);

        Delta(db, cursor).Should()
            .BeEmpty("etiket sayacı sunucu projeksiyonunu değiştirmiyor");
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
        var tckn = TestTckn.NewValid();

        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "@sibel_s", null), ("youtube", "ornekkanal", null), ("tiktok", "sibel.tt", null) },
            fullName: "Sibel S", address: "İstanbul", phone: TestPhone.NewE164(),
            email: "sibel@example.com", tckn: tckn,
            whatsAppConsent: true, smsConsent: false, nowUnix: 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        groupId.Should().NotBeNullOrEmpty();

        // Handle normalize: baştaki '@' atılır.
        var ig = repo.FindByPlatformAndUsername("instagram", "sibel_s");
        var yt = repo.FindByPlatformAndUsername("youtube", "ornekkanal");
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
        ig.Tckn.Should().Be(tckn);
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
            new (string, string, string?)[] { ("instagram", "sibel_s", null), ("youtube", "ornekkanal", null) },
            "Sibel S", "İstanbul", null, null, null, false, false, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        // İkinci kayıt: aynı Instagram + yeni Facebook → grup yeniden kullanılmalı (merge).
        var g2 = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "sibel_s", null), ("facebook", "sibel.fb", null) },
            "Sibel S", "İstanbul", null, null, null, false, false, 6000, formId: Guid.NewGuid(), submittedAtMs: 6_000_000);

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
        var telefon = TestPhone.NewE164();
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "sibelvip", null) },
            "Örnek Müşteri", "İzmir", telefon, null, null, true, false, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        // AYRI satır AÇILMAMALI — mevcut satır güncellenmeli (geçmiş korunur).
        var all = repo.GetRecent(1000).Where(c => c.Platform == "instagram").ToList();
        all.Should().HaveCount(1);
        var c = all[0];
        c.Id.Should().Be("shop1");
        c.Phone.Should().Be(telefon);
        c.Address.Should().Be("İzmir");
        c.TotalAmount.Should().Be(250m);          // alışveriş geçmişi korundu
        c.GroupId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void UpsertPersonFromIntake_youtube_merges_into_channelId_row_via_handle()
    {
        var repo = CreateRepository();

        // Chat'ten kaydedilmiş YouTube müşterisi: Username=channelId, DisplayName=@handle.
        repo.Insert(new Customer("yt1", "youtube", "UCabc123channel", "@ornekkanal", null,
            100, 100, false, null, null, 2, 180m, null, null, null));

        // Form: müşteri @handle'ını yazıyor (channelId'yi bilmez).
        var telefon = TestPhone.NewE164();
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("youtube", "OrnekKanal", null) },      // farklı casing + @ yok
            "Sibel G", "Ankara", telefon, null, null, false, true, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        // channelId satırına birleşmeli, AYRI (youtube, handle) satırı açılmamalı.
        var yts = repo.GetRecent(1000).Where(c => c.Platform == "youtube").ToList();
        yts.Should().HaveCount(1);
        var c = yts[0];
        c.Id.Should().Be("yt1");
        c.Username.Should().Be("UCabc123channel");  // channelId korundu (chat eşleşmesi sürsün)
        c.Phone.Should().Be(telefon);
        c.TotalAmount.Should().Be(180m);            // geçmiş korundu
    }

    // ── R12-D01 (2026-09-16 denetimi): silinen kanalın handle köprüsü ──────
    //
    // YouTube satırı Username=channelId, DisplayName=@handle ile durur ve
    // handle-only bir form o satırı DisplayName köprüsüyle bulur
    // (UpsertPersonFromIntake_youtube_merges_into_channelId_row_via_handle).
    // Silme DisplayName'i '[Silindi]' yapınca köprü kopuyordu: silmeden ÖNCE
    // kabul edilmiş ama SONRA uygulanan handle-only form eşleşme bulamayıp
    // handle adına YENİ, korunmasız bir satır açıyordu. Tombstone yalnız
    // channelId kimliğini tanıdığı için o satır temizlenmiyordu.
    //
    // Sözleşme: uygulamanın eşleştirmede GÜVENDİĞİ bağ (channelId ↔ handle),
    // silme kararının da kapsamında olmalı. Kapsam handle-only girdiyle
    // sınırlı — channelId çözülebiliyorsa kanonik kimlik kazanır, yani bu
    // handle'ı ileride alacak BAŞKA biri kendi kanal kimliğiyle kaydolabilir.

    [Fact]
    public void RecordPurge_youtube_handle_koprusunu_de_kapsar()
    {
        var repo = CreateRepository();
        repo.Insert(new Customer("yt1", "youtube", "UCabc123channel", "@ornekkanal", null,
            100, 100, false, null, null, 0, 0m, null, null, null));

        // Yetkili silme kanal kimliğiyle iniyor; DisplayName köprüsü kopuyor.
        repo.RecordPurge("youtube", "UCabc123channel", 4000);

        // Silmeden önce kabul edilmiş, channelId taşımayan form şimdi uygulanıyor.
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("youtube", "OrnekKanal", null) },
            "Sibel G", "Ankara", TestPhone.NewE164(), "s@example.com", null, true, true, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        var yts = repo.GetRecent(1000).Where(c => c.Platform == "youtube").ToList();
        yts.Should().OnlyContain(c => c.Phone == null && c.FullName == null && c.Address == null,
            "silinen kanalın handle'ı adına korunmasız bir PII satırı doğmamalı");
    }

    [Fact]
    public void RecordPurge_youtube_silmeden_onceki_koprulemeyi_bozmaz()
    {
        // Karşı kontrol 1: silme YOKKEN handle-only form hâlâ kanal satırına
        // birleşiyor (köprünün kendisi çalışır durumda).
        var repo = CreateRepository();
        repo.Insert(new Customer("yt1", "youtube", "UCabc123channel", "@ornekkanal", null,
            100, 100, false, null, null, 0, 0m, null, null, null));

        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("youtube", "OrnekKanal", null) },
            "Sibel G", "Ankara", TestPhone.NewE164(), null, null, false, true, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);
        repo.RecordPurge("youtube", "UCabc123channel", 6000);

        var yts = repo.GetRecent(1000).Where(c => c.Platform == "youtube").ToList();
        yts.Should().HaveCount(1);
        yts[0].Phone.Should().BeNull();
    }

    [Fact]
    public void RecordPurge_youtube_baska_handle_ve_baska_kanali_etkilemez()
    {
        // Karşı kontrol 2: karar yalnız silinen kimliğin bağını kapsar.
        var repo = CreateRepository();
        repo.Insert(new Customer("yt1", "youtube", "UCabc123channel", "@ornekkanal", null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        repo.RecordPurge("youtube", "UCabc123channel", 4000);

        // (a) Başka bir handle serbest.
        var telefon = TestPhone.NewE164();
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("youtube", "baskakisi", null) },
            "Başka Kişi", "İzmir", telefon, null, null, false, true, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);
        repo.FindByPlatformAndUsername("youtube", "baskakisi")!.Phone
            .Should().Be(telefon);

        // (b) Aynı görünen adı taşıyan BAŞKA bir kanal kimliği de serbest —
        //     kanonik kimlik çözülebildiğinde yeniden kayıt yolu açık kalır.
        repo.Insert(new Customer("yt2", "youtube", "UCxyz999other", "@ornekkanal", null,
            200, 200, false, null, null, 0, 0m, null, null, null));
        var ikinciKanalTelefonu = TestPhone.NewE164();
        repo.UpdatePhone("yt2", ikinciKanalTelefonu);
        repo.GetById("yt2")!.Phone.Should().Be(ikinciKanalTelefonu);
    }

    [Fact]
    public void CountAll_and_CountRegistered_reflect_phone_presence()
    {
        var repo = CreateRepository();
        // 2 chat-only (telefonsuz) + 1 kayıtlı (telefonlu)
        repo.Insert(new Customer("a", "instagram", "u1", "U1", null, 1, 1, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("b", "youtube", "UCx", "@u2", null, 1, 1, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("c", "instagram", "u3", "U3", null, 1, 1, false, null, null, 0, 0m, null, "Adres", TestPhone.NewE164()));

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
        repo.Insert(new Customer("ig1", "instagram", "ornek.musteri", "ornek.musteri", null,
            100, 100, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("yt1", "youtube", "UCabc", "@ayse", null,
            100, 100, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));

        var updated = repo.BackfillFullNameForIdentities(
            new[] { ("instagram", "ornek.musteri") }, "Örnek Müşteri", submittedAtMs: 5_000_000);

        updated.Should().Be(2); // eşleşen satırın tüm grubu
        repo.GetById("ig1")!.FullName.Should().Be("Örnek Müşteri");
        repo.GetById("yt1")!.FullName.Should().Be("Örnek Müşteri");
        repo.GetById("ig1")!.DisplayName.Should().Be("ornek.musteri"); // dokunulmadı
    }

    [Fact]
    public void BackfillFullNameForIdentities_does_not_overwrite_existing_fullname()
    {
        var repo = CreateRepository();
        repo.Insert(new Customer("ig1", "instagram", "u", "u", null,
            100, 100, false, null, null, 0, 0m, null, null, null, FullName: "Zaten Var"));

        var updated = repo.BackfillFullNameForIdentities(
            new[] { ("instagram", "u") }, "Yeni İsim", submittedAtMs: 5_000_000);

        updated.Should().Be(0);
        repo.GetById("ig1")!.FullName.Should().Be("Zaten Var");
    }

    [Fact]
    public void UpsertPersonFromIntake_links_by_phone_when_usernames_differ()
    {
        var repo = CreateRepository();
        var telefon = TestPhone.NewE164();
        // Önceden IG'den kaydolmuş, telefonlu, gruplu müşteri.
        repo.Insert(new Customer("ig1", "instagram", "ayse", "Ayşe Y", null,
            1, 1, false, null, null, 0, 0m, null, "Adr", telefon, GroupId: "g1"));

        // Aynı kişi FB'den FARKLI kullanıcı adıyla ama AYNI telefonla kaydoluyor.
        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("facebook", "ayse.fb", null) },
            "Örnek Müşteri", "Adr", telefon, null, null, false, true, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        groupId.Should().Be("g1"); // mevcut grup telefonla bulundu, korundu
        repo.GetById("ig1")!.GroupId.Should().Be("g1");
        var members = repo.GetGroupMembers("g1");
        members.Select(m => m.Platform).Should().Contain(new[] { "instagram", "facebook" });
    }

    [Fact]
    public void UpsertPersonFromIntake_phone_merge_propagates_blacklist()
    {
        var repo = CreateRepository();
        var telefon = TestPhone.NewE164();
        // Kara listeli, telefonlu, tekil (grupsuz) mevcut müşteri.
        repo.Insert(new Customer("bad1", "instagram", "kotu", "Kötü", null,
            1, 1, true, "dolandırıcı", null, 0, 0m, 999, "Adr", telefon));

        // Aynı telefonla FB'den yeni kayıt → aynı gruba çekilir + kara liste yayılır.
        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("facebook", "kotu.fb", null) },
            "Kötü Kişi", "Adr", telefon, null, null, false, true, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        var members = repo.GetGroupMembers(groupId);
        members.Should().HaveCount(2);
        members.Should().OnlyContain(m => m.IsBlacklisted);
    }

    // Telefonsuz form da kimlikleri bir gruba koyar (mevcut gruba katılım ya da yeni
    // grup), kara liste ise satır bayrağından okunur (sohbet, etiket kuyruğu,
    // çekiliş). Yayılım yalnız telefon dalında koşunca telefonsuz formla gruba giren
    // yeni kimlik işaretsiz kalıyor, kişi o platformdan alışveriş yapabiliyordu.

    [Fact]
    public void UpsertPersonFromIntake_phoneless_form_propagates_group_blacklist_to_new_identity()
    {
        var repo = CreateRepository();
        // Kara listeli üyesi olan mevcut grup.
        repo.Insert(new Customer("bad1", "instagram", "uye.ig", "Üye", null,
            1, 1, true, "ödemedi", null, 0, 0m, 999, null, null, GroupId: "g1"));

        // Telefonsuz form: mevcut IG kimliği + yeni TikTok kimliği → TikTok g1'e girer.
        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "uye.ig", null), ("tiktok", "uye.tt", null) },
            "Form Kişi", "Adres", phone: null, email: null, tckn: null,
            whatsAppConsent: false, smsConsent: false, nowUnix: 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        groupId.Should().Be("g1");
        var tt = repo.FindByPlatformAndUsername("tiktok", "uye.tt")!;
        tt.GroupId.Should().Be("g1");
        tt.IsBlacklisted.Should().BeTrue();
        tt.BlacklistReason.Should().Be("ödemedi");
        tt.BlacklistedAt.Should().Be(999, "yayılım kaynak üyenin tarihini taşır, telefon dalıyla aynı");
    }

    [Fact]
    public void UpsertPersonFromIntake_phoneless_form_without_blacklisted_member_blacklists_nobody()
    {
        // Karşı kontrol: yayılım her formda koşuyor; grupta kara listeli üye yoksa
        // kimse işaretlenmez, başka grubun kara listesi de bu gruba sızmaz.
        var repo = CreateRepository();
        repo.Insert(new Customer("ok1", "instagram", "uye.ig", "Üye", null,
            1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "g1"));
        repo.Insert(new Customer("bad2", "instagram", "baska.ig", "Başka", null,
            1, 1, true, "ödemedi", null, 0, 0m, 999, null, null, GroupId: "g2"));

        var groupId = repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "uye.ig", null), ("tiktok", "uye.tt", null) },
            "Form Kişi", "Adres", phone: null, email: null, tckn: null,
            whatsAppConsent: false, smsConsent: false, nowUnix: 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        groupId.Should().Be("g1");
        var members = repo.GetGroupMembers("g1");
        members.Should().HaveCount(2);
        members.Should().OnlyContain(m => !m.IsBlacklisted && m.BlacklistReason == null && m.BlacklistedAt == null);
    }

    [Fact]
    public void UpsertPersonFromIntake_stores_real_fullname_without_overwriting_chat_displayname()
    {
        var repo = CreateRepository();
        // Chat'ten gelmiş IG satırı: DisplayName = IG takma adı (gerçek isim değil).
        repo.Insert(new Customer("ig1", "instagram", "ornek.musteri", "ornek.musteri", null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        var phone = TestPhone.NewE164();

        // Form: gerçek Ad Soyad farklı.
        repo.UpsertPersonFromIntake(
            new (string, string, string?)[] { ("instagram", "ornek.musteri", null) },
            "Örnek Müşteri", "Adres", phone, "e@example.test", null, true, true, 5000, formId: Guid.NewGuid(), submittedAtMs: 5_000_000);

        var c = repo.GetById("ig1")!;
        c.DisplayName.Should().Be("ornek.musteri"); // chat takma adı korundu (chat eşleşmesi sürsün)
        c.FullName.Should().Be("Örnek Müşteri");    // gerçek isim ayrı kolonda saklandı
        c.Phone.Should().Be(phone);
    }
}
