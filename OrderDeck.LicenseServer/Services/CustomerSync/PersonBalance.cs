using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiye (kişi havuzu) — spec 2026-10-11 §4.0. Defter satır başına kalır; havuz okumada toplanır, çekim çok
/// bacaklı yazılır (S2). Bu dosyanın VARLIĞI deploy README'nin "S1 tabanı" kontrolüdür.
///
/// <para><b>S1:</b> kişi (<see cref="MembersAsync"/>), çekim grubu okuyucuları (<see cref="IsAnchor"/>,
/// <see cref="AnchorOfAsync"/>, <see cref="GroupSumAsync"/>), grup geri alması ve toplanmış liste; S2'nin biçimini
/// okur ve geri alır (genişlet/daralt). <b>S2:</b> kip bayrağı (<see cref="IsPoolModeAsync"/>), çekim kümesi
/// (<see cref="ResolveDrawSetAsync"/>, <see cref="PersonDrawSetAsync"/>), plan (<see cref="PlanDraw"/>) ve çok bacaklı
/// çekim yazımı (<see cref="DrawAsync"/>; kişi kilidi, üyeliğin ikinci okuması, baştan kurma). <see cref="DrawAsync"/>'i
/// yalnız havuz kipindeki uçlar çağırır: kip kapalı lisansta para davranışı S1'deki gibidir.</para>
/// </summary>
public sealed class PersonBalance
{
    public const string KindPurchaseDeduction = "purchase-deduction";
    public const string KindManualAdjustment = "manual-adjustment";
    public const string KindReversal = "reversal";

    /// <summary>Jeton çakışmasında baştan kurma denemesi (spec §4.5, §4.13); sonra 503 <c>balance-busy</c>.</summary>
    public const int MaxAttempts = 3;

    private const string BalanceRowIndex = "IX_CustomerBalances_LicenseId_WpfCustomerId";

    /// <summary>Hareketin PK'sı (göç <c>20260602125823_AddCustomerBalance</c>): çapanın Id'si istemcinin anahtarıdır.</summary>
    private const string TransactionPrimaryKey = "PK_CustomerBalanceTransactions";

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

    // ── Çekim (§4.2, §4.8, §4.9, §4.13) ──────────────────────────────────────

    /// <summary>
    /// Tutar kuralı. <c>UpToPool</c> — WPF apply (ve ileride banka): <c>Σ ≤ 0</c> → <c>NoBalance</c>,
    /// <c>applied = min(Requested, Σ)</c>. <c>AllOrNothing</c> — panel K5 (elle eksi, artı hareketin iptali):
    /// <c>max(0, Σ) &lt; Requested</c> → <c>Insufficient</c>, kısmi çekim yok (spec §4.8, §4.9).
    /// </summary>
    public enum DrawAmountRule { UpToPool, AllOrNothing }

    /// <summary>Bir çekim (spec §4.2 adım 5, §3.2 satır biçimleri; K5 §4.8, §4.9).</summary>
    /// <param name="AnchorRowId">Çapanın satırı (a), asıl kayıt: WPF'te istekteki satır, panelde rota satırı ya da
    /// iptal edilen hareketin satırı.</param>
    /// <param name="AnchorTransactionId">Çapanın Id'si = grubun <c>DrawGroupId</c>'si (tek bacaklıda da).</param>
    /// <param name="AnchorIdIsIdempotencyKey">true: Id istemcinin anahtarı; PK ihlali <c>DuplicateKey</c> döner, çağıran
    /// kazananı oynatır. false: sunucunun ürettiği Id; PK ihlali yakalanmaz.</param>
    /// <param name="Requested">Çekilecek tutar, &gt; 0 ve en çok 2 ondalık (apply: <c>min(Amount, ProductTotal)</c>,
    /// ikisi de <c>invalid-money-scale</c> ile doğrulanmış; K5: <c>|tutar|</c>). Kuruş altı tutar
    /// <see cref="ArgumentOutOfRangeException"/> atar: <c>decimal(18,2)</c> bacağın <c>Amount</c>'unu ve satırın
    /// bakiyesini ayrı ayrı yuvarlar, D1 bozulurdu.</param>
    /// <param name="MemberIds">WPF'in üye listesi (W): dolu → <see cref="ResolveDrawSetAsync"/>; null →
    /// <see cref="PersonDrawSetAsync"/> (panel, P = S).</param>
    /// <param name="OriginalAmount">Yalnız çapaya yazılır (§3.2, D4); <paramref name="SaleScope"/>,
    /// <paramref name="Reason"/> ve <paramref name="ReversesTransactionId"/> da öyle.</param>
    /// <param name="ScopeLockResource">Dolu → her deneme kendi işlemini açar ve <c>SalesCommandLock</c>'u alır (kapsam
    /// kuralı, satış senkronu §6.6; plan fark 7). <c>SalesCommandRejection</c> çağırana yükselir.</param>
    public sealed record DrawRequest(
        Guid LicenseId, Guid AnchorRowId, Guid AnchorTransactionId, bool AnchorIdIsIdempotencyKey, string Kind,
        decimal Requested, DrawAmountRule Rule, IReadOnlyCollection<Guid>? MemberIds, decimal? OriginalAmount,
        string? SaleScope, string? Reason, Guid? ReversesTransactionId, Guid CreatedByCustomerId, string? ScopeLockResource);

    /// <summary>
    /// <c>Stopped</c>: geri çağrı durdurdu (ör. Lookup kaydı buldu). <c>NoBalance</c>: kural 1'in kesin kolu ya da
    /// UpToPool'da <c>Σ ≤ 0</c>. <c>CustomerNotSynced</c>: kümenin kural 1/3a/3b reddi ya da kural 3b'nin izlenen
    /// satırlarla taze denetimi (S∖P'nin eksi üyesi artıya geçmiş). <c>AlreadyReversed</c>: N01 ihlali (çekim türü
    /// iptal). <c>DuplicateKey</c>: aynı anahtarla yarış (PK). <c>Busy</c>: <see cref="MaxAttempts"/> denemede
    /// kurulamadı (503 <c>balance-busy</c>).
    /// </summary>
    public enum DrawStatus
    {
        Drawn, Stopped, NoBalance, NothingToApply, Insufficient, CustomerNotSynced, AlreadyReversed, DuplicateKey, Busy,
    }

    /// <param name="Applied">Bütün bacakların toplamı (&gt; 0 yalnız <c>Drawn</c>'da).</param>
    /// <param name="Pool">Planın Σ'sı, izlenen satırlardan, eksiler dahil (§4.0-4); Σ kurulmadıysa 0.</param>
    /// <param name="Legs">Yazılan hareket satırı sayısı, çapa dahil (<c>Drawn</c> dışında 0).</param>
    /// <param name="RejectedRowIds">Yalnız <c>CustomerNotSynced</c>'te dolu (§5.5 günlüğü, Görev 5).</param>
    public sealed record DrawResult(
        DrawStatus Status, Guid AnchorTransactionId, decimal Applied, decimal Pool, int Legs, IReadOnlyList<Guid> RejectedRowIds);

    /// <summary>
    /// Havuz kipindeki bütün para çeken yazımların tek yolu (spec §4.2 adım 1–7; K5 §4.8, §4.9): WPF apply, panel elle
    /// eksi ve artı hareketin iptali (ileride banka). Para yalnız P'den çekilir; S∖P'nin eksileri havuza sayılır ve
    /// kilitlenir, çekilmez (D3). Plan <see cref="PlanDraw"/>'dan; yazım tek <c>SaveChanges</c> (§3.2: ortak
    /// <c>CreatedAt</c>, açıklama/kapsam/iptal bağı yalnız çapada, tek bacaklıda da <c>DrawGroupId = Id</c>).
    ///
    /// <para><b>Deneme</b> (her biri baştan; 2.+ denemede <c>ChangeTracker.Clear()</c>): kapsam kilidi (varsa, kendi
    /// işleminde) → <paramref name="beforeAttempt"/> (çağıranın Lookup'ı ve kapsam yüklemi; true → <c>Stopped</c>) →
    /// çekim kümesi → P ve S∖P eksilerinin bakiye satırları İZLENEN nesneler olarak (tek sorgu) → <b>üyeliğin ikinci
    /// okuması</b> (S(a) ve numara; fark → baştan, deneme sayılır — §4.2-2, §4.11, plan fark 18a) → kural 3b izlenen
    /// satırlarla (S∖P'nin eksi üyesi artıya geçtiyse <c>CustomerNotSynced</c>, §4.0-3b) → Σ izlenen satırlardan →
    /// tutar kuralı → plan → yazım. Çapanın bakiye satırı yoksa 0 ile açılır (§4.2-5, §4.12).</para>
    ///
    /// <para><b>Kişi kilidi (M6, §4.13):</b> çekilmeyen her izlenen satırın (P'nin satırlı üyeleri, S∖P'nin eksileri,
    /// 0 çekilen çapa) <c>UpdatedAt</c>'i "değişti" işaretlenir; değeri <c>StampCustomerBalanceVersions</c> ilerletir.
    /// Aynı kişiye eşzamanlı iki çekimden biri jetonda çakışır ve taze planla baştan kurulur.</para>
    ///
    /// <para><b>Baştan kurma:</b> jeton çakışması, bakiye satırı açma yarışı, kilitlenme kurbanlığı (1205, okumalar
    /// dahil — <see cref="IsDeadlock"/>) ve üyelik değişimi; en çok <see cref="MaxAttempts"/> deneme, sonra
    /// <c>Busy</c> ve TEK uyarı satırı. N01 ihlali → <c>AlreadyReversed</c>; istemci anahtarının PK ihlali →
    /// <c>DuplicateKey</c>. "Yeniden yükle, deltayı yeniden uygula" çok satırlı kayıtta yanlıştır (M12).</para>
    ///
    /// <para>Çağıran DbContext'te izlenen değişiklik bırakmamalı (izleyici denemeler arasında temizlenir) ve çağırmadan
    /// önce izleyiciye bir şey eklememeli: izlenen bir bakiye satırı yükleme sorgusunda tazelenmezdi.</para>
    ///
    /// <para>Çağıran açık bir işlem tutmamalı (tutarsa <see cref="InvalidOperationException"/>): kapsam kuralında
    /// deneme kendi işlemini açar; 1205 bütün işlemi düşürür, dıştaki bir işlemde yeniden denemek yanlış olurdu
    /// (çağıranın önceki yazımları sessizce geri alınır ya da sonraki komut 3930 alır).</para>
    /// </summary>
    /// <param name="beforeAttempt">(deneme numarası 1'den, ct): kilitten SONRA, okumalardan ÖNCE her denemede.</param>
    public async Task<DrawResult> DrawAsync(
        DrawRequest request, Func<int, CancellationToken, Task<bool>>? beforeAttempt, CancellationToken ct)
    {
        // Ön koşullar; ikisi de bugünkü hiçbir çağıranda tutmaz (apply tutarı doğrular, uçlar işlem açmaz), savunmadır.
        // InMemory'de CurrentTransaction her zaman null.
        if (_db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("DrawAsync açık bir işlem içinde çağrılamaz.");
        if (decimal.Round(request.Requested, 2) != request.Requested)
            throw new ArgumentOutOfRangeException(nameof(request), "Requested kuruş hassasiyetini aşamaz.");

        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1) _db.ChangeTracker.Clear();

            // try denemenin kilidini, geri çağrısını ve OKUMALARINI da kapsar: kilitlenme kurbanı bir okuma da olabilir
            // (§4.2-6 S2 notu; S1'in grup geri almasında görüldü). Okumalar yazmaz; yeniden denemek güvenli.
            string retryReason;
            try
            {
                // Kapsam kuralı (satış senkronu §6.6) deneme başına kendi işleminde: kilitlenme kurbanı işlemi ve kilidi
                // bütünüyle düşürür, aynı işlemde yeniden denemek yanlış olurdu (plan fark 7). Commit edilmeyen işlem
                // atılınca geri alınır; kilit işleme aittir. InMemory'de işlem yok (kilit zaten no-op).
                await using var tx = request.ScopeLockResource is not null && _db.Database.IsRelational()
                    ? await _db.Database.BeginTransactionAsync(ct)
                    : null;
                if (tx is not null && request.ScopeLockResource is { } resource)
                    await SalesCommandLock.AcquireAsync(_db, resource, SalesCommandLock.DefaultTimeout, ct);

                if (beforeAttempt is not null && await beforeAttempt(attempt, ct))
                    return Rejected(DrawStatus.Stopped, request, 0m);

                var resolved = request.MemberIds is { } memberIds
                    ? await ResolveDrawSetAsync(request.LicenseId, request.AnchorRowId, memberIds, ct)
                    : new DrawSetResult(await PersonDrawSetAsync(request.LicenseId, request.AnchorRowId, ct), null);
                if (resolved.Rejection is { } rejection)
                    // Code ∈ {CodeNoBalance, CodeCustomerNotSynced} (Görev 2).
                    return rejection.Code == CodeCustomerNotSynced
                        ? new DrawResult(DrawStatus.CustomerNotSynced, request.AnchorTransactionId, 0m, 0m, 0, rejection.RowIds)
                        : Rejected(DrawStatus.NoBalance, request, 0m);
                var set = resolved.Set ?? throw new InvalidOperationException("Çekim kümesi ne küme ne ret döndü.");

                // §4.2-2: P'nin ve S∖P'nin eksi üyelerinin bakiye satırları izlenen nesneler olarak, TEK sorguda.
                // AsTracking: bağlamın varsayılanı ne olursa olsun jeton dokunuşu ancak izlenen girdide UPDATE olur.
                var ids = set.Draw.Concat(set.LockedNegatives).Select(m => m.WpfCustomerId)
                    .Append(request.AnchorRowId).Distinct().ToList();
                var rows = await _db.CustomerBalances.AsTracking()
                    .Where(b => b.LicenseId == request.LicenseId && ids.Contains(b.WpfCustomerId))
                    .ToDictionaryAsync(b => b.WpfCustomerId, ct);

                // Üyeliğin ikinci okuması (§4.2-2, §4.11): üyelik okuması ile bakiye yüklemesi arasında kaydedilen bir
                // ayırma burada görünür; bundan sonra kaydedilen ayırmayı kanca (jeton) yakalar.
                var again = await MembersAsync(request.LicenseId, request.AnchorRowId, ct);
                if (MembershipChanged(set.Person, again))
                {
                    retryReason = "üyelik değişti";
                }
                else
                {
                    // Kural 3b taze veriyle (§4.0-3b): S∖P'nin izlemesiz okumada eksi olan bir üyesi, iki okuma arasında
                    // commit eden bir iadeyle artıya geçmiş olabilir. İzlenen jeton iade sonrasınındır, yani yazım
                    // iadeden SONRA sıralanır; o sırada o para W'de görülmeyen paradır → 409, hiçbir şey yazılmaz.
                    // Yok sayıp çekmek ya da Σ ≤ 0'da kesin NoBalance dönmek WPF'e MarkNoBalance yazdırırdı. Yalnız
                    // WPF yolunda olur (panelde LockedNegatives boş). İzlenen yüklemeden SONRA gelen iadeyi jeton yakalar.
                    var nowPositive = set.LockedNegatives
                        .Where(m => rows.TryGetValue(m.WpfCustomerId, out var b) && b.Balance > 0m)
                        .Select(m => m.WpfCustomerId)
                        .OrderBy(id => id.ToString("N"), StringComparer.Ordinal)
                        .ToList();
                    if (nowPositive.Count > 0)
                        return new DrawResult(
                            DrawStatus.CustomerNotSynced, request.AnchorTransactionId, 0m, 0m, 0, nowPositive);

                    // Σ izlenen satırlardan (kümenin izlemesiz okuması bayat olabilir). S∖P'nin üyesi 0'a indiyse
                    // havuza girmez; satırı yine izlenir ve jetonu dokunulur.
                    var draw = set.Draw
                        .Select(m => m with { Balance = rows.TryGetValue(m.WpfCustomerId, out var b) ? b.Balance : 0m })
                        .ToList();
                    var locked = set.LockedNegatives
                        .Sum(m => rows.TryGetValue(m.WpfCustomerId, out var b) ? Math.Min(0m, b.Balance) : 0m);
                    var pool = draw.Sum(m => m.Balance) + locked;

                    decimal applied;
                    if (request.Rule == DrawAmountRule.UpToPool)
                    {
                        if (pool <= 0m) return Rejected(DrawStatus.NoBalance, request, pool);
                        applied = Math.Min(request.Requested, pool);
                    }
                    else
                    {
                        if (Math.Max(0m, pool) < request.Requested) return Rejected(DrawStatus.Insufficient, request, pool);
                        applied = request.Requested;
                    }
                    if (applied <= 0m) return Rejected(DrawStatus.NothingToApply, request, pool);

                    var legs = PlanDraw(request.AnchorRowId, draw, applied);
                    StageDraw(request, rows, legs, DateTimeOffset.UtcNow);
                    await _db.SaveChangesAsync(ct);
                    if (tx is not null) await tx.CommitAsync(ct);

                    if (legs.Count > 1)
                        _log.LogInformation(
                            "Ortak bakiye: çok bacaklı çekim yazıldı ({Legs} bacak, license={LicenseId}, anchor={AnchorId})",
                            legs.Count, request.LicenseId, request.AnchorTransactionId);
                    return new DrawResult(DrawStatus.Drawn, request.AnchorTransactionId, applied, pool, legs.Count, []);
                }
            }
            // Sıra önemli: DbUpdateConcurrencyException, DbUpdateException'dan türer (plan fark 9).
            catch (DbUpdateConcurrencyException)
            {
                retryReason = "jeton çakışması";
            }
            catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
            {
                // Çekim türü iptalde N01 hakem: eşzamanlı ikinci iptal bütün bacaklarıyla geri alındı.
                _db.ChangeTracker.Clear();
                return Rejected(DrawStatus.AlreadyReversed, request, 0m);
            }
            catch (DbUpdateException ex) when (IsDuplicateBalanceRow(ex))
            {
                // Çapanın satırını eşzamanlı biri açtı (işlem bütünüyle geri alındı).
                retryReason = "bakiye satırı açma yarışı";
            }
            catch (DbUpdateException ex) when (request.AnchorIdIsIdempotencyKey && IsDuplicateTransactionKey(ex))
            {
                // Aynı anahtarla yarış: bütün bacaklar geri alındı; kazananı çağıran oynatır (§4.2-6).
                _db.ChangeTracker.Clear();
                return Rejected(DrawStatus.DuplicateKey, request, 0m);
            }
            // Exception, DbUpdateException değil: varsayılan yürütme stratejisi 1205'i sarar, satır okunurken gelen
            // 1205 çıplak gelir (bkz. IsDeadlock).
            catch (Exception ex) when (IsDeadlock(ex))
            {
                retryReason = "kilitlenme (1205)";
            }

            // Hiçbir şey yazılmadı; plan taze okumayla baştan kurulur (döngü başı izleyiciyi temizler).
            if (attempt >= MaxAttempts)
            {
                _db.ChangeTracker.Clear();
                // balance-busy'nin TEK uyarı satırı (denetleyiciler yalnız Debug yazar).
                _log.LogWarning(
                    "Ortak bakiye: çekim {Attempts} denemede kurulamadı, son neden: {Reason}; balance-busy (license={LicenseId}, anchor={AnchorId})",
                    MaxAttempts, retryReason, request.LicenseId, request.AnchorTransactionId);
                return Rejected(DrawStatus.Busy, request, 0m);
            }
            _log.LogInformation(
                "Ortak bakiye: çekim {Attempt}. denemede kurulamadı ({Reason}), baştan kuruluyor (license={LicenseId}, anchor={AnchorId})",
                attempt, retryReason, request.LicenseId, request.AnchorTransactionId);
        }
    }

    private static DrawResult Rejected(DrawStatus status, DrawRequest request, decimal pool) =>
        new(status, request.AnchorTransactionId, 0m, pool, 0, []);

    /// <summary>
    /// Üyeliğin ikinci okuması farklı mı: a'nın numarası (ordinal) ya da <c>(WpfCustomerId, Purged)</c> kümesi.
    /// Silinmişlik de karşılaştırılır: K3'ün silinmiş üyesi P'ye silinmişliği yüzünden girer.
    /// </summary>
    private static bool MembershipChanged(Person before, Person after) =>
        !string.Equals(before.GroupId, after.GroupId, StringComparison.Ordinal)
        || !before.Members.Select(m => (m.WpfCustomerId, m.Purged)).ToHashSet()
            .SetEquals(after.Members.Select(m => (m.WpfCustomerId, m.Purged)));

    /// <summary>
    /// Planı izleyiciye yazar (§3.2, §4.2-5): ilk bacak her zaman çapadır (<see cref="PlanDraw"/>). Çekilen satırın
    /// bakiyesi düşer; çekilmeyen her izlenen satıra jeton dokunuşu (kişi kilidi). Çapanın satırı yoksa 0 ile açılır:
    /// eklenen satır kendi değeriyle yazılır, tekil indeks yarışı <c>IsDuplicateBalanceRow</c> ile baştan kurulur.
    /// </summary>
    private void StageDraw(
        DrawRequest request, IReadOnlyDictionary<Guid, CustomerBalance> rows, IReadOnlyList<DrawLeg> legs,
        DateTimeOffset now)
    {
        if (!rows.ContainsKey(request.AnchorRowId))
            _db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = request.LicenseId, WpfCustomerId = request.AnchorRowId,
                Balance = 0m, UpdatedAt = now,
            });

        var drawn = new HashSet<Guid>();
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var isAnchor = i == 0;
            _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = isAnchor ? request.AnchorTransactionId : Guid.NewGuid(),
                LicenseId = request.LicenseId,
                WpfCustomerId = leg.WpfCustomerId,
                Amount = -leg.Amount,
                Kind = request.Kind,
                OriginalAmount = isAnchor ? request.OriginalAmount : null,
                SaleScope = isAnchor ? request.SaleScope : null,
                Reason = isAnchor ? request.Reason : null,
                ReversesTransactionId = isAnchor ? request.ReversesTransactionId : null,
                DrawGroupId = request.AnchorTransactionId,
                CreatedByCustomerId = request.CreatedByCustomerId,
                CreatedAt = now,
            });
            // Yalnız çapa 0 olabilir; > 0 çekilen satırın izlenen bakiyesi > 0'dır, yani satırı yüklüdür.
            if (leg.Amount == 0m) continue;
            var row = rows[leg.WpfCustomerId];
            row.Balance -= leg.Amount;
            row.UpdatedAt = now;
            drawn.Add(leg.WpfCustomerId);
        }

        foreach (var (id, row) in rows)
            if (!drawn.Contains(id))
                _db.Entry(row).Property(b => b.UpdatedAt).IsModified = true;
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
    /// Aynı anahtarla yarış: hareketin PK'sı (çapa = istemcinin anahtarı). Genel <c>DbUpdateException</c> süzgeci
    /// yetmez: havuz yazımında bakiye satırı açma yarışı ve N01 ihlali de <c>DbUpdateException</c>'dır, onlar oynatmaya
    /// değil yeniden denemeye ve <c>already-reversed</c>'e gider (plan fark 9).
    /// </summary>
    public static bool IsDuplicateTransactionKey(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(TransactionPrimaryKey, StringComparison.Ordinal);

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
