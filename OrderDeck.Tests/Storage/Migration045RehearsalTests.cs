using System.Diagnostics;
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
/// </summary>
public sealed class Migration045RehearsalTests(ITestOutputHelper output)
{
    internal const string RehearsalDbVariable = "ORDERDECK_045_REHEARSAL_DB";

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
            Seed044(factory, customers: 20_000);
            var before = Snapshot(factory);

            var sw = Stopwatch.StartNew();
            new MigrationRunner(factory).Run();
            sw.Stop();
            output.WriteLine($"045 göçü: {before.Customers} müşteri, {before.Labels} etiket, {sw.ElapsedMilliseconds} ms");

            before.SearchIndexOk.Should().BeTrue("üretilmiş 044 veritabanı tutarlı başlar");
            before.FkViolations.Should().Be(0);
            AssertMigrated(factory, before);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "göç açılışta koşar — kullanıcı bekler");
        }
        finally { Drop(path); }
    }

    [RehearsalFact]
    public void Gercek_yedek_kopyasinda_prova()
    {
        // Yalnız elle: ORDERDECK_045_REHEARSAL_DB = yayın bilgisayarının orderdeck.db'si ya da yedeği
        // (uygulama KAPALIYKEN). Kaynak SQLite'la hiç açılmaz: dosya (varsa -wal'ıyla) geçici bir
        // kopyaya alınır, göç kopyada koşar, kopya silinir. Çıktıda yalnız sayılar ve süre.
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

            AssertMigrated(factory, before);
            output.WriteLine(
                $"045 provası: {before.Customers} müşteri, {before.Labels} etiket, {before.Tombstones} mezar taşı, " +
                $"FK ihlali {before.FkViolations}, arama dizini {(before.SearchIndexOk ? "tutarlı" : "TUTARSIZ")}, {sw.ElapsedMilliseconds} ms");
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
                c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(45);

            var id = Guid.NewGuid().ToString("N");
            using (var old = PreviousReleaseConnection(path))
            {
                old.Execute(V098.InsertCustomer, new
                {
                    Id = id, Platform = "tiktok", Username = "Eski.Surum", DisplayName = "eski", AvatarUrl = (string?)null,
                    FirstSeenAt = 1L, LastSeenAt = 1L, IsBlacklisted = 0, BlacklistReason = (string?)null, Notes = (string?)null,
                    TotalLabelsPrinted = 0, TotalAmount = 0m, BlacklistedAt = (long?)null, Address = (string?)null,
                    Phone = (string?)null, RecipientPaysActive = 0, GroupId = (string?)null, Email = (string?)null,
                    Tckn = (string?)null, WhatsAppConsent = 0, SmsConsent = 0, FullName = (string?)null,
                    City = (string?)null, District = (string?)null,
                });
                old.Execute(V098.ScrubIfTombstoned, new { id });
                old.Execute(V098.UpdateNotes, new { id, notes = "eski sürümde yazıldı" });
                old.Execute(V098.UpdatePhone, new { id, phone = TestPhone.NewE164(), now = 2L });
                old.Execute(V098.IncrementLabelStats, new { id, labelDelta = 1, amountDelta = 10m, lastSeenAt = 3L });
                old.Execute(V098.TombstoneUpsert, new { platform = "tiktok", username = "SİLİNEN.ÖRNEK", purgedAtUnix = 5L });
            }

            using var conn = factory.Open();
            conn.ExecuteScalar<string?>("SELECT IdentityKey FROM Customer WHERE Id = @id", new { id })
                .Should().BeNull("eski sürüm anahtarı yazmaz; tetikleyici de yazmaz — fonksiyon eski ikilide yok (U6)");
            conn.ExecuteScalar<long?>("SELECT NotesChangedAt FROM Customer WHERE Id = @id", new { id })
                .Should().NotBeNull("045 tetikleyicileri yerleşik SQL: eski sürümün düzenlemesi de damgalanır, v2 imleci onu yeniden gönderir (U15)");
            conn.ExecuteScalar<long?>("SELECT PhoneChangedAt FROM Customer WHERE Id = @id", new { id }).Should().NotBeNull();
            conn.ExecuteScalar<long>("SELECT Value FROM SyncSeqCounter WHERE Id = 1")
                .Should().Be(conn.ExecuteScalar<long>("SELECT MAX(SyncSeq) FROM Customer"),
                    "eski sürümün yazımları da silinmeye dayanıklı sayaçtan numaralanır");

            new CustomerSyncRepository(factory).HealIdentityKeys().Should().Be(2, "müşteri satırı + mezar taşı");
            conn.ExecuteScalar<string>("SELECT IdentityKey FROM Customer WHERE Id = @id", new { id }).Should().Be("eski.surum");
            conn.ExecuteScalar<string>("SELECT IdentityKey FROM CustomerPurgeTombstone WHERE Username = 'SİLİNEN.ÖRNEK'")
                .Should().Be(CustomerIdentity.KeyOf("SİLİNEN.ÖRNEK"));
            new CustomerSyncRepository(factory).GetForPush(0, 10).Should().ContainSingle(r => r.Id == id)
                .Which.Fields.NotesChangedAt.Should().NotBeNull("yeni sürümün biçim-2 gönderimi eski sürümün notunu damgasıyla götürür");
        }
        finally { Drop(path); }
    }

    // ── yardımcılar ─────────────────────────────────────────────────────

    private sealed record Counts(long Customers, long Labels, long Tombstones, long FkViolations, bool SearchIndexOk);

    private static Counts Snapshot(IDbConnectionFactory factory)
    {
        using var c = factory.Open();
        return new Counts(
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Label"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM CustomerPurgeTombstone"),
            c.Query("PRAGMA foreign_key_check").LongCount(),
            SearchIndexOk(c));
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

    private static void AssertMigrated(IDbConnectionFactory factory, Counts before)
    {
        using var c = factory.Open();
        c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(45);
        Snapshot(factory).Should().Be(before, "göç satır eklemez/silmez, yeni FK ihlali üretmez, arama indeksini bozmaz");
        // Boş/boşluk kullanıcı adı kimlik değildir: anahtarı bilerek NULL (C1 incelemesi M-1).
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer WHERE IdentityKey IS NULL AND od_identity_key(Username) IS NOT NULL").Should().Be(0);
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer WHERE IdentityKey IS NOT od_identity_key(Username)")
            .Should().Be(0, "geri doldurma C# anahtarının aynısı");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM CustomerPurgeTombstone WHERE IdentityKey IS NULL AND od_identity_key(Username) IS NOT NULL").Should().Be(0);
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
    }

    private static void Seed044(IDbConnectionFactory factory, int customers)
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
        conn.Execute(@"INSERT INTO Customer (Id, Platform, Username, DisplayName, FirstSeenAt, LastSeenAt, FullName, Phone,
                                            Address, City, GroupId, Notes, IsBlacklisted)
                       VALUES (@Id, @Platform, @Username, @DisplayName, @SeenAt, @SeenAt, @FullName, @Phone,
                               @Address, @City, @GroupId, @Notes, @IsBlacklisted)", rows, tx);
        conn.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Price, AddedAt)
                       VALUES (@Id, 's1', @CustomerId, @Platform, @Username, 'A1', 10, 1)",
            rows.SelectMany(r => Enumerable.Range(0, 2).Select(_ => new
                { Id = Guid.NewGuid().ToString("N"), CustomerId = r.Id, r.Platform, r.Username })), tx);
        conn.Execute("INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt) VALUES (@Platform, @Username, 1)",
            rows.Where((_, i) => i % 1000 == 0).Select(r => new { r.Platform, r.Username }), tx);
        tx.Commit();
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
    /// IncrementLabelStats, TombstoneUpsertSql). PR-3'ten önce son sürüm değişirse buradaki metinler o
    /// sürümden yenilenir.</summary>
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

        public const string TombstoneUpsert = @"
            INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt)
            VALUES (@platform, @username, @purgedAtUnix)
            ON CONFLICT(Platform, Username) DO UPDATE SET
                PurgedAt = MIN(CustomerPurgeTombstone.PurgedAt, excluded.PurgedAt)";
    }
}
