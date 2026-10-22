using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Sunucu kişisinin üye kümesinin TEK kaynağı (ortak bakiye spec 2026-10-11 §2, §4.13; §15-5): ödeme isteği kirası
/// (<c>PaymentPersonResolver.ServerGroupAsync</c>) ve ortak bakiye (<see cref="PersonBalance.MembersAsync"/>) aynı
/// fonksiyonu çağırır — satış senkronunun PersonKey'i bu spec'in S(a) kişisidir.
///
/// <para><b>Gruplu kuralı</b> masaüstündeki <c>CustomerGroup.IsGrouped</c> ile aynı: boş ya da yalnız boşluktan
/// oluşan numara gruplanmamış demektir. <b>Eşitlik ordinal, ham değer üzerinde</b> (kırpma ve harf katlaması yok):
/// SQL aday daraltır (GroupId BIN2; '=' sondaki boşluğu yine yutar), kesin eşleme bellekte.</para>
/// </summary>
public static class PersonMembership
{
    /// <summary>Kişinin bir üyesi: asıl kayıt Id'si ve KVKK ile silinmiş mi.</summary>
    public sealed record MemberRow(Guid Id, bool Purged);

    public static bool IsGrouped(string? groupId) => !string.IsNullOrWhiteSpace(groupId);

    /// <summary>Lisansın, numarası <paramref name="groupId"/> ile ordinal eşit asıl kayıtları (silinmişler dahil,
    /// kopyalar hariç — varsayılan sorgu süzgeci).</summary>
    public static async Task<IReadOnlyList<MemberRow>> GroupMembersAsync(
        LicenseDbContext db, Guid licenseId, string groupId, CancellationToken ct)
    {
        var rows = await db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null && p.GroupId == groupId)
            .Select(p => new { p.Id, p.GroupId, p.PurgedAt })
            .ToListAsync(ct);
        return rows
            .Where(r => string.Equals(r.GroupId, groupId, StringComparison.Ordinal))
            .Select(r => new MemberRow(r.Id, r.PurgedAt is not null))
            .ToList();
    }
}
