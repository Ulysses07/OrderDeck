using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using Dapper;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Satış akışı öğesinin yerele uygulanması (spec 2026-10-13 §5.4 <c>ApplyItem</c>, §3.2, §4.4, §6.1, §9; DM4, DM8).
/// Komut yanıtı da (§4.6-7) aynı fonksiyonla yazılır.
///
/// <para><b>İşlem ve kilitler.</b> Öğe başına tek <see cref="SyncApplyScope"/>: 049'un dokuz tetikleyicisi susar —
/// inen satır kuyruğa alınmaz (<c>SyncedAt</c> ve <c>Revision</c> değişmez, DM8; F05 onayı bozulmaz) ve sunucunun
/// yazdığı satır ABORT edilmez (<c>Authority = 'server'</c> oturumun etiketi, mod 1'deki kargo bağı ve dosya). Kilit
/// sırası U17: önce <see cref="CustomerBusySet"/>, sonra SQLite yazma kilidi; etiketin müşterisi ödeme akışındaysa
/// öğe <see cref="SalesFeedApplyStatus.Busy"/> döner.</para>
///
/// <para><b>Oturum.</b> Yerelde yoksa <c>Origin = 'remote'</c>, <c>Authority = 'server'</c> ile eklenir; onaylı doğar
/// (<c>SyncedAt = now</c>) — gönderim kuyruğuna ve geri doldurmaya (yalnız <c>Origin = 'local'</c>) girmez. Uzak
/// oturum hiçbir zaman açık inmez: <c>EndedAt</c> boşsa devir anı yazılır (§3.2, §4.9-d); <c>GetActive</c> ve açılıştaki
/// kurtarma ekranı onu görmez. Kendi bitmiş oturumu <c>ReleasedAt</c> alır; <c>Authority = 'server'</c> YALNIZ işlem
/// içinde okunan <c>SalesSyncState.Mode = 1</c> iken (W1 <see cref="SessionRepository.ApplyRelease"/> ile aynı kural).
/// Kendi hâlâ açık (aktif) oturumu (force-end): yalnız <c>ReleasedAt</c> saklanır, <c>EndedAt</c> ve <c>Authority</c>
/// olduğu gibi kalır, sonuç <see cref="SalesFeedApplyResult.SessionEndedRemotely"/> taşır — canlı yayın kesilmez (M18).
/// Her durumda aynı Id'li <c>RemoteLiveSession</c> satırı silinir.</para>
///
/// <para><b>Sipariş.</b> Yerel satır onaysızsa (<c>SyncedAt IS NULL</c>) hiçbir şey yazılmaz (U5,
/// <see cref="SalesFeedApplyStatus.Stalled"/>). Yerel <c>ServerVersion</c>'dan eski öğe yazılmaz (sürüm yalnız ileri
/// gider; aradaki komut yanıtı geri alınmaz). Var olan satıra beş birim (fiyat, iptal, yedek, kargo bağı, baskı) ve
/// <c>ServerVersion</c> yazılır; oluşum alanları, CustomerId, <c>SyncedAt</c> ve <c>Revision</c> hiç (DM4, DM8).
/// Yeni satır yan listedeki oturum ve müşteri sağlandıktan sonra eklenir: CustomerId yönlendirmeden çözülür
/// (<c>CustomerIdSql.Resolve</c>), <c>SyncedAt = StockSyncedAt = now</c> (aynı etiket stoktan ikinci kez düşmez).
/// Müşteri yalnız eklemede gerekir (FK); güncelleme müşteriye dokunmaz. Müşteri yerelde yoksa ve aynı kimlik başka
/// Id'deyse <see cref="SalesFeedApplyStatus.PendingCustomer"/>; yoksa taslak açılır
/// (<see cref="CustomerSyncRepository.EnsureSalesDraft"/>). Yerel müşteri silinmişse (ya da taslak öyle diyorsa) inen
/// satırın takma adı boş, mesajı "" yazılır — inen satır için, ayardan bağımsız (§9). Etkilenen müşterilerin toplamları
/// aynı işlemde etiketlerden türetilir (<see cref="CustomerAggregateSql"/>, §6.1).</para>
///
/// <para><b>İlk yetişme.</b> <see cref="SalesFeedApplyOptions.CountCatchUpOverwrite"/> ∧ işlem içinde
/// <c>FirstCatchUpAt</c> boş ∧ etiket kendi (<c>Origin = 'local'</c>) oturumunda ∧ birimi farklıysa yazmadan önce
/// <see cref="SalesConflictLog.CatchUpOverwrite"/> izi düşer: Id ve birim adları, metin yok.</para>
///
/// <para><b>Dosya.</b> Yalnız <see cref="SalesFeedApplyOptions.ApplyShipments"/> iken yerel aynaya upsert edilir
/// (<c>OriginReplicaId</c>, <c>ServerVersion</c>, karar alanları, tutar; <c>SyncedAt = now</c>). İşlem içinde okunan
/// yerel kip 0 iken (mod On dönemini görmemiş kopyanın <c>disable</c> turu, §8.5) bu kopya dosyalarının yazıcısıdır:
/// yerel satır onaysızsa hiçbir şey yazılmaz (<see cref="SalesFeedApplyStatus.Stalled"/>, U5'in dosya karşılığı; tur
/// önce kargoyu gönderir), kargoya verilmemiş ve kaynağı başka kopya olan dosya eklenmez, yerelde varsa silinir (2.
/// adımın kuralı öğe anında; dekont onu bu arada seçemez). Kip 1'de gönderilmemiş dosya oluşamaz (§8.4-3): sunucu
/// yazar.</para>
///
/// <para><b><c>disable</c> turu</b> (<see cref="SalesFeedApplyOptions.OwnRowsOnly"/>, §8.5-1): yalnız kendi
/// (<c>Origin = 'local'</c>) oturum ve etiketleri yazılır — başka bilgisayarın komutlarının sonucu kendi satırına iner;
/// uzak oturum ve etiket eklenmez ya da tazelenmez (2. adım siler). Dosyalar yukarıdaki kip kuralıyla.</para>
/// </summary>
public sealed class SalesFeedRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly CustomerBusySet? _busy;

    /// <param name="busy">Ödeme akışındaki müşteriler (U13). DI tekil örneği verir; null yalnız testlerde (hiçbir
    /// müşteri meşgul sayılmaz).</param>
    public SalesFeedRepository(IDbConnectionFactory factory, CustomerBusySet? busy = null)
    {
        _factory = factory;
        _busy = busy;
    }

    /// <summary>Tek akış öğesini kendi <see cref="SyncApplyScope"/> işleminde uygular.</summary>
    /// <param name="sideSessions">Sayfanın yan listesindeki oturumlar (siparişin oturumu buradan sağlanır).</param>
    /// <param name="drafts">Sayfanın yan listesindeki müşteri taslakları.</param>
    /// <param name="now">Onay ve iz anı (unix saniye).</param>
    /// <exception cref="SalesFeedContractException">Öğe sunucu sözleşmesine uymuyor (öğeye özgü).</exception>
    public SalesFeedApplyResult ApplyItem(
        SalesFeedItem item,
        IReadOnlyCollection<SalesFeedSession> sideSessions,
        IReadOnlyCollection<SalesFeedCustomerDraft> drafts,
        SalesFeedApplyOptions options,
        long now)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sideSessions);
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(options);

        switch (item.Kind)
        {
            case SalesFeedItemKind.Session:
                return ApplySession(item.Session ?? throw Missing(item), options, now);
            case SalesFeedItemKind.Order:
                return ApplyOrder(item.Order ?? throw Missing(item), sideSessions, drafts, options, now);
            case SalesFeedItemKind.Shipment:
                var shipment = item.Shipment ?? throw Missing(item);
                return options.ApplyShipments
                    ? ApplyShipment(shipment, now)
                    : SalesFeedApplyResult.Of(SalesFeedApplyStatus.Ignored);
            default:
                throw new SalesFeedContractException($"Bilinmeyen akış öğesi türü (seq {item.ChangeSeq}).");
        }
    }

    private static SalesFeedContractException Missing(SalesFeedItem item) =>
        new($"Akış öğesinin satırı yok: {item.Kind} (seq {item.ChangeSeq}).");

    // ── oturum ───────────────────────────────────────────────────────────

    private SalesFeedApplyResult ApplySession(SalesFeedSession session, SalesFeedApplyOptions options, long now) => Locked(_ =>
    {
        using var scope = SyncApplyScope.Begin(_factory);
        if (options.OwnRowsOnly && !IsOwnSession(scope.Connection, scope.Transaction, session.Id))
            return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Ignored);                  // Dispose geri alır
        var write = EnsureSession(scope.Connection, scope.Transaction, session, now);
        scope.Commit();
        return new SalesFeedApplyResult(
            write.Changed ? SalesFeedApplyStatus.Applied : SalesFeedApplyStatus.Unchanged,
            Array.Empty<string>(),
            write.Changed ? [session.Id] : Array.Empty<string>(),
            Array.Empty<string>(),
            write.EndedRemotely,
            0,
            AggregateRecomputeResult.None);
    });

    /// <summary>Uzak oturumun eklenmesi: onaylı doğar, açık inmez (§3.2). Tetikleyiciler kilit altında susar.</summary>
    private const string InsertRemoteSessionSql = @"
        INSERT INTO StreamSession
          (Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Origin, Authority, OwnerMachineName, ReleasedAt)
        VALUES
          (@Id, @Title, @StartedAt, @EndedAt, @Platforms, @Notes, @Now, 'remote', 'server', @OwnerMachineName,
           @ReleasedAt)";

    /// <summary>Uzak oturumun güncel hâli (sunucunun satırı). <c>Origin = 'remote'</c> koşulu: kendi oturumuna
    /// hiçbir zaman sunucunun başlığı ya da kapanışı yazılmaz.</summary>
    private const string RefreshRemoteSessionSql = @"
        UPDATE StreamSession SET Title = @Title, EndedAt = @EndedAt, Platforms = @Platforms, Notes = @Notes,
               OwnerMachineName = @OwnerMachineName, ReleasedAt = @ReleasedAt
         WHERE Id = @Id AND Origin = 'remote'";

    /// <summary>Kendi oturumunun devri: yalnız <c>ReleasedAt</c> ve (çağıranın kipe göre verdiği)
    /// <c>Authority</c>. <c>EndedAt</c>, <c>SyncedAt</c> ve <c>Revision</c>'a dokunulmaz.</summary>
    private const string StoreOwnReleaseSql = @"
        UPDATE StreamSession SET ReleasedAt = @ReleasedAt, Authority = @Authority
         WHERE Id = @Id AND Origin = 'local'";

    private readonly record struct SessionWrite(bool Changed, bool EndedRemotely);

    private static SessionWrite EnsureSession(IDbConnection conn, IDbTransaction tx, SalesFeedSession s, long now)
    {
        var removedLive = conn.Execute("DELETE FROM RemoteLiveSession WHERE Id = @id", new { id = s.Id }, tx) > 0;
        var local = conn.QuerySingleOrDefault<SessionRow>(
            @"SELECT Origin, Authority, EndedAt, ReleasedAt, Title, Platforms, Notes, OwnerMachineName
              FROM StreamSession WHERE Id = @id",
            new { id = s.Id }, tx);
        var platforms = JsonSerializer.Serialize(s.Platforms);

        if (local is null)
        {
            if (s.ReleasedAt is not { } releasedAt)
                throw new SalesFeedContractException($"Uzak oturum devredilmemiş: {s.Id}.");
            conn.Execute(InsertRemoteSessionSql, new
            {
                s.Id, s.Title, s.StartedAt,
                EndedAt = s.EndedAt ?? releasedAt,
                Platforms = platforms, s.Notes, Now = now, s.OwnerMachineName,
                ReleasedAt = releasedAt,
            }, tx);
            return new SessionWrite(Changed: true, EndedRemotely: false);
        }

        if (local.Origin == StreamSessionOrigin.Remote)
        {
            // EndedAt sunucuda tekdüze (§4.1): dolu değer boşa dönmez. Uzak oturum yerelde de açık kalmaz.
            var endedAt = s.EndedAt ?? local.EndedAt ?? s.ReleasedAt ?? local.ReleasedAt;
            var releasedAt = s.ReleasedAt ?? local.ReleasedAt;
            var differs = !string.Equals(local.Title, s.Title, StringComparison.Ordinal)
                          || local.EndedAt != endedAt
                          || !string.Equals(local.Platforms, platforms, StringComparison.Ordinal)
                          || !string.Equals(local.Notes, s.Notes, StringComparison.Ordinal)
                          || !string.Equals(local.OwnerMachineName, s.OwnerMachineName, StringComparison.Ordinal)
                          || local.ReleasedAt != releasedAt;
            if (differs)
                conn.Execute(RefreshRemoteSessionSql, new
                {
                    s.Id, s.Title, EndedAt = endedAt, Platforms = platforms, s.Notes, s.OwnerMachineName,
                    ReleasedAt = releasedAt,
                }, tx);
            return new SessionWrite(differs || removedLive, EndedRemotely: false);
        }

        // Kendi oturumu. Kip işlem içinde okunur (BEGIN IMMEDIATE: yazma kilidi okumadan önce alındı).
        var mode = conn.ExecuteScalar<long?>("SELECT Mode FROM SalesSyncState WHERE Id = 1", transaction: tx) ?? 0;
        var ownReleasedAt = s.ReleasedAt ?? local.ReleasedAt;
        var authority = local.EndedAt is not null && ownReleasedAt is not null && mode == 1
            ? StreamSessionAuthority.Server
            : local.Authority;
        var ownDiffers = ownReleasedAt != local.ReleasedAt || !string.Equals(authority, local.Authority, StringComparison.Ordinal);
        if (ownDiffers)
            conn.Execute(StoreOwnReleaseSql, new { s.Id, ReleasedAt = ownReleasedAt, Authority = authority }, tx);
        return new SessionWrite(ownDiffers || removedLive, EndedRemotely: local.EndedAt is null && ownReleasedAt is not null);
    }

    // ── sipariş ──────────────────────────────────────────────────────────

    private const string ReadLabelSql = @"
        SELECT l.CustomerId, l.Price, l.PriceCorrectedAt, l.PaidPrice, l.CancelledAt, l.CancelReason,
               l.IsTentativeBackup, l.ShipmentId, l.PrintedAt, l.PrintBatchId, l.PrintSeq,
               l.SyncedAt, l.StockSyncedAt, l.ServerVersion, s.Origin AS SessionOrigin
          FROM Label l LEFT JOIN StreamSession s ON s.Id = l.SessionId
         WHERE l.Id = @id";

    /// <summary>Yeni satır: oluşum alanları ve birimler sunucudan, CustomerId yönlendirmeden çözülerek (aynı ifadede,
    /// U12), onaylı doğar (<c>SyncedAt = StockSyncedAt = now</c>, Revision 0). Kilit altında: <c>Authority =
    /// 'server'</c> oturuma ekleme ABORT edilmez.</summary>
    private static readonly string InsertLabelSql = @"
        INSERT INTO Label
          (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, AddedAt, PrintedAt,
           CancelledAt, CancelReason, IsShippingFee, IsBackupPromoted, IsTentativeBackup, ParentLabelId, ShipmentId,
           ProductId, ProductVariantId, PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice, ServerVersion,
           SyncedAt, StockSyncedAt)
        VALUES
          (@Id, @SessionId, " + CustomerIdSql.Resolve("@CustomerId") + @", @Platform, @Username, @DisplayName,
           @MessageText, @Code, @Price, @AddedAt, @PrintedAt,
           @CancelledAt, @CancelReason, @IsShippingFee, @IsBackupPromoted, @IsTentativeBackup, @ParentLabelId,
           @ShipmentId, @ProductId, @ProductVariantId, @PrintBatchId, @PrintSeq, @PriceCorrectedAt, @PaidPrice,
           @Version, @Now, @Now)";

    /// <summary>Var olan onaylı satıra değişen birimler ve sürüm. <c>StockSyncedAt</c> yalnız boşsa onayla doldurulur.
    /// <c>SyncedAt IS NOT NULL</c>: U5 işlem içinde zaten denetlendi; savunma.</summary>
    private const string WriteUnitsSql = @"
        UPDATE Label SET
               Price             = CASE WHEN @wPrice = 1 THEN @Price ELSE Price END,
               PriceCorrectedAt  = CASE WHEN @wPrice = 1 THEN @PriceCorrectedAt ELSE PriceCorrectedAt END,
               PaidPrice         = CASE WHEN @wPrice = 1 THEN @PaidPrice ELSE PaidPrice END,
               CancelledAt       = CASE WHEN @wCancel = 1 THEN @CancelledAt ELSE CancelledAt END,
               CancelReason      = CASE WHEN @wCancel = 1 THEN @CancelReason ELSE CancelReason END,
               IsTentativeBackup = CASE WHEN @wBackup = 1 THEN @IsTentativeBackup ELSE IsTentativeBackup END,
               ShipmentId        = CASE WHEN @wShipment = 1 THEN @ShipmentId ELSE ShipmentId END,
               PrintedAt         = CASE WHEN @wPrint = 1 THEN @PrintedAt ELSE PrintedAt END,
               PrintBatchId      = CASE WHEN @wPrint = 1 THEN @PrintBatchId ELSE PrintBatchId END,
               PrintSeq          = CASE WHEN @wPrint = 1 THEN @PrintSeq ELSE PrintSeq END,
               ServerVersion     = @Version,
               StockSyncedAt     = COALESCE(StockSyncedAt, SyncedAt)
         WHERE Id = @Id AND SyncedAt IS NOT NULL";

    private SalesFeedApplyResult ApplyOrder(
        SalesFeedOrder order,
        IReadOnlyCollection<SalesFeedSession> sideSessions,
        IReadOnlyCollection<SalesFeedCustomerDraft> drafts,
        SalesFeedApplyOptions options,
        long now)
    {
        if (order.SessionId is not { } sessionId)
            throw new SalesFeedContractException($"Sipariş oturumsuz: {order.Id}.");
        var side = sideSessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        var draft = drafts.FirstOrDefault(d => string.Equals(d.Id, order.CustomerId, StringComparison.Ordinal));

        return Locked(isBusy =>
        {
            using var scope = SyncApplyScope.Begin(_factory);
            var conn = scope.Connection;
            var tx = scope.Transaction;

            // BEGIN IMMEDIATE: yazma kilidi okumadan önce alındı — okunan satır yazılana kadar değişemez.
            var local = conn.QuerySingleOrDefault<LabelRow>(ReadLabelSql, new { id = order.Id }, tx);
            if (options.OwnRowsOnly && !(local is not null
                    ? local.SessionOrigin == StreamSessionOrigin.Local
                    : IsOwnSession(conn, tx, sessionId)))
                return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Ignored);               // disable turu: uzak satır inmez
            if (local is not null && local.SyncedAt is null)
                return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Stalled);           // U5; Dispose geri alır
            if (local?.ServerVersion is { } known && order.Version < known)
                return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Unchanged);

            var customerId = local?.CustomerId
                             ?? conn.ExecuteScalar<string>($"SELECT {CustomerIdSql.Resolve("@id")}", new { id = order.CustomerId }, tx)
                             ?? order.CustomerId;
            if (isBusy(customerId)) return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Busy);

            var session = side is not null
                ? EnsureSession(conn, tx, side, now)
                : RequireLocalSession(conn, tx, sessionId, order.Id);

            var labelChanged = true;
            var catchUp = 0;
            if (local is null)
            {
                var outcome = draft is not null
                    ? CustomerSyncRepository.EnsureSalesDraft(conn, tx, draft.Id, draft.Platform, draft.Username,
                        draft.DisplayName, draft.PurgedAt, now)
                    : CustomerExists(conn, tx, order.CustomerId)
                        ? SalesDraftOutcome.Exists
                        : throw new SalesFeedContractException($"Siparişin müşteri taslağı yan listede yok: {order.Id}.");
                if (outcome == SalesDraftOutcome.PendingCustomer)
                    return SalesFeedApplyResult.Of(SalesFeedApplyStatus.PendingCustomer);  // oturum yazımı da geri alınır

                // §9: inen satır için, ayardan bağımsız. Taslak sunucuda silinmiş diyorsa da (müşteri akışının silme
                // öğesi henüz inmemiş olabilir).
                var masked = draft?.PurgedAt is not null || CustomerPurged(conn, tx, order.CustomerId);
                conn.Execute(InsertLabelSql, new
                {
                    order.Id, SessionId = sessionId, order.CustomerId, order.Platform, order.Username,
                    DisplayName = masked ? null : order.DisplayName,
                    MessageText = masked ? "" : order.MessageText,
                    order.Code, Price = (double)order.Price, order.AddedAt, order.PrintedAt,
                    order.CancelledAt, order.CancelReason,
                    IsShippingFee = order.IsShippingFee ? 1 : 0,
                    IsBackupPromoted = order.IsBackupPromoted ? 1 : 0,
                    IsTentativeBackup = order.IsTentativeBackup ? 1 : 0,
                    order.ParentLabelId, order.ShipmentId, order.ProductId, order.ProductVariantId,
                    order.PrintBatchId, order.PrintSeq, order.PriceCorrectedAt,
                    PaidPrice = (double?)order.PaidPrice,
                    order.Version, Now = now,
                }, tx);
            }
            else
            {
                var before = Units.Of(local);
                var changed = before.ChangedUnits(Units.Of(order));
                var versionChanged = local.ServerVersion != order.Version;
                if (changed.Count > 0 && options.CountCatchUpOverwrite
                    && local.SessionOrigin == StreamSessionOrigin.Local && FirstCatchUpPending(conn, tx))
                {
                    SalesConflictLog.Insert(conn, tx, now, order.Id, SalesConflictLog.CatchUpOverwrite,
                        SalesConflictLog.UnitsReason(changed));
                    catchUp = 1;
                }
                if (changed.Count > 0 || versionChanged || local.StockSyncedAt is null)
                    WriteUnits(conn, tx, order, changed);
                labelChanged = changed.Count > 0 || versionChanged;
            }

            // Eski ve yeni CustomerId (§6.1). CustomerId yazılmadığı için güncellemede ikisi aynıdır.
            var after = conn.ExecuteScalar<string>("SELECT CustomerId FROM Label WHERE Id = @id", new { id = order.Id }, tx);
            var aggregates = CustomerAggregateSql.Recompute(conn, tx,
                new[] { local?.CustomerId, after }.Where(id => id is not null).Select(id => id!));
            scope.Commit();

            var anyChange = labelChanged || session.Changed;
            return new SalesFeedApplyResult(
                anyChange ? SalesFeedApplyStatus.Applied : SalesFeedApplyStatus.Unchanged,
                labelChanged ? [order.Id] : Array.Empty<string>(),
                session.Changed ? [sessionId] : Array.Empty<string>(),
                Array.Empty<string>(),
                session.EndedRemotely,
                catchUp,
                aggregates);
        });
    }

    /// <summary>Yan listede oturum yoksa yerelde olmalı (FK); değilse öğe sözleşmeye uymuyor.</summary>
    private static SessionWrite RequireLocalSession(IDbConnection conn, IDbTransaction tx, string sessionId, string orderId)
    {
        if (conn.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM StreamSession WHERE Id = @sessionId)",
                new { sessionId }, tx) == 0)
            throw new SalesFeedContractException($"Siparişin oturumu yan listede yok: {orderId}.");
        return new SessionWrite(Changed: false, EndedRemotely: false);
    }

    /// <summary>Oturum yerelde var ve bu bilgisayarda açılmış (<c>Origin = 'local'</c>).</summary>
    private static bool IsOwnSession(IDbConnection conn, IDbTransaction tx, string sessionId) =>
        conn.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM StreamSession WHERE Id = @sessionId AND Origin = 'local')",
            new { sessionId }, tx) != 0;

    private static bool CustomerExists(IDbConnection conn, IDbTransaction tx, string customerId) =>
        conn.ExecuteScalar<long>(
            $"SELECT EXISTS(SELECT 1 FROM Customer WHERE Id = {CustomerIdSql.Resolve("@id")})",
            new { id = customerId }, tx) != 0;

    private static bool CustomerPurged(IDbConnection conn, IDbTransaction tx, string customerId) =>
        conn.ExecuteScalar<long>(
            $"SELECT EXISTS(SELECT 1 FROM Customer WHERE Id = {CustomerIdSql.Resolve("@id")} AND PurgedAt IS NOT NULL)",
            new { id = customerId }, tx) != 0;

    private static bool FirstCatchUpPending(IDbConnection conn, IDbTransaction tx) =>
        conn.ExecuteScalar<long>(
            "SELECT EXISTS(SELECT 1 FROM SalesSyncState WHERE Id = 1 AND FirstCatchUpAt IS NULL)", transaction: tx) != 0;

    /// <summary>Değişen birimlerin alanları ve sürüm. Değişmeyen birim yazılmaz (REAL kolonun değeri decimal'den geri
    /// dönüşte oynamasın).</summary>
    private static void WriteUnits(IDbConnection conn, IDbTransaction tx, SalesFeedOrder o, IReadOnlyCollection<string> changed)
    {
        var written = conn.Execute(WriteUnitsSql, new
        {
            o.Id, o.Version,
            wPrice = changed.Contains(SalesPushUnits.Price) ? 1 : 0,
            wCancel = changed.Contains(SalesPushUnits.Cancel) ? 1 : 0,
            wBackup = changed.Contains(SalesPushUnits.Backup) ? 1 : 0,
            wShipment = changed.Contains(SalesPushUnits.Shipment) ? 1 : 0,
            wPrint = changed.Contains(SalesPushUnits.Print) ? 1 : 0,
            Price = (double)o.Price,
            o.PriceCorrectedAt,
            PaidPrice = (double?)o.PaidPrice,
            o.CancelledAt,
            o.CancelReason,
            IsTentativeBackup = o.IsTentativeBackup ? 1 : 0,
            o.ShipmentId,
            o.PrintedAt,
            o.PrintBatchId,
            o.PrintSeq,
        }, tx);
        if (written != 1)
            throw new InvalidOperationException("Etiket aynı işlemde okunduğu hâlde yazılamadı; öğe uygulanmadı.");
    }

    // ── dosya ────────────────────────────────────────────────────────────

    /// <summary>Yeni dosya aynası: CustomerId yönlendirmeden çözülerek, onaylı doğar. Kilit altında: mod 1'de ABORT
    /// edilmez.</summary>
    private static readonly string InsertShipmentSql = @"
        INSERT INTO Shipment
          (Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, OriginReplicaId, ServerVersion)
        VALUES
          (@Id, " + CustomerIdSql.Resolve("@CustomerId") + @", @Status, @CreatedAt, @HeldAt, @ShippedAt,
           @CumulativeAmount, @Now, @OriginReplicaId, @Version)";

    /// <summary>Var olan dosya aynası: karar alanları, tutar, kaynak kopya, sürüm ve onay. CustomerId ve Revision'a
    /// dokunulmaz.</summary>
    private const string RefreshShipmentSql = @"
        UPDATE Shipment SET Status = @Status, HeldAt = @HeldAt, ShippedAt = @ShippedAt,
               CumulativeAmount = @CumulativeAmount, OriginReplicaId = @OriginReplicaId, ServerVersion = @Version,
               SyncedAt = @Now
         WHERE Id = @Id";

    /// <summary>Yerel kip 0'da başka kopyaya geçmiş, kargoya verilmemiş ve onaylı yerel dosya (2. adımın
    /// <c>DropForeignShipmentsSql</c>'inin öğe anındaki karşılığı). Kilit altında; silme tetikleyicisi yok.</summary>
    private const string DropForeignShipmentItemSql = "DELETE FROM Shipment WHERE Id = @id";

    private SalesFeedApplyResult ApplyShipment(SalesFeedShipment s, long now) => Locked(_ =>
    {
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;

        // BEGIN IMMEDIATE: yazma kilidi okumadan önce alındı — okunan satır ve kip yazılana kadar değişemez.
        var local = conn.QuerySingleOrDefault<ShipmentRow>(
            @"SELECT Status, HeldAt, ShippedAt, CumulativeAmount, OriginReplicaId, ServerVersion, SyncedAt
              FROM Shipment WHERE Id = @id",
            new { id = s.Id }, tx);

        // Yerel kip 0 (mod On dönemini görmemiş kopyanın disable turu, §8.5): bu kopya dosyalarının yazıcısıdır —
        // dekontun yerel kargo adımı yazmayı sürdürür, TR_Shipment_ServerOwned_* susar. §8.4-3'ün "gönderilmemiş dosya
        // oluşamaz" güvencesi yalnız kip 1'de geçerli.
        var mode = conn.ExecuteScalar<long?>("SELECT Mode FROM SalesSyncState WHERE Id = 1", transaction: tx) ?? 0;
        if (mode == 0)
        {
            if (local is not null && local.SyncedAt is null)
                return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Stalled);      // U5'in dosya karşılığı; Dispose geri alır
            if (IsForeignOpenShipment(conn, tx, s))
            {
                // Off'ta başka kopyanın olacak açık dosyası (§4.10): 2. adım zaten silerdi. Hiç eklenmez, varsa hemen
                // kalkar — o ana kadar bu kopyanın dekontu onu seçip etiket bağlayamaz.
                if (local is null) return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Ignored);
                conn.Execute(DropForeignShipmentItemSql, new { id = s.Id }, tx);
                scope.Commit();
                return new SalesFeedApplyResult(SalesFeedApplyStatus.Applied, Array.Empty<string>(),
                    Array.Empty<string>(), [s.Id], false, 0, AggregateRecomputeResult.None);
            }
        }

        if (local?.ServerVersion is { } known && s.Version < known)
            return SalesFeedApplyResult.Of(SalesFeedApplyStatus.Unchanged);

        var status = s.Status.ToString();
        var param = new
        {
            s.Id, s.CustomerId, Status = status, s.CreatedAt, s.HeldAt, s.ShippedAt,
            CumulativeAmount = (double)s.CumulativeAmount, s.OriginReplicaId, s.Version, Now = now,
        };
        bool changed;
        if (local is null)
        {
            conn.Execute(InsertShipmentSql, param, tx);
            changed = true;
        }
        else
        {
            changed = !string.Equals(local.Status, status, StringComparison.Ordinal)
                      || local.HeldAt != s.HeldAt
                      || local.ShippedAt != s.ShippedAt
                      || local.CumulativeAmount != s.CumulativeAmount
                      || !SameId(local.OriginReplicaId, s.OriginReplicaId)
                      || local.ServerVersion != s.Version
                      || local.SyncedAt is null;
            if (changed) conn.Execute(RefreshShipmentSql, param, tx);
        }
        scope.Commit();
        return new SalesFeedApplyResult(
            changed ? SalesFeedApplyStatus.Applied : SalesFeedApplyStatus.Unchanged,
            Array.Empty<string>(), Array.Empty<string>(),
            changed ? [s.Id] : Array.Empty<string>(),
            false, 0, AggregateRecomputeResult.None);
    });

    /// <summary>Kargoya verilmemiş ve kaynağı bu veritabanının kopyası olmayan dosya (<c>DropForeignShipmentsSql</c>'in
    /// öğe karşılığı). Kaynağı boş dosya ve kopya kimliği okunamayan veritabanı yabancı sayılmaz.</summary>
    private static bool IsForeignOpenShipment(IDbConnection conn, IDbTransaction tx, SalesFeedShipment s)
    {
        if (s.Status == ShipmentStatus.Shipped || s.OriginReplicaId is not { } origin) return false;
        var own = conn.ExecuteScalar<string?>("SELECT ReplicaId FROM _meta WHERE Id = 1", transaction: tx);
        return own is not null && !SameId(origin, own);
    }

    // ── uzak satırların temizliği ────────────────────────────────────────

    /// <summary>Uzak oturumların etiketleri (FK sırası: önce etiketler; <c>LabelBackup</c> kaskadla gider).</summary>
    private const string DeleteRemoteLabelsSql = @"
        DELETE FROM Label WHERE SessionId IN (SELECT Id FROM StreamSession WHERE Origin = 'remote')";

    /// <summary>Akıştan inmiş oturumlar. <c>Origin = 'local'</c> satırlara hiç dokunulmaz.</summary>
    private const string DeleteRemoteSessionsSql = @"
        DELETE FROM StreamSession WHERE Origin = 'remote'";

    /// <summary>
    /// Akıştan inmiş bütün satırları siler (spec 2026-10-13 §5.4 "lisans değişince temizlik"; <c>disable</c> turunun
    /// 2. adımı da aynı temizliği kullanır, §8.5): <c>Origin = 'remote'</c> oturumlar ve etiketleri,
    /// <c>RemoteLiveSession</c>'ın tamamı; etkilenen müşterilerin toplamları aynı işlemde etiketlerden yeniden türetilir
    /// (§6.1). Bu bilgisayarın kendi (<c>Origin = 'local'</c>) oturum ve etiketlerine, kargo aynasına ve müşteri
    /// satırlarına dokunulmaz.
    ///
    /// <para>Tek <see cref="SyncApplyScope"/> işlemi; kilit sırası U17 (önce <see cref="CustomerBusySet"/>). Etkilenen
    /// müşterilerden biri ödeme akışındaysa hiçbir şey silinmez ve sonuç <see cref="SalesRemotePurgeResult.Busy"/>
    /// döner — ödeme isteği o kişinin toplamını okurken altından etiket çekilmesin (U13); çağıran sonraki turda yeniden
    /// dener. İdempotent: silinecek satır yoksa hiçbir şey yazılmaz.</para>
    /// </summary>
    public SalesRemotePurgeResult PurgeRemoteRows() => Locked(isBusy =>
    {
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;

        var customers = conn.Query<string>(
            @"SELECT DISTINCT l.CustomerId FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
               WHERE s.Origin = 'remote'", transaction: tx).ToList();
        if (customers.Any(isBusy)) return SalesRemotePurgeResult.WasBusy;   // Dispose geri alır

        var labels = conn.Execute(DeleteRemoteLabelsSql, transaction: tx);
        var sessions = conn.Execute(DeleteRemoteSessionsSql, transaction: tx);
        var live = conn.Execute("DELETE FROM RemoteLiveSession", transaction: tx);
        var aggregates = CustomerAggregateSql.Recompute(conn, tx, customers);
        scope.Commit();
        return new SalesRemotePurgeResult(false, sessions, labels, live, aggregates);
    });

    // ── disable turunun 2. adımı (§8.5-2; W2-11) ─────────────────────────

    /// <summary>Kendi yayınları yerel otoriteye döner (Off'ta her bilgisayar kendi yayınını push'la yazar). Authority
    /// hiçbir tetikleyici listesinde değil: kuyruğa almaz, SyncedAt ve Revision değişmez.</summary>
    private const string DisableSessionAuthoritySql = @"
        UPDATE StreamSession SET Authority = 'local' WHERE Origin = 'local' AND Authority = 'server'";

    /// <summary>Off'ta başka kopyanın olacak kargoya verilmemiş dosyalar (§4.10: sunucu <c>OriginReplicaId</c>'yi bağlı
    /// etiketlerin sahibine göre yeniden yazdı, son akış taşıdı). NULL kaynak bu veritabanınındır (049 başlığı): kalır.
    /// Kargoya verilmiş dosya her kipte tek yönlüdür (§7.1) ve kalır.</summary>
    private const string DropForeignShipmentsSql = @"
        DELETE FROM Shipment
         WHERE Status <> 'Shipped'
           AND OriginReplicaId IS NOT NULL
           AND lower(replace(OriginReplicaId, '-', '')) <> @replica";

    /// <summary>
    /// <c>disable</c> turunun 2. adımı (spec 2026-10-13 §8.5-2, §6.5-1, M15, M19): son akış boş sayfaya kadar
    /// uygulandıktan sonra TEK <see cref="SyncApplyScope"/> işleminde —
    /// <list type="bullet">
    ///   <item><c>SalesSyncState.Mode = 0</c>; <c>ModeSeenAt = </c><paramref name="modeSeenAt"/> (boşsa eskisi kalır).</item>
    ///   <item>Kendi (<c>Origin = 'local'</c>) yayınları <c>Authority = 'local'</c>.</item>
    ///   <item><c>Origin = 'remote'</c> etiket ve oturumlar ile <c>RemoteLiveSession</c> silinir (lisans değişimi
    ///   temizliğinin aynısı, <see cref="PurgeRemoteRows"/>); etkilenen müşterilerin toplamları aynı işlemde etiketlerden
    ///   türetilir (§6.1). Off'ta uzak etiket kalmaz: kirasız kümülatif çift tam istek penceresi (R1) açılmaz.</item>
    ///   <item>Kaynağı bu kopya (<paramref name="replicaId"/>) olmayan kargoya verilmemiş dosyalar silinir; kalanlar yerel
    ///   otoriteye döner (mod 0'da 049'un dosya tetikleyicileri ABORT etmez).</item>
    ///   <item>Akış hata kayıtları (U10) silinir: Off'ta akış uygulanmaz, yeniden açılış akışı baştan alır.</item>
    /// </list>
    /// Kilit sırası U17 (önce <see cref="CustomerBusySet"/>). Silinecek uzak etiketlerin bir müşterisi ödeme akışındaysa
    /// hiçbir şey yazılmaz (<see cref="SalesModeOffResult.Busy"/>; yerel kip de değişmez): çağıran sonraki turda yeniden
    /// dener. İdempotent: yerel kip zaten 0 olan kopyada (mod On dönemini hiç görmemiş Ws, ya da yarıda kalmış tur) da
    /// aynı sonucu verir. Etiket satırlarına (kendi), müşteri satırlarına ve bekleyen komutlara dokunulmaz — bekleyen
    /// komutların yerel yazıma çevrilmesi 3. adımdır, kilit dışında.
    /// </summary>
    /// <param name="modeSeenAt"><c>sales/state</c>'in <c>modeChangedAt</c>'i (unix saniye).</param>
    /// <param name="replicaId">Bu veritabanının kopya kimliği.</param>
    public SalesModeOffResult ApplyModeOff(long? modeSeenAt, Guid replicaId) => Locked(isBusy =>
    {
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;

        var customers = conn.Query<string>(
            @"SELECT DISTINCT l.CustomerId FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
               WHERE s.Origin = 'remote'", transaction: tx).ToList();
        if (customers.Any(isBusy)) return SalesModeOffResult.WasBusy;   // Dispose geri alır

        if (conn.Execute("UPDATE SalesSyncState SET Mode = 0, ModeSeenAt = COALESCE(@modeSeenAt, ModeSeenAt) WHERE Id = 1",
                new { modeSeenAt }, tx) != 1)
            throw new InvalidOperationException("SalesSyncState satırı yok (göç 049).");
        var toLocal = conn.Execute(DisableSessionAuthoritySql, transaction: tx);
        var labels = conn.Execute(DeleteRemoteLabelsSql, transaction: tx);
        var sessions = conn.Execute(DeleteRemoteSessionsSql, transaction: tx);
        var live = conn.Execute("DELETE FROM RemoteLiveSession", transaction: tx);
        var aggregates = CustomerAggregateSql.Recompute(conn, tx, customers);
        var shipments = conn.Execute(DropForeignShipmentsSql, new { replica = replicaId.ToString("N") }, tx);
        var failures = conn.Execute("DELETE FROM SalesFeedFailure", transaction: tx);
        scope.Commit();
        return new SalesModeOffResult(false, toLocal, sessions, labels, live, shipments, failures, aggregates);
    });

    // ── ortak ────────────────────────────────────────────────────────────

    /// <summary>Gövdeyi <see cref="CustomerBusySet"/> kilidi altında koşar (U17: küme → SQLite yazma kilidi). Küme yoksa
    /// (testler) hiçbir müşteri meşgul sayılmaz.</summary>
    private T Locked<T>(Func<Func<string, bool>, T> body)
        => _busy is null ? body(static _ => false) : _busy.RunLocked(body);

    /// <summary>Kimlik karşılaştırması: aynı GUID'in farklı yazımı farklı sayılmaz.</summary>
    private static bool SameId(string? a, string? b) =>
        a is null || b is null
            ? a is null && b is null
            : string.Equals(a, b, StringComparison.Ordinal)
              || (Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb) && ga == gb);

    /// <summary>Etiketin beş birimi (yerel biçim).</summary>
    private sealed record Units(
        decimal Price, long? PriceCorrectedAt, decimal? PaidPrice,
        long? CancelledAt, string? CancelReason,
        bool IsTentativeBackup,
        string? ShipmentId,
        long? PrintedAt, string? PrintBatchId, long? PrintSeq)
    {
        public static Units Of(LabelRow r) => new(
            r.Price, r.PriceCorrectedAt, r.PaidPrice, r.CancelledAt, r.CancelReason, r.IsTentativeBackup != 0,
            r.ShipmentId, r.PrintedAt, r.PrintBatchId, r.PrintSeq);

        public static Units Of(SalesFeedOrder o) => new(
            o.Price, o.PriceCorrectedAt, o.PaidPrice, o.CancelledAt, o.CancelReason, o.IsTentativeBackup,
            o.ShipmentId, o.PrintedAt, o.PrintBatchId, o.PrintSeq);

        /// <summary>Farklı birimler, teldeki adlarıyla (<see cref="SalesPushUnits"/>).</summary>
        public IReadOnlyList<string> ChangedUnits(Units other)
        {
            var changed = new List<string>();
            if (Price != other.Price || PriceCorrectedAt != other.PriceCorrectedAt || PaidPrice != other.PaidPrice)
                changed.Add(SalesPushUnits.Price);
            if (CancelledAt != other.CancelledAt || !string.Equals(CancelReason, other.CancelReason, StringComparison.Ordinal))
                changed.Add(SalesPushUnits.Cancel);
            if (IsTentativeBackup != other.IsTentativeBackup)
                changed.Add(SalesPushUnits.Backup);
            if (!SameId(ShipmentId, other.ShipmentId))
                changed.Add(SalesPushUnits.Shipment);
            if (PrintedAt != other.PrintedAt || !string.Equals(PrintBatchId, other.PrintBatchId, StringComparison.Ordinal)
                || PrintSeq != other.PrintSeq)
                changed.Add(SalesPushUnits.Print);
            return changed;
        }
    }

    /// <summary>Dapper ara sınıfları: SQLite INTEGER → long, REAL → decimal.</summary>
    private sealed class SessionRow
    {
        public string Origin { get; set; } = StreamSessionOrigin.Local;
        public string Authority { get; set; } = StreamSessionAuthority.Local;
        public long? EndedAt { get; set; }
        public long? ReleasedAt { get; set; }
        public string? Title { get; set; }
        public string? Platforms { get; set; }
        public string? Notes { get; set; }
        public string? OwnerMachineName { get; set; }
    }

    private sealed class LabelRow
    {
        public string CustomerId { get; set; } = "";
        public decimal Price { get; set; }
        public long? PriceCorrectedAt { get; set; }
        public decimal? PaidPrice { get; set; }
        public long? CancelledAt { get; set; }
        public string? CancelReason { get; set; }
        public long IsTentativeBackup { get; set; }
        public string? ShipmentId { get; set; }
        public long? PrintedAt { get; set; }
        public string? PrintBatchId { get; set; }
        public long? PrintSeq { get; set; }
        public long? SyncedAt { get; set; }
        public long? StockSyncedAt { get; set; }
        public long? ServerVersion { get; set; }
        public string? SessionOrigin { get; set; }
    }

    private sealed class ShipmentRow
    {
        public string Status { get; set; } = "";
        public long? HeldAt { get; set; }
        public long? ShippedAt { get; set; }
        public decimal CumulativeAmount { get; set; }
        public string? OriginReplicaId { get; set; }
        public long? ServerVersion { get; set; }
        public long? SyncedAt { get; set; }
    }
}
