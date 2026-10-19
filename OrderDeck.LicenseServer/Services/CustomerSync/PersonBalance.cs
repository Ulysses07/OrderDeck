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
/// (genişlet/daralt). <b>S2:</b> <c>ResolveDrawSetAsync</c>, <c>PlanDraw</c>, <c>DrawAsync</c>.</para>
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
    private readonly ILogger<PersonBalance> _log;

    public PersonBalance(LicenseDbContext db, ILogger<PersonBalance> log)
    {
        _db = db;
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
}
