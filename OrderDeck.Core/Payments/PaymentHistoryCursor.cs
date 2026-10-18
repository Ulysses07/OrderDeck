namespace OrderDeck.Core.Payments;

/// <summary>Ödeme geçmişi geri doldurmasının aşaması (spec 2026-10-13 §6.4 "Ödeme geçmişi", §8.2-6).</summary>
public enum PaymentHistoryPhase
{
    /// <summary>Yerel <c>PaymentJob</c>'lar <c>rowid</c> sırasıyla kirasız kayda çevrilip kuyruğa yazılıyor.</summary>
    Jobs = 0,

    /// <summary>Bütün işler kuyruğa yazıldı; kuyruğun boşalması ve <c>hello {paymentHistoryCompleted: true}</c>
    /// bekleniyor.</summary>
    Done = 1,

    /// <summary>Tamamlandı: <c>paymentHistoryCompleted</c> sunucuya gitti. Bu kopya ve bu lisans için bir daha iş
    /// yok.</summary>
    Completed = 2,
}

/// <summary>
/// <c>SyncCursor('payment-history-out', LicenseKey)</c> satırının anlamı (spec 2026-10-13 §3.2, §8.2-6; W3-06).
/// <see cref="OrderDeck.Core.Sales.SalesBackfillCursor"/>'ın deseni: tek satır aşamayı ve aşamanın son işlenen
/// <c>PaymentJob.rowid</c>'ini birlikte taşır.
///
/// <para><b>Kodlama:</b> <c>Seq = aşama × 10^12 + rowid</c>; <c>Seq = 2 × 10^12</c> tamamlanma işaretidir.
/// <c>LastId</c> imlecin ait olduğu kopya kimliğidir (<c>_meta.ReplicaId</c>): geri yüklemeden sonra kimlik döner. Önceki
/// kopya tamamladıysa yeni kopya işleri yeniden göndermez, yalnız kendi tamamlanmasını bildirir (<see cref="Done"/>);
/// tamamlamadıysa imleç baştan başlar (§8.7) — kayıt Id'leri işten türetildiği için önceki kopyanın gönderdikleri sunucuda
/// <c>unchanged</c>'dir. Lisans değişince imleç yeni lisansın anahtarındadır.</para>
///
/// <para>Satır yoksa ya da <c>Seq</c> çözülemiyorsa imleç baştandır (<see cref="Start"/>): geri doldurma idempotent,
/// baştan koşmak zararsızdır.</para>
/// </summary>
public readonly record struct PaymentHistoryCursor(PaymentHistoryPhase Phase, long RowId)
{
    /// <summary><c>SyncCursor.Name</c>.</summary>
    public const string Name = "payment-history-out";

    /// <summary>Aşamanın çarpanı.</summary>
    public const long PhaseFactor = 1_000_000_000_000L;

    public static PaymentHistoryCursor Start => new(PaymentHistoryPhase.Jobs, 0);

    public static PaymentHistoryCursor Done => new(PaymentHistoryPhase.Done, 0);

    public static PaymentHistoryCursor Completed => new(PaymentHistoryPhase.Completed, 0);

    public long Encode() => (long)Phase * PhaseFactor + RowId;

    /// <summary>Çözülemeyen değer (negatif, bilinmeyen aşama, bitmiş aşamada rowid) <c>null</c>.</summary>
    public static PaymentHistoryCursor? Decode(long seq)
    {
        if (seq < 0) return null;
        var phase = seq / PhaseFactor;
        if (phase > (long)PaymentHistoryPhase.Completed) return null;
        var rowId = seq % PhaseFactor;
        if (phase >= (long)PaymentHistoryPhase.Done && rowId != 0) return null;
        return new PaymentHistoryCursor((PaymentHistoryPhase)phase, rowId);
    }

    /// <summary><paramref name="rowId"/>'ye kadar işlendi.</summary>
    public PaymentHistoryCursor After(long rowId) => this with { RowId = rowId };
}
