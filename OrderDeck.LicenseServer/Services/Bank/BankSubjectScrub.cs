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
/// sonucu durumdan okunur (<see cref="PaymentMatchMetrics"/>). Elle eşleme denetimi: satır kalır, müşteriyi gösteren ayrıntı
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
    /// <summary>Kişiye bağlı eşleşme satırında müşteri kimliği ve kanıt (<see cref="MatchForCustomer"/>).</summary>
    public const string LinkedMatchTarget = "PaymentMatches.WpfCustomerId+Evidence";
    /// <summary>Kişiye bağlı olmayan eşleşme satırında yalnız kanıt (<see cref="MatchEvidence"/>).</summary>
    public const string MatchEvidenceTarget = "PaymentMatches.Evidence";

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
                // Kararsız öneri kişiyle birlikte gider: satır tutarlı "öneri yok" durumuna iner.
                match.Status = PaymentMatchStatus.NoProposal;
                match.Layer = PaymentMatchLayer.None;
                match.Confidence = 0m;
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
