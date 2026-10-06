using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// TcknProtectedColumns göçünü gerçek SQL Server'a karşı doğrular.
///
/// BU TESTİN GERÇEK DEĞERİ: <c>RelationalApiFactory</c> tabanlı testler
/// (örn. <c>NetgsmAccountUniqueIndexTests</c>) zaten her çalışmada TÜM göç
/// zincirini gerçek SQL Server'a uyguluyor — <c>Program.cs</c> açılışta
/// <c>db.Database.IsRelational()</c> ise <c>Migrate()</c> çağırıyor
/// (<c>EnsureCreated</c> yalnız InMemory sağlayıcı için çalışıyor, bkz.
/// <c>Program.cs</c> ~845-852). Yani "zincir SQL Server'a hiç patlamadan
/// uygulanıyor mu" sorusu zaten defalarca, dolaylı yoldan kanıtlanmış.
///
/// Ama o testlerin hepsi göçü BOŞ bir veritabanına, baştan sona TEK seferde
/// uyguluyor — aradan hiçbir satır geçmeden. Hiçbiri "eski şemada (Tc
/// nvarchar(11)) yazılmış bir satır, TcknProtectedColumns'tan SONRA da
/// kayıpsız duruyor mu, ve kolon gerçekten 512'ye mi büyüdü" sorusunu
/// sormuyor. Bu dosya göçü ikiye bölüyor (hedeften bir önceki noktaya kadar
/// → eski şemaya satır yaz → yalnız hedefi uygula) ve tam o soruyu
/// soruyor — <see cref="IMigrator"/> ile sürülen bu iki adımlı senaryoyu
/// kanıtlayan tek yer burası.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class TcknProtectedColumnsMigrationTests
{
    private readonly SqlServerContainerFixture _sql;

    public TcknProtectedColumnsMigrationTests(SqlServerContainerFixture sql) => _sql = sql;

    /// <summary>
    /// Göçü ikiye bölüp araya eski şemada bir satır sokuyor: hiçbir
    /// RelationalApiFactory testi bunu yapmıyor (hepsi boş DB'ye zinciri tek
    /// seferde uyguluyor). Kanıtlanan: (a) göçten ÖNCE kolon gerçekten
    /// nvarchar(11); (b) göçten SONRA aynı satırın Tc değeri değişmeden
    /// duruyor (AlterColumn veri taşımıyor/şifrelemiyor); (c) her iki kolon
    /// da (Shoppers.Tc, IntakeFormSubmissions.Tckn) 512'ye büyümüş.
    /// </summary>
    [Fact]
    public async Task Eski_duz_metni_korur_ve_kolonlari_512ye_buyutur()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);

        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_TcknProtectedColumns"));
        var previous = all[all.IndexOf(target) - 1];

        // Hedeften BİR ÖNCEKİ noktaya kadar uygula: bu, TcknProtectedColumns
        // uygulanmadan ÖNCEKİ şema (Tc/Tckn hâlâ nvarchar(11)) — göç öncesi
        // prod satırlarını taklit edebilmek için şart.
        await migrator.MigrateAsync(previous);

        var shopperId = Guid.NewGuid();
        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var ozetDegeri = $"h-{Guid.NewGuid():N}";
        var legacyPlain = TestTckn.NewValid();
        var now = DateTimeOffset.UtcNow;

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();

            // Testin kendini kanıtlaması için şart: aşağıdaki "512'ye
            // büyüdü" iddiası, kolon "zaten hep 512'ydi" ihtimalinden
            // ancak BURADA 11 olduğunu göstererek ayrışıyor (her iki kolon
            // için de).
            (await ColumnLengthAsync(conn, "Shoppers", "Tc")).Should().Be(11,
                "TcknProtectedColumns henüz uygulanmadı");
            (await ColumnLengthAsync(conn, "IntakeFormSubmissions", "Tckn")).Should().Be(11,
                "TcknProtectedColumns henüz uygulanmadı");

            // Eski şemaya (nvarchar(11)) düz metin TCKN yazıyoruz — göç
            // öncesi bir prod satırını taklit eder. Yalnızca Shoppers'ın
            // NOT NULL kolonları (+ Tc) dolduruluyor; DeletedAt gibi
            // nullable alanlar atlanıyor.
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO Shoppers
                    (Id, FullName, Phone, AuthVersion, PasswordHash, Address, Tc,
                     NotificationsEnabledBroadcast, NotificationsEnabledOrders, NotificationsEnabledPayments,
                     SmsConsent, CreatedAt, UpdatedAt)
                VALUES
                    (@id, @fullName, @phone, 0, @ozetDegeri, @address, @tc,
                     1, 1, 1, 0, @now, @now)
                """;
            insert.Parameters.AddWithValue("@id", shopperId);
            insert.Parameters.AddWithValue("@fullName", "Göç Testi Shopper");
            insert.Parameters.AddWithValue("@phone", phone);
            insert.Parameters.AddWithValue("@ozetDegeri", ozetDegeri);
            insert.Parameters.AddWithValue("@address", "Göç Testi Adres");
            insert.Parameters.AddWithValue("@tc", legacyPlain);
            insert.Parameters.AddWithValue("@now", now);
            await insert.ExecuteNonQueryAsync();
        }

        // Hedef göçü uygula. Bu yalnızca AlterColumn (kolon genişliği); satır
        // taşımıyor, hiçbir değeri şifrelemiyor — şifreleme ayrı bir arka
        // plan işinin (TcknBackfillJob, 2. sürümde eklendi) görevi. O yüzden
        // eski düz metin, göçten SONRA da aynen durmalı.
        await migrator.MigrateAsync(target);

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();

            await using var select = conn.CreateCommand();
            select.CommandText = "SELECT Tc FROM Shoppers WHERE Id = @id";
            select.Parameters.AddWithValue("@id", shopperId);
            var tcAfter = (string?)await select.ExecuteScalarAsync();
            tcAfter.Should().Be(legacyPlain, "göç veriyi taşımıyor, yalnızca kolonu büyütüyor");

            (await ColumnLengthAsync(conn, "Shoppers", "Tc")).Should().Be(512);
            (await ColumnLengthAsync(conn, "IntakeFormSubmissions", "Tckn")).Should().Be(512);
        }
    }

    private static async Task<int> ColumnLengthAsync(SqlConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT CHARACTER_MAXIMUM_LENGTH
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @table AND COLUMN_NAME = @column
            """;
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
