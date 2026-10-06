using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Eski sürümdeki bilgisayarlar birleştirmeden sonra da kopyanın Id'sini
/// kullanmaya devam eder (yönlendirmeyi okumazlar): sipariş, kargo, bakiye
/// uygulaması o Id ile gelir. Müşteri Id'si kabul eden her sınır, Id'yi önce
/// buradan geçirir: kopya asıl kayda çevrilir, kopya olmayan (asıl kayıt,
/// bilinmeyen Id, başka lisansın Id'si) aynen döner. Zincir (kopyanın
/// kopyası) birleştirme işinde düzleştirilir; burada yine de savunma amaçlı
/// en çok <see cref="MaxHops"/> adım izlenir.
///
/// <para>Kopyalar varsayılan sorgulardan gizli (LicenseDbContext'teki sorgu
/// filtresi); bu sınıf onları açıkça ister (IgnoreQueryFilters).</para>
/// </summary>
public sealed class CustomerIdResolver
{
    private const int MaxHops = 3;
    private readonly LicenseDbContext _db;
    public CustomerIdResolver(LicenseDbContext db) => _db = db;

    /// <summary>Her girdi Id'si için asıl kayıt Id'si (kopya değilse kendisi).</summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> CanonicalOfAsync(
        Guid licenseId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = ids.Distinct().ToDictionary(id => id, id => id);
        var pending = result.Keys.ToList();
        for (var hop = 0; hop < MaxHops && pending.Count > 0; hop++)
        {
            var next = await _db.WpfCustomerProjections.IgnoreQueryFilters()
                .Where(p => p.LicenseId == licenseId && pending.Contains(p.Id) && p.MergedIntoId != null)
                .Select(p => new { p.Id, Target = p.MergedIntoId!.Value })
                .ToDictionaryAsync(x => x.Id, x => x.Target, ct);
            if (next.Count == 0) break;
            foreach (var key in result.Keys.ToList())
                if (next.TryGetValue(result[key], out var target)) result[key] = target;
            pending = next.Values.Distinct().ToList();
        }
        return result;
    }

    public async Task<Guid> CanonicalOfAsync(Guid licenseId, Guid id, CancellationToken ct)
        => (await CanonicalOfAsync(licenseId, [id], ct))[id];

    /// <summary>Sipariş/kargo <c>CustomerId</c>'si gibi "N" biçimli hex Id'ler
    /// için. Çözümlenemeyen (Guid olmayan) değer aynen döner.</summary>
    public async Task<IReadOnlyDictionary<string, string>> CanonicalHexOfAsync(
        Guid licenseId, IEnumerable<string> hexes, CancellationToken ct)
    {
        var distinct = hexes.Distinct().ToList();
        var parsed = distinct
            .Select(h => (Hex: h, Ok: Guid.TryParseExact(h, "N", out var g), Id: g))
            .ToList();
        var map = await CanonicalOfAsync(licenseId, parsed.Where(x => x.Ok).Select(x => x.Id), ct);
        return parsed.ToDictionary(
            x => x.Hex,
            x => x.Ok && map[x.Id] != x.Id ? map[x.Id].ToString("N") : x.Hex);
    }

    /// <summary>
    /// Rotasında lisans taşımayan panel uçları için: Id çağıran yayıncının
    /// (kiracının) lisanslarından birine aitse (lisans, asıl kayıt), değilse
    /// null. Kopya Id'si de bulunur — eski bir yer imi/sekme kopyanın Id'sini
    /// taşıyabilir; sahiplik kopyanın KENDİ lisansından doğrulanır, kiracı
    /// yalıtımı kopyalar için de geçerli.
    /// </summary>
    public async Task<(Guid LicenseId, Guid CanonicalId)?> LocateForTenantAsync(
        Guid tenantCustomerId, Guid id, CancellationToken ct)
    {
        var row = await _db.WpfCustomerProjections.IgnoreQueryFilters()
            .Where(p => p.Id == id && p.License.CustomerId == tenantCustomerId)
            .Select(p => new { p.LicenseId, p.MergedIntoId })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var canonicalId = row.MergedIntoId is null
            ? id
            : await CanonicalOfAsync(row.LicenseId, id, ct);
        return (row.LicenseId, canonicalId);
    }
}
