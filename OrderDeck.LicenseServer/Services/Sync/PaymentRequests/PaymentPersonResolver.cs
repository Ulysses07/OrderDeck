using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

/// <summary>Bir ödeme isteğinin kişisi (§2 PersonKey, §6.4-b).</summary>
/// <param name="OwnerWpfCustomerId">Asıl kayda çözülmüş sahip ("N" hex) ya da GUID'siz sahibin ham Id'si.</param>
/// <param name="GroupId">Sahibin asıl kaydı gruplu ise ham <c>GroupId</c>'si; değilse null.</param>
/// <param name="Members">İstemcinin üyeleri: GUID'liler asıl kayda çözülmüş "N" hex, GUID'sizler ham; ordinal tekil ve
/// sıralı.</param>
public sealed record PaymentPerson(
    string PersonKey,
    byte[] PersonKeyHash,
    string OwnerWpfCustomerId,
    string? GroupId,
    IReadOnlyList<string> Members);

/// <summary>
/// Ödeme isteğinin kişisini kurar — <c>begin</c> ve <c>backfill</c> aynı yoldan (spec 2026-10-13 §2 PersonKey, §6.4-b):
/// iki uçta aynı kişi aynı anahtarı ve özeti alır, öbür bilgisayarın <c>begin</c>'i geri doldurulmuş kaydı bulur.
/// <list type="bullet">
///   <item>GUID'li üyeler ve sahip asıl kayda çözülür (<see cref="CustomerIdResolver"/>), GUID'siz üyeler ham kalır.</item>
///   <item>Anahtar sahibin asıl kaydının ham <c>GroupId</c>'sinden (<see cref="PaymentPersonKey"/>).</item>
///   <item>Sunucunun grubu (<see cref="ServerGroupAsync"/>): sahibin grubundaki asıl kayıtlar, ordinal eşitlikle.</item>
/// </list>
/// </summary>
public sealed class PaymentPersonResolver
{
    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;

    public PaymentPersonResolver(LicenseDbContext db, CustomerIdResolver customerIds)
    {
        _db = db;
        _customerIds = customerIds;
    }

    /// <summary>
    /// Üyeler asıl kayda çözülmüş hâlde: GUID'li ("N" hex) üye asıl kaydın küçük harf "N" hex'i, GUID'siz üye ham; ordinal
    /// tekil ve sıralı.
    /// </summary>
    public async Task<IReadOnlyList<string>> CanonicalMembersAsync(
        Guid licenseId, IEnumerable<string> memberIds, CancellationToken ct)
    {
        var ids = memberIds.ToList();
        var canonical = await CanonicalOfAsync(licenseId, ids, ct);
        return Canonicalize(ids, canonical);
    }

    /// <summary>
    /// Kişi: üyeler asıl kayda çözülür, anahtar sahibin asıl kaydının ham <c>GroupId</c>'sinden. Sahip GUID'siz ise
    /// <c>r:{ham Id}</c>. <paramref name="ownerId"/> <paramref name="memberIds"/>'te olmalı (uçlar doğrular).
    /// </summary>
    public async Task<PaymentPerson> ResolveAsync(
        Guid licenseId, string ownerId, IEnumerable<string> memberIds, CancellationToken ct)
    {
        var ids = memberIds.Append(ownerId).ToList();
        var canonical = await CanonicalOfAsync(licenseId, ids, ct);
        var members = Canonicalize(memberIds, canonical);

        if (!PaymentPersonKey.TryParseGuid(ownerId, out var ownerGuid))
        {
            var rawKey = PaymentPersonKey.ForRaw(ownerId);
            return new PaymentPerson(rawKey, PaymentPersonKey.Hash(rawKey), ownerId, null, members);
        }

        var owner = canonical[ownerGuid];
        var groupId = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.Id == owner)
            .Select(p => p.GroupId)
            .FirstOrDefaultAsync(ct);
        var key = PaymentPersonKey.ForCanonical(owner, groupId);
        return new PaymentPerson(
            key, PaymentPersonKey.Hash(key), owner.ToString("N"),
            PaymentPersonKey.IsGrouped(groupId) ? groupId : null, members);
    }

    /// <summary>
    /// Sahibin grubundaki asıl kayıtlar ("N" hex, ordinal sıralı): lisansın kopya olmayan projeksiyonlarından
    /// <c>GroupId</c>'si sahibinkiyle ORDINAL eşit olanlar. Kolon BIN2: SQL harf farkını ayırır ama sondaki boşluğu yine
    /// yutarak getirir; kesin eşitlik bellekte, ordinal denetlenir (ortak bakiye §4.13'ün yöntemi).
    /// Gruplanmamış sahipte boş. Sorgu <see cref="PersonMembership"/>'te; ortak bakiye aynı kaynağı kullanır (§15-5).
    /// </summary>
    public async Task<IReadOnlyList<string>> ServerGroupAsync(Guid licenseId, string? groupId, CancellationToken ct) =>
        (await ServerGroupMembersAsync(licenseId, groupId, ct)).Select(m => m.Id).ToList();

    /// <summary>
    /// <see cref="ServerGroupAsync"/>'in üyeleri, KVKK silinmişlik bilgisiyle (aynı sorgu, aynı sıra). Kiranın (b) adımı
    /// yalnız canlı üyeleri ister (ortak bakiye §15-7, §4.0-3); arama ve kayıt silinmişleri de taşır.
    /// </summary>
    public async Task<IReadOnlyList<ServerMember>> ServerGroupMembersAsync(
        Guid licenseId, string? groupId, CancellationToken ct)
    {
        if (groupId is null) return [];
        var rows = await PersonMembership.GroupMembersAsync(_db, licenseId, groupId, ct);
        return rows
            .Select(r => new ServerMember(r.Id.ToString("N"), r.Purged))
            .DistinctBy(m => m.Id, StringComparer.Ordinal)
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Sunucu grubunun bir üyesi: asıl kaydın "N" hex Id'si ve KVKK ile silinmiş mi.</summary>
    public sealed record ServerMember(string Id, bool Purged);

    /// <summary>
    /// Kayıt aramasının üye kümesi: verilen üyeler ve GUID'li üyelerin kopyaları ("N" hex), ordinal tekil.
    ///
    /// <para>Kayıt üyeleri yazıldıkları andaki asıl kayıtla saklanır ve birleştirme onları yeniden anahtarlamaz
    /// (<see cref="CustomerIdentityMerger"/> ödeme isteği kayıtlarına dokunmaz). t1'e yazılmış kayıt, t1 t2'ye
    /// birleştirildikten sonra t2'nin kopyalarıyla bulunur. Kayıt sahibini daima üye olarak taşıdığı için PersonKey'i
    /// <c>c:{t1}</c> olan kayıt da üye kesişimiyle bulunur. Yeniden anahtarlamak yerine okuma genişletilir: tekil
    /// <c>Sending</c> indeksi ve birincil anahtar birleştirmede çakışabilirdi.</para>
    /// </summary>
    /// <param name="members">Asıl kayda çözülmüş üyeler (<see cref="CanonicalMembersAsync"/>, <see cref="ResolveAsync"/>).</param>
    public async Task<IReadOnlyList<string>> WithCopiesAsync(
        Guid licenseId, IReadOnlyList<string> members, CancellationToken ct)
    {
        var guids = members
            .Select(m => PaymentPersonKey.TryParseGuid(m, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToList();
        if (guids.Count == 0) return members;
        var copies = await _customerIds.CopiesOfAsync(licenseId, guids, ct);
        if (copies.Count == 0) return members;
        return members
            .Concat(copies.Select(c => c.ToString("N")))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Kaydın sahibi bugün isteğin sahibiyle aynı kişi mi (geri doldurmanın yeniden oynatması; bakiye apply'ın A5b
    /// deseni). <paramref name="storedOwner"/> kaydın yazıldığı andaki asıl kayıttır ve birleştirme onu yeniden
    /// anahtarlamaz; bu yüzden ikisi de BUGÜNKÜ asıl kayda çözülerek karşılaştırılır. GUID'siz sahip yalnız ordinal
    /// eşitlikle aynıdır.
    /// </summary>
    /// <param name="storedOwner">Kaydın <c>OwnerWpfCustomerId</c>'si.</param>
    /// <param name="canonicalOwner">İsteğin asıl kayda çözülmüş sahibi (<see cref="PaymentPerson.OwnerWpfCustomerId"/>).</param>
    public async Task<bool> IsSameOwnerAsync(
        Guid licenseId, string storedOwner, string canonicalOwner, CancellationToken ct)
    {
        if (string.Equals(storedOwner, canonicalOwner, StringComparison.Ordinal)) return true;
        if (!PaymentPersonKey.TryParseGuid(storedOwner, out var stored)
            || !PaymentPersonKey.TryParseGuid(canonicalOwner, out var owner))
            return false;
        return await _customerIds.CanonicalOfAsync(licenseId, stored, ct) == owner;
    }

    /// <summary>
    /// Saklanacak üye satırları. Birincil anahtar <c>(RecordId, WpfCustomerId)</c> varsayılan (harf duyarsız) harmanda:
    /// ordinal farklı ama SQL'de eşit iki GUID'siz ham Id (yalnız harf ya da sondaki boşlukla ayrılan) aynı kayda iki satır
    /// olarak yazılamaz — 500 yerine her çiftin ordinal ilki saklanır (<see cref="StorageKey"/>). GUID'li üyeler küçük harf
    /// "N" hex olduğundan etkilenmez.
    /// </summary>
    public static List<string> StorableMembers(IEnumerable<string> members) =>
        members
            .GroupBy(StorageKey, StringComparer.Ordinal)
            .Select(g => g.Order(StringComparer.Ordinal).First())
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Üye satırının birincil anahtardaki eşitlik sınıfı: varsayılan harmanın yuttuğu harf ve sondaki boşluk
    /// farkı katlanır.</summary>
    public static string StorageKey(string member) => member.TrimEnd(' ').ToUpperInvariant();

    private Task<IReadOnlyDictionary<Guid, Guid>> CanonicalOfAsync(
        Guid licenseId, IEnumerable<string> ids, CancellationToken ct) =>
        _customerIds.CanonicalOfAsync(
            licenseId,
            ids.Select(m => PaymentPersonKey.TryParseGuid(m, out var id) ? id : (Guid?)null).OfType<Guid>(),
            ct);

    private static List<string> Canonicalize(IEnumerable<string> memberIds, IReadOnlyDictionary<Guid, Guid> canonical) =>
        memberIds
            .Select(m => PaymentPersonKey.TryParseGuid(m, out var id) ? canonical[id].ToString("N") : m)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
