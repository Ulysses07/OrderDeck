using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// İlgili kişinin silme talebinde banka verisinin ortak kuralları (KVKK K-7). KVKK silmesi (ShopperPurgeService) ve IBAN ile
/// banka veri talebi aracı (BankSubjectRequestService) aynı kuralları kullanır. Yeniden uygulanabilir (idempotent); kaydetmez.
/// <para>Hareket: açıklama, gönderen adı ve ham kayıt silinir; tutar, tarih, kısaltılmış IBAN ve tanıma kodu kalır (özetin
/// silinmesi avukat kararı). Eşleşme: kişinin müşteri kimliği ve gerekçe metni silinir; satır ölçüm kaydı olarak kalır, ölçüm
/// sonucu durumdan okunur (<see cref="PaymentMatchMetrics"/>).</para>
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
}
