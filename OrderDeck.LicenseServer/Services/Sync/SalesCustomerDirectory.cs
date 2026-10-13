using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>Bir müşteri projeksiyonunun akışa giren alanları.</summary>
/// <param name="MergedIntoId">Doluysa satır kopyadır; asıl kayıt bu Id'dir.</param>
public sealed record SalesCustomerRow(
    Guid Id,
    Guid? MergedIntoId,
    string Platform,
    string Username,
    string? DisplayName,
    DateTimeOffset? PurgedAt);

/// <summary>Bir siparişin <c>CustomerId</c>'sinin akıştaki karşılığı.</summary>
/// <param name="Id">Asıl kaydın hex'i; çözülemiyorsa (GUID değil, kopya değil) ham değer.</param>
/// <param name="Canonical">Asıl kaydın projeksiyonu; GUID olmayan ya da projeksiyonu bulunmayan müşteride null.</param>
public readonly record struct SalesCustomerResolution(string Id, SalesCustomerRow? Canonical);

/// <summary>
/// Sipariş müşterilerinin asıl kayda çözümü — spec 2026-10-13 §4.4 yan listesi. Eski sürüm birleştirmeden sonra da
/// kopyanın Id'sini kullanabilir; birleştirici siparişleri asıl kayda taşır ama geç gelen veri kopyada kalabilir.
/// Kopya, <c>MergedIntoId</c> zinciri boyunca asıl kayda çevrilir (<see cref="CustomerSync.CustomerIdResolver"/> ile
/// aynı kural ve aynı adım sınırı); çözüm bellekte, yüklenen satırlar üzerinde yapılır.
///
/// <para>CustomerId'si GUID olmayan siparişin projeksiyonu yoktur (WPF projeksiyon gönderiminde atlar,
/// <c>WpfCustomerProjectionSyncService</c>); GUID'li ama projeksiyonu sunucuya hiç gelmemiş müşteri de olabilir. İkisi de
/// hata değildir: <see cref="SalesCustomerResolution.Canonical"/> null döner ve taslak siparişin kendi alanlarından
/// kurulur.</para>
/// </summary>
public sealed class SalesCustomerDirectory
{
    /// <summary>İzlenen en çok kopya adımı; <see cref="CustomerSync.CustomerIdResolver"/> ile aynı. Zincir
    /// birleştirme işinde düzleştirilir; sınır döngüye ve bozuk veriye karşı.</summary>
    public const int MaxHops = 3;

    public static readonly SalesCustomerDirectory Empty = new([]);

    private readonly Dictionary<Guid, SalesCustomerRow> _rows;

    public SalesCustomerDirectory(IEnumerable<SalesCustomerRow> rows)
    {
        _rows = new Dictionary<Guid, SalesCustomerRow>();
        foreach (var row in rows) _rows[row.Id] = row;
    }

    /// <summary>Ham <c>CustomerId</c>'yi asıl kayda çözer.</summary>
    public SalesCustomerResolution Resolve(string rawCustomerId)
    {
        if (!TryParse(rawCustomerId, out var id)) return new(rawCustomerId, null);

        var current = id;
        var hops = 0;
        while (hops < MaxHops && _rows.TryGetValue(current, out var row) && row.MergedIntoId is { } target)
        {
            current = target;
            hops++;
        }

        // Asıl kayıt Id'si değişmediyse ham yazım korunur (CustomerIdResolver.CanonicalHexOfAsync ile aynı).
        var hex = current == id ? rawCustomerId : current.ToString("N");
        return new(hex, _rows.GetValueOrDefault(current));
    }

    /// <summary>
    /// Verilen ham Id'lerin ve asıl kayıtlarının projeksiyonlarını yükler. Kopyalar varsayılan sorgulardan gizli
    /// (A5b); burada açıkça istenir. Lisans sınırını yalnız açık <c>LicenseId</c> koşulu çizer.
    /// </summary>
    public static async Task<SalesCustomerDirectory> LoadAsync(
        LicenseDbContext db, Guid licenseId, IEnumerable<string> rawCustomerIds, CancellationToken ct)
    {
        var rows = new Dictionary<Guid, SalesCustomerRow>();
        var pending = rawCustomerIds
            .Select(raw => TryParse(raw, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        // İlk okuma + MaxHops adım: zincirin sonundaki asıl kayıt da yüklenir.
        for (var hop = 0; hop <= MaxHops && pending.Count > 0; hop++)
        {
            var batch = pending;
            var loaded = await db.WpfCustomerProjections
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.LicenseId == licenseId && batch.Contains(p.Id))
                .Select(p => new SalesCustomerRow(
                    p.Id, p.MergedIntoId, p.Platform, p.Username, p.DisplayName, p.PurgedAt))
                .ToListAsync(ct);
            foreach (var row in loaded) rows[row.Id] = row;

            pending = loaded
                .Where(r => r.MergedIntoId is { } target && !rows.ContainsKey(target))
                .Select(r => r.MergedIntoId!.Value)
                .Distinct()
                .ToList();
        }

        return new SalesCustomerDirectory(rows.Values);
    }

    /// <summary>Sipariş ve kargo <c>CustomerId</c>'leri "N" biçimli hex (bkz. <c>CustomerIdResolver</c>).</summary>
    private static bool TryParse(string raw, out Guid id) => Guid.TryParseExact(raw, "N", out id);
}
