using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Lisans geneli kargo ayarının (<see cref="LicenseShippingSettings"/>, spec 2026-10-13 §4.8, §6.2) tek okuma ve
/// yazma yolu: <c>GET/PUT shipping-settings</c>, <c>sales/state</c> ve <c>sales-sync shipping set</c> CLI'ı bunu
/// kullanır.
///
/// <para><b>İyimser eşzamanlılık.</b> Yazan, gördüğü sürümü (<c>expectedVersion</c>; satır yoksa 0) verir. Güncel
/// sürüm farklıysa hiçbir şey yazılmaz ve <see cref="Stale"/> döner. Eşit sürümü bekleyen eşzamanlı iki yazımın ikisi
/// de denetimi geçebilir; hakem veritabanıdır: güncellemede <see cref="LicenseShippingSettings.Version"/> jetonu
/// (<see cref="DbUpdateConcurrencyException"/>), ilk eklemede birincil anahtar. Kaybeden de <see cref="Stale"/>
/// alır ve kazananın satırını görür; ikinci bir satır ya da sessiz üzerine yazım olmaz.</para>
/// </summary>
public static class LicenseShippingSettingsStore
{
    /// <summary>Ayarın tel ve rapor biçimi. Satır yoksa <see cref="Missing"/>: sürüm 0, değerler boş.</summary>
    /// <param name="Fee">Kargo ücreti (<see cref="LicenseShippingSettings.ShippingFee"/>).</param>
    /// <param name="UpdatedBy"><c>cli</c> ya da yazan kopyanın ReplicaId'si.</param>
    public sealed record Snapshot(
        int Version,
        decimal? FreeShippingThreshold,
        decimal? Fee,
        DateTimeOffset? UpdatedAt,
        string? UpdatedBy);

    public static readonly Snapshot Missing = new(LicenseShippingSettings.MissingVersion, null, null, null, null);

    public abstract record WriteResult(Snapshot Current);

    /// <summary>Yazıldı; <see cref="WriteResult.Current"/> yeni satır.</summary>
    public sealed record Written(Snapshot Current) : WriteResult(Current);

    /// <summary>Beklenen sürüm güncel değildi ya da eşzamanlı bir yazım kazandı; hiçbir şey yazılmadı.
    /// <see cref="WriteResult.Current"/> veritabanındaki güncel hâl.</summary>
    public sealed record Stale(Snapshot Current) : WriteResult(Current);

    /// <summary>Lisansın ayarı; satır yoksa <see cref="Missing"/>. İzlemesiz okur.</summary>
    public static async Task<Snapshot> ReadAsync(LicenseDbContext db, Guid licenseId, CancellationToken ct) =>
        await db.LicenseShippingSettings.AsNoTracking()
            .Where(s => s.LicenseId == licenseId)
            .Select(s => new Snapshot(s.Version, s.FreeShippingThreshold, s.ShippingFee, s.UpdatedAt, s.UpdatedBy))
            .SingleOrDefaultAsync(ct)
        ?? Missing;

    /// <summary>
    /// Ayarı yazar: satır yoksa sürüm 1 ile ekler, varsa sürümü bir artırır. Değerler çağıranda
    /// <see cref="LicenseShippingSettings.IsValid"/> ile doğrulanmış olmalı. Kendi <c>SaveChanges</c>'ini çağırır;
    /// bağlamda bekleyen başka değişiklik olmamalı.
    /// </summary>
    /// <param name="expectedVersion">Yazanın gördüğü sürüm (satır yoksa 0).</param>
    /// <param name="updatedBy"><see cref="LicenseShippingSettings.UpdatedByCli"/> ya da ReplicaId metni.</param>
    public static async Task<WriteResult> WriteAsync(
        LicenseDbContext db,
        Guid licenseId,
        int expectedVersion,
        decimal? threshold,
        decimal? fee,
        string updatedBy,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!LicenseShippingSettings.IsValid(threshold, fee))
            throw new ArgumentException("Kargo ayarı geçersiz: ikisi de boş ya da ikisi de > 0 olmalı.");

        var row = await db.LicenseShippingSettings.SingleOrDefaultAsync(s => s.LicenseId == licenseId, ct);
        var current = row?.Version ?? LicenseShippingSettings.MissingVersion;
        if (current != expectedVersion)
            return new Stale(row is null ? Missing : ToSnapshot(row));

        var inserting = row is null;
        if (row is null)
        {
            row = new LicenseShippingSettings { LicenseId = licenseId };
            db.LicenseShippingSettings.Add(row);
        }
        row.FreeShippingThreshold = threshold;
        row.ShippingFee = fee;
        row.Version = current + 1;
        row.UpdatedAt = now;
        row.UpdatedBy = updatedBy;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Jeton: biz okuduktan sonra satır başka bir yazımla değişti (ya da silindi).
            return new Stale(await ReadAsync(db, licenseId, ct));
        }
        catch (DbUpdateException) when (inserting)
        {
            // Eşzamanlı ilk ekleme birincil anahtara çarptı. Hata "şimdi bakınca satır var mı" diye sınıflandırılır
            // (SalesReplicaService deseni): varsa yarışı kaybettik; yoksa bilinmeyen bir yazma hatasıdır ve yutulmaz
            // (ör. lisans arada silindi, FK).
            var fresh = await ReadAsync(db, licenseId, ct);
            if (fresh == Missing) throw;
            return new Stale(fresh);
        }

        return new Written(ToSnapshot(row));
    }

    private static Snapshot ToSnapshot(LicenseShippingSettings row) =>
        new(row.Version, row.FreeShippingThreshold, row.ShippingFee, row.UpdatedAt, row.UpdatedBy);
}
