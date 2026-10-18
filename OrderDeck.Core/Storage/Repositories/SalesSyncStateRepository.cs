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

/// <summary>Kargo ayarı sağlayıcısının okuması (<see cref="SalesSyncStateRepository.GetShippingCache"/>; spec 2026-10-13
/// §6.2): yerel kip ve <c>sales/state</c>'in sakladığı lisans geneli kargo ayarı. Ayar sunucuda hiç yazılmadıysa üç
/// alan da boş.</summary>
/// <param name="CopyPending"><c>disable</c> turunun kargo ayarı kopyası bekliyor
/// (<see cref="SalesSyncStateRepository.ModeOffShippingCopyMarker"/>): kip 0 yazıldı, lisans geneli değer yerel ayara henüz
/// yazılamadı.</param>
public sealed record SalesShippingCache(
    int Mode, int? Version, decimal? FreeShippingThreshold, decimal? ShippingFee, bool CopyPending = false);

/// <summary>Mod On açılışının 1. adımında boşaltılan giden kuyrukların kalanı (spec 2026-10-13 §8.4-1;
/// <see cref="SalesSyncStateRepository.GetOpeningBacklog"/>). Canlı (açık) yayının oturumu ve etiketleri sayılmaz: iki
/// kipte aynı yargılanırlar.</summary>
/// <param name="Customers">Gönderim imlecinin üstünde, verilen tavanın altında kalan, silinmemiş müşteriler.</param>
/// <param name="EndedSessions">Gönderilmemiş, bitmiş kendi yayınları (devri sunucudan görülmemiş olanlar dahil).</param>
/// <param name="EndedSessionLabels">Bitmiş yayınların gönderilmemiş etiketleri.</param>
/// <param name="Shipments">Gönderilmemiş kargo dosyaları.</param>
/// <param name="Payments">Gönderilmemiş dekontlar.</param>
/// <param name="PaymentRecords">Gönderilmeyi bekleyen ödeme kayıtları (<c>PendingPaymentRecord</c>; App'teki
/// <c>PaymentRecordSender</c> gönderir, açılışın ilk kuyruğu).</param>
public sealed record SalesOpeningBacklog(
    int Customers, int EndedSessions, int EndedSessionLabels, int Shipments, int Payments, int PaymentRecords)
{
    public bool IsEmpty =>
        Customers + EndedSessions + EndedSessionLabels + Shipments + Payments + PaymentRecords == 0;
}

/// <summary><see cref="SalesSyncStateRepository.TryEnableMode"/>'un sonucu.</summary>
public enum SalesModeEnableStatus
{
    /// <summary>Yerel <c>Mode = 1</c> yazıldı (bu açılış).</summary>
    Enabled,
    /// <summary>Yerel kip zaten 1: hiçbir şey yazılmadı.</summary>
    AlreadyOn,
    /// <summary>Kilit altında kargo kuyruğu ya da bitmiş yayınların etiket kuyruğu boş değildi: hiçbir şey yazılmadı,
    /// yerel kip 0 kaldı.</summary>
    Backlog,
}

/// <param name="PendingShipments">Kilit altında sayılan gönderilmemiş dosya (yalnız <see cref="SalesModeEnableStatus.Backlog"/>).</param>
/// <param name="PendingLabels">Kilit altında sayılan, bitmiş yayınların gönderilmemiş etiketi.</param>
/// <param name="SessionsToServer">Hemen <c>server</c> olan, devri yerelde bilinen kendi bitmiş yayınları.</param>
public sealed record SalesModeEnableResult(
    SalesModeEnableStatus Status, int PendingShipments, int PendingLabels, int SessionsToServer);

/// <summary>İlk yetişmenin ufukta tamamlanması (<see cref="SalesSyncStateRepository.CompleteFirstCatchUp"/>).</summary>
/// <param name="Aggregates">Bütün müşterilerin toplamlarının yeniden hesabı (§6.1).</param>
/// <param name="CatchUpOverwrites">Bu açılıştan beri kendi etiketinde sunucunun değeriyle ezilen birim izleri
/// (<see cref="SalesConflictLog.CatchUpOverwrite"/>).</param>
public sealed record SalesFirstCatchUpResult(AggregateRecomputeResult Aggregates, int CatchUpOverwrites);

/// <summary>
/// Satış senkronunun yerel durumu (tek satır, <c>SalesSyncState.Id = 1</c>).
///
/// <para><b>Yazıcılar:</b> sunucunun bildirdiği değerler (<see cref="SaveServerState"/>: kargo ayarının önbelleği ve
/// KVKK boşaltma ayarı) her turda. <c>Mode</c>, <c>ModeSeenAt</c> ve <c>FirstCatchUpAt</c>'i yalnız kip geçişleri
/// yazar: mod On açılışı (§8.4; W2-10, <see cref="TryEnableMode"/> ve <see cref="CompleteFirstCatchUp"/>) ve
/// <c>disable</c> turu (§8.5). Mod Off'ta (bugün her lisans) <c>Mode</c> 0 kalır.</para>
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

    /// <summary>
    /// <c>disable</c> turunun 2. adımından sonraki kargo ayarı kopyasının (§8.5-2, §6.2) kalıcı "bekliyor" işareti:
    /// <c>SyncCursor</c> satırı (lisanssız, <c>LicenseKey = ''</c>; <c>Seq</c> = turun <c>ModeSeenAt</c>'i). 2. adım
    /// (<see cref="SalesFeedRepository.ApplyModeOff"/>) onu yerel kip 0 ile AYNI işlemde yazar, kopya başarıyla
    /// yazılınca silinir (<see cref="ClearModeOffShippingCopyPending"/>). İşlemle kopya arasında süreç kapanırsa
    /// açılışta ve turun başında kopya yeniden denenir — tur bir daha koşmadığı (ModeSeenAt yazıldı) için bellek içi
    /// işaret kaybolurdu.
    /// </summary>
    public const string ModeOffShippingCopyMarker = "sales-modeoff-shipping-copy";

    /// <summary><see cref="ModeOffShippingCopyMarker"/> var mı.</summary>
    public bool IsModeOffShippingCopyPending()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>(
            "SELECT EXISTS (SELECT 1 FROM SyncCursor WHERE Name = @name AND LicenseKey = '')",
            new { name = ModeOffShippingCopyMarker }) == 1;
    }

    /// <summary>Kargo ayarı kopyası yazıldı: <see cref="ModeOffShippingCopyMarker"/> silinir.</summary>
    public void ClearModeOffShippingCopyPending()
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM SyncCursor WHERE Name = @name AND LicenseKey = ''",
            new { name = ModeOffShippingCopyMarker });
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
    /// Yerel kip ve lisans geneli kargo ayarının önbelleği, TEK sorguda (<c>ShippingSettingsProvider</c>, §6.2): kip ile
    /// değer aynı satır okumasından gelir; <c>disable</c> turunun kopya işareti
    /// (<see cref="ModeOffShippingCopyMarker"/>) da aynı sorguda. Satır yoksa kip 0 (<see cref="GetMode"/> gibi) ve önbellek
    /// boş.
    /// </summary>
    public SalesShippingCache GetShippingCache()
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingleOrDefault<ShippingRow>(
            """
            SELECT Mode, ShippingVersion, FreeShippingThreshold, ShippingFee,
                   EXISTS (SELECT 1 FROM SyncCursor WHERE Name = @name AND LicenseKey = '') AS CopyPending
            FROM SalesSyncState WHERE Id = 1
            """, new { name = ModeOffShippingCopyMarker });
        return row is null
            ? new SalesShippingCache(0, null, null, null)
            : new SalesShippingCache((int)row.Mode, (int?)row.ShippingVersion,
                ToDecimal(row.FreeShippingThreshold), ToDecimal(row.ShippingFee), row.CopyPending != 0);
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

    /// <summary>
    /// Lisans geneli kargo ayarının önbelleğini sunucunun döndürdüğü satırla günceller (ayar ekranının
    /// <c>PUT shipping-settings</c>'i: 200'ün satırı ya da 409 <c>stale</c>'in <c>current</c>'ı; §4.8, §6.2). Yalnız
    /// <see cref="SaveServerState"/>'in üç kargo alanını yazar: KVKK boşaltma ayarı bu yanıtta yoktur. Koşulsuz yazar —
    /// eşzamanlı bir <c>sales/state</c> turu daha eski sürümü üstüne yazarsa bir sonraki tur düzeltir; o arada eski
    /// sürümle giden yazım <c>stale</c>, ödeme isteği <c>settings-stale</c> alır.
    /// </summary>
    /// <returns>Satır değiştiyse true.</returns>
    public bool SaveShippingCache(int version, decimal? threshold, decimal? fee)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            """
            UPDATE SalesSyncState
            SET ShippingVersion = @version,
                FreeShippingThreshold = @threshold,
                ShippingFee = @fee
            WHERE Id = 1
              AND (ShippingVersion IS NOT @version
                   OR FreeShippingThreshold IS NOT @threshold
                   OR ShippingFee IS NOT @fee)
            """,
            new { version, threshold = (double?)threshold, fee = (double?)fee }) == 1;
    }

    /// <summary>
    /// Ödeme geçmişi geri doldurması bitti ve sunucuya bildirildi (spec 2026-10-13 §3.2, §8.2-6; W3-06):
    /// <c>PaymentHistorySentAt</c>'i yazar. Yalnız tanı damgasıdır — tamamlanma kararı lisans ve kopya başına imlecin
    /// (<c>SyncCursor('payment-history-out', LicenseKey)</c>): geri yüklenen veritabanı damgayı yedekten getirse de imleç
    /// yeni kopya için baştan başlar (§8.7).
    /// </summary>
    public void MarkPaymentHistorySent(long at)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE SalesSyncState SET PaymentHistorySentAt = @at WHERE Id = 1", new { at });
    }

    // ── mod On açılışı (§8.4; W2-10) ────────────────────────────────────

    /// <summary>Müşteri gönderim kuyruğunun şu anki tavanı (<c>MAX(SyncSeq)</c>; boş tabloda 0). Açılışın 1. adımı
    /// gönderimden ÖNCE okur: gönderimden sonra doğan müşteri (yayında sohbet) kuyruğu "boş değil" saydırmaz.</summary>
    public long MaxCustomerSyncSeq()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>("SELECT COALESCE(MAX(SyncSeq), 0) FROM Customer");
    }

    /// <summary>
    /// Açılışın 1. adımında boşaltılan giden kuyrukların kalanı (§8.4-1), tek bağlantıda. Ölçütler gönderim
    /// servislerininki: <c>SyncedAt IS NULL</c>; müşteride imlecin üstü (silinmiş satır gönderilmez, sayılmaz;
    /// <c>SyncOutboxRepository.CountPending</c> ile aynı). Canlı yayının oturumu ve etiketleri sayılmaz.
    /// </summary>
    /// <param name="customerWatermark">Müşteri gönderim imleci (<c>customer-projection-out-v2</c>).</param>
    /// <param name="customerCeiling">Gönderimden önce okunan tavan (<see cref="MaxCustomerSyncSeq"/>).</param>
    public SalesOpeningBacklog GetOpeningBacklog(long customerWatermark, long customerCeiling)
    {
        using var conn = _factory.Open();
        var r = conn.QuerySingle<(long, long, long, long, long, long)>(
            """
            SELECT (SELECT COUNT(*) FROM Customer
                     WHERE SyncSeq > @customerWatermark AND SyncSeq <= @customerCeiling AND PurgedAt IS NULL),
                   (SELECT COUNT(*) FROM StreamSession
                     WHERE SyncedAt IS NULL AND EndedAt IS NOT NULL AND Origin = 'local'),
                   (SELECT COUNT(*) FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
                     WHERE l.SyncedAt IS NULL AND s.EndedAt IS NOT NULL),
                   (SELECT COUNT(*) FROM Shipment WHERE SyncedAt IS NULL),
                   (SELECT COUNT(*) FROM Payment WHERE SyncedAt IS NULL),
                   (SELECT COUNT(*) FROM PendingPaymentRecord)
            """,
            new { customerWatermark, customerCeiling });
        return new SalesOpeningBacklog((int)r.Item1, (int)r.Item2, (int)r.Item3, (int)r.Item4, (int)r.Item5, (int)r.Item6);
    }

    /// <summary>
    /// Açılışın 2. adımı (§8.4-2), TEK <see cref="SyncApplyScope"/> işleminde (BEGIN IMMEDIATE: yazma kilidi okumadan
    /// önce alınır, denetim ile yazım arasına yerel yazım giremez):
    /// <list type="number">
    ///   <item>Kip zaten 1 ise hiçbir şey yazılmaz (<see cref="SalesModeEnableStatus.AlreadyOn"/>).</item>
    ///   <item>Kargo kuyruğu ve canlı olmayan yayınların etiket kuyruğu YENİDEN sayılır; biri boş değilse (ör. 1. adım
    ///   gönderirken kaydedilen dekontun kargo yazımı) hiçbir şey yazılmaz (<see cref="SalesModeEnableStatus.Backlog"/>).
    ///   Canlı yayının etiketleri denetime girmez.</item>
    ///   <item><c>Mode = 1</c>, <c>ModeSeenAt = </c><paramref name="modeSeenAt"/>, <c>FirstCatchUpAt = NULL</c> (ilk
    ///   yetişme bu açılış için baştan; Off → On yeniden açılışı dahil, §8.5). Bundan sonra 049'un
    ///   <c>TR_Shipment_ServerOwned_*</c> ve <c>TR_Label_ShipmentLink</c> tetikleyicileri yerel kargo yazımını ABORT eder:
    ///   gönderilmemiş kargo kararı doğamaz.</item>
    ///   <item>Yerelde devri bilinen (<c>ReleasedAt</c> dolu) kendi BİTMİŞ yayınları <c>Authority = 'server'</c>: ilk
    ///   yetişme birkaç tur sürse de onlarda yerel yol seçilmez. Kilit altında: tetikleyiciler susar, oturum kuyruğa
    ///   girmez (<c>SyncedAt</c>, <c>Revision</c> değişmez). Hâlâ açık (canlı) yayın — force-end görmüş olsa da — yerel
    ///   kalır (M18).</item>
    ///   <item>Akış imleci (<paramref name="cursorName"/>, <paramref name="licenseKey"/>) 0: akış baştan, açılış başına
    ///   bir kez (§8.4-3).</item>
    ///   <item><see cref="SalesConflictLog.ModeOpened"/> izi: ilk yetişmenin üstüne yazım sayısının tabanı.</item>
    /// </list>
    /// </summary>
    /// <param name="modeSeenAt"><c>sales/state</c>'in <c>modeChangedAt</c>'i (unix saniye).</param>
    /// <param name="now">İzin anı (unix saniye).</param>
    public SalesModeEnableResult TryEnableMode(long? modeSeenAt, string cursorName, string licenseKey, long now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;

        var mode = conn.ExecuteScalar<long?>("SELECT Mode FROM SalesSyncState WHERE Id = 1", transaction: tx)
                   ?? throw new InvalidOperationException("SalesSyncState satırı yok (göç 049).");
        if (mode == 1) return new SalesModeEnableResult(SalesModeEnableStatus.AlreadyOn, 0, 0, 0);

        var (shipments, labels) = conn.QuerySingle<(long, long)>(
            """
            SELECT (SELECT COUNT(*) FROM Shipment WHERE SyncedAt IS NULL),
                   (SELECT COUNT(*) FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
                     WHERE l.SyncedAt IS NULL AND s.EndedAt IS NOT NULL)
            """, transaction: tx);
        if (shipments + labels > 0)
            return new SalesModeEnableResult(SalesModeEnableStatus.Backlog, (int)shipments, (int)labels, 0); // geri alınır

        conn.Execute(
            "UPDATE SalesSyncState SET Mode = 1, ModeSeenAt = @modeSeenAt, FirstCatchUpAt = NULL WHERE Id = 1",
            new { modeSeenAt }, tx);
        var sessions = conn.Execute(EnableSessionAuthoritySql, transaction: tx);
        SyncCursorRepository.Upsert(conn, tx, cursorName, licenseKey, seq: 0);
        SalesConflictLog.Insert(conn, tx, now, null, SalesConflictLog.ModeOpened,
            "sessions=" + sessions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        scope.Commit();
        return new SalesModeEnableResult(SalesModeEnableStatus.Enabled, 0, 0, sessions);
    }

    /// <summary>
    /// Yerel kip 1 kalmışken görülen YENİ mod On dönemi (§8.5 tablosu "Off → On (yeniden) | §8.4 baştan"): sunucu modu 1
    /// ve <paramref name="modeSeenAt"/> (state'in <c>modeChangedAt</c>'i) yerel <c>ModeSeenAt</c>'ten yeni. Bu kopya
    /// araya giren Off dönemini bitirmedi — yarım kalmış <c>disable</c> turu son akışı yalnız kendi satırlarıyla
    /// uygularken imleci yok saydığı uzak öğelerin ötesine taşımış olabilir — ya da hiç görmedi. TEK
    /// <see cref="SyncApplyScope"/> işleminde, kilit altında yeniden okuyarak açılışın yazımı baştan:
    /// <c>ModeSeenAt</c>, <c>FirstCatchUpAt = NULL</c>, devri yerelde bilinen kendi bitmiş yayınları <c>server</c>, akış
    /// imleci 0 ve <see cref="SalesConflictLog.ModeOpened"/> izi (ilk yetişme sayımının tabanı).
    ///
    /// <para>Giden kuyruk denetimi (<see cref="TryEnableMode"/>'un kilit altındaki sayımı) yok: yerel kip 1'de görülen kip
    /// 1'dir, gönderimler olağan mod On yolundan gider; açılış penceresine bağlı değildir.</para>
    /// </summary>
    /// <returns>Devri bilinen ve <c>server</c> olan kendi yayını sayısı; kip 1 değilse ya da dönem zaten görüldüyse
    /// (<c>ModeSeenAt</c> boş ya da ≥ <paramref name="modeSeenAt"/>) hiçbir şey yazılmaz, <c>null</c>.</returns>
    public int? TryReopenMode(long modeSeenAt, string cursorName, string licenseKey, long now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;

        var (mode, seen) = conn.QuerySingleOrDefault<(long Mode, long? Seen)>(
            "SELECT Mode, ModeSeenAt FROM SalesSyncState WHERE Id = 1", transaction: tx);
        if (mode != 1 || seen is not { } seenAt || modeSeenAt <= seenAt) return null;

        conn.Execute("UPDATE SalesSyncState SET ModeSeenAt = @modeSeenAt, FirstCatchUpAt = NULL WHERE Id = 1",
            new { modeSeenAt }, tx);
        var sessions = conn.Execute(EnableSessionAuthoritySql, transaction: tx);
        SyncCursorRepository.Upsert(conn, tx, cursorName, licenseKey, seq: 0);
        SalesConflictLog.Insert(conn, tx, now, null, SalesConflictLog.ModeOpened,
            "reopened sessions=" + sessions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        scope.Commit();
        return sessions;
    }

    /// <summary>Devri yerelde bilinen kendi bitmiş yayınları (§8.4-2). Açık yayın (<c>EndedAt IS NULL</c>) yerel kalır.</summary>
    private const string EnableSessionAuthoritySql = @"
        UPDATE StreamSession SET Authority = 'server'
         WHERE Origin = 'local' AND Authority = 'local' AND EndedAt IS NOT NULL AND ReleasedAt IS NOT NULL";

    /// <summary>
    /// Açılışın 4. adımı (§8.4-4, §6.1): akış ufka ulaşınca (boş sayfa, bekleyen öğe yok) TEK işlemde bütün müşterilerin
    /// toplamları etiketlerden yeniden hesaplanır ve <c>FirstCatchUpAt</c> yazılır. Kip 1 değilse ya da ilk yetişme zaten
    /// tamamsa hiçbir şey yazılmaz. Sonuç bu açılıştan beri düşen <see cref="SalesConflictLog.CatchUpOverwrite"/>
    /// izlerini sayar (son <see cref="SalesConflictLog.ModeOpened"/> satırından sonrakiler; tur sayısından ve yeniden
    /// başlatmadan bağımsız).
    ///
    /// <para><see cref="CustomerAggregateSql.DeriveAllOnce"/> gibi müşteri meşguliyet kümesini almaz: yazılan değer
    /// etiketlerden türetilmiş değerdir — akışın etkilenen müşteriler için aynı işlemde yazdığıyla aynı.</para>
    /// </summary>
    /// <returns>Bu çağrı ilk yetişmeyi tamamladıysa sonuç; aksi hâlde <c>null</c>.</returns>
    public SalesFirstCatchUpResult? CompleteFirstCatchUp(long now)
    {
        using var write = DbWrite.Begin(_factory);
        var conn = write.Connection;
        var tx = write.Transaction;
        var (mode, firstCatchUpAt) = conn.QuerySingleOrDefault<(long, long?)>(
            "SELECT Mode, FirstCatchUpAt FROM SalesSyncState WHERE Id = 1", transaction: tx);
        if (mode != 1 || firstCatchUpAt is not null) return null;

        var aggregates = CustomerAggregateSql.RecomputeAll(conn, tx);
        conn.Execute(
            "UPDATE SalesSyncState SET FirstCatchUpAt = @now WHERE Id = 1 AND Mode = 1 AND FirstCatchUpAt IS NULL",
            new { now }, tx);
        var overwrites = conn.ExecuteScalar<long>(
            """
            SELECT COUNT(*) FROM SalesConflictLog
             WHERE Kind = @catchUp
               AND Id > COALESCE((SELECT MAX(Id) FROM SalesConflictLog WHERE Kind = @opened), 0)
            """,
            new { catchUp = SalesConflictLog.CatchUpOverwrite, opened = SalesConflictLog.ModeOpened }, tx);
        write.Commit();
        return new SalesFirstCatchUpResult(aggregates, (int)overwrites);
    }

    private static decimal? ToDecimal(double? value) => value is { } v ? (decimal)v : null;

    private sealed class ShippingRow
    {
        public long Mode { get; set; }
        public long? ShippingVersion { get; set; }
        public double? FreeShippingThreshold { get; set; }
        public double? ShippingFee { get; set; }
        public long CopyPending { get; set; }
    }

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
