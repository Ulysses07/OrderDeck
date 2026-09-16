using System;
using System.Collections.Generic;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// R10-D02: yerel tombstone (ScrubPersonalData) kalıcı bir bariyer değildi.
/// Silmeden ÖNCE sunucudan çekilmiş ama SONRA uygulanan bir form cevabı
/// (UpsertPersonFromIntake / UpsertFromIntakeForm) ya da backfill, temizlenmiş
/// alanları geri dolduruyordu; ingest imleci tombstone'un ötesinde olduğu için
/// scrub bir daha koşmuyordu. Denetim kapanış koşulu: karar üstünlüğü
/// "istekten önce if purged okuması"yla değil, UYGULANAN YAZIDA (WHERE
/// koşulunda) sağlanmalı. Bu testler o sözleşmeyi sabitler.
/// </summary>
public class CustomerPurgeTombstoneTests
{
    private static readonly IReadOnlyList<(string Platform, string Username, string? PreferredDisplayName)>
        AyseIdentity = new List<(string, string, string?)> { ("instagram", "ayse_y", null) };

    private static (InMemorySqlite Db, CustomerRepository Repo, string CustomerId) SeedScrubbedAyse()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.UpsertPersonFromIntake(
            AyseIdentity, "Ayşe Yılmaz", "Adres 1", "+905551112233",
            "ayse@example.com", "12345678901", whatsAppConsent: true, smsConsent: true,
            nowUnix: 1000);

        var row = repo.FindByPlatformAndUsername("instagram", "ayse_y")!;
        repo.ScrubPersonalData(row.Id).Should().Be(1);
        return (db, repo, row.Id);
    }

    [Fact]
    public void Scrub_sonrasi_gec_gelen_intake_upsert_kisisel_veriyi_diriltmez()
    {
        var (db, repo, _) = SeedScrubbedAyse();
        using (db)
        {
            // Silmeden önce çekilmiş, silmeden SONRA uygulanan form cevabı senaryosu.
            repo.UpsertPersonFromIntake(
                AyseIdentity, "Ayşe Yılmaz", "Adres 1", "+905551112233",
                "ayse@example.com", "12345678901", whatsAppConsent: true, smsConsent: true,
                nowUnix: 2000);

            var row = repo.FindByPlatformAndUsername("instagram", "ayse_y")!;
            row.DisplayName.Should().Be("[Silindi]");
            row.FullName.Should().BeNull();
            row.Phone.Should().BeNull();
            row.Address.Should().BeNull();
            row.Email.Should().BeNull();
            row.Tckn.Should().BeNull();
            row.WhatsAppConsent.Should().BeFalse();
            row.SmsConsent.Should().BeFalse();
        }
    }

    [Fact]
    public void Scrub_sonrasi_backfill_fullname_yazmaz()
    {
        var (db, repo, _) = SeedScrubbedAyse();
        using (db)
        {
            // Backfill'in mevcut filtresi "FullName boş olanlar" — temizlenmiş
            // satır tam da bu filtreye düşüyordu (denetim probu: yalnız isim dirildi).
            var updated = repo.BackfillFullNameForIdentities(
                new List<(string, string)> { ("instagram", "ayse_y") }, "Ayşe Yılmaz");

            updated.Should().Be(0);
            repo.FindByPlatformAndUsername("instagram", "ayse_y")!.FullName.Should().BeNull();
        }
    }

    [Fact]
    public void Scrub_sonrasi_legacy_form_upsert_yazmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.UpsertFromIntakeForm("ayse_form", "Ayşe Yılmaz", "Adres 1", "+905551112233", nowUnix: 1000);
        var seeded = repo.FindByPlatformAndUsername("form", "ayse_form")!;
        repo.ScrubPersonalData(seeded.Id);

        var returned = repo.UpsertFromIntakeForm("ayse_form", "Ayşe Yılmaz", "Adres 1", "+905551112233", nowUnix: 2000);

        // Dönen kayıt iyimser kopya DEĞİL, temizlenmiş gerçek satır olmalı —
        // çağıran onu ekrana/başka yazılara taşıyabilir.
        returned.DisplayName.Should().Be("[Silindi]");
        returned.Phone.Should().BeNull();

        var row = repo.FindByPlatformAndUsername("form", "ayse_form")!;
        row.DisplayName.Should().Be("[Silindi]");
        row.Address.Should().BeNull();
        row.Phone.Should().BeNull();
    }

    [Fact]
    public void Scrub_purgedAt_damgasi_idempotent()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.UpsertPersonFromIntake(AyseIdentity, "Ayşe Yılmaz", "Adres 1", null,
            null, null, false, false, nowUnix: 1000);
        var id = repo.FindByPlatformAndUsername("instagram", "ayse_y")!.Id;

        repo.ScrubPersonalData(id);
        long? first;
        using (var conn = db.Open())
            first = conn.ExecuteScalar<long?>("SELECT PurgedAt FROM Customer WHERE Id=@id", new { id });
        first.Should().NotBeNull("scrub kalıcı tombstone damgası bırakmalı");

        // İkinci scrub (ör. eski yedek geri yüklendi, ingest tombstone'u yeniden
        // okudu) ilk damgayı EZMEMELİ — silme kararının tarihi kanıt değeri taşır.
        repo.ScrubPersonalData(id);
        using (var conn = db.Open())
            conn.ExecuteScalar<long?>("SELECT PurgedAt FROM Customer WHERE Id=@id", new { id })
                .Should().Be(first);
    }

    // ───────────────────────── R11-D01 ─────────────────────────
    // R10-D02 tombstone'u Customer SATIRINA yazıyordu. Ingest, PurgedAt'li bir
    // kayıt için yerelde eşleşen satır bulamazsa hiçbir şey yazmadan imleci
    // ilerletiyordu: karar hiçbir yerde durmuyor, sonradan inen form cevabı /
    // chat satırı aynı kimliği SIFIRDAN tam kişisel veriyle açıyordu ve
    // tombstone bir daha inmediği için ihlal kalıcılaşıyordu. Aşağıdaki
    // testler "silme kararı yerel satırın varlığına bağlı değildir"
    // sözleşmesini sabitler.

    [Fact]
    public void Yerelde_satir_yokken_de_silme_karari_kaydedilir()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        // Sunucu "bu kişi silindi" diyor; bu bilgisayarda kaydı hiç yok.
        repo.RecordPurge("instagram", "ayse_y", purgedAtUnix: 1500)
            .Should().Be(0, "temizlenecek yerel satır yok");

        using var conn = db.Open();
        conn.ExecuteScalar<long?>(
            "SELECT PurgedAt FROM CustomerPurgeTombstone WHERE Platform='instagram' AND Username='ayse_y'")
            .Should().Be(1500, "karar satırdan bağımsız bir yerde durmalı");
    }

    [Fact]
    public void Satirsiz_silmeden_sonra_gec_gelen_intake_kisiyi_diriltmez()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.RecordPurge("instagram", "ayse_y", purgedAtUnix: 1500);

        // Silmeden ÖNCE sunucudan çekilmiş, silmeden SONRA uygulanan form cevabı.
        repo.UpsertPersonFromIntake(
            AyseIdentity, "Ayşe Yılmaz", "Adres 1", "+905551112233",
            "ayse@example.com", "12345678901", whatsAppConsent: true, smsConsent: true,
            nowUnix: 2000);

        var row = repo.FindByPlatformAndUsername("instagram", "ayse_y")!;
        row.DisplayName.Should().Be("[Silindi]");
        row.FullName.Should().BeNull();
        row.Address.Should().BeNull();
        row.Phone.Should().BeNull();
        row.Email.Should().BeNull();
        row.Tckn.Should().BeNull();
        row.WhatsAppConsent.Should().BeFalse();
        row.SmsConsent.Should().BeFalse();

        using var conn = db.Open();
        conn.ExecuteScalar<long?>("SELECT PurgedAt FROM Customer WHERE Id=@id", new { id = row.Id })
            .Should().Be(1500, "damga sunucunun bildirdiği silme anı olmalı, 'şimdi' değil");
    }

    [Fact]
    public void Satirsiz_silmeden_sonra_gec_gelen_legacy_form_kisiyi_diriltmez()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.RecordPurge("form", "ayse_form", purgedAtUnix: 1500);

        var returned = repo.UpsertFromIntakeForm(
            "ayse_form", "Ayşe Yılmaz", "Adres 1", "+905551112233", nowUnix: 2000);

        // Dönen kayıt iyimser kopya DEĞİL — çağıran onu ekrana/başka yazılara taşıyabilir.
        returned.DisplayName.Should().Be("[Silindi]");
        returned.Address.Should().BeNull();
        returned.Phone.Should().BeNull();

        var row = repo.FindByPlatformAndUsername("form", "ayse_form")!;
        row.DisplayName.Should().Be("[Silindi]");
        row.Address.Should().BeNull();
        row.Phone.Should().BeNull();
    }

    [Fact]
    public void Satirsiz_silmeden_sonra_chat_satiri_takma_ad_ve_avatar_yazmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.RecordPurge("tiktok", "ayse_tt", purgedAtUnix: 1500);

        // Silinen kişi yayına tek bir yorum yazıyor → chat akışı satır açar.
        // Takma ad ve avatar da ScrubPersonalData'nın temizlediği alanlar;
        // aynı kişinin onlarla geri gelmesi silmeyi anlamsızlaştırırdı.
        repo.Insert(new Customer(
            Id: "chat-1", Platform: "tiktok", Username: "ayse_tt",
            DisplayName: "Ayşe", AvatarUrl: "https://cdn.example/a.jpg",
            FirstSeenAt: 2000, LastSeenAt: 2000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null));

        var row = repo.FindByPlatformAndUsername("tiktok", "ayse_tt")!;
        row.DisplayName.Should().Be("[Silindi]");
        row.AvatarUrl.Should().BeNull();
    }

    [Fact]
    public void Silme_karari_harf_duyarsiz_eslesir()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        // Sunucu kimliği büyük harfle bildiriyor, form küçük harfle geliyor.
        // Eşleştirme her yerde (FindExistingForIntake) harf duyarsız; bariyerin
        // ondan gevşek olması, büyük/küçük harf yazarak silmeyi aşmak demekti.
        repo.RecordPurge("instagram", "Ayse_Y", purgedAtUnix: 1500);

        repo.UpsertPersonFromIntake(
            AyseIdentity, "Ayşe Yılmaz", "Adres 1", "+905551112233",
            null, null, false, false, nowUnix: 2000);

        repo.FindByPlatformAndUsername("instagram", "ayse_y")!.Phone.Should().BeNull();
    }

    [Fact]
    public void Tekrarlanan_silme_bildirimi_ilk_tarihi_korur()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.RecordPurge("instagram", "ayse_y", purgedAtUnix: 1500);
        // Eski yedek geri yüklendi, imleç geri gitti, tombstone yeniden indi.
        repo.RecordPurge("instagram", "ayse_y", purgedAtUnix: 9000);

        using var conn = db.Open();
        conn.ExecuteScalar<long>(
            "SELECT PurgedAt FROM CustomerPurgeTombstone WHERE Platform='instagram' AND Username='ayse_y'")
            .Should().Be(1500, "silme tarihi adli kayıt — sonraki bildirim ezmemeli");
    }

    [Fact]
    public void Satir_varken_silme_hem_temizler_hem_karari_kimlige_yazar()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.UpsertPersonFromIntake(AyseIdentity, "Ayşe Yılmaz", "Adres 1", "+905551112233",
            null, null, false, false, nowUnix: 1000);

        repo.RecordPurge("instagram", "ayse_y", purgedAtUnix: 1500).Should().Be(1);

        repo.FindByPlatformAndUsername("instagram", "ayse_y")!.Phone.Should().BeNull();

        // Karar satırda da kimlikte de durmalı: satır bir gün yedekten geri
        // gelse (ya da yerel temizlikte düşse) bariyer kimlikte kalır.
        using var conn = db.Open();
        conn.ExecuteScalar<long?>(
            "SELECT PurgedAt FROM CustomerPurgeTombstone WHERE Platform='instagram' AND Username='ayse_y'")
            .Should().Be(1500);
    }

    [Fact]
    public void Temizlenmemis_satirda_intake_upsert_normal_calisir()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.UpsertPersonFromIntake(AyseIdentity, "Ayşe Yılmaz", "Adres 1", null,
            null, null, false, false, nowUnix: 1000);

        // Kontrol: bariyer yalnız PurgedAt'li satırları kapsar, normal akış aynı.
        repo.UpsertPersonFromIntake(AyseIdentity, "Ayşe Yılmaz", "Adres 2", "+905551112233",
            null, null, false, false, nowUnix: 2000);

        var row = repo.FindByPlatformAndUsername("instagram", "ayse_y")!;
        row.Address.Should().Be("Adres 2");
        row.Phone.Should().Be("+905551112233");
        row.FullName.Should().Be("Ayşe Yılmaz");
    }
}
