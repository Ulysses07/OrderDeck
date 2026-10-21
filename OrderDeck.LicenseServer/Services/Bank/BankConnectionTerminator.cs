using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Kaldırmanın sonucu. Yerel silme her durumda yapılmıştır. Obifin tarafı: <see cref="ObifinRemoved"/> kaldırıldı,
/// <see cref="ObifinFailed"/> kaldırılamadı, <see cref="ObifinUnowned"/> Obifin listesinde var ama bu lisansa ait olduğu
/// doğrulanamadı (dokunulmadı), <see cref="SharedObifinUser"/> Obifin kullanıcısı başka lisansta da kayıtlı (Obifin'e hiç
/// gidilmedi), <see cref="ObifinNotAttempted"/> yalnız yerel silme (otomatik hizmet sonu işi; Obifin'e gidilmedi).
/// <see cref="ObifinCopyDeletionId"/>: "Kaldır ve sil"in açtığı, Obifin'den yazılı silme teyidi bekleyen kayıt
/// (<c>ObifinCopyDeletions</c>); yalnız yerel silmede null. <see cref="Accounts"/> silinen hesap satırı;
/// <see cref="AccountsScrubbed"/> yalnız yerel silmede IBAN alanları boşaltılıp sahiplik kanıtı olarak bırakılan onaylı hesap.</summary>
public sealed record BankRemovalResult(IReadOnlyList<long> ObifinRemoved, IReadOnlyList<long> ObifinFailed,
    IReadOnlyList<long> ObifinUnowned, bool ObifinUnreachable, bool SharedObifinUser, bool ObifinNotAttempted,
    int Transactions, int Matches, int Memories, int Gaps, int Accounts, int AccountsScrubbed, int BankConnections,
    Guid? ObifinCopyDeletionId)
{
    public bool NeedsManualObifinAction => ObifinNotAttempted || ObifinUnreachable || SharedObifinUser
        || ObifinFailed.Count > 0 || ObifinUnowned.Count > 0;

    /// <summary>Yöneticinin Obifin'de elle yapacağı iş (admin bildirimi ve hizmet sonu e-postası); kimlik bilgisi içermez. Elle iş
    /// yoksa boş.</summary>
    public string ManualActionText()
    {
        if (ObifinNotAttempted)
            return "Obifin'e gidilmedi (yalnız yerel silme): admin/obifin'de bu lisans için 'Kaldır ve sil'e basın.";
        if (SharedObifinUser)
            return "Bu Obifin kullanıcısı başka bir lisansta da kayıtlı; Obifin'e hiç gidilmedi. Obifin panelinden YALNIZ bu " +
                "lisansın banka bağlantısını elle kaldırın, diğer lisansınkine dokunmayın.";
        if (ObifinUnreachable)
            return "Obifin'e ulaşılamadı: Obifin panelinden bu lisansın banka bağlantılarını elle kaldırın.";
        var parts = new List<string>();
        if (ObifinFailed.Count > 0)
            parts.Add($"kaldırılamayan banka bağlantıları #{string.Join(", #", ObifinFailed)}: Obifin panelinden elle kaldırın");
        if (ObifinUnowned.Count > 0)
            parts.Add($"bu lisansa ait olduğu doğrulanamayan #{string.Join(", #", ObifinUnowned)}: elle kontrol edin, başka " +
                "lisansınsa dokunmayın");
        return parts.Count == 0 ? "" : "Obifin'de " + string.Join("; ", parts) + ".";
    }
}

/// <summary>Hesap reddinin sonucu (KVKK Ö-10): silinen hareket, eşleşme ve IBAN bağı (öğrettiği bağlar ve kalan doğrulaması
/// kalmadığı için silinen bağlar); açılan dekont gap'i.</summary>
public sealed record BankAccountRejectionResult(int Transactions, int Matches, int Memories, int GapsReopened);

/// <summary>
/// Banka bağlantısının durdurulması, yeniden etkinleştirilmesi, kaldırılıp verisinin silinmesi ve hizmet sonunda yalnız yerel
/// verisinin silinmesi (KVKK K-6, Ö-9); Obifin'in yazılı silme teyidinin kaydı; onaysız hesabın reddi (Ö-10).
/// <see cref="BankHasher"/> İSTEMEZ: banka modülü anahtarsız kapalıyken de durdurma ve silme çalışır (silme KVKK yükümlülüğü).
/// Bağdaştırıcı (<see cref="PaymentMatchReconciler"/>) da hasher istemez; hesap reddinde IBAN bağı süre kuralı oradan uygulanır.
/// <para><b>Kaldır ve sil</b> (<see cref="RemoveAndDeleteAsync"/>, yalnız yöneticinin eylemi — <c>bankaapi/sil</c>'i çağıran
/// TEK yol): önce bağlantı tek SaveChanges'le durdurulur (partiler sürerken çekim ve eşleştirme yazmasın). Obifin tarafı lisansa
/// özgüdür, bkz. <see cref="RemoveFromObifinAsync"/>. Sonra lisansın bütün banka verisi ve Obifin kimliği silinir, imha kaydı
/// aynı işlemde yazılır. Obifin API'sinde hareket silme ucu yok: Obifin'deki kopyanın silinmesi yazılı istenir ve
/// <c>ObifinCopyDeletions</c> kaydıyla izlenir. Obifin'e ulaşılamazsa ya da kaldırma düşerse yerel silme yine yapılır.</para>
/// <para><b>Yalnız yerel silme</b> (<see cref="PurgeLocalDataAsync"/>, otomatik hizmet sonu işi): Obifin'e gidilmez; banka
/// verisi silinir, "Kaldır ve sil"in sahiplik kanıtı kalır — Obifin kimliği, yerel <c>BankConnections</c> (yalnız
/// <c>BankaApiId</c>) ve ONAYLI hesap satırları (kişisel veri taşıyabilen alanları boşaltılmış; onaysız hesaplar silinir).
/// <c>BankDataPurgedAt</c> yazılır — yönetici Obifin tarafını sonra "Kaldır ve sil" ile kapatır.</para>
/// <para>Yerel silme <see cref="BatchSize"/>'lık partilerle yapılır (saklama işinin kalıbı): lisansın bütün satırları bir
/// kerede yüklenmez, her parti imha kaydını aynı SaveChanges'te yazar; bağlantının kendisi ya da <c>BankDataPurgedAt</c>
/// işareti EN SON. Eşzamanlı bir eşleştirme yazısıyla (jeton) çakışırsa bütün silme bir kez baştan yeniden denenir (her parti
/// sorgusu baştan koşar; sayılar birikir, düşen partinin izi ve imha kaydı izleyiciden atılır). Günlüğe kimlik bilgisi yazılmaz;
/// yalnız lisans kimliği, sayılar ve istisna türü.</para>
/// <para><b>Obifin teyidi</b> (<see cref="ConfirmObifinCopyDeletedAsync"/>): "Kaldır ve sil"in açtığı <c>ObifinCopyDeletions</c>
/// kaydı yöneticinin "Teyit alındı"sıyla kapanır, imha kaydı yazılır. <b>Hesap reddi</b> (<see cref="RejectAccountAsync"/>):
/// onay kaldırılır, hesabın verisi partilerle silinir.</para>
/// </summary>
public sealed class BankConnectionTerminator
{
    /// <summary>Tek SaveChanges'te en fazla bu kadar satır (BankDataRetentionJob ile aynı); partiler arasında izleyici boşalır.</summary>
    public const int BatchSize = 500;
    /// <summary>Obifin tarafındaki bekleyen işin (Kaldır ve sil, yazılı silme teyidi) "GECİKTİ" sayıldığı süre (plan karar 21).</summary>
    public static readonly TimeSpan ObifinFollowUpOverdue = TimeSpan.FromDays(14);
    /// <summary>Obifin teyidinin imha kaydındaki hedef.</summary>
    public const string ObifinCopyTarget = "Obifin.HareketKopyasi";
    /// <summary>Yerel silmede sahiplik kanıtı olarak bırakılan onaylı hesabın boşaltılan alanları (imha kaydı hedefi, eylem
    /// <c>nulled</c>).</summary>
    public const string ScrubbedAccountTarget = "BankAccounts.IbanMasked+IbanHash+NotificationNote";

    public const string StoppedByAdminMessage = "Yönetici çekimi durdurdu.";
    public const string LicenseStillEndedMessage = "Lisans sona ermiş ya da iptal edilmiş; bağlantı etkinleştirilemez.";
    public const string NoConnectionMessage = "Bu lisansın Obifin bağlantısı yok.";
    public const string CopyDeletionNotFoundMessage = "Obifin silme teyidi kaydı bulunamadı.";
    public const string AlreadyConfirmedMessage = "Bu Obifin silme teyidi zaten kaydedilmiş.";
    public const string AccountNotFoundMessage = "Hesap bulunamadı.";

    private readonly LicenseDbContext _db;
    private readonly IObifinClient _client;
    private readonly IDataProtectionProvider _protection;
    private readonly PaymentMatchReconciler _reconciler;
    private readonly ILogger<BankConnectionTerminator> _log;

    public BankConnectionTerminator(LicenseDbContext db, IObifinClient client, IDataProtectionProvider protection,
        PaymentMatchReconciler reconciler, ILogger<BankConnectionTerminator> log)
    { _db = db; _client = client; _protection = protection; _reconciler = reconciler; _log = log; }

    /// <summary>Disabled + DisabledAt (hizmet sonu sayacı) + DisabledByAdmin. Lisans bitişiyle zaten Disabled ise yalnız
    /// DisabledByAdmin yazılır (tarih değişmez): yenilenen lisans yöneticinin durdurduğu bağlantıyı açmasın.</summary>
    public async Task StopAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException(NoConnectionMessage);
        if (conn.Status == ObifinConnectionStatus.Disabled && conn.DisabledByAdmin) return;
        MarkStopped(conn, DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Yönetici durdurması, kaydetmez (<see cref="StopAsync"/>, <see cref="RemoveAndDeleteAsync"/>): Disabled + DisabledAt
    /// (hizmet sonu sayacı) + LastError; zaten Disabled ise (lisans bitişi) DisabledAt ve LastError değişmez, sayaç baştan
    /// başlamaz. Her durumda DisabledByAdmin ve yeni UpdatedAt: çekimin kimlik koruması yarıdaki turun kaydını düşürür.</summary>
    private static void MarkStopped(ObifinConnection conn, DateTimeOffset now)
    {
        if (conn.Status != ObifinConnectionStatus.Disabled)
        {
            conn.Status = ObifinConnectionStatus.Disabled;
            conn.DisabledAt = now;
            conn.LastError = StoppedByAdminMessage;
        }
        conn.DisabledByAdmin = true;
        conn.UpdatedAt = now;
    }

    /// <summary>Yalnız lisans geçerliyse: Unverified'a döner (yönetici Doğrula'ya basar), hizmet sonu alanları boşalır
    /// (<see cref="ObifinConnectionService.ClearServiceEnd"/>). Disabled olmayan bağlantıda bir şey yapmaz.</summary>
    public async Task ResumeAsync(Guid licenseId, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException(NoConnectionMessage);
        if (conn.Status != ObifinConnectionStatus.Disabled) return;
        var now = DateTimeOffset.UtcNow;
        if (!await ObifinConnectionService.IsLicenseActiveAsync(_db, licenseId, now, ct))
            throw new ObifinValidationException(LicenseStillEndedMessage);
        conn.Status = ObifinConnectionStatus.Unverified;
        ObifinConnectionService.ClearServiceEnd(conn);
        conn.LastError = null;
        conn.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Yöneticinin "Kaldır ve sil"i. ÖNCE bağlantıyı tek SaveChanges'le durdurur (<see cref="MarkStopped"/>): yönetici
    /// sayfası <c>obifin-poll</c> kilidini almaz, partili silme saniyeler sürer. Bağlantı en son adıma kadar Verified kalsaydı arada
    /// koşan çekim turunun kaydı kimlik korumasından geçer (UpdatedAt değişmemiş), sink ve telafi taraması (eşleştirme kapısı açık)
    /// yeni hareket ve eşleşme yazardı; döngüden sonra yazılan satır bağlantısız lisansta kalır, imha kaydına girmezdi. Durdurmayla
    /// yarıdaki turun kaydı düşer, sonraki turlar Disabled bağlantıyı atlar, <see cref="ObifinConnectionService.MatchingActive"/>
    /// eşleştirmeyi kapatır. Silme yarıda düşerse bağlantı Disabled kalır (güvenli yön; hizmet sonu sayacı işler). Sonra Obifin'de
    /// bu lisansa ait banka bağlantıları kaldırılır (lisansa özgü, <see cref="RemoveFromObifinAsync"/>) ve lisansın bütün banka
    /// verisi ile Obifin kimliği silinir; Obifin kopyasının yazılı silme teyidi için <c>ObifinCopyDeletions</c> kaydı açılır.</summary>
    public async Task<BankRemovalResult> RemoveAndDeleteAsync(Guid licenseId, string trigger, string actor, CancellationToken ct)
    {
        var conn = await _db.ObifinConnections.FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct)
            ?? throw new ObifinValidationException(NoConnectionMessage);
        MarkStopped(conn, DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();   // conn bundan sonra yalnız okunur (kimlik, lisans)
        var obifin = await RemoveFromObifinAsync(conn, ct);
        return await DeleteLocalWithRetryAsync(licenseId, trigger, actor, keepConnection: false, obifin, ct);
    }

    /// <summary>Otomatik hizmet sonu silmesi (Görev 14): Obifin'e GİDİLMEZ. Banka verisi silinir; Obifin kimliği, yerel
    /// <c>BankConnections</c> ve onaylı hesap satırları (IBAN alanları boş) kalır, <c>BankDataPurgedAt</c> yazılır. İdempotent:
    /// ikinci koşu kalan satırları siler, boşaltılmış hesabı yeniden saymaz.</summary>
    public async Task<BankRemovalResult> PurgeLocalDataAsync(Guid licenseId, string trigger, string actor, CancellationToken ct)
    {
        if (!await _db.ObifinConnections.AnyAsync(c => c.LicenseId == licenseId, ct))
            throw new ObifinValidationException(NoConnectionMessage);
        return await DeleteLocalWithRetryAsync(licenseId, trigger, actor, keepConnection: true, ObifinOutcome.Skipped, ct);
    }

    private sealed record ObifinOutcome(List<long> Removed, List<long> Failed, List<long> Unowned, bool Unreachable, bool Shared,
        bool NotAttempted)
    {
        public static ObifinOutcome Skipped => new([], [], [], false, false, true);
        public static ObifinOutcome Unreached => new([], [], [], true, false, false);
    }

    /// <summary>
    /// Obifin tarafı, LİSANSA ÖZGÜ (Faz 2 spec §13: tek paylaşılan Obifin kullanıcısı olabilir; <c>ObifinConnection</c>'da yalnız
    /// <c>LicenseId</c> tekil):
    /// <list type="number">
    /// <item>Aynı (BaseUrl, UserCode) başka bir lisansın bağlantısında da varsa Obifin'e HİÇ gidilmez: o kullanıcının altındaki
    /// bağlantıların hangisinin kime ait olduğu buradan güvenle bilinemez.</item>
    /// <item>Yalnız bu lisansın yerel <c>BankConnections</c>'ındaki ve ONAYLI hesaplarının (Görev 12; Obifin hesap listesindeki
    /// <c>BankaApiId</c>) bağlantıları kaldırılır. Obifin panelinden eklenmiş bağlantı yalnız onaylı hesaptan bilinir; hizmet
    /// sonu yerel silmesi bu yüzden onaylı hesap satırını bırakır (<see cref="DeleteLocalAsync"/>), tanıma o silmeden sonra da
    /// çalışır.</item>
    /// <item>Obifin listesindeki diğer bağlantılara dokunulmaz; "elle kontrol" diye raporlanır.</item>
    /// </list>
    /// Kimlik çözülemez ya da iki listeden biri alınamazsa hiçbir şey kaldırılmaz (ulaşılamadı). Kaldırma istek jetonuyla değil
    /// çağıranın jetonuyla koşar; yönetici sayfası <see cref="CancellationToken.None"/> geçirir.
    /// </summary>
    private async Task<ObifinOutcome> RemoveFromObifinAsync(ObifinConnection conn, CancellationToken ct)
    {
        var licenseId = conn.LicenseId;
        var sameUserCode = await _db.ObifinConnections.AsNoTracking()
            .Where(c => c.LicenseId != licenseId && c.UserCode == conn.UserCode).Select(c => c.BaseUrl).ToListAsync(ct);
        if (sameUserCode.Any(b => string.Equals(b.TrimEnd('/'), conn.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            _log.LogWarning("Obifin kaldırma: Obifin kullanıcısı başka lisansta da kayıtlı, Obifin'e gidilmedi — lisans={LicenseId}",
                licenseId);
            return new ObifinOutcome([], [], [], Unreachable: false, Shared: true, NotAttempted: false);
        }
        var creds = ObifinConnectionService.ResolveCredentials(_protection, conn);
        if (creds is null)
        {
            _log.LogWarning("Obifin kaldırma: saklı kimlik çözülemedi — lisans={LicenseId}; Obifin'de elle kaldırılmalı", licenseId);
            return ObifinOutcome.Unreached;
        }

        var owned = (await _db.BankConnections.AsNoTracking().Where(b => b.LicenseId == licenseId)
            .Select(b => b.BankaApiId).ToListAsync(ct)).ToHashSet();
        var approvedAccounts = (await _db.BankAccounts.AsNoTracking().Where(a => a.LicenseId == licenseId && a.ApprovedAt != null)
            .Select(a => a.ObifinAccountId).ToListAsync(ct)).ToHashSet();
        IReadOnlyList<ObifinBankConnectionDto> list;
        try
        {
            list = await _client.ListBankConnectionsAsync(creds, ct);
            if (approvedAccounts.Count > 0)
                foreach (var a in await _client.ListAccountsAsync(creds, ct))
                    if (approvedAccounts.Contains(a.Id) && a.BankaApiId is { } apiId) owned.Add(apiId);
        }
        catch (Exception ex) when (ObifinConnectionService.DescribeClientFailure(ex, ct) is not null)
        {
            _log.LogWarning("Obifin kaldırma: banka bağlantı ya da hesap listesi alınamadı — lisans={LicenseId}: {Type}",
                licenseId, ex.GetType().Name);
            return ObifinOutcome.Unreached;
        }

        var removed = new List<long>();
        var failed = new List<long>();
        var unowned = new List<long>();
        foreach (var bc in list)
        {
            if (!owned.Contains(bc.BankaApiId))
            {
                unowned.Add(bc.BankaApiId);
                continue;
            }
            try
            {
                await _client.RemoveBankConnectionAsync(creds, bc.BankaApiId, ct);
                removed.Add(bc.BankaApiId);
            }
            catch (Exception ex) when (ObifinConnectionService.DescribeClientFailure(ex, ct) is not null)
            {
                failed.Add(bc.BankaApiId);
                _log.LogWarning("Obifin kaldırma: banka bağlantısı #{BankaApiId} kaldırılamadı — lisans={LicenseId}: {Type}",
                    bc.BankaApiId, licenseId, ex.GetType().Name);
            }
        }
        if (unowned.Count > 0)
            _log.LogWarning("Obifin kaldırma: {Count} banka bağlantısının bu lisansa ait olduğu doğrulanamadı, dokunulmadı — " +
                "lisans={LicenseId}", unowned.Count, licenseId);
        return new ObifinOutcome(removed, failed, unowned, Unreachable: false, Shared: false, NotAttempted: false);
    }

    /// <summary>Silinen satır sayıları: partiler ayrı kaydedildiği için yeniden denemede birikir.</summary>
    private sealed class Tally
    {
        public int Transactions, Matches, Memories, Gaps, Accounts, AccountsScrubbed, BankConnections;
        public Guid? CopyDeletionId;
    }

    private async Task<BankRemovalResult> DeleteLocalWithRetryAsync(Guid licenseId, string trigger, string actor,
        bool keepConnection, ObifinOutcome obifin, CancellationToken ct)
    {
        var tally = new Tally();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DeleteLocalAsync(licenseId, trigger, actor, keepConnection, tally, ct);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                // Arada bir eşleştirme yazısı jetonu ilerletti. Kaydedilmiş partiler gitti (sayıları tally'de); düşen partinin izi
                // ve imha kaydı atılır, kalan satırlar taze okunup bir kez yeniden silinir — her parti sorgusu baştan koşar,
                // yeniden deneme idempotent.
                _db.ChangeTracker.Clear();
                _log.LogInformation("Banka verisi silinirken eşzamanlı bir eşleştirme yazısıyla çakışıldı — lisans={LicenseId}; " +
                    "kalan satırlar bir kez yeniden siliniyor", licenseId);
            }
        }
        _log.LogWarning("Banka verisi silindi — lisans={LicenseId}, tetikleyici={Trigger}, yalnız yerel={Local}: hareket={Transactions}, " +
            "eşleşme={Matches}, IBAN bağı={Memories}, gap={Gaps}, silinen hesap={Accounts}, boşaltılan onaylı hesap={Scrubbed}, " +
            "banka bağlantısı={BankConnections}; Obifin'de kaldırılan={Removed}, kaldırılamayan={Failed}, sahibi " +
            "doğrulanamayan={Unowned}, ulaşılamadı={Unreachable}, paylaşılan kullanıcı={Shared}",
            licenseId, trigger, keepConnection, tally.Transactions, tally.Matches, tally.Memories, tally.Gaps, tally.Accounts,
            tally.AccountsScrubbed, tally.BankConnections, obifin.Removed.Count, obifin.Failed.Count, obifin.Unowned.Count,
            obifin.Unreachable, obifin.Shared);
        return new BankRemovalResult(obifin.Removed, obifin.Failed, obifin.Unowned, obifin.Unreachable, obifin.Shared,
            obifin.NotAttempted, tally.Transactions, tally.Matches, tally.Memories, tally.Gaps, tally.Accounts,
            tally.AccountsScrubbed, tally.BankConnections, tally.CopyDeletionId);
    }

    /// <summary>Lisansın banka verisini <see cref="BatchSize"/>'lık partilerle siler: her parti imha kaydını AYNI SaveChanges'te
    /// yazar, partiler arasında izleyici boşalır. Bağlantının kendisi (kaldırma) ya da <c>BankDataPurgedAt</c> işareti ile onaylı
    /// hesapların boşaltılması (yerel silme) ve "Kaldır ve sil"in Obifin teyit kaydı EN SON, tek SaveChanges'te: önceki bir parti
    /// düşerse bağlantı ve işaret kalır, iş ertesi gün (ya da yönetici yeniden basınca) kalanı siler.
    /// <paramref name="keepConnection"/>: "Kaldır ve sil"in sahiplik kanıtı kalır — Obifin kimliği, yerel BankConnections ve onaylı
    /// hesap satırları (kişisel veri taşıyabilen alanları son adımda boşalır); onaysız hesaplar silinir, BankDataPurgedAt
    /// yazılır.</summary>
    private async Task DeleteLocalAsync(Guid licenseId, string trigger, string actor, bool keepConnection, Tally tally,
        CancellationToken ct)
    {
        var criterion = keepConnection ? "hizmet sonu + 30 gün (yalnız yerel)" : "bağlantı kaldırıldı";
        void Record(string target, int count) => DataDestruction.Record(_db, licenseId, target, DataDestruction.Actions.Deleted,
            count, criterion, trigger, actor);

        // Hareketler eşleşme satırlarıyla birlikte (eşleşme harekete bağlı).
        while (true)
        {
            var batch = await _db.BankTransactions.Where(t => t.LicenseId == licenseId).OrderBy(t => t.Id).Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            var ids = batch.Select(t => t.Id).ToList();
            var matches = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
            _db.PaymentMatches.RemoveRange(matches);
            _db.BankTransactions.RemoveRange(batch);
            Record("PaymentMatches", matches.Count);
            Record("BankTransactions", batch.Count);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            tally.Matches += matches.Count;
            tally.Transactions += batch.Count;
            if (batch.Count < BatchSize) break;
        }
        tally.Memories += await DeleteBatchesAsync(
            _db.CustomerIbanMemories.Where(m => m.LicenseId == licenseId).OrderBy(m => m.Id), "CustomerIbanMemories", Record, ct);
        tally.Gaps += await DeleteBatchesAsync(
            _db.PaymentMatchGaps.Where(g => g.LicenseId == licenseId).OrderBy(g => g.Id), "PaymentMatchGaps", Record, ct);
        // Yerel silmede ONAYLI hesap satırı kalır (son adımda boşalır): "Kaldır ve sil" Obifin panelinden eklenmiş, yerelde
        // BankConnections satırı olmayan bağlantının bu lisansa ait olduğunu yalnız onaylı hesaptan bilir (RemoveFromObifinAsync).
        // Silinseydi o bağlantı "sahibi doğrulanamadı" diye Obifin'de kalır, Obifin hizmet bittiği halde bankadan çekmeyi
        // sürdürürdü (K-6). Onaysız hesap kanıt değildir, silinir.
        var accounts = _db.BankAccounts.Where(a => a.LicenseId == licenseId);
        if (keepConnection) accounts = accounts.Where(a => a.ApprovedAt == null);
        tally.Accounts += await DeleteBatchesAsync(accounts.OrderBy(a => a.Id), "BankAccounts", Record, ct);
        if (!keepConnection)
            tally.BankConnections += await DeleteBatchesAsync(
                _db.BankConnections.Where(b => b.LicenseId == licenseId).OrderBy(b => b.Id), "BankConnections", Record, ct);

        // Son adım, tek SaveChanges: bağlantı (ya da işaret ve onaylı hesapların boşaltılması) ve Obifin teyit kaydı.
        var now = DateTimeOffset.UtcNow;
        var scrubbed = 0;
        Guid? copyId = null;
        var connections = await _db.ObifinConnections.Where(c => c.LicenseId == licenseId).ToListAsync(ct);
        if (keepConnection)
        {
            foreach (var c in connections) { c.BankDataPurgedAt ??= now; c.UpdatedAt = now; }
            // Onaylı hesap: kimlik (ObifinAccountId, ApprovedAt, ApprovedBy) kalır, kişisel veri taşıyabilen alanlar boşalır.
            // Lisansın onaylı hesabı birkaç tanedir, parti gerekmez. Kalan satırı "Kaldır ve sil" siler; lisans yenilenip
            // bağlantı yeniden doğrulanırsa hesap yenilemesi alanları yeniden doldurur, onay geçerli kalır.
            var approved = await _db.BankAccounts.Where(a => a.LicenseId == licenseId && a.ApprovedAt != null
                && (a.IbanMasked != "" || a.IbanHash != null || a.NotificationNote != null)).ToListAsync(ct);
            foreach (var a in approved) { a.IbanMasked = ""; a.IbanHash = null; a.NotificationNote = null; }
            DataDestruction.Record(_db, licenseId, ScrubbedAccountTarget, DataDestruction.Actions.Nulled, approved.Count,
                criterion, trigger, actor);
            scrubbed = approved.Count;
        }
        else
        {
            _db.ObifinConnections.RemoveRange(connections);
            Record("ObifinConnections", connections.Count);
            // KVKK Ö-9 öneri 3: Obifin'deki kopyanın yazılı silme teyidi bağlantı satırından bağımsız izlenir (FK yok, budanmaz).
            // Hatırlatma: hizmet sonu işi (Görev 14); kapatma: ConfirmObifinCopyDeletedAsync.
            var copy = new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = licenseId, RequestedAt = now, RequestedBy = actor };
            _db.ObifinCopyDeletions.Add(copy);
            copyId = copy.Id;
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        // Kayıttan sonra: yeniden denemede iki kez sayılmasın, düşen denemenin kimliği dönmesin.
        tally.AccountsScrubbed += scrubbed;
        tally.CopyDeletionId = copyId ?? tally.CopyDeletionId;
    }

    /// <summary>Sıralı sorgunun seçtiği satırları <see cref="BatchSize"/>'lık partilerle siler, her partinin imha kaydını aynı
    /// SaveChanges'te yazar. Silinen satır sorgudan düşer: her parti baştan sorgulanır.</summary>
    private async Task<int> DeleteBatchesAsync<T>(IQueryable<T> rows, string target, Action<string, int> record,
        CancellationToken ct) where T : class
    {
        var total = 0;
        while (true)
        {
            var batch = await rows.Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) return total;
            _db.RemoveRange(batch);
            record(target, batch.Count);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            total += batch.Count;
            if (batch.Count < BatchSize) return total;
        }
    }

    /// <summary>Obifin'in hareket kopyasını sildiğine dair yazılı teyit alındı (KVKK Ö-9 öneri 3; plan karar 21): kayıt kapanır,
    /// imha kaydı (<see cref="ObifinCopyTarget"/>, tetikleyici <c>processor-confirmation</c>) aynı SaveChanges'te yazılır. Teyit
    /// yazısının kendisi sistemde saklanmaz (repo dışı KVKK klasörü). Kişisel veri yok. Kaydın lisansını döner (denetim).</summary>
    public async Task<Guid> ConfirmObifinCopyDeletedAsync(Guid copyDeletionId, string actor, CancellationToken ct)
    {
        var row = await _db.ObifinCopyDeletions.FirstOrDefaultAsync(d => d.Id == copyDeletionId, ct)
            ?? throw new ObifinValidationException(CopyDeletionNotFoundMessage);
        if (row.ConfirmedAt is not null) throw new ObifinValidationException(AlreadyConfirmedMessage);
        row.ConfirmedAt = DateTimeOffset.UtcNow;
        row.ConfirmedBy = actor;
        DataDestruction.Record(_db, row.LicenseId, ObifinCopyTarget, DataDestruction.Actions.Deleted, 1,
            "Obifin yazılı silme teyidi", DataDestruction.Triggers.ProcessorConfirmation, actor);
        await _db.SaveChangesAsync(ct);
        return row.LicenseId;
    }

    /// <summary>
    /// KVKK Ö-10: hesabı reddeder ya da onayını geri alır ve o hesabın bu lisanstaki hareketlerini, eşleşmelerini ve bu
    /// hareketlerden öğrenilmiş IBAN bağlarını (<c>SourceBankTransactionId</c>) <see cref="BatchSize"/>'lık partilerle siler; imha
    /// kaydı tetikleyici <c>account-rejected</c>, yapan <paramref name="actor"/>. Onay EN ÖNCE kaldırılır: arada koşan çekim turu
    /// o hesabın yeni hareketini yazmaz (çekim onayı sayfa başına taze okur; yazarsa yeniden basmak yeter, idempotent). Dekont
    /// bağı silinen ödemenin gap'i açılır (<see cref="ReopenGapsAsync"/>).
    /// <para>Başka hareketten gelen bağ kalır (Görev 10'un sonraki görev kuralı): reddedilen hesabın havalesi o bağı tazelemişse
    /// (kararı <c>IbanConfirmedAt</c> işaretli) başlangıç reddedilen hesap dışındaki kalan doğrulamalardan yeniden hesaplanır; kalan
    /// yoksa bağ silinir ve partiyle aynı SaveChanges'te imha kaydına girer (ölçüt <c>hesap reddi, kalan doğrulama yok</c>).
    /// Kural bağdaştırıcıda (<see cref="PaymentMatchReconciler.RewindIbanConfirmationsAsync"/>), kaldırmayla ortak.</para>
    /// <para>Hesap satırı kalır: yer tutucudur, Obifin'den geldikçe onaysız sayılır. Eşzamanlı eşleştirme yazısıyla çakışırsa bir
    /// kez yeniden denenir (düşen partinin izi ve imha kaydı atılır). <see cref="BankHasher"/> istemez.</para>
    /// </summary>
    public async Task<BankAccountRejectionResult> RejectAccountAsync(Guid accountId, string actor, CancellationToken ct)
    {
        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new ObifinValidationException(AccountNotFoundMessage);
        var (licenseId, obifinAccountId) = (account.LicenseId, account.ObifinAccountId);
        if (account.ApprovedAt is not null)
        {
            account.ApprovedAt = null;
            account.ApprovedBy = null;
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();

        const string criterion = "onaysız ya da reddedilen hesap (Ö-10)";
        void Record(string target, int count) => DataDestruction.Record(_db, licenseId, target, DataDestruction.Actions.Deleted,
            count, criterion, DataDestruction.Triggers.AccountRejected, actor);
        int transactions = 0, matches = 0, memories = 0, gaps = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                while (true)
                {
                    var batch = await _db.BankTransactions
                        .Where(t => t.LicenseId == licenseId && t.ObifinAccountId == obifinAccountId)
                        .OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
                    if (batch.Count == 0) break;
                    var ids = batch.Select(t => t.Id).ToList();
                    var batchMatches = await _db.PaymentMatches.Where(m => ids.Contains(m.BankTransactionId)).ToListAsync(ct);
                    // Görev 10 kuralı: bu partinin kararlarının tazelediği, başka havalenin öğrettiği bağlar. Silinen küme hesabın
                    // BÜTÜN hareketleri: sonraki partide silinecek havale kalan doğrulama sayılmaz.
                    var byId = batch.ToDictionary(t => t.Id);
                    var rewound = await _reconciler.RewindIbanConfirmationsAsync(
                        batchMatches.Select(m => (m, byId[m.BankTransactionId])).ToList(),
                        t => t.LicenseId == licenseId && t.ObifinAccountId == obifinAccountId, "hesap reddi",
                        DataDestruction.Triggers.AccountRejected, actor, ct);
                    var batchMemories = await _db.CustomerIbanMemories
                        .Where(m => m.LicenseId == licenseId && m.SourceBankTransactionId != null
                            && ids.Contains(m.SourceBankTransactionId.Value))
                        .ToListAsync(ct);
                    var reopened = await ReopenGapsAsync(licenseId, batchMatches, ct);
                    _db.PaymentMatches.RemoveRange(batchMatches);
                    _db.CustomerIbanMemories.RemoveRange(batchMemories);
                    _db.BankTransactions.RemoveRange(batch);
                    Record("PaymentMatches", batchMatches.Count);
                    Record("CustomerIbanMemories", batchMemories.Count);
                    Record("BankTransactions", batch.Count);
                    await _db.SaveChangesAsync(ct);
                    _db.ChangeTracker.Clear();
                    transactions += batch.Count; matches += batchMatches.Count;
                    memories += batchMemories.Count + rewound; gaps += reopened;
                    if (batch.Count < BatchSize) break;
                }
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                // Düşen partinin izi (silmeler, açılan gap, geri çekilen bağ ve imha kayıtları) atılır; kaydedilmiş partiler gitti.
                _db.ChangeTracker.Clear();
            }
        }
        _log.LogWarning("Banka hesabı reddedildi, verisi silindi — lisans={LicenseId}, hesap={ObifinAccountId}: hareket={Transactions}, " +
            "eşleşme={Matches}, IBAN bağı={Memories}, açılan gap={Gaps}", licenseId, obifinAccountId, transactions, matches, memories, gaps);
        return new BankAccountRejectionResult(transactions, matches, memories, gaps);
    }

    /// <summary>Dekont bağı silinecek eşleşmelerin ödemelerine açık gap (bağdaştırıcının kaldırma kuralı: dekont ölçümden
    /// düşmesin): gap'i varsa yeniden açılır, yoksa <see cref="PaymentMatchGapReason.UnlinkedByAdmin"/> yazılır. İzleyiciye ekler;
    /// partinin SaveChanges'i yazar. Açılan ya da yazılan gap sayısını döner.</summary>
    private async Task<int> ReopenGapsAsync(Guid licenseId, List<PaymentMatch> matches, CancellationToken ct)
    {
        var payments = matches.Where(m => m.PaymentId != null).Select(m => m.PaymentId!.Value).Distinct().ToList();
        if (payments.Count == 0) return 0;
        var existing = await _db.PaymentMatchGaps.Where(g => payments.Contains(g.PaymentId)).ToListAsync(ct);
        foreach (var g in existing) { g.ResolvedAt = null; g.ResolvedBankTransactionId = null; }
        var now = DateTimeOffset.UtcNow;
        foreach (var paymentId in payments.Except(existing.Select(g => g.PaymentId)))
            _db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = licenseId, PaymentId = paymentId,
                Reason = PaymentMatchGapReason.UnlinkedByAdmin, CreatedAt = now });
        return payments.Count;
    }
}
