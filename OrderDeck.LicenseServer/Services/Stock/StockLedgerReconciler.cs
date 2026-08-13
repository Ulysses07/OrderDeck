namespace OrderDeck.LicenseServer.Services.Stock;

/// <summary>
/// Defterin toplandığı anahtar. Varyant null ise ürün seviyesi.
/// </summary>
public readonly record struct StockKey(Guid ProductId, Guid? ProductVariantId);

/// <summary>
/// Mutabakata giren siparişin stok açısından tek ilgilendiren yüzü. Fiyat,
/// müşteri, platform gibi alanlar bilerek yok — defter bunları umursamıyor.
/// </summary>
/// <param name="OrderId">Yalnız izlenebilirlik için; mutabakat kararını etkilemez.</param>
public sealed record LedgerOrderState(
    Guid OrderId,
    Guid? ProductId,
    Guid? ProductVariantId,
    bool IsShippingFee,
    bool IsCancelled,
    bool IsTentativeBackup);

/// <summary>Bir anahtara yazılacak fark. Sıfır fark asla üretilmez.</summary>
public sealed record LedgerDelta(StockKey Key, int QuantityDelta);

/// <summary>
/// Bir siparişin <b>olması gereken</b> stok etkisi ile <b>hâlihazırdaki</b> etkisi
/// arasındaki farkı üretir. Saf: veritabanı, saat, kimlik yok.
///
/// Neden "olay ekleme" değil de mutabakat: WPF aynı siparişi defalarca gönderir.
/// <c>LabelRepository</c>'de iptal (<c>MarkCancelled</c>), <b>iptali geri alma</b>
/// (<c>Uncancel</c>), basım (<c>MarkPrinted</c>) ve fiyat düzeltme
/// (<c>UpdatePrice</c>) hepsi <c>SyncedAt = NULL</c> yapıyor — yani satır yeniden
/// senkron kuyruğuna giriyor. "Sipariş geldi → −1 yaz" tasarımı ikinci gelişte
/// stoğu ikinci kez düşürürdü. Burada fark sıfırsa hiçbir şey yazılmaz.
///
/// Ters işlem de aynı fonksiyondan çıkar: iptal edilmiş siparişin olması gereken
/// etkisi 0'dır, defterde −1 duruyorsa fark +1 olur ve çağıran bunu
/// <c>CancelReturn</c> olarak yazar. Hiçbir satır silinmez.
/// </summary>
public static class StockLedgerReconciler
{
    /// <param name="order">Siparişin güncel hâli.</param>
    /// <param name="existing">
    /// Bu siparişin bugüne kadar yazılmış hareketlerinin anahtar bazlı
    /// <b>toplamı</b>. Boş sözlük "hiç yazılmamış" demektir.
    /// </param>
    public static IReadOnlyList<LedgerDelta> Reconcile(
        LedgerOrderState order,
        IReadOnlyDictionary<StockKey, int> existing)
    {
        var desired = Desired(order);
        var deltas = new List<LedgerDelta>();

        foreach (var (key, want) in desired)
        {
            existing.TryGetValue(key, out var have);
            if (want != have) deltas.Add(new LedgerDelta(key, want - have));
        }

        // Artık istenmeyen anahtarlar: iptal, varyant yeniden bağlama, ürün
        // değişikliği. Sıfırlanacak kadar ters hareket yazılır.
        foreach (var (key, have) in existing)
        {
            if (desired.ContainsKey(key)) continue;
            if (have != 0) deltas.Add(new LedgerDelta(key, -have));
        }

        return deltas;
    }

    /// <summary>
    /// Siparişin olması gereken stok etkisi. En fazla tek girdi döner; sipariş
    /// stoğu ilgilendirmiyorsa boş.
    /// </summary>
    private static Dictionary<StockKey, int> Desired(LedgerOrderState o)
    {
        var desired = new Dictionary<StockKey, int>();

        // Ürün bağlanmamış: kullanıcı kararı gereği satış YİNE OLUR, kart
        // "tanımlı değil" der; stok hareketi yazılmaz.
        if (o.ProductId is null) return desired;

        // Kargo ücreti satırı ürün değil.
        if (o.IsShippingFee) return desired;

        // İptal edilmiş siparişin olması gereken etkisi sıfırdır.
        if (o.IsCancelled) return desired;

        // Geçici yedek henüz satış değil: asıl satış iptal edilirse yedek
        // yükselir ve O ZAMAN düşülür. Şimdi düşmek çift sayım olurdu.
        if (o.IsTentativeBackup) return desired;

        // Miktar alanı yok: her etiket bir adettir.
        desired[new StockKey(o.ProductId.Value, o.ProductVariantId)] = -1;
        return desired;
    }
}
