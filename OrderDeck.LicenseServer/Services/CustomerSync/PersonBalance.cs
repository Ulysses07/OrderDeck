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
    /// kurbanlığında <c>ChangeTracker.Clear()</c>; çapa, bacaklar, bacakların güncel satırları ve "geri alındı mı"
    /// taze okunur, grup baştan kurulur; en çok <see cref="MaxAttempts"/> deneme, sonra
    /// <see cref="ReverseOutcome.Busy"/>. Bugünkü "yeniden yükle, deltayı yeniden uygula" kalıbı çok satırlı kayıtta
    /// YANLIŞ: EF yalnız çakışan girdiyi döndürür, öbür satırlar bellekteki <c>eski + delta</c>'yı korur ve delta
    /// onlara iki kez eklenirdi. N01 ihlali (eşzamanlı geri alma) bütün işlemi geri alır →
    /// <see cref="ReverseOutcome.AlreadyReversed"/>; istemci başarı sayar. Para yalnız eklendiği için eksi denetimi
    /// ve kişi kilidi gerekmez.</para>
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

            var anchor = await _db.CustomerBalanceTransactions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == anchorId && t.LicenseId == licenseId, ct);
            if (anchor is null || !IsAnchor(anchor)) return ReverseOutcome.NotFound;
            // Hızlı yol; asıl hakem N01 indeksi (aşağıdaki catch).
            if (await _db.CustomerBalanceTransactions.AnyAsync(t => t.ReversesTransactionId == anchorId, ct))
                return ReverseOutcome.AlreadyReversed;

            // Bacaklar Id sırasıyla: eşzamanlı iki geri alma geri alma satırlarını (N01 tekil anahtarları) aynı
            // sırayla ekler — biri öbürünü bekler, kilitlenme (1205) olmaz.
            List<CustomerBalanceTransaction> legs = anchor.DrawGroupId is null
                ? [anchor]
                : await _db.CustomerBalanceTransactions.AsNoTracking()
                    .Where(t => t.LicenseId == licenseId && t.DrawGroupId == anchorId)
                    .OrderBy(t => t.Id)
                    .ToListAsync(ct);
            await StageGroupReversalAsync(licenseId, anchor, legs, createdByCustomerId, ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                if (legs.Count > 1)
                    _log.LogInformation(
                        "Ortak bakiye: çok bacaklı çekim grup olarak geri alındı ({Legs} bacak, license={LicenseId}, anchor={AnchorId})",
                        legs.Count, licenseId, anchorId);
                return ReverseOutcome.Reversed;
            }
            // Sıra önemli: DbUpdateConcurrencyException, DbUpdateException'dan türer.
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Grup baştan kurulur (döngü başı).
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
                _log.LogWarning(
                    "Ortak bakiye: grup geri alması {Attempts} denemede de çakıştı, balance-busy (license={LicenseId}, anchor={AnchorId})",
                    MaxAttempts, licenseId, anchorId);
                return ReverseOutcome.Busy;
            }
            catch (DbUpdateException ex) when (IsDuplicateReversal(ex))
            {
                _db.ChangeTracker.Clear();
                return ReverseOutcome.AlreadyReversed;
            }
            catch (DbUpdateException ex) when ((IsDuplicateBalanceRow(ex) || IsDeadlock(ex)) && attempt < MaxAttempts)
            {
                // Bacağın satırını eşzamanlı biri açtı ya da kilitlenme kurbanı olduk (işlem bütünüyle geri alındı):
                // taze okumayla baştan.
            }
            catch (DbUpdateException ex) when (IsDuplicateBalanceRow(ex) || IsDeadlock(ex))
            {
                _db.ChangeTracker.Clear();
                _log.LogWarning(
                    "Ortak bakiye: grup geri alması {Attempts} denemede de satır açma yarışı ya da kilitlenme, balance-busy (license={LicenseId}, anchor={AnchorId})",
                    MaxAttempts, licenseId, anchorId);
                return ReverseOutcome.Busy;
            }
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

    /// <summary>SQL Server kilitlenme kurbanı (1205): işlem bütünüyle geri alındı, yeniden kurmak güvenli.</summary>
    public static bool IsDeadlock(DbUpdateException ex) => ex.InnerException is SqlException { Number: 1205 };
}
