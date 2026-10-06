using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// CustomerProjectionFullSync göçünü gerçek SQL Server'a karşı, satırları
/// DOLU bir tabloya karşı doğrular.
///
/// Neden gerekli: diğer tüm RelationalApiFactory testleri göç zincirini BOŞ
/// bir veritabanına baştan sona tek seferde uyguluyor (bkz.
/// <see cref="TcknProtectedColumnsMigrationTests"/> sınıf dokümanı — aynı
/// gerekçe burada da geçerli). Prod'da bugün ~3.900 WpfCustomerProjections
/// satırı var (177'si ASCII-dışı kullanıcı adı, 12'si 'İ' içeriyor); bu göç
/// (a) yeni NOT NULL <c>IdentityKey</c> kolonunu ekliyor ve TÜM var olan
/// satırları geriye dönük dolduruyor, (b) <c>ChangeSeq</c> rowversion
/// kolonunu ekliyor, (c) <c>IdentityKey</c>'e BIN2 collation veriyor —
/// üçü de yalnızca satır VARKEN ve Unicode kenar durumlarıyla gözlenebilir
/// davranışlar. Bu dosya göçü ikiye bölüp (hedeften bir önceki noktaya kadar
/// → eski şemaya "zor" kullanıcı adlarıyla satır yaz → yalnız hedefi uygula)
/// tam bunu kanıtlıyor.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerProjectionFullSyncMigrationTests
{
    private readonly SqlServerContainerFixture _sql;

    public CustomerProjectionFullSyncMigrationTests(SqlServerContainerFixture sql) => _sql = sql;

    /// <summary>
    /// Eski şemada (IdentityKey/ChangeSeq henüz yok) "zor" kullanıcı adlarıyla
    /// dört satır yazıp yalnız hedef göçü uyguluyor. Kanıtlanan:
    /// (a) her satır için IdentityKey, <see cref="WpfCustomerProjection.IdentityKeyOf"/>
    ///     ile HESAPLANAN değere eşit (sabit string değil — göçteki SQL
    ///     backfill ile C# tarafının en azından bu dört örnekte ÖRTÜŞTÜĞÜnü
    ///     kanıtlar);
    /// (b) ChangeSeq her satır için sıfırdan büyük ve BİRBİRİNDEN FARKLI
    ///     (rowversion satır başına benzersiz — tek bir toplu UPDATE bile
    ///     her satıra kendi değerini verir);
    /// (c) yeni eklenen nullable kolonlar null, bool kolonlar false;
    /// (d) IdentityKey kolonunun collation'ı Latin1_General_100_BIN2;
    /// (e) BIN2 SAYESINDE "ayşe🌸" satırının anahtarı "ayşe" ile TAM eşit
    ///     DEĞİL — varsayılan (CI_AS) collation'da bu ikisi eşit sayılabiliyordu.
    /// </summary>
    [Fact]
    public async Task Eski_satirlari_IdentityKey_ile_doldurur_ChangeSeq_atar_ve_BIN2_tam_esitler()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);

        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_CustomerProjectionFullSync"));
        var previous = all[all.IndexOf(target) - 1];

        // Hedeften BİR ÖNCEKİ noktaya kadar uygula: IdentityKey/ChangeSeq ve
        // diğer yeni kolonlar henüz yok — göç öncesi prod satırlarını taklit
        // edebilmek için şart.
        await migrator.MigrateAsync(previous);

        var customerId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var email = $"musteri-{Guid.NewGuid():N}@example.test";
        var ozetDegeri = $"h-{Guid.NewGuid():N}";
        var licenseKey = $"LIC-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // Dört "zor" kullanıcı adı: baştan/sondan boşluklu + büyük harfli +
        // Türkçe büyük 'İ', zaten küçük harf 'Ş', emoji içeren, düz ASCII.
        var melikeId = Guid.NewGuid();
        var suleId = Guid.NewGuid();
        var aysegulId = Guid.NewGuid();
        var asciiId = Guid.NewGuid();
        const string melikeUsername = "  MELİKE ";
        const string suleUsername = "Şule";
        const string aysegulUsername = "ayşe🌸";
        const string asciiUsername = "mehmetyilmaz";

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();

            // Yalnızca Customers/Licenses'ın NOT NULL kolonları dolduruluyor
            // (RowVersion rowversion — otomatik, INSERT'te hiç verilmiyor).
            // SkuCode "STD": Sku.HasData ile göç zincirinde zaten tohumlanmış.
            await using var insertCustomer = conn.CreateCommand();
            insertCustomer.CommandText = """
                INSERT INTO Customers (Id, Email, Name, PasswordHash, AuthVersion, CreatedAt, Unsubscribed)
                VALUES (@id, @email, @name, @ozetDegeri, 0, @now, 0)
                """;
            insertCustomer.Parameters.AddWithValue("@id", customerId);
            insertCustomer.Parameters.AddWithValue("@email", email);
            insertCustomer.Parameters.AddWithValue("@name", "Göç Testi Müşteri");
            insertCustomer.Parameters.AddWithValue("@ozetDegeri", ozetDegeri);
            insertCustomer.Parameters.AddWithValue("@now", now);
            await insertCustomer.ExecuteNonQueryAsync();

            await using var insertLicense = conn.CreateCommand();
            insertLicense.CommandText = """
                INSERT INTO Licenses (Id, LicenseKey, CustomerId, SkuCode, ActivationSlots, IssuedAt, ExpiresAt, ShopperAppEnabled)
                VALUES (@id, @licenseKey, @customerId, 'STD', 1, @now, @expiresAt, 0)
                """;
            insertLicense.Parameters.AddWithValue("@id", licenseId);
            insertLicense.Parameters.AddWithValue("@licenseKey", licenseKey);
            insertLicense.Parameters.AddWithValue("@customerId", customerId);
            insertLicense.Parameters.AddWithValue("@now", now);
            insertLicense.Parameters.AddWithValue("@expiresAt", now.AddYears(1));
            await insertLicense.ExecuteNonQueryAsync();

            // Eski şemaya (IdentityKey/ChangeSeq yok) dört WpfCustomerProjection
            // satırı.
            await using var insertProjections = conn.CreateCommand();
            insertProjections.CommandText = """
                INSERT INTO WpfCustomerProjections (Id, LicenseId, Platform, Username, UpdatedAt)
                VALUES (@melikeId, @licenseId, @platform, @melikeUsername, @now),
                       (@suleId, @licenseId, @platform, @suleUsername, @now),
                       (@aysegulId, @licenseId, @platform, @aysegulUsername, @now),
                       (@asciiId, @licenseId, @platform, @asciiUsername, @now)
                """;
            insertProjections.Parameters.AddWithValue("@melikeId", melikeId);
            insertProjections.Parameters.AddWithValue("@suleId", suleId);
            insertProjections.Parameters.AddWithValue("@aysegulId", aysegulId);
            insertProjections.Parameters.AddWithValue("@asciiId", asciiId);
            insertProjections.Parameters.AddWithValue("@licenseId", licenseId);
            insertProjections.Parameters.AddWithValue("@platform", "tiktok");
            insertProjections.Parameters.AddWithValue("@melikeUsername", melikeUsername);
            insertProjections.Parameters.AddWithValue("@suleUsername", suleUsername);
            insertProjections.Parameters.AddWithValue("@aysegulUsername", aysegulUsername);
            insertProjections.Parameters.AddWithValue("@asciiUsername", asciiUsername);
            insertProjections.Parameters.AddWithValue("@now", now);
            await insertProjections.ExecuteNonQueryAsync();
        }

        // Hedef göçü uygula: IdentityKey/ChangeSeq ve diğer yeni kolonlar
        // eklenir, IdentityKey var olan satırlar için geriye dönük doldurulur.
        await migrator.MigrateAsync(target);

        var rows = await db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId)
            .ToListAsync();
        rows.Should().HaveCount(4);

        foreach (var row in rows)
        {
            // Sabit string DEĞİL: göçteki SQL backfill'in ürettiği değer,
            // C# IdentityKeyOf'un AYNI (DB'den geri okunan) Username için
            // hesapladığıyla karşılaştırılıyor.
            row.IdentityKey.Should().Be(WpfCustomerProjection.IdentityKeyOf(row.Username),
                $"göçteki SQL backfill ile IdentityKeyOf aynı sonucu vermeli (Username='{row.Username}')");
            AssertYeniKolonlarBosDolu(row);
        }

        // rowversion satır başına benzersiz: tek bir toplu UPDATE (backfill)
        // bile her satıra KENDİ değerini verir, hiçbirini aynı değere eşitlemez.
        var changeSeqs = rows.Select(r => r.ChangeSeq).ToList();
        changeSeqs.Should().OnlyContain(v => v > 0, "rowversion motor tarafından atanır, sıfır kalmamalı");
        changeSeqs.Should().OnlyHaveUniqueItems("rowversion satır başına benzersizdir");

        // BIN2 tam eşitlik: varsayılan (CI_AS) collation "ayşe🌸" ile "ayşe"yi
        // eşit sayabiliyordu (emoji göz ardı edilir); BIN2 bayt-bayt
        // karşılaştırdığı için bu sorgu "ayşe🌸" satırını YAKALAMAMALI.
        var exactMatches = await db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && p.IdentityKey == "ayşe")
            .ToListAsync();
        exactMatches.Should().BeEmpty("BIN2 altında 'ayşe🌸' ile 'ayşe' tam eşit DEĞİL");

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            (await ColumnCollationAsync(conn, "WpfCustomerProjections", "IdentityKey"))
                .Should().Be("Latin1_General_100_BIN2");
        }
    }

    /// <summary>Bu göçte eklenen nullable kolonların null, bool kolonların
    /// false olduğunu doğrular — eski satırlar için beklenen, çünkü göç
    /// hiçbirine değer atamıyor (yalnız IdentityKey geriye dönük doldurulur).</summary>
    private static void AssertYeniKolonlarBosDolu(WpfCustomerProjection p)
    {
        p.DisplayName.Should().BeNull();
        p.GroupId.Should().BeNull();
        p.FullNameChangedAt.Should().BeNull();
        p.DisplayNameChangedAt.Should().BeNull();
        p.GroupIdChangedAt.Should().BeNull();
        p.City.Should().BeNull();
        p.District.Should().BeNull();
        p.RecipientPaysActive.Should().BeFalse();
        p.RecipientPaysChangedAt.Should().BeNull();
        p.AddressChangedAt.Should().BeNull();
        p.Email.Should().BeNull();
        p.TcknProtected.Should().BeNull();
        p.WhatsAppConsent.Should().BeFalse();
        p.SmsConsent.Should().BeFalse();
        p.PhoneChangedAt.Should().BeNull();
        p.EmailChangedAt.Should().BeNull();
        p.TcknChangedAt.Should().BeNull();
        p.WhatsAppConsentChangedAt.Should().BeNull();
        p.SmsConsentChangedAt.Should().BeNull();
        p.IsBlacklisted.Should().BeFalse();
        p.BlacklistReason.Should().BeNull();
        p.BlacklistedAt.Should().BeNull();
        p.BlacklistChangedAt.Should().BeNull();
        p.Notes.Should().BeNull();
        p.NotesChangedAt.Should().BeNull();
        p.MergedIntoId.Should().BeNull();
    }

    private static async Task<string> ColumnCollationAsync(SqlConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLLATION_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @table AND COLUMN_NAME = @column
            """;
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
