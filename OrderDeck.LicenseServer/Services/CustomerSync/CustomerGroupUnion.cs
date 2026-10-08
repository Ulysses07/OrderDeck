using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Sunucuda kişi gruplarını (<see cref="WpfCustomerProjection.GroupId"/>)
/// birleştiren iki bir kerelik işin ORTAK yazımı: elle gruplama
/// (<see cref="CustomerGroupingJob"/>) ve "@" ikizi birleştirmesi
/// (<see cref="CustomerIdentityMergeJob"/>, <c>--at-twins</c>). Masaüstündeki
/// elle birleştirmenin (<c>CustomerRepository.MergeIntoGroup</c> +
/// <c>PropagateGroupBlacklist</c>) aynası: birleşen grupların BÜTÜN üyeleri tek
/// numaraya toplanır — geride üye kalırsa kişinin grubu bilgisayarlarda bölünür —
/// ve grupta kara listede biri varsa yayılır. Her yazım DAMGALIDIR
/// (<see cref="StampAt"/>): bilgisayarlar değişiklik akışıyla benimser.
/// Kaydetmez; çağıranın işleminde, izlenen örnekler üzerinde çalışır.
/// </summary>
internal static class CustomerGroupUnion
{
    /// <summary>Yazılacak damga: koşunun anı; satırın damgası milisaniye
    /// düzeyinde ondan eski değilse (saati ileri bir bilgisayarın damgası)
    /// onun 1 ms sonrası. Bilgisayarlar damgayı milisaniyeyle karşılaştırır ve
    /// yeni olmayanı yok sayar: değer değişip damga eskide kalsaydı o damgayı
    /// bilen bilgisayar farkı hiç almaz, kalıcı ayrışırdı (A3 kalite
    /// incelemesinin dersi).</summary>
    internal static DateTimeOffset StampAt(DateTimeOffset now, DateTimeOffset? current)
        => current is { } c && c.ToUnixTimeMilliseconds() >= now.ToUnixTimeMilliseconds()
            ? c.AddMilliseconds(1)
            : now;

    /// <summary>Grup numarasının karşılaştırma anahtarı: kırpılmış; boş ya da
    /// yalnız boşluksa null (grup yok). Karşılaştırma ORDİNAL — numara bir kimlik,
    /// bilgisayarlar da onu bayt bayt karşılaştırır.</summary>
    internal static string? KeyOf(string? groupId)
        => string.IsNullOrWhiteSpace(groupId) ? null : groupId.Trim();

    /// <summary>Lisansın, numarası (<see cref="KeyOf"/>) verilenlerden biri olan
    /// ASIL kayıtları — silinmişler DAHİL (kara liste kaynağı olabilirler; hedef
    /// asla). İzlenen örnekler: çağıranın işleminde yazılır. SQL süzgeci yalnız
    /// aday daraltır (kolonun varsayılan collation'ı harf farkını yok sayar);
    /// kesin eşleme bellekte, ordinal. Bellekte de asıl kayıt süzülür: aynı
    /// işlemde kopyaya çevrilmiş ama henüz kaydedilmemiş satır SQL'de hâlâ asıl
    /// görünür.</summary>
    internal static async Task<List<WpfCustomerProjection>> LoadGroupRowsAsync(
        LicenseDbContext db, Guid licenseId, IReadOnlyCollection<string> groupIds, CancellationToken ct)
    {
        if (groupIds.Count == 0) return [];
        var keys = groupIds.ToList();
        var candidates = await db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null && p.GroupId != null
                        && keys.Contains(p.GroupId.Trim()))
            .ToListAsync(ct);
        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        return candidates
            .Where(p => p.MergedIntoId is null && KeyOf(p.GroupId) is { } key && wanted.Contains(key))
            .ToList();
    }

    /// <summary>Asıl, silinmemiş ve numarası hedefle BİREBİR aynı olmayan her
    /// satırı hedefe taşır, damgasıyla. Zaten hedefte olana dokunulmaz (damga da
    /// değişmez). Taşınan satır sayısı döner.</summary>
    internal static int MoveToGroup(IEnumerable<WpfCustomerProjection> rows, string target, DateTimeOffset now)
    {
        var moved = 0;
        foreach (var row in rows.Where(r => r.MergedIntoId is null && r.PurgedAt is null
                                            && !string.Equals(r.GroupId, target, StringComparison.Ordinal)))
        {
            row.GroupId = target;
            row.GroupIdChangedAt = StampAt(now, row.GroupIdChangedAt);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Masaüstündeki <c>PropagateGroupBlacklist</c>'in (elle birleştirme kolu)
    /// aynası: <paramref name="group"/> son hâliyle TEK grup olmalı. Kaynak =
    /// kara listedeki asıl kayıtlardan en yeni BlacklistedAt'li (boşlar sona),
    /// eşitlikte küçük Id ("N" metni — masaüstüyle aynı bozma: herkes aynı
    /// kaynağı seçsin). Silinmiş satır KAYNAK olabilir (KVKK boşaltması kara
    /// listeyi korur; masaüstü de onu seçer) ama HEDEF olmaz. Kara listede
    /// olmayan her asıl, silinmemiş üye kaynağın sebebi ve tarihiyle (tarih yoksa
    /// koşunun anı) kara listeye girer, damgasıyla. Gruplama kara listeden
    /// kaçışın yolu olmasın. Kara listeye alınan satır sayısı döner.
    /// </summary>
    internal static int PropagateBlacklist(IReadOnlyCollection<WpfCustomerProjection> group, DateTimeOffset now)
    {
        var source = group.Where(r => r.MergedIntoId is null && r.IsBlacklisted)
            .OrderByDescending(r => r.BlacklistedAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.Id.ToString("N"), StringComparer.Ordinal)
            .FirstOrDefault();
        if (source is null) return 0;

        var propagated = 0;
        foreach (var row in group.Where(r => r.MergedIntoId is null && r.PurgedAt is null && !r.IsBlacklisted))
        {
            row.IsBlacklisted = true;
            row.BlacklistReason = source.BlacklistReason;
            row.BlacklistedAt = source.BlacklistedAt ?? now;
            row.BlacklistChangedAt = StampAt(now, row.BlacklistChangedAt);
            propagated++;
        }
        return propagated;
    }
}
