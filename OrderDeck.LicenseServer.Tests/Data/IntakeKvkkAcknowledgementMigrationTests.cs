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
/// IntakeKvkkAcknowledgement göçü gerçek SQL Server'da, DOLU bir tabloya
/// karşı (<see cref="TcknProtectedColumnsMigrationTests"/> deseni): hedeften
/// bir önceki göçe kadar kur → eski şemaya bir kayıt yaz → yalnız hedefi
/// uygula. Göç yalnız ekleme yapmalı: iki kolon da nullable, varsayılan
/// değersiz — 2026-10-13 öncesi kayıtlar "onay yok" (null) olarak kalır;
/// geriye dönük onay UYDURULMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class IntakeKvkkAcknowledgementMigrationTests
{
    private readonly SqlServerContainerFixture _sql;

    public IntakeKvkkAcknowledgementMigrationTests(SqlServerContainerFixture sql) => _sql = sql;

    [Fact]
    public async Task Eski_kayitlar_bos_kalir_kolonlar_nullable_eklenir()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);

        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_IntakeKvkkAcknowledgement"));
        await migrator.MigrateAsync(all[all.IndexOf(target) - 1]);

        // Göç yalnız IntakeFormSubmissions'a kolon ekliyor: müşteri ve form
        // ayarı bugünkü modelle eski şemaya yazılabilir. Gönderim satırı ise
        // ham SQL ile — bugünkü model yeni kolonları da yazmaya çalışırdı.
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"musteri-{Guid.NewGuid():N}@example.test",
            Name = "KVKK Göç Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var config = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = $"g-{Guid.NewGuid():N}"[..10],
            WhatsAppPhone = TestPhone.NewE164(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        db.IntakeFormConfigs.Add(config);
        await db.SaveChangesAsync();

        var submissionId = Guid.NewGuid();
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();

            // Testin kendini kanıtlaması: aşağıdaki "kolon var ve boş" iddiası
            // ancak kolonun BURADA olmadığı gösterilerek anlam kazanıyor.
            (await ColumnAsync(conn, "KvkkAcknowledgedAt")).Should().BeNull("göç henüz uygulanmadı");
            (await ColumnAsync(conn, "KvkkTextVersion")).Should().BeNull("göç henüz uygulanmadı");

            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO IntakeFormSubmissions
                    (Id, IntakeFormConfigId, Username, FullName, Address,
                     WhatsAppConsent, SmsConsent, SubmittedAt)
                VALUES
                    (@id, @configId, @username, @fullName, @address, 1, 0, @now)
                """;
            insert.Parameters.AddWithValue("@id", submissionId);
            insert.Parameters.AddWithValue("@configId", config.Id);
            insert.Parameters.AddWithValue("@username", "gocoks");
            insert.Parameters.AddWithValue("@fullName", "Göç Testi Müşteri");
            insert.Parameters.AddWithValue("@address", "Göç Testi Adres");
            insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync(target);

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();

            var at = await ColumnAsync(conn, "KvkkAcknowledgedAt");
            at.Should().NotBeNull();
            at!.Value.Nullable.Should().Be("YES");
            at.Value.DataType.Should().Be("datetimeoffset");

            var version = await ColumnAsync(conn, "KvkkTextVersion");
            version.Should().NotBeNull();
            version!.Value.Nullable.Should().Be("YES");
            version.Value.MaxLength.Should().Be(32);

            await using var select = conn.CreateCommand();
            select.CommandText = """
                SELECT KvkkAcknowledgedAt, KvkkTextVersion, WhatsAppConsent
                FROM IntakeFormSubmissions WHERE Id = @id
                """;
            select.Parameters.AddWithValue("@id", submissionId);
            await using var reader = await select.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue("göç satırı silmemeli");
            reader.IsDBNull(0).Should().BeTrue("eski kayda geriye dönük onay zamanı yazılmamalı");
            reader.IsDBNull(1).Should().BeTrue("eski kayda geriye dönük metin sürümü yazılmamalı");
            reader.GetBoolean(2).Should().BeTrue("mevcut izin kolonu dokunulmadan kalmalı");
        }
    }

    private static async Task<(string Nullable, string DataType, int? MaxLength)?> ColumnAsync(
        SqlConnection conn, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = 'IntakeFormSubmissions' AND COLUMN_NAME = @column
            """;
        cmd.Parameters.AddWithValue("@column", column);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2));
    }
}
