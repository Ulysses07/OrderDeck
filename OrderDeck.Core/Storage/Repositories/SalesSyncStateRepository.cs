using Dapper;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Yerel <c>SalesSyncState</c> satırı (göç 049; spec 2026-10-13 §3.2). Zamanlar unix saniye.</summary>
public sealed record SalesSyncStateRow(
    int Mode,
    long? ModeSeenAt,
    int? ShippingVersion,
    decimal? FreeShippingThreshold,
    decimal? ShippingFee,
    bool ScrubOrderText,
    long? FirstCatchUpAt,
    long? AggregatesDerivedAt,
    long? ShipmentsDerivedAt,
    long? PaymentHistorySentAt);

/// <summary>
/// Satış senkronunun yerel durumu (tek satır, <c>SalesSyncState.Id = 1</c>).
///
/// <para><b>Yazıcılar:</b> W1 yalnız sunucunun bildirdiği değerleri saklar (<see cref="SaveServerState"/>:
/// kargo ayarının önbelleği ve KVKK boşaltma ayarı). <c>Mode</c>, <c>ModeSeenAt</c> ve
/// <c>FirstCatchUpAt</c>'i W1 HİÇ yazmaz: kip geçişleri (§8.4 açılış, §8.5 <c>disable</c> turu) W2'nindir.
/// Mod Off'ta (bugün her lisans) <c>Mode</c> 0 kalır.</para>
///
/// <para><b>Görülen kip (§2, §8.5):</b> istemci 0'ı ancak Off turu tamamen bitince bildirir — yerel
/// <c>Mode = 0</c> ∧ <c>PendingSalesCommand</c> boş; aksi hâlde 1 (<see cref="GetSeenMode"/>).</para>
/// </summary>
public sealed class SalesSyncStateRepository
{
    private readonly IDbConnectionFactory _factory;

    public SalesSyncStateRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>Yerel kip: 0 = Off, 1 = On. Satır yoksa 0.</summary>
    public int GetMode()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>("SELECT COALESCE((SELECT Mode FROM SalesSyncState WHERE Id = 1), 0)");
    }

    /// <summary>Çevrimdışı kuyrukta bekleyen satış komutu var mı (§5.5).</summary>
    public bool HasPendingSalesCommands()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>("SELECT EXISTS (SELECT 1 FROM PendingSalesCommand)") == 1;
    }

    /// <summary>
    /// v2 isteklerinin taşıdığı görülen kip: yerel <c>Mode = 0</c> ∧ <c>PendingSalesCommand</c> boş ise 0,
    /// aksi hâlde 1. Tek sorgu: iki okuma arasına giren bir yazım yarım bir karar üretmez.
    /// </summary>
    public int GetSeenMode()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            """
            SELECT CASE
                WHEN COALESCE((SELECT Mode FROM SalesSyncState WHERE Id = 1), 0) = 0
                     AND NOT EXISTS (SELECT 1 FROM PendingSalesCommand)
                THEN 0 ELSE 1 END
            """);
    }

    public SalesSyncStateRow Get()
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingleOrDefault<Row>(
            """
            SELECT Mode, ModeSeenAt, ShippingVersion, FreeShippingThreshold, ShippingFee, ScrubOrderText,
                   FirstCatchUpAt, AggregatesDerivedAt, ShipmentsDerivedAt, PaymentHistorySentAt
            FROM SalesSyncState WHERE Id = 1
            """) ?? throw new InvalidOperationException("SalesSyncState satırı yok (göç 049).");
        return new SalesSyncStateRow(
            (int)row.Mode, row.ModeSeenAt, (int?)row.ShippingVersion,
            ToDecimal(row.FreeShippingThreshold), ToDecimal(row.ShippingFee), row.ScrubOrderText != 0,
            row.FirstCatchUpAt, row.AggregatesDerivedAt, row.ShipmentsDerivedAt, row.PaymentHistorySentAt);
    }

    /// <summary>
    /// <c>sales/state</c>'in bildirdiği lisans geneli kargo ayarını (§6.2; ayar hiç yazılmadıysa üçü de
    /// <c>null</c>) ve KVKK boşaltma ayarını (§9) saklar. Değerler aynıysa yazmaz: 30 saniyelik turun
    /// her seferinde yazma kilidi almaması için.
    /// </summary>
    /// <returns>Satır değiştiyse true.</returns>
    public bool SaveServerState(int? shippingVersion, decimal? threshold, decimal? fee, bool scrubOrderText)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            """
            UPDATE SalesSyncState
            SET ShippingVersion = @shippingVersion,
                FreeShippingThreshold = @threshold,
                ShippingFee = @fee,
                ScrubOrderText = @scrub
            WHERE Id = 1
              AND (ShippingVersion IS NOT @shippingVersion
                   OR FreeShippingThreshold IS NOT @threshold
                   OR ShippingFee IS NOT @fee
                   OR ScrubOrderText IS NOT @scrub)
            """,
            new
            {
                shippingVersion,
                // REAL kolon: decimal sürücüde metin olarak bağlanırdı; karşılaştırma sayısal olsun.
                threshold = (double?)threshold,
                fee = (double?)fee,
                scrub = scrubOrderText ? 1 : 0,
            }) == 1;
    }

    private static decimal? ToDecimal(double? value) => value is { } v ? (decimal)v : null;

    private sealed class Row
    {
        public long Mode { get; set; }
        public long? ModeSeenAt { get; set; }
        public long? ShippingVersion { get; set; }
        public double? FreeShippingThreshold { get; set; }
        public double? ShippingFee { get; set; }
        public long ScrubOrderText { get; set; }
        public long? FirstCatchUpAt { get; set; }
        public long? AggregatesDerivedAt { get; set; }
        public long? ShipmentsDerivedAt { get; set; }
        public long? PaymentHistorySentAt { get; set; }
    }
}
