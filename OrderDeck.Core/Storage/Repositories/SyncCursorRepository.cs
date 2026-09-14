using System.Globalization;
using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Bir sync imlecinin veritabanındaki hali: hangi aile (Name), hangi lisans
/// (LicenseKey) ve kaldığı yer. <see cref="Seq"/> tekil artan sayaç aileleri
/// için, <see cref="UpdatedAt"/>+<see cref="LastId"/> (zaman, id) çifti
/// aileleri için — bir imleç yalnız kendi biçimini doldurur.
/// </summary>
public sealed record SyncCursor(
    string Name,
    string LicenseKey,
    long? Seq,
    DateTimeOffset? UpdatedAt,
    Guid? LastId);

/// <summary>
/// R6-04: sync imleçleri, tarif ettikleri veriyle AYNI SQLite dosyasında
/// yaşar (gerekçe: göç 038 başlığı). settings.json'daki eski imleç alanları
/// yalnız ilk dokunuşta tohum olarak okunur ve sonra temizlenir; kalıcı
/// kaynak burasıdır.
/// </summary>
public sealed class SyncCursorRepository
{
    private readonly IDbConnectionFactory _factory;

    public SyncCursorRepository(IDbConnectionFactory factory) => _factory = factory;

    public SyncCursor? Get(string name, string licenseKey)
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingleOrDefault<Row>(
            "SELECT Name, LicenseKey, Seq, UpdatedAt, LastId FROM SyncCursor " +
            "WHERE Name = @name AND LicenseKey = @licenseKey",
            new { name, licenseKey });
        if (row is null) return null;

        return new SyncCursor(
            row.Name,
            row.LicenseKey,
            row.Seq,
            row.UpdatedAt is null
                ? null
                : DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
            row.LastId is null ? null : Guid.Parse(row.LastId));
    }

    public void Upsert(string name, string licenseKey,
        long? seq = null, DateTimeOffset? updatedAt = null, Guid? lastId = null)
    {
        using var conn = _factory.Open();
        conn.Execute(
            """
            INSERT INTO SyncCursor (Name, LicenseKey, Seq, UpdatedAt, LastId)
            VALUES (@name, @licenseKey, @seq, @updatedAt, @lastId)
            ON CONFLICT (Name, LicenseKey) DO UPDATE SET
                Seq = excluded.Seq,
                UpdatedAt = excluded.UpdatedAt,
                LastId = excluded.LastId
            """,
            new
            {
                name,
                licenseKey,
                seq,
                updatedAt = updatedAt?.ToString("O"),
                lastId = lastId?.ToString("D"),
            });
    }

    private sealed record Row(string Name, string LicenseKey, long? Seq,
        string? UpdatedAt, string? LastId);
}
