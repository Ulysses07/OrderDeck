namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Elle eşlemenin IBAN hafızasına etkisi (<see cref="PaymentMatchReconciler.ManualMatchAsync"/>). Karar her durumda
/// yazılır; bu yalnız hareketin karşı IBAN'ının öğrenilip öğrenilmediğini ve nedenini söyler (Banka Eşleştirme sayfasının
/// bildirimi).</summary>
public enum ManualMatchIbanOutcome
{
    /// <summary>Yeni bağ yazıldı; aynı IBAN'ın kararsız satırları yeniden değerlendirildi.</summary>
    Learned,
    /// <summary>Bağ zaten bu müşteriye kayıtlıydı; süresi tazelendi (KVKK K-6). Bağ değişmediği için yeniden değerlendirme yok.</summary>
    Refreshed,
    /// <summary>Hareketin IBAN özeti yok: banka karşı IBAN'ı boş ya da maskeli gönderdi.</summary>
    NoValidIban,
    /// <summary>Özet KVKK Ö-7 kesme anından (22.10.2026) önce çekilmiş: maskeli değerden üretilmiş olabilir, öğretmez.</summary>
    LegacyHash,
    /// <summary>Dışlanan işlem kodlu hareket (POS tahsilatı vb.): karşı IBAN müşterinin değil.</summary>
    Excluded,
    /// <summary>IBAN başka bir müşteriye kayıtlı: bağ ezilmez.</summary>
    ConflictOtherCustomer,
}

/// <param name="Iban">Hareketin IBAN'ının öğrenilip öğrenilmediği.</param>
/// <param name="Reevaluated">Yeniden değerlendirilen kararsız satır (aynı lisans ve IBAN özeti, son 30 gün).</param>
/// <param name="ProposedToCustomer">Bunlardan şimdi bu müşteriye öneri alan.</param>
/// <param name="More">Tetik başına sınıra (<see cref="PaymentMatchReconciler.MaxReevaluationsPerTrigger"/>) takıldı; kalanını
/// telafi taraması (<see cref="BankMatchSweepJob"/>) yapar.</param>
/// <param name="ReevaluationFailed">Karar ve bağ kaydedildi, yeniden değerlendirme düştü; telafi taraması tamamlar.</param>
public sealed record ManualMatchResult(ManualMatchIbanOutcome Iban, int Reevaluated = 0, int ProposedToCustomer = 0, bool More = false,
    bool ReevaluationFailed = false);
