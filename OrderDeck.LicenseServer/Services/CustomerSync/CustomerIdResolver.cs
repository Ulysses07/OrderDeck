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

    /// <summary>
    /// <see cref="CanonicalOfAsync(Guid, IEnumerable{Guid}, CancellationToken)"/> ile aynı döngü (aynı lisans, en çok
    /// <see cref="MaxHops"/> adım, IgnoreQueryFilters) ve aynı varış; ek olarak zincirin izlediği kopyaların bayrakları
    /// (<see cref="CustomerChain"/>). Kanıt isteyen iki okuyucu içindir: bağlantıdan müşteriye çözücü
    /// (<c>ShopperCustomerResolver</c>) geçici kopyadan telefon kanıtı olmadan çıkmaz; kişi düzeyi IBAN okuyucusu
    /// (<c>PaymentMatcher</c>) geçici ya da silinmiş kopyadan geçen bağı asıl kayda saymaz (tasarım §2.5; IBAN bağında
    /// geçici kopya S2b planı Karar 7'dir, tasarım orada yalnız silinmiş kopyayı söyler). Zincir kuralı
    /// tek yerde, bu sınıfta kalır: o okuyucular <c>MergedIntoId</c>'yi kendileri izlemez. Döngü
    /// <see cref="CanonicalOfAsync(Guid, IEnumerable{Guid}, CancellationToken)"/>'unkinin eşidir, yalnız okunan sütun
    /// fazladır; o metot ve çağıranları bayrak okumaz, sorguları değişmez. İkisinin aynı kayda vardığını
    /// <c>CustomerIdResolverTests</c> sınar.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CustomerChain>> ChainOfAsync(
        Guid licenseId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = ids.Distinct().ToDictionary(id => id, id => new CustomerChain(id, false, false));
        var pending = result.Keys.ToList();
        for (var hop = 0; hop < MaxHops && pending.Count > 0; hop++)
        {
            var next = await _db.WpfCustomerProjections.IgnoreQueryFilters()
                .Where(p => p.LicenseId == licenseId && pending.Contains(p.Id) && p.MergedIntoId != null)
                .Select(p => new { p.Id, Target = p.MergedIntoId!.Value, p.CreatedByShopper, p.PurgedAt })
                .ToDictionaryAsync(x => x.Id, ct);
            if (next.Count == 0) break;
            foreach (var key in result.Keys.ToList())
                if (next.TryGetValue(result[key].Canonical, out var copy))
                    result[key] = new CustomerChain(
                        copy.Target,
                        result[key].ViaProvisionalCopy || copy.CreatedByShopper,
                        result[key].ViaPurgedCopy || copy.PurgedAt is not null);
            pending = next.Values.Select(x => x.Target).Distinct().ToList();
        }
        return result;
    }

    /// <summary>
    /// <see cref="CanonicalOfAsync(Guid, IEnumerable{Guid}, CancellationToken)"/>'un tersi: verilen kayıtlara doğrudan ya
    /// da zincirle (en çok <see cref="MaxHops"/> adım) yönlenen kopyaların Id'leri; girdiler dahil değil. Bir kişinin
    /// kopya Id'siyle saklanmış satırlarını (ör. sipariş <c>CustomerId</c>'si) SQL'de aramak için.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> CopiesOfAsync(Guid licenseId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var seen = ids.ToHashSet();
        var copies = new List<Guid>();
        var frontier = seen.ToList();
        for (var hop = 0; hop < MaxHops && frontier.Count > 0; hop++)
        {
            var next = await _db.WpfCustomerProjections.IgnoreQueryFilters()
                .Where(p => p.LicenseId == licenseId && p.MergedIntoId != null && frontier.Contains(p.MergedIntoId.Value))
                .Select(p => p.Id)
                .ToListAsync(ct);
            frontier = next.Where(seen.Add).ToList();
            copies.AddRange(frontier);
        }
        return copies;
    }

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

/// <summary>
/// <see cref="CustomerIdResolver.ChainOfAsync"/>'un bir Id için sonucu. <see cref="Canonical"/>,
/// <see cref="CustomerIdResolver.CanonicalOfAsync(Guid, IEnumerable{Guid}, CancellationToken)"/>'un vardığı Id'dir
/// (asıl kayıt; bilinmeyen ya da başka lisansın Id'si aynen; adım sınırına takılan zincirde son ulaşılan kopya).
/// Bayraklar yalnız yönlendirmesi İZLENEN kopyalardan gelir; varılan satırın kendi bayrağı sayılmaz: geçici ya da
/// silinmiş asıl kayıt zincir değildir, onu çağıran kendisi okur.
/// </summary>
/// <param name="Canonical">Varılan Id.</param>
/// <param name="ViaProvisionalCopy">İzlenen bir kopya shopper'ın açtığı geçici kayıttı (<c>CreatedByShopper</c>):
/// ondan geçen bağlantı ya da IBAN bağı kanıtsız bir kişiden gelmiş olabilir.</param>
/// <param name="ViaPurgedCopy">İzlenen bir kopya KVKK silmesiyle silinmişti (<c>PurgedAt</c> dolu).</param>
public readonly record struct CustomerChain(Guid Canonical, bool ViaProvisionalCopy, bool ViaPurgedCopy);
