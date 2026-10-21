using System.Text.Json;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;

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
}
