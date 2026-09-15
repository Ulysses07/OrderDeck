using System;
using System.Collections.Generic;
using Dapper;
using FluentAssertions;
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
