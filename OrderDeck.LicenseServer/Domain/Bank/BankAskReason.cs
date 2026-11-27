namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>
/// Havalenin neden sorulduğu: kapalı küme (banka havalesi spec'i 2026-10-10 §5.2, sıra oradaki). String saklanır, ad
/// değişmez: <see cref="BankAskReasons.Parse"/> bilinmeyen adı atlar, adı değişen neden sessizce kaybolur.
/// </summary>
public enum BankAskReason
{
    /// <summary>Öneri yalnız açıklamadaki kullanıcı adına dayanıyor.</summary>
    UsernameOnly = 0,

    /// <summary>Öneri yalnız ad benzerliğine dayanıyor.</summary>
    NameOnly = 1,

    /// <summary>IBAN bu müşteri için havale onayıyla henüz onaylanmadı.</summary>
    IbanNotConfirmed = 2,

    /// <summary>IBAN birden çok kişiye düşüyor.</summary>
    IbanShared = 3,

    /// <summary>IBAN başka müşteriye kayıtlı, açıklamadaki kullanıcı adı farklı.</summary>
    IbanOtherCustomer = 4,

    /// <summary>IBAN için daha önce "Hayır" ya da geri alma var.</summary>
    IbanDisputed = 5,

    /// <summary>Gönderen IBAN'ı tam gelmedi; bu havaleden öğrenilemez.</summary>
    IbanHashUnverified = 6,

    /// <summary>Önerilen müşteri birleştirilmiş ya da silinmiş.</summary>
    CustomerNotCanonical = 7,

    /// <summary>Açık istek yok ya da istenen tutar bilinmiyor.</summary>
    NoExpectation = 8,

    /// <summary>İstenen tutar ile gelen tutar farklı.</summary>
    AmountMismatch = 9,

    /// <summary>Açık isteklerden hangisinin ödendiği belirsiz.</summary>
    AmbiguousExpectations = 10,

    /// <summary>İstek kaydı güvenilir değil (§2.1).</summary>
    ExpectationUnreliable = 11,

    /// <summary>Bu kişiye şu anda ödeme isteği gönderiliyor (canlı kira).</summary>
    RequestInProgress = 12,

    /// <summary>Satış senkronu kapalı ya da bir bilgisayar güncel değil.</summary>
    SalesSyncNotReady = 13,

    /// <summary>Aynı tutarda bekleyen dekont var.</summary>
    PendingReceipt = 14,

    /// <summary>Müşteride havaleye ya da isteğe bağlanmamış onaylı ödeme var.</summary>
    UnlinkedApprovedPayment = 15,

    /// <summary>Başka bir kişinin doğrulanmamış dekontuyla aynı tutar.</summary>
    ReceiptOtherCustomer = 16,

    /// <summary>Havale 3 günden eski.</summary>
    TooOld = 17,
}

/// <summary>Sorma nedenlerinin saklama biçimi: ayrık, enum sırasıyla, virgülle ayrılmış adlar; boş küme null.</summary>
public static class BankAskReasons
{
    /// <summary>Sütun uzunluğu. Bütün nedenler birlikte sığar.</summary>
    public const int MaxLength = 512;

    private static readonly Dictionary<string, BankAskReason> ByName =
        Enum.GetValues<BankAskReason>().ToDictionary(r => r.ToString(), StringComparer.Ordinal);

    public static string? Format(IEnumerable<BankAskReason> reasons)
    {
        var names = reasons.Distinct().Order().Select(r => r.ToString()).ToList();
        return names.Count == 0 ? null : string.Join(',', names);
    }

    /// <summary>Yalnız tam adı tanır (<c>Enum.TryParse</c> sayıyı ve boşluklu adı da kabul ederdi). Bilinmeyen kod atlanır:
    /// küme büyüyüp imaj geri alınırsa satır okunur kalsın.</summary>
    public static IReadOnlyList<BankAskReason> Parse(string? stored)
    {
        var reasons = new List<BankAskReason>();
        if (string.IsNullOrEmpty(stored)) return reasons;
        foreach (var name in stored.Split(','))
            if (ByName.TryGetValue(name, out var reason)) reasons.Add(reason);
        return reasons;
    }
}
