using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// İlgili kişinin silme talebinde banka verisinin ortak kuralları (KVKK K-7). KVKK silmesi (ShopperPurgeService) ve IBAN ile
/// banka veri talebi aracı (BankSubjectRequestService) aynı kuralları kullanır. Yeniden uygulanabilir (idempotent); kaydetmez.
/// <para>Hareket: açıklama, gönderen adı ve ham kayıt silinir; tutar, tarih, kısaltılmış IBAN ve tanıma kodu kalır (özetin
/// silinmesi avukat kararı). Eşleşme: kişinin müşteri kimliği ve gerekçe metni silinir; satır ölçüm kaydı olarak kalır, ölçüm
/// sonucu durumdan okunur (<see cref="PaymentMatchMetrics"/>). Silinen bağın özetinden gelen hareketin kararsız satırında
/// IBAN'dan türeyen öneri de gider (<see cref="IbanDerivedProposal"/>): kişi düzeyi eşleştirici bağı kişinin öbür satırına ve
/// kopyadaki bağı asıl kayda sayar, kimlik kuralı o öneriyi bulmaz. Elle eşleme denetimi: satır kalır, müşteriyi gösteren ayrıntı
/// silinir (<see cref="ManualMatchAudits"/>).</para>
/// </summary>
public static class BankSubjectScrub
{
    // İmha kaydı hedefleri (KVKK Ö-13): aynı sütunun imhası her silme yolunda tek hedef adıyla yazılır — dış özet, geri yükleme
    // prosedürü ve avukat paketi hedefe göre gruplar. KVKK silmesi ve banka veri talebi aracı bunları ortak kullanır.
    /// <summary>Silinen IBAN bağı (satır).</summary>
    public const string IbanMemoryTarget = "CustomerIbanMemories";
    /// <summary>Boşaltılan hareket metni (<see cref="Transaction"/>).</summary>
    public const string TransactionTextTarget = "BankTransactions.Description+CounterpartyName+RawJson";
    /// <summary>Kişiye bağlı eşleşme satırında müşteri kimliği ve kanıt (<see cref="MatchForCustomer"/>); IBAN'dan türeyen önerisi
    /// boşalan satır da (<see cref="IbanDerivedProposal"/>).</summary>
    public const string LinkedMatchTarget = "PaymentMatches.WpfCustomerId+Evidence";
    /// <summary>Kişiye bağlı olmayan eşleşme satırında yalnız kanıt (<see cref="MatchEvidence"/>; önerisiz satırın IBAN'dan
    /// türeyen kanıtı da, <see cref="IbanDerivedProposal"/>).</summary>
    public const string MatchEvidenceTarget = "PaymentMatches.Evidence";

    /// <summary>Eşleştiricinin IBAN hafızasından gelen kanıt biçimlerinin ortak alt dizesi (<c>iban-memory</c>,
    /// <c>username={anahtar};iban-memory</c>, <c>conflict:username={anahtar},iban-memory</c>, <c>iban-memory-shared:{n}</c>):
    /// telafi taramasının 2. aşamasıyla aynı sözleşme. Biçim değişirse burası da değişir.</summary>
    private const string IbanMemoryEvidence = "iban-memory";

    /// <summary>Kaydedildiğinde bir şey silecek mi: açıklama, gönderen adı ya da ham kayıt hâlâ dolu. Saklama kuralının çoktan
    /// boşalttığı hareket "silindi" diye sayılmaz.</summary>
    public static bool HasText(BankTransaction tx) => tx.Description is not null || tx.CounterpartyName is not null || tx.RawJson is not null;

    public static void Transaction(BankTransaction tx, DateTimeOffset now)
    {
        tx.Description = null;
        tx.CounterpartyName = null;
        tx.RawJson = null;
        tx.DescriptionPurgedAt ??= now;
    }

    /// <summary>Satır <paramref name="wpfCustomerId"/>'ye bağlı mı (öneri ya da gerçek müşteri).</summary>
    public static bool References(PaymentMatch match, Guid wpfCustomerId)
        => match.ProposedWpfCustomerId == wpfCustomerId || match.ActualWpfCustomerId == wpfCustomerId;

    /// <summary>Kanıt her durumda silinir; kimlikler yalnız <paramref name="wpfCustomerId"/>'ye eşitse. Satır o müşteriye bağlı
    /// değilse <see cref="MatchEvidence"/> ile aynıdır: kişinin birden çok müşteri kimliği için sırayla çağrılabilir.</summary>
    public static void MatchForCustomer(PaymentMatch match, Guid wpfCustomerId, DateTimeOffset now)
    {
        match.Evidence = null;
        if (match.ProposedWpfCustomerId == wpfCustomerId)
        {
            match.ProposedWpfCustomerId = null;
            if (match.Status == PaymentMatchStatus.Proposed)
            {
                // Kararsız öneri kişiyle birlikte gider: satır tutarlı "öneri yok" durumuna iner. IBAN desteği de öneri alanıdır
                // (eşleştirici öneri yokken hep false yazar); eşleştiricinin sürümü kalır, silme yeniden hesap değildir.
                match.Status = PaymentMatchStatus.NoProposal;
                match.Layer = PaymentMatchLayer.None;
                match.Confidence = 0m;
                match.SupportedByIbanMemory = false;
            }
            else if (match.Status == PaymentMatchStatus.ManualOnly)
            {
                // Öneriden farklı müşteriye elle verilmiş karar ölçümde çelişki sayılıyordu (ManualOnly + öneri). Öneri silinince
                // sonuç kaybolmasın: Classify ikisini aynı sayar.
                match.Status = PaymentMatchStatus.Contradicted;
            }
        }
        if (match.ActualWpfCustomerId == wpfCustomerId)
        {
            match.ActualWpfCustomerId = null;
            // Kararın doğruladığı IBAN bağı kişinin IBAN hafızasıyla birlikte silinir; işaret artık hiçbir bağa ait değildir
            // (kararı boşaltan yol işareti de boşaltır, Görev 10 kuralı). Durum, dekont bağı ve karar anı ölçüm için kalır.
            // Karar anı bağdaştırıcının karar kilididir (PaymentMatchReconciler): müşterisi boşalan karar başka bir kişinin
            // dekontuna aday olmaz, elle eşleme de önce kaldırma ister.
            match.IbanConfirmedAt = null;
        }
        match.UpdatedAt = now;
    }

    public static void MatchEvidence(PaymentMatch match, DateTimeOffset now)
    {
        match.Evidence = null;
        match.UpdatedAt = now;
    }

    /// <summary>
    /// KVKK silmesinde bağı silinen IBAN özetinden gelen hareketin eşleşme satırı (S2b planı Karar 7; sahip kararı 27.11.2026).
    /// Kişi düzeyi eşleştirici bağı kişinin öbür satırına (aynı <c>GroupId</c>) ve kopyadaki bağı asıl kayda sayar: öneri bağın
    /// müşterisini değil kişinin başka bir kaydını gösterebilir, kimlik kuralı (<see cref="MatchForCustomer"/>) onu bulmaz, öneri
    /// "IBAN destekli" kalırdı. Kural yalnız kararsız satıra (gerçek müşteri ve karar anı boş, Decision ≠ None değil) ve IBAN'dan
    /// türeyen iz taşıyan satıra uygulanır: katman IBAN hafızası, IBAN desteği ya da kanıtta <c>iban-memory</c>. Önerilen müşteri
    /// özetin silinmeden kalan bir bağının müşterisi değilse öneri boşalır (<see cref="MatchForCustomer"/>'ın kararsız öneri
    /// kolunun eşi: öneri yok, katman yok, güven 0, IBAN desteği yok), kanıt da. Sürüm kalır: silme yeniden hesap değildir. Öneri
    /// ve kanıtı zaten boş satıra yazılmaz. Kalan bağ satır düzeyinde karşılaştırılır: kalan bağ kişinin başka satırındaysa öneri
    /// fazla boşalır (temkinli yön); dar tekil <c>(LicenseId, IbanHash)</c> dururken kalan bağ olmaz. İnsan kararlı ve karar
    /// katmanındaki satıra bu kural dokunmaz (kararları kişiyle aramak S4). Yeniden uygulanabilir.
    /// </summary>
    /// <param name="remainingBondCustomers">Satırın hareketinin özetinin silinmeden kalan bağlarının müşterileri.</param>
    /// <returns>Satıra yazdıysa imha kaydının hedefi: öneri boşaldıysa <see cref="LinkedMatchTarget"/>, yalnız kanıt boşaldıysa
    /// <see cref="MatchEvidenceTarget"/>; dokunmadıysa null.</returns>
    public static string? IbanDerivedProposal(PaymentMatch match, IReadOnlyCollection<Guid> remainingBondCustomers,
        DateTimeOffset now)
    {
        if (match.ActualWpfCustomerId is not null || match.DecidedAt is not null || match.Decision != PaymentMatchDecision.None)
            return null;
        var ibanTrace = match.Layer == PaymentMatchLayer.IbanMemory || match.SupportedByIbanMemory
            || match.Evidence?.Contains(IbanMemoryEvidence, StringComparison.Ordinal) == true;
        if (!ibanTrace) return null;
        if (match.ProposedWpfCustomerId is { } proposed && remainingBondCustomers.Contains(proposed)) return null;
        var target = match.ProposedWpfCustomerId is not null ? LinkedMatchTarget
            : match.Evidence is not null ? MatchEvidenceTarget
            : null;
        if (target is null) return null;
        match.ProposedWpfCustomerId = null;
        if (match.Status == PaymentMatchStatus.Proposed) match.Status = PaymentMatchStatus.NoProposal;
        match.Layer = PaymentMatchLayer.None;
        match.Confidence = 0m;
        match.SupportedByIbanMemory = false;
        match.Evidence = null;
        match.UpdatedAt = now;
        return target;
    }

    /// <summary>
    /// <see cref="IbanDerivedProposal"/>'ı silinen bağların (<paramref name="deletedBonds"/>: silinmek üzere işaretli ya da arada
    /// silinmiş olabilir, yalnız lisansı, özeti ve kimliği okunur) özetlerinden gelen hareketlerin eşleşme satırlarına uygular.
    /// Satırlar izlenerek okunur; izlenen satır izleyicideki değeriyle döner, kural onu çağıranın öbür kurallarından SONRAki
    /// haliyle görür. Kalan bağlar izlenmeden okunur, silinenler kimlikle dışlanır. Kaydetmez. Yazılan satırları ve imha kaydı
    /// hedeflerini döner. KVKK silmesi (ShopperPurgeService) kullanır.
    /// </summary>
    public static async Task<List<(PaymentMatch Match, string Target)>> IbanDerivedProposalsAsync(LicenseDbContext db,
        IEnumerable<CustomerIbanMemory> deletedBonds, DateTimeOffset now, CancellationToken ct)
    {
        var written = new List<(PaymentMatch Match, string Target)>();
        foreach (var license in deletedBonds.GroupBy(b => b.LicenseId))
        {
            var licenseId = license.Key;
            var deletedIds = license.Select(b => b.Id).ToHashSet();
            var hashes = license.Select(b => b.IbanHash).Distinct(StringComparer.Ordinal).ToList();
            var remaining = (await db.CustomerIbanMemories.AsNoTracking()
                    .Where(b => b.LicenseId == licenseId && hashes.Contains(b.IbanHash))
                    .Select(b => new { b.Id, b.IbanHash, b.WpfCustomerId })
                    .ToListAsync(ct))
                .Where(b => !deletedIds.Contains(b.Id))
                .ToLookup(b => b.IbanHash, b => b.WpfCustomerId, StringComparer.Ordinal);
            var rows = await (from m in db.PaymentMatches
                              join t in db.BankTransactions on m.BankTransactionId equals t.Id
                              where t.LicenseId == licenseId && t.CounterpartyIbanHash != null
                                    && hashes.Contains(t.CounterpartyIbanHash)
                              select new { Match = m, Hash = t.CounterpartyIbanHash })
                .ToListAsync(ct);
            foreach (var row in rows)
                if (IbanDerivedProposal(row.Match, remaining[row.Hash!].ToList(), now) is { } target)
                    written.Add((row.Match, target));
        }
        return written;
    }

    /// <summary>Elle eşleme denetim satırının ayrıntısının imha kaydı hedefi. bank-data-cleanup'ın eski satır çevirmesi de bunu
    /// yazar: aynı sütunun imhası tek hedef adıyla gruplanır.</summary>
    public const string ManualMatchAuditTarget = "AuditLogs.Details";

    /// <summary>Elle eşleme denetim satırının (<see cref="AuditEvents.BankMatchManual"/>) müşteriyi gösteren ayrıntısı:
    /// <c>{"wpfCustomerId":"…"}</c>. Banka Eşleştirme sayfası aynı metni AuditService'le yazar (camelCase); iki taraf da testte
    /// metin olarak sabit (<c>AdminBankaEslestirmePageTests</c>, <c>ShopperPurgeServiceTests</c>).</summary>
    public static string ManualMatchAuditDetails(Guid wpfCustomerId) => JsonSerializer.Serialize(new { wpfCustomerId });

    /// <summary>Bu müşterilerden birini gösteren elle eşleme denetim satırları (KVKK Ö-14). Satır 24 ay durur ve hareketi
    /// (tutar, tarih, maskeli IBAN) hedefler: eşleşme satırında müşteriyi boşaltan silme bunların ayrıntısını da boşaltır
    /// (<see cref="ManualMatchAudit"/>), yoksa "bu kişi bu havaleyi ödedi" bağı denetimde yaşardı. Hedef hareket (TargetId)
    /// süzülmez: hareket 180 günde silinir, denetim satırı kalır. AuditLogs'ta ayrıntı indeksi yok, sorgu tabloyu tarar (seyrek,
    /// elle tetiklenen silme).</summary>
    public static IQueryable<AuditLogEntry> ManualMatchAudits(IQueryable<AuditLogEntry> audits, IEnumerable<Guid> wpfCustomerIds)
    {
        var details = wpfCustomerIds.Select(ManualMatchAuditDetails).Distinct().ToList();
        return audits.Where(a => a.EventType == AuditEvents.BankMatchManual && a.Details != null && details.Contains(a.Details));
    }

    /// <summary>Denetim satırında müşterinin kimliği gider; satır (hangi yöneticinin hangi havaleyi ne zaman elle eşlediği)
    /// hesap verebilirlik için kalır.</summary>
    public static void ManualMatchAudit(AuditLogEntry audit) => audit.Details = null;

    /// <summary>
    /// Verilen asıl kayıtlar ve onlara birleştirilmiş kopyaları (eski Id'ler; asıl kayıtlar dahil). Kopyalar varsayılan
    /// sorgulardan gizli; burada açıkça istenir ve zincir <see cref="SalesCustomerDirectory.MaxHops"/> adım geriye izlenir
    /// (CustomerIdResolver'ın ileri yönde izlediği kadar). Birleştirme eşleşmeyi ve IBAN bağını asıl kayda taşır, eski Id'yle
    /// yazılmış satırları taşımaz: geç sipariş ve birleştirmeden önce yazılmış elle eşleme denetimi (<see cref="ManualMatchAudits"/>;
    /// elle eşleme o günkü asıl kaydı yazar). KVKK silmesi ve banka veri talebinin silmesi kullanır.
    /// </summary>
    public static async Task<HashSet<Guid>> WithMergedCopiesAsync(LicenseDbContext db, Guid licenseId, IEnumerable<Guid> canonical,
        CancellationToken ct)
    {
        var ids = canonical.ToHashSet();
        var frontier = ids.ToList();
        for (var hop = 0; hop < SalesCustomerDirectory.MaxHops && frontier.Count > 0; hop++)
        {
            var targets = frontier;
            var copies = await db.WpfCustomerProjections
                .IgnoreQueryFilters()
                .Where(p => p.LicenseId == licenseId
                    && p.MergedIntoId != null
                    && targets.Contains(p.MergedIntoId.Value))
                .Select(p => p.Id)
                .ToListAsync(ct);
            frontier = copies.Where(ids.Add).ToList();
        }
        return ids;
    }
}
