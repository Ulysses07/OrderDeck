using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiye (kişi havuzu) — spec 2026-10-11 §4.0. Defter satır başına kalır; havuz okumada toplanır, çekim çok
/// bacaklı yazılır (S2). Bu dosyanın VARLIĞI deploy README'nin "S1 tabanı" kontrolüdür.
///
/// <para><b>S1 (bu sınıf):</b> kişi (<see cref="MembersAsync"/>), çekim grubu okuyucuları (<see cref="IsAnchor"/>,
/// <see cref="AnchorOfAsync"/>, <see cref="GroupSumAsync"/>), grup geri alması ve toplanmış liste. S1 kip bayrağını
/// OKUMAZ ve çok bacaklı çekim YAZMAZ — para davranışı değişmez; S2'nin biçimini yalnız okur ve geri alır
/// (genişlet/daralt). <b>S2:</b> <see cref="IsPoolModeAsync"/> (kip bayrağını okur), <c>ResolveDrawSetAsync</c>,
/// <c>PlanDraw</c>, <c>DrawAsync</c>.</para>
/// </summary>
public sealed class PersonBalance
{
    public const string KindPurchaseDeduction = "purchase-deduction";
    public const string KindManualAdjustment = "manual-adjustment";
    public const string KindReversal = "reversal";

    /// <summary>Jeton çakışmasında baştan kurma denemesi (spec §4.5, §4.13); sonra 503 <c>balance-busy</c>.</summary>
    public const int MaxAttempts = 3;

    private const string BalanceRowIndex = "IX_CustomerBalances_LicenseId_WpfCustomerId";

    private readonly LicenseDbContext _db;
    private readonly CustomerIdResolver _customerIds;
    private readonly ILogger<PersonBalance> _log;

    public PersonBalance(LicenseDbContext db, CustomerIdResolver customerIds, ILogger<PersonBalance> log)
    {
        _db = db;
        _customerIds = customerIds;
        _log = log;
    }

    // ── Kişi (§2, §4.0) ──────────────────────────────────────────────────────

    /// <summary>Kişinin bir üyesi: satır bakiyesi (satır yoksa 0) ve silinmişlik.</summary>
    public sealed record Member(Guid WpfCustomerId, decimal Balance, bool Purged);

    /// <param name="CanonicalId">İstekteki Id'nin asıl kaydı (a).</param>
    /// <param name="GroupId">a'nın ham numarası; gruplu değilse de olduğu gibi.</param>
    /// <param name="Members">S(a), "N" metnine göre ordinal sıralı.</param>
    public sealed record Person(Guid CanonicalId, string? GroupId, IReadOnlyList<Member> Members);

    /// <summary>
    /// S(a) ve üye başına bakiye. <paramref name="canonicalId"/> asıl kayıt olmalı (çağıran
    /// <see cref="CustomerIdResolver"/>'dan geçirir). Projeksiyon yoksa kişi yalnız a'dır.
    /// </summary>
    public async Task<Person> MembersAsync(Guid licenseId, Guid canonicalId, CancellationToken ct)
    {
        var head = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.Id == canonicalId)
            .Select(p => new { p.GroupId, p.PurgedAt })
            .FirstOrDefaultAsync(ct);
        IReadOnlyList<PersonMembership.MemberRow> rows = head is not null && PersonMembership.IsGrouped(head.GroupId)
            ? await PersonMembership.GroupMembersAsync(_db, licenseId, head.GroupId!, ct)
            : [new PersonMembership.MemberRow(canonicalId, head?.PurgedAt is not null)];

        var ids = rows.Select(r => r.Id).ToList();
        var balances = await _db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == licenseId && ids.Contains(b.WpfCustomerId))
            .ToDictionaryAsync(b => b.WpfCustomerId, b => b.Balance, ct);
        var members = rows
            .Select(r => new Member(r.Id, balances.GetValueOrDefault(r.Id), r.Purged))
            .OrderBy(m => m.WpfCustomerId.ToString("N"), StringComparer.Ordinal)
            .ToList();
        return new Person(canonicalId, head?.GroupId, members);
    }

    /// <summary>
    /// Lisans havuz kipinde mi: <c>License.PersonBalancePoolSince</c> dolu (spec §3.1, §7.2). Kip lisans başına, her
    /// istekte okunur (§4.0); panel ve Shopper kullanır, WPF uçları sahiplik sorgusunun ikinci alanından okur.
    /// Bilinmeyen lisans satır kipidir (false).
    /// </summary>
    public Task<bool> IsPoolModeAsync(Guid licenseId, CancellationToken ct) =>
        _db.Licenses.AsNoTracking().AnyAsync(l => l.Id == licenseId && l.PersonBalancePoolSince != null, ct);

    // ── Çekim kümesi (§4.0) ──────────────────────────────────────────────────

    /// <summary>409 kodu: kişinin hiçbir üyesi sunucuda yok — kesin; yeni açılmış müşteri bekletilmez (§4.0 kural 1).</summary>
    public const string CodeNoBalance = "no-balance";

    /// <summary>
    /// 409 kodu (K2): sahip satırı sunucuda yok ya da WPF'in kişisi (W) sunucununkiyle (S) uyuşmuyor (§4.0 kural 1,
    /// 3a, 3b). Bir gönderim + çekme turuyla kapanır (§6.1-b).
    /// </summary>
    public const string CodeCustomerNotSynced = "customer-not-synced";

    /// <summary>
    /// Çekim kümesi (spec 2026-10-11 §2, §4.0-4). <c>Draw</c> = P: WPF'te (W ∩ S)'nin canlıları ∪ S'nin silinmişleri,
    /// panelde (K5) S'nin hepsi. <c>LockedNegatives</c> = S∖P'nin eksi bakiyeli üyeleri (sıfırlar yok sayılır; panelde
    /// boş): para onlardan çekilmez ama havuza sayılır ve kilitlenir — görülmeyen borç havuzu açmaz (D3, §4.13). İki
    /// liste de <c>Person.Members</c>'ın sırasında ("N" ordinal).
    /// </summary>
    /// <param name="AnchorId">a: isteğin asıl kaydı (çapanın satırı).</param>
    /// <param name="Person">S(a), <see cref="MembersAsync"/>'ten.</param>
    public sealed record DrawSet(
        Guid AnchorId, Person Person, IReadOnlyList<Member> Draw, IReadOnlyList<Member> LockedNegatives)
    {
        /// <summary>Σ = Σ_P + Σ_{S∖P, Balance &lt; 0}. Kullanılabilir = max(0, Σ).</summary>
        public decimal Pool => Draw.Sum(m => m.Balance) + LockedNegatives.Sum(m => m.Balance);
    }

    /// <param name="Code"><see cref="CodeNoBalance"/> ya da <see cref="CodeCustomerNotSynced"/>.</param>
    /// <param name="RowIds">customer-not-synced'e yol açan satırlar, "N" ordinal sıralı (§5.5 günlüğü); no-balance'ta
    /// boş.</param>
    public sealed record DrawSetRejection(string Code, IReadOnlyList<Guid> RowIds);

    /// <summary>Tam olarak biri dolu: <c>Set</c> ya da <c>Rejection</c>.</summary>
    public sealed record DrawSetResult(DrawSet? Set, DrawSetRejection? Rejection);

    /// <summary>
    /// Havuz kipinde WPF preview ve apply'ın çekim kümesi P ve 409 kuralları — spec 2026-10-11 §4.0 kural 1–4.
    /// W = <paramref name="memberIds"/> ∪ {a}, her Id asıl kayda çözülmüş; W'nin yalnız sunucuda var olan CANLI
    /// üyeleri kullanılır, silinmişler S'den alınır (K3).
    /// <list type="number">
    /// <item>a'nın projeksiyonu yoksa: W'nin hiçbir Id'si de yoksa <see cref="CodeNoBalance"/> (kesin; apply bunu 409
    /// döner, preview 200 <c>Balance = 0</c> — plan fark 19), en az biri varsa <see cref="CodeCustomerNotSynced"/>
    /// (<c>RowIds</c> = [a]).</item>
    /// <item>Sunucuda olmayan W Id'si atılır; W'nin silinmiş üyeleri yok sayılır.</item>
    /// <item>(a) W'nin S'de olmayan canlı üyesi ya da (b) S'nin W'de olmayan, bakiyesi &gt; 0 canlı üyesi varsa
    /// <see cref="CodeCustomerNotSynced"/> (<c>RowIds</c> = ikisinin birleşimi).</item>
    /// <item><see cref="DrawSet"/>: P ve S∖P'nin eksileri.</item>
    /// </list>
    /// <para>Plan fark 5: <see cref="MembersAsync"/> "a sunucuda yok"u ayırt etmez (projeksiyon yoksa S = {a}); kural 1
    /// ve 2'nin varlık ve silinmişlik bilgisi <see cref="KnownMembersAsync"/>'in tek sorgusundan gelir. Yalnız okur;
    /// iki okuma arasında üyelik değişirse yazıcı (<c>DrawAsync</c>) üyeliği kayıttan önce yeniden okur.</para>
    /// </summary>
    /// <param name="anchorId">a — asıl kayıt olmalı (çağıran <see cref="CustomerIdResolver"/>'dan geçirir).</param>
    public async Task<DrawSetResult> ResolveDrawSetAsync(
        Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var known = await KnownMembersAsync(licenseId, anchorId, memberIds, ct);

        // Kural 1: çapa yazılamaz (hareket projeksiyona FK). Hiçbir üye yoksa hiçbir üyede para da olamaz (bakiye
        // satırı projeksiyona FK): kesin no-balance.
        if (!known.ContainsKey(anchorId))
            return new DrawSetResult(null, known.Count == 0
                ? new DrawSetRejection(CodeNoBalance, [])
                : new DrawSetRejection(CodeCustomerNotSynced, [anchorId]));

        // Kural 2 + K3: sunucuda olmayan Id sözlükte yok; W'nin silinmişleri burada düşer.
        var wLive = LiveIds(known);
        var person = await MembersAsync(licenseId, anchorId, ct);

        // Kural 3: W ile S'nin CANLI üyeleri. 3a S dışından, 3b S içinden gelir: iki küme ayrık.
        var inPerson = person.Members.Select(m => m.WpfCustomerId).ToHashSet();
        var notSynced = wLive.Where(id => !inPerson.Contains(id))
            .Concat(person.Members
                .Where(m => !m.Purged && m.Balance > 0m && !wLive.Contains(m.WpfCustomerId))
                .Select(m => m.WpfCustomerId))
            .OrderBy(id => id.ToString("N"), StringComparer.Ordinal)
            .ToList();
        if (notSynced.Count > 0)
            return new DrawSetResult(null, new DrawSetRejection(CodeCustomerNotSynced, notSynced));

        return new DrawSetResult(BuildDrawSet(anchorId, person, wLive), null);
    }

    /// <summary>
    /// Panelin çekim kümesi (K5, §4.8, §4.9): P = S(a), eksiler dahil; <c>LockedNegatives</c> boş. Panel WPF üye
    /// listesi taşımaz; 409 kuralı yok.
    /// </summary>
    /// <param name="anchorId">a — asıl kayıt (rota satırı ya da hedef hareketin satırı).</param>
    public async Task<DrawSet> PersonDrawSetAsync(Guid licenseId, Guid anchorId, CancellationToken ct)
    {
        var person = await MembersAsync(licenseId, anchorId, ct);
        return new DrawSet(anchorId, person, person.Members, []);
    }

    /// <summary>
    /// Yeniden oynatmanın <c>RemainingBalance</c>'ı (§4.2; plan fark 18c): kayıtlı anahtar hiçbir zaman
    /// <see cref="CodeCustomerNotSynced"/> almaz, bu yüzden kural 1 ve 3 UYGULANMAZ. <paramref name="memberIds"/> null
    /// ya da boşsa max(0, Σ_S); değilse kural 2 ve 4'le kurulan kümenin max(0, Σ)'sı (kural 1 uygulanmaz: a sunucuda
    /// yoksa S = {a} olur ve küme boş kalır → 0; kayıtlı anahtarda çapa hareketi projeksiyona FK olduğundan bu kol
    /// pratikte oluşmaz).
    /// </summary>
    public async Task<decimal> PoolBalanceAsync(
        Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid>? memberIds, CancellationToken ct)
    {
        if (memberIds is null || memberIds.Count == 0)
        {
            var whole = await MembersAsync(licenseId, anchorId, ct);
            return Math.Max(0m, whole.Members.Sum(m => m.Balance));
        }

        var known = await KnownMembersAsync(licenseId, anchorId, memberIds, ct);
        var person = await MembersAsync(licenseId, anchorId, ct);
        return Math.Max(0m, BuildDrawSet(anchorId, person, LiveIds(known)).Pool);
    }

    /// <summary>
    /// W = <paramref name="memberIds"/> ∪ {a}, asıl kayda çözülmüş (kopya Id'si eski PC'den gelebilir); döner: W'nin
    /// sunucuda projeksiyonu olan Id'si → silinmiş mi. TEK sorgu; sorgu süzgeci kopyaları gizler — girdiler asıl
    /// kayda çözülmüş olduğundan doğru (plan fark 5).
    /// </summary>
    private async Task<Dictionary<Guid, bool>> KnownMembersAsync(
        Guid licenseId, Guid anchorId, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var canonical = await _customerIds.CanonicalOfAsync(licenseId, memberIds.Append(anchorId), ct);
        var w = canonical.Values.Append(anchorId).Distinct().ToList();
        return await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && w.Contains(p.Id))
            .Select(p => new { p.Id, Purged = p.PurgedAt != null })
            .ToDictionaryAsync(x => x.Id, x => x.Purged, ct);
    }

    /// <summary>W'nin sunucuda var olan canlı üyeleri: bilinmeyenler (kural 2) ve silinmişler (K3) düşer.</summary>
    private static HashSet<Guid> LiveIds(IReadOnlyDictionary<Guid, bool> known) =>
        known.Where(kv => !kv.Value).Select(kv => kv.Key).ToHashSet();

    /// <summary>
    /// Kural 4 (saf): P = (W ∩ S)'nin canlıları ∪ S'nin silinmişleri; S∖P'nin eksi bakiyeli üyeleri kilitlenen eksiler
    /// (sıfırlar yok sayılır). Sıra <c>person.Members</c>'ınki.
    /// </summary>
    private static DrawSet BuildDrawSet(Guid anchorId, Person person, IReadOnlySet<Guid> wLive)
    {
        var draw = person.Members
            .Where(m => m.Purged || wLive.Contains(m.WpfCustomerId))
            .ToList();
        var lockedNegatives = person.Members
            .Where(m => !m.Purged && !wLive.Contains(m.WpfCustomerId) && m.Balance < 0m)
            .ToList();
        return new DrawSet(anchorId, person, draw, lockedNegatives);
    }

    // ── Çekim planı (§4.2 adım 4, M7) ────────────────────────────────────────

    /// <summary>Planın bir satırı: o satırdan çekilecek tutar (≥ 0). Yazıcı (<c>DrawAsync</c>) <c>−Amount</c> yazar.</summary>
    public sealed record DrawLeg(Guid WpfCustomerId, decimal Amount);

    /// <summary>
    /// Çekim planı — spec 2026-10-11 §4.2 adım 4, M7 (sıra belirlenimli). Saf: veritabanı, saat, rastgelelik yok;
    /// girdi listesinin sırası sonucu değiştirmez. Her adımda o satırdan <c>min(max(0, bakiye), kalan)</c> alınır.
    /// Sıra: çapa (alıcının parası önce, K1) → silinmiş üyeler (ayırmada erişilemez kalabilecek para önce, K3) →
    /// canlı üyeler; iki grupta da bakiye büyükten küçüğe, eşitlikte Id ("N", ordinal). Silinmişler arasında eşitlik
    /// kırıcı da Id (plan fark 18b).
    ///
    /// <para>İlk eleman HER ZAMAN çapadır ve tutarı 0 olabilir: çapa satırı tutarı 0 olan bir düşüm satırıdır (§3.2).
    /// Çapa <paramref name="draw"/>'da yoksa bakiyesi 0 sayılır (bakiye satırı olmayabilir, §4.2 adım 5). Çapa dışında
    /// bakiyesi ≤ 0 olan üye ve 0 çekilen üye listede yoktur.</para>
    ///
    /// <para>Bacakların toplamı <c>min(max(0, amount), çapanın artısı + Σ artı bakiyeler)</c>. Çağıran
    /// <c>amount ≤ max(0, Σ)</c> garantisini verir (Σ, <c>S∖P</c>'nin eksilerini de içerir); burada denetlenmez,
    /// yalnız kırpılır.</para>
    /// </summary>
    /// <param name="anchorId">Çapa: istekteki (rota/sahip) satırın asıl kaydı.</param>
    /// <param name="draw">Çekim kümesi P (<c>DrawSet.Draw</c>); eksi ve sıfır üyeler içerebilir.</param>
    /// <param name="amount">Uygulanacak tutar (≥ 0 beklenir; eksi 0 sayılır).</param>
    public static IReadOnlyList<DrawLeg> PlanDraw(Guid anchorId, IReadOnlyList<Member> draw, decimal amount)
    {
        var remaining = Math.Max(0m, amount);

        var anchorBalance = draw.FirstOrDefault(m => m.WpfCustomerId == anchorId)?.Balance ?? 0m;
        var fromAnchor = Math.Min(Math.Max(0m, anchorBalance), remaining);
        var legs = new List<DrawLeg> { new(anchorId, fromAnchor) };
        remaining -= fromAnchor;

        var others = draw
            .Where(m => m.WpfCustomerId != anchorId && m.Balance > 0m)
            .OrderByDescending(m => m.Purged)
            .ThenByDescending(m => m.Balance)
            .ThenBy(m => m.WpfCustomerId.ToString("N"), StringComparer.Ordinal);
        foreach (var member in others)
        {
            if (remaining <= 0m) break;
            var take = Math.Min(member.Balance, remaining);
            legs.Add(new DrawLeg(member.WpfCustomerId, take));
            remaining -= take;
        }

        return legs;
    }

    // ── Çekim grupları (§2, §3.2) ────────────────────────────────────────────

    /// <summary>Çapa ⇔ <c>DrawGroupId IS NULL OR DrawGroupId = Id</c>. Eski tek bacaklı satır kendi çapasıdır.</summary>
    public static bool IsAnchor(CustomerBalanceTransaction t) => t.DrawGroupId is null || t.DrawGroupId == t.Id;

    /// <summary>Satırın grubu: çapanın Id'si (<c>DrawGroupId ?? Id</c>).</summary>
    public static Guid GroupKeyOf(CustomerBalanceTransaction t) => t.DrawGroupId ?? t.Id;

    /// <summary>Satırın çapası (çapaysa kendisi). Bacağın çapası yoksa (yetim bacak — <c>verify</c> sayar) null.</summary>
    public async Task<CustomerBalanceTransaction?> AnchorOfAsync(CustomerBalanceTransaction row, CancellationToken ct)
    {
        if (IsAnchor(row)) return row;
        return await _db.CustomerBalanceTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == row.DrawGroupId && t.LicenseId == row.LicenseId, ct);
    }

    /// <summary>Grubun bütün satırlarının toplamı (çekimde ≤ 0). Eski tek bacaklı satırda kendi tutarı (sorgu yok).</summary>
    public async Task<decimal> GroupSumAsync(CustomerBalanceTransaction anchor, CancellationToken ct)
    {
        if (anchor.DrawGroupId is null) return anchor.Amount;
        return await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == anchor.LicenseId && t.DrawGroupId == anchor.Id)
            .SumAsync(t => t.Amount, ct);
    }

    /// <summary>
    /// Grup olarak geri alınabilen çekim (spec §4.9, M8): düşüm ya da elle eksi grubu (çapa ≤ 0). <c>reversal</c>
    /// türündeki gruplar — çekim türü iptalin çapası/bacağı ve grup geri alma satırları — geri alınamaz
    /// (409 <c>not-reversible</c>): çok bacaklı "iptalin iptali" zinciri kapalı.
    /// </summary>
    public static bool IsGroupReversible(CustomerBalanceTransaction anchor) =>
        anchor.Amount <= 0m && anchor.Kind is KindPurchaseDeduction or KindManualAdjustment;

    // ── Grup geri alması (§4.5) ──────────────────────────────────────────────

    public enum ReverseOutcome { Reversed, AlreadyReversed, NotFound, Busy }

    /// <summary>
    /// Bir çekimin grup geri alması (spec §4.5, M12, M13). Grubun HER bacağı için bir <c>reversal</c> satırı:
    /// <c>Amount = −bacak.Amount</c>, <c>ReversesTransactionId = bacak.Id</c> (N01 bacak başına hakem), ortak
    /// <c>CreatedAt</c>; çok bacaklı grupta hepsi geri alma çapasına (<c>DrawGroupId</c> = çapanın geri alma
    /// satırının Id'si) bağlanır, eski tek bacaklı satırın geri alması bugünkü biçimde <c>DrawGroupId = NULL</c>.
    /// Açıklama yalnız çapanınkinde.
    ///
    /// <para>Para her bacağın GÜNCEL satırına eklenir (kimlik birleşmesi bacağı taşımış olabilir, §4.12); tutarı 0
    /// olan bacakta bakiye satırına dokunulmaz; satır yoksa açılır (artı tutar her zaman güvenli). Tek
    /// <c>SaveChanges</c>.</para>
    ///
    /// <para><b>Çakışma:</b> jeton (<c>DbUpdateConcurrencyException</c>), satır açma yarışı ya da kilitlenme
    /// kurbanlığında (denemenin okumaları dahil) <c>ChangeTracker.Clear()</c>; çapa, bacaklar, bacakların güncel
    /// satırları ve "geri alındı mı" taze okunur, grup baştan kurulur; en çok <see cref="MaxAttempts"/> deneme, sonra
    /// <see cref="ReverseOutcome.Busy"/>. Bugünkü "yeniden yükle, deltayı yeniden uygula" kalıbı çok satırlı kayıtta
    /// YANLIŞ: EF yalnız çakışan girdiyi döndürür, öbür satırlar bellekteki <c>eski + delta</c>'yı korur ve delta
    /// onlara iki kez eklenirdi. N01 ihlali (eşzamanlı geri alma) bütün işlemi geri alır →
    /// <see cref="ReverseOutcome.AlreadyReversed"/>; istemci başarı sayar. Para yalnız eklendiği için eksi denetimi
    /// ve kişi kilidi gerekmez.</para>
    ///
    /// <para><b>Bacak sahibinin ikinci okuması (M12'nin tamamlayıcısı):</b> birleştirici bacağı asıl kayda taşımayı
    /// ve kopyanın (sıfır bakiyeli de olsa) satırını damgalamayı TEK işlemde yapar. Damga geri almayı ancak kopyanın
    /// jetonu birleşmeden ÖNCE okunduysa korur; bacak okunduktan sonra, bakiye satırı okunmadan önce commit eden bir
    /// birleşmede jeton birleşme sonrasınındır, UPDATE tutar ve para kopyaya yazılırdı. Bu yüzden bakiye satırları
    /// okunduktan SONRA, kayıttan önce bacakların güncel sahipleri yeniden okunur; biri değiştiyse grup baştan kurulur
    /// (deneme sayılır). Değişmediyse okunan jetonlar birleşmeden öncedir; sonra gelen birleşme kayıtta çakışır. Kalan
    /// dar pencere jetonsuz yazımlardadır: bu ikinci okumayla kayıt arasında birleşen bir sahip, açılacak (henüz var
    /// olmayan) bir bakiye satırına ya da tutarı 0 olan bir bacağa denk gelirse geri alma satırı kopyada kalabilir —
    /// birleştiricinin kendi penceresi gibi (§4.12), <c>verify</c> "kopya satırda hareket ya da bakiye" diye sayar.</para>
    ///
    /// <para>Çağıran DbContext'te izlenen başka değişiklik bırakmamalı: denemeler arasında izleyici temizlenir.</para>
    /// </summary>
    /// <param name="anchorId">Çapanın Id'si (çağıran bacağı <see cref="AnchorOfAsync"/> ile çözer).</param>
    public async Task<ReverseOutcome> ReverseGroupAsync(
        Guid licenseId, Guid anchorId, Guid createdByCustomerId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1) _db.ChangeTracker.Clear();

            // try denemenin OKUMALARINI da kapsar: yazarlar N01 anahtarlarında kilitlenince (aşağıda) aynı anahtarın
            // kuyruğunda bekleyen hızlı yol okuması da döngüye girer ve en ucuz kurban odur (1205). Okumalar hiçbir
            // şey yazmaz; yeniden denemek güvenli.
            string retryReason;
            try
            {
                var anchor = await _db.CustomerBalanceTransactions.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == anchorId && t.LicenseId == licenseId, ct);
                if (anchor is null || !IsAnchor(anchor)) return ReverseOutcome.NotFound;
                // Hızlı yol; asıl hakem N01 indeksi (aşağıdaki catch).
                if (await _db.CustomerBalanceTransactions.AnyAsync(t => t.ReversesTransactionId == anchorId, ct))
                    return ReverseOutcome.AlreadyReversed;

                // Id sırası yalnız okumayı (ve hazırlanan satırları) belirlenimli kılar; INSERT'lerin SQL sırası garanti
                // DEĞİL (EF eklenen girdileri anahtarla sıralamaz, çok satırlı INSERT planı rastgele reversal Id'sine
                // göre dizebilir). Eşzamanlı iki grup geri alması N01 anahtarlarını ters sırayla kilitleyip 1205
                // alabilir: onu aşağıdaki IsDeadlock dalı karşılar (deneme → hızlı yol → AlreadyReversed). O dal gerekli.
                List<CustomerBalanceTransaction> legs = anchor.DrawGroupId is null
                    ? [anchor]
                    : await _db.CustomerBalanceTransactions.AsNoTracking()
                        .Where(t => t.LicenseId == licenseId && t.DrawGroupId == anchorId)
                        .OrderBy(t => t.Id)
                        .ToListAsync(ct);
                await StageGroupReversalAsync(licenseId, anchor, legs, createdByCustomerId, ct);

                if (await LegOwnersChangedAsync(licenseId, legs, ct))
                {
                    // Bakiye satırları okunmadan önce birleşme oldu: okunan jeton birleşme sonrasının, koruma yok.
                    retryReason = "bacak taşındı (kimlik birleşmesi)";
                }
                else
                {
                    await _db.SaveChangesAsync(ct);
                    if (legs.Count > 1)
                        _log.LogInformation(
                            "Ortak bakiye: çok bacaklı çekim grup olarak geri alındı ({Legs} bacak, license={LicenseId}, anchor={AnchorId})",
                            legs.Count, licenseId, anchorId);
                    return ReverseOutcome.Reversed;
                }
            }
            // Sıra önemli: DbUpdateConcurrencyException, DbUpdateException'dan türer.
            catch (DbUpdateConcurrencyException)
            {
                retryReason = "jeton çakışması";
            }
            catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
            {
                _db.ChangeTracker.Clear();
                return ReverseOutcome.AlreadyReversed;
            }
            catch (DbUpdateException ex) when (IsDuplicateBalanceRow(ex))
            {
                // Bacağın satırını eşzamanlı biri açtı (işlem bütünüyle geri alındı).
                retryReason = "bakiye satırı açma yarışı";
            }
            // Exception, DbUpdateException değil: varsayılan yürütme stratejisi 1205'i sarar, satır okunurken gelen
            // 1205 ise çıplak gelir (bkz. IsDeadlock).
            catch (Exception ex) when (IsDeadlock(ex))
            {
                // Kilitlenme kurbanı (kayıtta işlem bütünüyle geri alındı; okumada yazılan bir şey yok).
                retryReason = "kilitlenme (1205)";
            }

            // Hiçbir şey yazılmadı; grup taze okumayla baştan kurulur (döngü başı izleyiciyi temizler).
            if (attempt >= MaxAttempts)
            {
                _db.ChangeTracker.Clear();
                // balance-busy'nin TEK uyarı satırı (denetleyici yalnız Debug yazar).
                _log.LogWarning(
                    "Ortak bakiye: grup geri alması {Attempts} denemede kurulamadı, son neden: {Reason}; balance-busy (license={LicenseId}, anchor={AnchorId})",
                    MaxAttempts, retryReason, licenseId, anchorId);
                return ReverseOutcome.Busy;
            }
            _log.LogInformation(
                "Ortak bakiye: grup geri alması {Attempt}. denemede kurulamadı ({Reason}), baştan kuruluyor (license={LicenseId}, anchor={AnchorId})",
                attempt, retryReason, licenseId, anchorId);
        }
    }

    private async Task StageGroupReversalAsync(
        Guid licenseId, CustomerBalanceTransaction anchor, IReadOnlyList<CustomerBalanceTransaction> legs,
        Guid createdByCustomerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var reversalAnchorId = Guid.NewGuid();
        Guid? drawGroupId = anchor.DrawGroupId is null ? null : reversalAnchorId;
        foreach (var leg in legs)
        {
            var isAnchor = leg.Id == anchor.Id;
            var amount = -leg.Amount;
            _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = isAnchor ? reversalAnchorId : Guid.NewGuid(),
                LicenseId = licenseId,
                WpfCustomerId = leg.WpfCustomerId,
                Amount = amount,
                Kind = KindReversal,
                Reason = isAnchor ? $"Reverse of {anchor.Id:N}" : null,
                ReversesTransactionId = leg.Id,
                DrawGroupId = drawGroupId,
                CreatedByCustomerId = createdByCustomerId,
                CreatedAt = now,
            });
            if (amount == 0m) continue;

            // Aynı çekimin iki bacağı birleşmeden sonra aynı satıra düşebilir: önce izlenenlerde (eklenmiş dahil).
            var balance = _db.CustomerBalances.Local
                              .FirstOrDefault(b => b.LicenseId == licenseId && b.WpfCustomerId == leg.WpfCustomerId)
                          ?? await _db.CustomerBalances
                              .FirstOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == leg.WpfCustomerId, ct);
            if (balance is null)
            {
                balance = new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = leg.WpfCustomerId,
                    Balance = 0m, UpdatedAt = now,
                };
                _db.CustomerBalances.Add(balance);
            }
            balance.Balance += amount;
            balance.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Bacaklardan birinin güncel sahibi (<c>WpfCustomerId</c>) hazırlanan geri almadakinden farklı mı, ya da bacak
    /// artık yok mu. <see cref="StageGroupReversalAsync"/> bakiye satırlarını okuduktan SONRA çağrılır (bkz.
    /// <see cref="ReverseGroupAsync"/>, "bacak sahibinin ikinci okuması"). Tek sorgu.
    /// </summary>
    private async Task<bool> LegOwnersChangedAsync(
        Guid licenseId, IReadOnlyList<CustomerBalanceTransaction> legs, CancellationToken ct)
    {
        var ids = legs.Select(l => l.Id).ToList();
        var owners = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && ids.Contains(t.Id))
            .Select(t => new { t.Id, t.WpfCustomerId })
            .ToDictionaryAsync(t => t.Id, t => t.WpfCustomerId, ct);
        return legs.Any(l => !owners.TryGetValue(l.Id, out var owner) || owner != l.WpfCustomerId);
    }

    // ── Toplanmış liste (§4.6, §4.10, M9) ────────────────────────────────────

    /// <summary>Listedeki bir grup.</summary>
    /// <param name="Id">Çapanın Id'si (eski satırda kendi Id'si).</param>
    /// <param name="AnchorWpfCustomerId">Çapanın satırı — Shopper'ın açıklama maskesi (K4) için.</param>
    /// <param name="Amount">Grubun verilen satırlardaki bacaklarının toplamı.</param>
    public sealed record HistoryEntry(
        Guid Id, Guid AnchorWpfCustomerId, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, Guid? ReversesTransactionId, DateTimeOffset CreatedAt);

    /// <summary>
    /// Hareketleri gruba göre toplar (spec §4.6; saf). Aynı <c>DrawGroupId ?? Id</c>'li satırlar tek satır olur;
    /// tutar verilen satırların toplamı (ayırmadan sonra öbür kişide kalan bacaklar sayılmaz — listedeki tutarların
    /// toplamı her zaman bakiyeye eşit); tür, tutarlar, açıklama ve iptal bağı çapadan. Çapası bulunmayan (yetim)
    /// bacak kendi alanlarıyla listelenir; <c>verify</c> onu ayrıca sayar. Yeniden eskiye sıralı; bir grubun bütün
    /// satırları aynı <c>CreatedAt</c>'le yazıldığı için grup bölünmez.
    /// </summary>
    /// <param name="rows">Listelenecek satırların hareketleri.</param>
    /// <param name="anchors">Grup anahtarı → çapa (çapa başka bir satırda olabilir).</param>
    public static IReadOnlyList<HistoryEntry> AggregateHistory(
        IEnumerable<CustomerBalanceTransaction> rows, IReadOnlyDictionary<Guid, CustomerBalanceTransaction> anchors) =>
        rows
            .GroupBy(GroupKeyOf)
            .Select(g =>
            {
                var anchor = anchors.TryGetValue(g.Key, out var found) ? found : g.OrderBy(r => r.Id).First();
                return new HistoryEntry(
                    g.Key, anchor.WpfCustomerId, g.Sum(r => r.Amount), anchor.Kind, anchor.OriginalAmount,
                    anchor.ShippingDeducted, anchor.Reason, anchor.ReversesTransactionId, g.Max(r => r.CreatedAt));
            })
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToList();

    /// <summary>
    /// Verilen satırların son <paramref name="take"/> GRUBU, toplanmış (§4.6). Satır kipinde tek satır; havuz kipinde
    /// (S2) S'nin bütün üyeleri.
    /// </summary>
    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(
        Guid licenseId, IReadOnlyCollection<Guid> rowIds, int take, CancellationToken ct)
    {
        var ids = rowIds.ToList();
        var keys = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && ids.Contains(t.WpfCustomerId))
            .GroupBy(t => t.DrawGroupId ?? t.Id)
            .Select(g => new { g.Key, At = g.Max(t => t.CreatedAt) })
            .OrderByDescending(x => x.At)
            .ThenByDescending(x => x.Key)
            .Take(take)
            .Select(x => x.Key)
            .ToListAsync(ct);
        if (keys.Count == 0) return [];

        var rows = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && ids.Contains(t.WpfCustomerId) && keys.Contains(t.DrawGroupId ?? t.Id))
            .ToListAsync(ct);
        var anchors = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && keys.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        return AggregateHistory(rows, anchors);
    }

    /// <summary>Verilen satırların son para hareketinin anı (M9): kilit dokunuşu "güncellendi" zamanını oynatmaz.</summary>
    public async Task<DateTimeOffset?> LastMovementAtAsync(
        Guid licenseId, IReadOnlyCollection<Guid> rowIds, CancellationToken ct)
    {
        var ids = rowIds.ToList();
        return await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && ids.Contains(t.WpfCustomerId))
            .MaxAsync(t => (DateTimeOffset?)t.CreatedAt, ct);
    }

    // ── Hata tanıma ──────────────────────────────────────────────────────────

    /// <summary>N01 hakemi: 2601/2627 + <c>ReversesTransactionId</c> indeksinin adı (öbür tekil yarışlar karışmasın).</summary>
    public static bool IsDuplicateReversal(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains("ReversesTransactionId", StringComparison.Ordinal);

    /// <summary>Bakiye satırı açma yarışı: tekil <c>(LicenseId, WpfCustomerId)</c> indeksi.</summary>
    public static bool IsDuplicateBalanceRow(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(BalanceRowIndex, StringComparison.Ordinal);

    /// <summary>
    /// SQL Server kilitlenme kurbanı (1205): işlem bütünüyle geri alındı, yeniden kurmak güvenli. 1205 geçici bir hata
    /// sayıldığı için varsayılan yürütme stratejisi (<c>UseSqlServer</c>, <c>EnableRetryOnFailure</c> yok) onu
    /// <see cref="DbUpdateException"/> olarak DEĞİL, <see cref="InvalidOperationException"/>'a sarılı ("likely due to a
    /// transient failure") fırlatır; yalnız <see cref="DbUpdateException"/>'a bakan bir süzgeç hiç tutmaz ve kurban 500
    /// alırdı (Testcontainers §8.2-3'te görüldü). Kabul edilen biçimler:
    /// <list type="bullet">
    /// <item>kayıt: <c>IOE → DbUpdateException → SqlException</c> ve çıplak <c>DbUpdateException → SqlException</c>
    /// (sarmalamayan bir stratejiyle gelen biçim);</item>
    /// <item>okuma: sorgu komutu çalışırken <c>IOE → SqlException</c> (strateji sarar), satırlar okunurken çıplak
    /// <c>SqlException</c> (<c>ReadAsync</c> stratejinin dışında; yarış testinde hızlı yol okumasında görüldü).</item>
    /// </list>
    /// </summary>
    public static bool IsDeadlock(Exception ex)
    {
        var inner = ex is InvalidOperationException { InnerException: { } wrapped } ? wrapped : ex;
        if (inner is DbUpdateException { InnerException: { } store }) inner = store;
        return inner is SqlException { Number: 1205 };
    }
}
