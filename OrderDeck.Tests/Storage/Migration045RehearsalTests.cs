using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// İnceleme M2: göç 045 gerçek boyutta ve önceki sürüme dönüşte. (1) üretilmiş prod boyu 044
/// veritabanı; (2) isteğe bağlı: yayın bilgisayarı yedeğinin KOPYASI (ortam değişkeniyle, yalnız
/// elle — kişisel veri repoya ya da CI'ya girmez, çıktıda yalnız sayılar); (3) önceki sürümün göç
/// koşucusu ve SQL'i şema 45 üstünde (U6, U15).
///
/// <para><b>Gerçek yedek provası (D6 Step 2b):</b> uygulama KAPALIYKEN — veritabanı ve <c>-wal</c>
/// dosyası ayrı ayrı kopyalanır; uygulama açıkken alınan kopya yırtık olabilir. Kopya
/// <c>%TEMP%\od045-*.db</c> adıyla kişisel veri taşır ve test sonunda silinir; test süreci çökerse
/// (ya da hata ayıklayıcıda durdurulursa) <c>%TEMP%\od045-*.db*</c> elle denetlenip silinmeli.</para>
/// </summary>
public sealed class Migration045RehearsalTests(ITestOutputHelper output)
{
    internal const string RehearsalDbVariable = "ORDERDECK_045_REHEARSAL_DB";

    /// <summary>v0.9.8'in kaydettiği uygulama fonksiyonları (<c>git show v0.9.8:OrderDeck.Core/Storage/
    /// SqliteSearchFunctions.cs</c>). Şema nesnelerinin çağırdığı her uygulama fonksiyonu bu kümede
    /// olmalı — yoksa önceki sürüm o nesneye dokunan her yazımda "no such function" ile düşer (U6).</summary>
    private static readonly string[] PreviousReleaseFunctions = ["od_search_key", "od_phone_key"];

    /// <summary>Gerçek yedek provası yalnız <see cref="RehearsalDbVariable"/> verilince koşar; yoksa
    /// "atlandı" görünür (sessizce geçti sayılmaz).</summary>
    internal sealed class RehearsalFactAttribute : FactAttribute
    {
        public RehearsalFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RehearsalDbVariable)))
                Skip = $"Elle prova: {RehearsalDbVariable} = yayın bilgisayarının orderdeck.db yedeği (D6 Step 2b)";
        }
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"od045-{Guid.NewGuid():N}.db");

    private static void Drop(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Prod_boyu_044_veritabani_045e_hatasiz_ve_tutarli_gecer()
    {
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory, EmbeddedMigrationScripts.UpTo(44)).Run();
            var seeded = Seed044(factory, customers: 20_000);
            seeded.Should().Be(new IdentityStats(DuplicateIdentityGroups: 400, TombstoneMatchedRows: 22),
                "ön koşul: tohumda harf/İ kopyaları ve yalnız kimlik anahtarıyla eşleşen mezar taşı var");
            var before = Snapshot(factory);

            var sw = Stopwatch.StartNew();
            new MigrationRunner(factory).Run();
            sw.Stop();
            output.WriteLine($"045 göçü: {before.Customers} müşteri, {before.Labels} etiket, {sw.ElapsedMilliseconds} ms");

            before.SearchIndexOk.Should().BeTrue("üretilmiş 044 veritabanı tutarlı başlar");
            before.FkViolations.Should().Be(0);
            var identity = AssertMigrated(factory, before);
            identity.Should().Be(seeded,
                "harf/İ farklı kopyalar aynı kimlik anahtarını alır; mezar taşı kimlik anahtarıyla canlı satırı bulur (U16)");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "göç açılışta koşar — kullanıcı bekler");
        }
        finally { Drop(path); }
    }

    [RehearsalFact]
    public void Gercek_yedek_kopyasinda_prova()
    {
        // Yalnız elle, uygulama KAPALIYKEN: ORDERDECK_045_REHEARSAL_DB = yayın bilgisayarının orderdeck.db'si
        // ya da yedeği. Kaynak SQLite'la hiç açılmaz: dosya ve (varsa) -wal'ı ayrı ayrı geçici kopyaya
        // alınır, göç kopyada koşar, kopya silinir (çökmede %TEMP%\od045-*.db* elle silinir — sınıf
        // dokümanı). Çıktıda yalnız sayılar ve süre.
        var source = Environment.GetEnvironmentVariable(RehearsalDbVariable)!;
        File.Exists(source).Should().BeTrue($"{RehearsalDbVariable} bir veritabanı dosyasını göstermeli");

        var path = TempDb();
        try
        {
            File.Copy(source, path);
            if (File.Exists(source + "-wal")) File.Copy(source + "-wal", path + "-wal");
            var factory = new SqliteConnectionFactory(path);
            using (var c = factory.Open())
                c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1")
                    .Should().Be(44, "prova sahadaki son sürümün (şema 44) veritabanını ister");
            var before = Snapshot(factory);

            var sw = Stopwatch.StartNew();
            new MigrationRunner(factory).Run();
            sw.Stop();

            var identity = AssertMigrated(factory, before);
            output.WriteLine(
                $"045 provası: {before.Customers} müşteri, {before.Labels} etiket, {before.Tombstones} mezar taşı, " +
                $"FK ihlali {before.FkViolations}, arama dizini {(before.SearchIndexOk ? "tutarlı" : "TUTARSIZ")}, {sw.ElapsedMilliseconds} ms; " +
                $"ilk turda yerelde birleşecek kimlik grubu {identity.DuplicateIdentityGroups}, mezar taşıyla eşleşen canlı satır {identity.TombstoneMatchedRows}");
        }
        finally { Drop(path); }
    }

    [Fact]
    public void Onceki_surum_sema_45_ile_calisir_yeni_surum_donuste_onarir()
    {
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();                                          // bu sürüm: şema 45
            new MigrationRunner(factory, EmbeddedMigrationScripts.UpTo(44)).Run();       // önceki sürümün koşucusu: no-op
            using (var c = factory.Open())
            {
                c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(45);
                // U6: hiçbir şema nesnesi (tetikleyici, indeks, görünüm) önceki sürümün kaydetmediği bir
                // uygulama fonksiyonunu çağırmaz — od_identity_key yalnız C# ve onarım SQL'inde.
                var called = c.Query<string>("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL")
                    .SelectMany(sql => Regex.Matches(sql, @"\b(od_[a-z_]+)\s*\(", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.ToLowerInvariant()))
                    .Distinct()
                    .ToList();
                called.Should().NotBeEmpty("ön koşul: 035'in arama tetikleyicileri uygulama fonksiyonu çağırır");
                called.Should().BeSubsetOf(PreviousReleaseFunctions);
                called.Should().NotContain("od_identity_key");
            }

            var kept = Guid.NewGuid().ToString("N");
            var purged = Guid.NewGuid().ToString("N");
            using (var old = PreviousReleaseConnection(path))
            {
                // Düzenlenen müşteri: v0.9.8'in bütün Customer yazım yolları.
                old.Execute(V098.InsertCustomer, NewCustomer(kept, "Eski.Surum", phone: null));
                old.Execute(V098.ScrubIfTombstoned, new { id = kept }).Should().Be(0, "kimlik silinmemiş");
                old.Execute(V098.UpdateNotes, new { id = kept, notes = "eski sürümde yazıldı" }).Should().Be(1);
                old.Execute(V098.UpdatePhone, new { id = kept, phone = TestPhone.NewE164(), now = 2L }).Should().Be(1);
                old.Execute(V098.IncrementLabelStats, new { id = kept, labelDelta = 1, amountDelta = 10m, lastSeenAt = 3L }).Should().Be(1);
                old.Execute(V098.UpdateBlacklist, new { id = kept, flag = 1, reason = "ödemedi", blacklistedAt = 4L }).Should().Be(1);
                old.Execute(V098.SetGroupId, new { id = kept, groupId = "g-eski" }).Should().Be(1);
                old.Execute(V098.SetRecipientPaysActive, new { customerId = kept, active = 1 }).Should().Be(1);

                // Silinmiş kimlik: mezar taşı ÖNCE, sonra sohbetin açtığı satır ve aynı işlemdeki boşaltma
                // (v0.9.8 Insert) — boşaltma GERÇEKTEN bir satırı günceller.
                old.Execute(V098.TombstoneUpsert, new { platform = "tiktok", username = "Silinen.Ornek", purgedAtUnix = 5L });
                old.Execute(V098.InsertCustomer, NewCustomer(purged, "Silinen.Ornek", phone: TestPhone.NewE164()));
                old.Execute(V098.ScrubIfTombstoned, new { id = purged }).Should().Be(1);
            }

            using var conn = factory.Open();
            foreach (var id in new[] { kept, purged })
                conn.ExecuteScalar<string?>("SELECT IdentityKey FROM Customer WHERE Id = @id", new { id })
                    .Should().BeNull("eski sürüm anahtarı yazmaz; tetikleyici de yazmaz — fonksiyon eski ikilide yok (U6)");
            foreach (var column in new[] { "DisplayNameChangedAt", "NotesChangedAt", "PhoneChangedAt", "BlacklistChangedAt",
                                           "GroupIdChangedAt", "RecipientPaysChangedAt" })
                conn.ExecuteScalar<long?>($"SELECT {column} FROM Customer WHERE Id = @id", new { id = kept })
                    .Should().NotBeNull($"045 tetikleyicileri yerleşik SQL: eski sürümün düzenlemesi de damgalanır ({column}), v2 imleci onu yeniden gönderir (U15)");
            var scrubbed = conn.QuerySingle<(string DisplayName, string? Phone, long? PurgedAt)>(
                "SELECT DisplayName, Phone, PurgedAt FROM Customer WHERE Id = @id", new { id = purged });
            scrubbed.Should().Be(("[Silindi]", (string?)null, (long?)5L), "eski sürümün boşaltması şema 45'te de çalışır");
            conn.ExecuteScalar<long>("SELECT Value FROM SyncSeqCounter WHERE Id = 1")
                .Should().Be(conn.ExecuteScalar<long>("SELECT MAX(SyncSeq) FROM Customer"),
                    "eski sürümün yazımları da silinmeye dayanıklı sayaçtan numaralanır");

            new CustomerSyncRepository(factory).HealIdentityKeys().Should().Be(3, "iki müşteri satırı + mezar taşı");
            conn.ExecuteScalar<string>("SELECT IdentityKey FROM Customer WHERE Id = @id", new { id = kept }).Should().Be("eski.surum");
            conn.ExecuteScalar<string>("SELECT IdentityKey FROM Customer WHERE Id = @id", new { id = purged }).Should().Be("silinen.ornek");
            conn.ExecuteScalar<string>("SELECT IdentityKey FROM CustomerPurgeTombstone WHERE Username = 'Silinen.Ornek'")
                .Should().Be("silinen.ornek");

            var push = new CustomerSyncRepository(factory).GetForPush(0, 10);
            var keptRow = push.Should().ContainSingle(r => r.Id == kept).Subject.Fields;
            keptRow.NotesChangedAt.Should().NotBeNull("yeni sürümün biçim-2 gönderimi eski sürümün düzenlemelerini damgalarıyla götürür");
            keptRow.BlacklistChangedAt.Should().NotBeNull();
            keptRow.GroupIdChangedAt.Should().NotBeNull();
            keptRow.RecipientPaysChangedAt.Should().NotBeNull();
            push.Should().ContainSingle(r => r.Id == purged).Which.Fields.PurgedAt
                .Should().NotBeNull("silinmiş satır gönderimde atlanır (C6) — boşaltmanın 'şimdi' damgaları gitmez");
        }
        finally { Drop(path); }
    }

    private static object NewCustomer(string id, string username, string? phone) => new
    {
        Id = id, Platform = "tiktok", Username = username, DisplayName = "eski", AvatarUrl = (string?)null,
        FirstSeenAt = 1L, LastSeenAt = 1L, IsBlacklisted = 0, BlacklistReason = (string?)null, Notes = (string?)null,
        TotalLabelsPrinted = 0, TotalAmount = 0m, BlacklistedAt = (long?)null, Address = (string?)null,
        Phone = phone, RecipientPaysActive = 0, GroupId = (string?)null, Email = (string?)null,
        Tckn = (string?)null, WhatsAppConsent = 0, SmsConsent = 0, FullName = (string?)null,
        City = (string?)null, District = (string?)null,
    };

    // ── yardımcılar ─────────────────────────────────────────────────────

    /// <param name="ContentDigest">Customer ve CustomerPurgeTombstone'un göçten önce de var olan
    /// kolonlarının özeti — göç hiçbir veriyi değiştirmez.</param>
    private sealed record Counts(long Customers, long Labels, long Tombstones, long FkViolations, bool SearchIndexOk,
        string ContentDigest);

    /// <param name="DuplicateIdentityGroups">Aynı platformda aynı kimlik anahtarını taşıyan satır grupları
    /// (eski harf duyarlı ingest'in bıraktığı yerel kopyalar — PR-3'ün ilk turunda birleşir).</param>
    /// <param name="TombstoneMatchedRows">Kimlik anahtarı bir mezar taşınınkiyle eşleşen canlı satırlar.</param>
    private sealed record IdentityStats(long DuplicateIdentityGroups, long TombstoneMatchedRows);

    private static Counts Snapshot(IDbConnectionFactory factory)
    {
        using var c = factory.Open();
        return new Counts(
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Label"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM CustomerPurgeTombstone"),
            c.Query("PRAGMA foreign_key_check").LongCount(),
            SearchIndexOk(c),
            ContentDigest(c));
    }

    private static string ContentDigest(System.Data.IDbConnection c)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Feed(string sql)
        {
            using var reader = c.ExecuteReader(sql);
            while (reader.Read())
                for (var i = 0; i < reader.FieldCount; i++)
                    hash.AppendData(Encoding.UTF8.GetBytes(
                        (reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)) + "\u001f"));
        }
        Feed(@"SELECT Id, Platform, Username, DisplayName, FullName, AvatarUrl, Phone, Email, Tckn, Address, City, District,
                      GroupId, Notes, IsBlacklisted, BlacklistReason, BlacklistedAt, PurgedAt, RecipientPaysActive,
                      WhatsAppConsent, SmsConsent, TotalLabelsPrinted, TotalAmount, FirstSeenAt, LastSeenAt
               FROM Customer ORDER BY Id");
        Feed("SELECT Platform, Username, PurgedAt FROM CustomerPurgeTombstone ORDER BY Platform, Username");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Arama indeksi (035, harici içerik, rowid'e bağlı) içerik tablosuyla tutarlı mı?
    /// <c>rank = 1</c>: FTS5 harici içeriği de doğrular (içerik tetikleyicisiz değişince "malformed"
    /// fırlatır; Id değişimi rowid'i korur, tutarlı kalır).</summary>
    private static bool SearchIndexOk(System.Data.IDbConnection c)
    {
        try
        {
            c.Execute("INSERT INTO CustomerFts(CustomerFts, rank) VALUES('integrity-check', 1)");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static IdentityStats AssertMigrated(IDbConnectionFactory factory, Counts before)
    {
        using var c = factory.Open();
        c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(45);
        Snapshot(factory).Should().Be(before,
            "göç satır eklemez/silmez ve var olan veriyi değiştirmez, yeni FK ihlali üretmez, arama indeksini bozmaz");
        // Boş/boşluk kullanıcı adı kimlik değildir: anahtarı bilerek NULL (C1 incelemesi M-1).
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer WHERE IdentityKey IS NOT od_identity_key(Username)")
            .Should().Be(0, "geri doldurma C# anahtarının aynısı (boş ad → NULL)");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM CustomerPurgeTombstone WHERE IdentityKey IS NOT od_identity_key(Username)")
            .Should().Be(0, "mezar taşına da aynı anahtar (U16)");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer WHERE IdentityKey = ''").Should().Be(0, "boş anahtar yazılmaz");
        c.ExecuteScalar<long>(@"SELECT COUNT(*) FROM Customer
                                WHERE COALESCE(FullNameChangedAt, DisplayNameChangedAt, GroupIdChangedAt, AddressChangedAt,
                                               RecipientPaysChangedAt, PhoneChangedAt, EmailChangedAt, TcknChangedAt,
                                               WhatsAppConsentChangedAt, SmsConsentChangedAt, BlacklistChangedAt,
                                               NotesChangedAt) IS NOT NULL")
            .Should().Be(0, "göç damga uydurmaz (kural 1)");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM (SELECT SyncSeq FROM Customer GROUP BY SyncSeq HAVING COUNT(*) > 1)")
            .Should().Be(0, "036/F07: SyncSeq benzersiz kalır");
        c.ExecuteScalar<long>("SELECT Value FROM SyncSeqCounter WHERE Id = 1")
            .Should().BeGreaterThanOrEqualTo(c.ExecuteScalar<long>("SELECT COALESCE(MAX(SyncSeq), 0) FROM Customer"),
                "sonraki numara her mevcut SyncSeq'in üstünde başlar (silinmeye dayanıklı sayaç)");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_GiveawayParticipant_CustomerId'")
            .Should().Be(1);
        c.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);

        return new IdentityStats(
            c.ExecuteScalar<long>(@"SELECT COUNT(*) FROM (SELECT 1 FROM Customer WHERE IdentityKey IS NOT NULL
                                                         GROUP BY Platform, IdentityKey HAVING COUNT(*) > 1)"),
            c.ExecuteScalar<long>(@"SELECT COUNT(*) FROM Customer c
                                    WHERE c.IdentityKey IS NOT NULL AND EXISTS (
                                        SELECT 1 FROM CustomerPurgeTombstone t
                                        WHERE t.Platform = c.Platform AND t.IdentityKey = c.IdentityKey)"));
    }

    /// <returns>Beklenen kimlik istatistikleri — C# kimlik anahtarıyla (<see cref="CustomerIdentity.KeyOrNull"/>)
    /// hesaplanır; göçün SQL geri doldurmasıyla karşılaştırılır.</returns>
    private static IdentityStats Seed044(IDbConnectionFactory factory, int customers)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("INSERT INTO StreamSession (Id, StartedAt) VALUES ('s1', 1)", transaction: tx);
        var rows = Enumerable.Range(0, customers).Select(i => new
        {
            Id = Guid.NewGuid().ToString("N"),
            Platform = (i % 3) switch { 0 => "tiktok", 1 => "instagram", _ => "youtube" },
            // Türkçe büyük harf, boşluk, nokta: kimlik anahtarı geri doldurmasını zorlar.
            // i == 3: sahada görülen boş kullanıcı adı — anahtarı NULL kalmalı (C1 incelemesi M-1).
            Username = i == 3 ? "   " : (i % 10) switch { 0 => $"  ÖRNEK.Şİ.{i} ", 1 => $"İÇERİK_{i}", 2 => $"Ornek.Kisi{i}", _ => $"kullanici{i}" },
            DisplayName = $"Takma {i}",
            FullName = i % 4 == 0 ? $"Örnek Müşteri {i}" : null,
            Phone = i % 5 == 0 ? TestPhone.NewE164() : null,                  // üretilir
            Address = i % 6 == 0 ? $"Örnek Sk. No {i}" : null,
            City = i % 12 == 0 ? "İzmir" : null,
            GroupId = i % 7 == 0 ? $"g{i / 14}" : null,
            Notes = i % 9 == 0 ? $"not {i}" : null,
            IsBlacklisted = i % 50 == 0 ? 1 : 0,
            SeenAt = 1_700_000_000L + i,
        }).ToList();
        // Eski harf duyarlı ingest'in bıraktığı yerel kopyalar (M-5): aynı platform, harf ya da İ farkı —
        // birebir tekil indeks (BINARY) ikisine de izin verir, kimlik anahtarı aynı.
        var duplicates = rows.Where((_, i) => i % 100 is 1 or 2).Select(r => r with
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = r.Username.StartsWith("İÇERİK_", StringComparison.Ordinal)
                ? "içerik_" + r.Username["İÇERİK_".Length..]
                : r.Username.ToUpperInvariant(),
        }).ToList();
        foreach (var (original, copy) in rows.Where((_, i) => i % 100 is 1 or 2).Zip(duplicates))
            if (copy.Username == original.Username || CustomerIdentity.KeyOf(copy.Username) != CustomerIdentity.KeyOf(original.Username))
                throw new InvalidOperationException("tohum: kopya aynı kimliğin farklı yazımı olmalı");
        var all = rows.Concat(duplicates).ToList();

        conn.Execute(@"INSERT INTO Customer (Id, Platform, Username, DisplayName, FirstSeenAt, LastSeenAt, FullName, Phone,
                                            Address, City, GroupId, Notes, IsBlacklisted)
                       VALUES (@Id, @Platform, @Username, @DisplayName, @SeenAt, @SeenAt, @FullName, @Phone,
                               @Address, @City, @GroupId, @Notes, @IsBlacklisted)", all, tx);
        conn.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Price, AddedAt)
                       VALUES (@Id, 's1', @CustomerId, @Platform, @Username, 'A1', 10, 1)",
            rows.SelectMany(r => Enumerable.Range(0, 2).Select(_ => new
                { Id = Guid.NewGuid().ToString("N"), CustomerId = r.Id, r.Platform, r.Username })), tx);

        // Mezar taşları: birebir adla (i % 1000 == 0) ve yalnız kimlik anahtarıyla eşleşen bir tane — harf
        // ve İ farkı NOCASE'e (yalnız ASCII) takılmaz, kimlik anahtarına takılır (U16).
        var tombstones = rows.Where((_, i) => i % 1000 == 0).Select(r => (r.Platform, r.Username)).ToList();
        if (customers > 101)
        {
            var target = rows[101];
            var variant = "İçerik_101";
            if (target.Username != "İÇERİK_101" || CustomerIdentity.KeyOf(variant) != CustomerIdentity.KeyOf(target.Username))
                throw new InvalidOperationException("tohum: mezar taşı yalnız kimlik anahtarıyla eşleşmeli");
            tombstones.Add((target.Platform, variant));
        }
        conn.Execute("INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt) VALUES (@Platform, @Username, 1)",
            tombstones.Select(t => new { t.Platform, t.Username }), tx);
        tx.Commit();

        var keyed = all.Select(r => (r.Platform, Key: CustomerIdentity.KeyOrNull(r.Username))).Where(r => r.Key is not null).ToList();
        var tombstoneKeys = tombstones.Select(t => (t.Platform, Key: CustomerIdentity.KeyOrNull(t.Username))).ToHashSet();
        return new IdentityStats(
            keyed.GroupBy(r => r).Count(g => g.Count() > 1),
            keyed.Count(r => tombstoneKeys.Contains(r)));
    }

    /// <summary>v0.9.8'in bağlantısı: SqliteSearchFunctions.Register'ı yalnız bu iki fonksiyonu
    /// kaydediyordu (od_identity_key YOK). 045'in bir tetikleyicisi uygulama fonksiyonu çağırsaydı
    /// bu bağlantıdaki her Customer yazımı "no such function" ile düşerdi.</summary>
    private static SqliteConnection PreviousReleaseConnection(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        c.Open();
        c.CreateFunction<string?, string?, string?, string>("od_search_key",
            (u, d, f) => CustomerSearch.BuildSearchKey(u, d, f), isDeterministic: true);
        c.CreateFunction<string?, string>("od_phone_key", p => CustomerSearch.NormalizePhoneKey(p), isDeterministic: true);
        return c;
    }

    /// <summary>v0.9.8 CustomerRepository'nin Customer yazımları, BİREBİR (`git show v0.9.8:OrderDeck.Core/
    /// Storage/Repositories/CustomerRepository.cs` — Insert, ScrubIfTombstonedSql, UpdateNotes, UpdatePhone,
    /// IncrementLabelStats, UpdateBlacklist, SetGroupId, SetRecipientPaysActive, TombstoneUpsertSql). PR-3'ten
    /// önce son sürüm değişirse buradaki metinler o sürümden yenilenir.</summary>
    private static class V098
    {
        public const string InsertCustomer = @"
            INSERT INTO Customer
              (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
               IsBlacklisted, BlacklistReason, Notes,
               TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone,
               RecipientPaysActive, GroupId, Email, Tckn, WhatsAppConsent, SmsConsent, FullName,
               City, District)
              VALUES
              (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @IsBlacklisted, @BlacklistReason, @Notes,
               @TotalLabelsPrinted, @TotalAmount, @BlacklistedAt, @Address, @Phone,
               @RecipientPaysActive, @GroupId, @Email, @Tckn, @WhatsAppConsent, @SmsConsent, @FullName,
               @City, @District)";

        public const string ScrubIfTombstoned = @"
            UPDATE Customer
            SET DisplayName = '[Silindi]', FullName = NULL, Address = NULL, City = NULL, District = NULL,
                Phone = NULL, Email = NULL, Tckn = NULL, AvatarUrl = NULL, WhatsAppConsent = 0, SmsConsent = 0,
                PurgedAt = COALESCE(PurgedAt, (
                    SELECT t.PurgedAt FROM CustomerPurgeTombstone t
                    WHERE t.Platform = Customer.Platform AND t.Username = Customer.Username))
            WHERE Id = @id
              AND EXISTS (
                    SELECT 1 FROM CustomerPurgeTombstone t
                    WHERE t.Platform = Customer.Platform AND t.Username = Customer.Username)";

        public const string UpdateNotes = "UPDATE Customer SET Notes=@notes WHERE Id=@id";

        public const string UpdatePhone = @"
            UPDATE Customer SET Phone=@phone, LastSeenAt=MAX(LastSeenAt+1, @now)
            WHERE Id=@id AND PurgedAt IS NULL";

        public const string IncrementLabelStats = @"
            UPDATE Customer
            SET TotalLabelsPrinted = TotalLabelsPrinted + @labelDelta,
                TotalAmount        = TotalAmount + @amountDelta,
                LastSeenAt         = @lastSeenAt
            WHERE Id = @id";

        public const string UpdateBlacklist = @"
            UPDATE Customer
            SET IsBlacklisted   = @flag,
                BlacklistReason = @reason,
                BlacklistedAt   = @blacklistedAt
            WHERE Id = @id";

        public const string SetGroupId = "UPDATE Customer SET GroupId = @groupId WHERE Id = @id";

        public const string SetRecipientPaysActive = "UPDATE Customer SET RecipientPaysActive=@active WHERE Id=@customerId";

        public const string TombstoneUpsert = @"
            INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt)
            VALUES (@platform, @username, @purgedAtUnix)
            ON CONFLICT(Platform, Username) DO UPDATE SET
                PurgedAt = MIN(CustomerPurgeTombstone.PurgedAt, excluded.PurgedAt)";
    }
}
