using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// R3-04: arama artık tüm tabloyu belleğe almak yerine SQL'de çalışıyor.
/// Buradaki testlerin tek işi, SQL'in <see cref="CustomerSearch.Matches"/> ile
/// AYNI kararı vermeye devam etmesi — iki ayrı yerde yaşayan bir kural sessizce
/// ayrışabilir, o yüzden karşılaştırma rastgele üretilmiş veri üzerinde ve
/// bellekteki kuralın kendisine karşı yapılıyor.
/// </summary>
public class CustomerSearchSqlTests
{
    private static readonly string[] FirstNames =
        { "Ayşe", "İbrahim", "Işıl", "Şeyma", "Ömer", "Çağla", "Ali", "Bilal", "Ülkü", "Gökhan" };

    private static readonly string[] LastNames =
        { "Yılmaz", "Şahin", "Öztürk", "Çelik", "Işık", "Delikurt", "Ünal", "Doğan" };

    private static readonly string[] Platforms = { "instagram", "tiktok", "youtube", "form" };

    private static Customer Make(int i, Random rnd) => new(
        Id: $"c-{i}",
        Platform: Platforms[i % Platforms.Length],
        Username: $"user{i}_{LastNames[rnd.Next(LastNames.Length)].ToLowerInvariant()}",
        DisplayName: $"{FirstNames[rnd.Next(FirstNames.Length)]} {LastNames[rnd.Next(LastNames.Length)]}",
        AvatarUrl: null,
        FirstSeenAt: 1000,
        // Ayrık LastSeenAt: sıralama beraberliği olmasın, karşılaştırma kesin olsun.
        LastSeenAt: 100_000 + i,
        IsBlacklisted: false, BlacklistReason: null, Notes: null,
        TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
        Address: null,
        Phone: i % 3 == 0 ? null : $"+9055{i:D8}",
        FullName: i % 4 == 0
            ? null
            : $"{FirstNames[rnd.Next(FirstNames.Length)]} {LastNames[rnd.Next(LastNames.Length)]}");

    private static IReadOnlyList<Customer> Reference(
        IEnumerable<Customer> all, string query, int limit,
        string? platform = null, bool registeredOnly = false) =>
        all.Where(c => CustomerSearch.Matches(c, query))
           .Where(c => string.IsNullOrEmpty(platform) || c.Platform == platform)
           .Where(c => !registeredOnly || !string.IsNullOrWhiteSpace(c.Phone))
           .OrderByDescending(c => c.LastSeenAt)
           .Take(limit)
           .ToList();

    [Fact]
    public void Sql_araması_rastgele_sorgularda_bellek_kuralıyla_aynı_sonucu_verir()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        var rnd = new Random(20260912);
        var all = Enumerable.Range(0, 400).Select(i => Make(i, rnd)).ToList();
        foreach (var c in all) repo.Insert(c);

        var queries = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            var source = rnd.Next(3) switch
            {
                0 => all[rnd.Next(all.Count)].Username,
                1 => all[rnd.Next(all.Count)].DisplayName ?? "",
                _ => all[rnd.Next(all.Count)].FullName ?? "",
            };
            if (source.Length == 0) continue;
            var start = rnd.Next(source.Length);
            var len = Math.Min(rnd.Next(1, 8), source.Length - start);
            queries.Add(source.Substring(start, len));
        }
        // Telefon parçaları + hiç eşleşmeyenler.
        queries.AddRange(new[] { "0550 000 00 12", "5500000012", "00001", "55", "zzzqqq", "ayşe yılmaz" });

        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q)) continue;
            repo.Search(q, limit: 500).Select(c => c.Id)
                .Should().Equal(Reference(all, q, 500).Select(c => c.Id), $"sorgu: '{q}'");
        }
    }

    [Fact]
    public void Üç_harften_kısa_sorgu_trigram_kullanmaz_ama_doğru_sonuç_döner()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        var rnd = new Random(7);
        var all = Enumerable.Range(0, 120).Select(i => Make(i, rnd)).ToList();
        foreach (var c in all) repo.Insert(c);

        foreach (var q in new[] { "a", "ay", "İş", "u1" })
        {
            // FTS5 trigram bu uzunlukta HATA VERMEDEN boş döner; plan bunu
            // bilmeli, yoksa sonuç yanlış "boş" olurdu (R3-03 sınıfı arıza).
            CustomerSearchPlan.Build(q).CanUseTrigram.Should().BeFalse($"sorgu: '{q}'");

            repo.Search(q, limit: 500).Select(c => c.Id)
                .Should().Equal(Reference(all, q, 500).Select(c => c.Id), $"sorgu: '{q}'");
        }
    }

    [Fact]
    public void Süzgeç_limitten_önce_uygulanır_eski_kayıt_kaybolmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        // 80 "ali" — en YENİ 79'u instagram, en ESKİ tek satır tiktok.
        for (int i = 0; i < 80; i++)
        {
            repo.Insert(new Customer(
                $"a-{i}", i == 0 ? "tiktok" : "instagram", $"ali{i}", "Ali Veli", null,
                FirstSeenAt: 1000, LastSeenAt: 100_000 + i,
                IsBlacklisted: false, BlacklistReason: null, Notes: null,
                TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
                Address: null, Phone: i == 0 ? "+905551112233" : null));
        }

        // Limit 50'den sonra dışarıda süzülseydi ikisi de boş dönerdi.
        repo.Search("ali", limit: 50, platform: "tiktok").Select(c => c.Id).Should().Equal("a-0");
        repo.Search("ali", limit: 50, registeredOnly: true).Select(c => c.Id).Should().Equal("a-0");
    }

    [Fact]
    public void GetRecent_en_yenileri_sınırlı_verir_ve_süzgeci_limitten_önce_uygular()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        var rnd = new Random(11);
        var all = Enumerable.Range(0, 200).Select(i => Make(i, rnd)).ToList();
        foreach (var c in all) repo.Insert(c);

        // Sınır: en yeni N satır, LastSeenAt DESC.
        repo.GetRecent(10).Select(c => c.Id)
            .Should().Equal(all.OrderByDescending(c => c.LastSeenAt).Take(10).Select(c => c.Id));

        // Süzgeç SQL'in içinde: 10 satırlık pencerede hiç tiktok olmasa bile
        // tiktok süzgeci en yeni 10 TIKTOK satırını getirmeli.
        repo.GetRecent(10, platform: "tiktok").Select(c => c.Id)
            .Should().Equal(all.Where(c => c.Platform == "tiktok")
                               .OrderByDescending(c => c.LastSeenAt).Take(10).Select(c => c.Id));

        repo.GetRecent(10, registeredOnly: true).Should()
            .OnlyContain(c => !string.IsNullOrWhiteSpace(c.Phone));
    }

    [Fact]
    public void Taze_pencere_dolduğunda_sonuç_tam_taramayla_aynı()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        // Pencere (5000) satırdan fazlası: yoğun terim ilk geçişte dolar ve
        // ikinci geçişe hiç gidilmez — o kısa devrenin doğru olduğunu kanıtla.
        const int total = 6000;
        var all = new List<Customer>(total);
        var rnd = new Random(99);
        for (int i = 0; i < total; i++) all.Add(Make(i, rnd));

        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            conn.Execute(
                @"INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl,
                                        FirstSeenAt, LastSeenAt, IsBlacklisted, BlacklistReason,
                                        Notes, TotalLabelsPrinted, TotalAmount, BlacklistedAt,
                                        Address, Phone, FullName)
                  VALUES (@Id, @Platform, @Username, @DisplayName, NULL,
                          @FirstSeenAt, @LastSeenAt, 0, NULL,
                          NULL, 0, 0, NULL,
                          NULL, @Phone, @FullName)",
                all.Select(c => new
                {
                    c.Id, c.Platform, c.Username, c.DisplayName,
                    c.FirstSeenAt, c.LastSeenAt, c.Phone, c.FullName
                }),
                tx);
            tx.Commit();
        }

        foreach (var q in new[] { "yılmaz", "ayşe", "user5999", "işık şahin" })
        {
            repo.Search(q, limit: 50).Select(c => c.Id)
                .Should().Equal(Reference(all, q, 50).Select(c => c.Id), $"sorgu: '{q}'");
        }
    }

    [Fact]
    public void Tetikleyiciler_her_yazma_yolunda_arama_anahtarlarını_güncel_tutar()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        // 1) Insert
        repo.Insert(new Customer(
            "t-1", "instagram", "@ibo", "İbrahim Şahin", null,
            FirstSeenAt: 1000, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null));
        repo.Search("brahim", limit: 10).Select(c => c.Id).Should().Equal("t-1");

        // 2) UpdatePhone — telefon anahtarı da tetikleyiciden gelir.
        repo.UpdatePhone("t-1", "+905339998877");
        repo.Search("99988", limit: 10).Select(c => c.Id).Should().Equal("t-1");

        // 3) İlgisiz kolon (tetikleyicinin UPDATE OF listesinde yok) — bozmamalı.
        repo.IncrementLabelStats("t-1", 1, 250m, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        repo.Search("brahim", limit: 10).Select(c => c.Id).Should().Equal("t-1");

        // 4) Intake upsert — ad değişince ESKİ ad artık bulunmamalı.
        repo.UpsertFromIntakeForm("@ibo2", "Şeyma Işık", "adres", "+905551112233", 2000);
        repo.Search("eyma", limit: 10).Should().ContainSingle();
        repo.UpsertFromIntakeForm("@ibo2", "Gökhan Ünal", "adres", "+905551112233", 3000);
        repo.Search("eyma", limit: 10).Should().BeEmpty();
        repo.Search("khan", limit: 10).Should().ContainSingle();

        // 5) KVKK boşaltma — kişisel veri hem kolondan hem indeksten gitmeli.
        repo.ScrubPersonalData("t-1");
        repo.Search("brahim", limit: 10).Should().BeEmpty();
        repo.Search("99988", limit: 10).Should().BeEmpty();

        // 6) Harici içerikli FTS5, tetikleyiciler yanlış değerle silerse sessizce
        // bozulur — integrity-check bunu yakalayan tek şey.
        using var conn = db.Open();
        conn.Execute("INSERT INTO CustomerFts(CustomerFts) VALUES('integrity-check')");
    }
}
