namespace OrderDeck.App.Services.Sync;

/// <summary>Reddedilen bir etiket gönderimi: kimlik ve sunucunun nedeni (<c>session-released</c>,
/// <c>shipment-shipped</c>, <c>session-owned-elsewhere</c>, …). Ad ya da metin taşımaz.</summary>
public sealed record RejectedLabelPush(string LabelId, string? Reason);

/// <summary>
/// Bir <c>orders/sync</c> turunun sunucunun değerini uyguladığı satırları (spec 2026-10-13 §5.2, §5.7 "Reddedilen
/// push"; W1-08): <see cref="SessionOrderSyncService.PushOutcome"/> ile yayılır, arayüz operatörü uyarır. Yalnız bu
/// turda gerçekten uygulanan satırlar sayılır — uçuşta değişen satır sonraki turda yeniden gelir. Kimlik ve sayı
/// taşır; ad, kullanıcı adı ya da mesaj metni taşımaz (§9).
/// </summary>
/// <param name="Ignored">Bir ya da birkaç birimi yok sayılan, sunucunun değeri uygulanan satır sayısı.</param>
/// <param name="Rejected">Reddedilen satırlar (sunucunun değeri zorla uygulandı ya da reddedilen ekleme son durumda
/// onaylandı): "N değişiklik uygulanamadı".</param>
/// <param name="PrintDiscardLabelIds">Basılmış ama sunucuda başka bilgisayarda komutla iptal edilmiş bulunan etiketler
/// (K3 <c>cancelled-elsewhere</c>): "kâğıtları imha et".</param>
public sealed record SalesPushOutcome(
    int Ignored,
    IReadOnlyList<RejectedLabelPush> Rejected,
    IReadOnlyList<string> PrintDiscardLabelIds)
{
    /// <summary>Uyarı gerektiren bir şey var mı: ret ya da imha edilecek kâğıt.</summary>
    public bool NeedsAttention => Rejected.Count > 0 || PrintDiscardLabelIds.Count > 0;
}
