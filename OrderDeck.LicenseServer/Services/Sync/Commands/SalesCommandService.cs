using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Stock;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// <c>POST sales/commands</c>'ın işleme hattı — spec 2026-10-13 §4.6. Devredilmiş yayının etiketini ya da kargo dosyasını
/// (<c>attach-shipment</c>, <c>shipment-decision</c>) değiştiren ve sahibi susmuş canlı yayını bitiren
/// (<c>force-end</c>) her komut buradan geçer; türün kuralını
/// <see cref="ISalesCommandHandler"/> uygular. Sıra:
/// <list type="number">
///   <item><b>İstek.</b> v2 zarfı (aksi 400 <c>invalid-envelope</c>), <c>commandId</c>, kayıtlı bir tür (aksi 400
///   <c>unknown-kind</c>), beklenen sürümler ve türün yükü (400). Lisans sahipliğini uç denetler (404).</item>
///   <item><b>Kalp atışı ve kapılar.</b> İstek kopyanın kalp atışıdır (<see cref="SalesReplicaService.AcceptAsync"/>).
///   Mod Off ise 409 <c>mode-off</c>: bugünkü (v0.9.10) davranış değişmez, komut hiçbir şey yazmaz. Alt sürüm
///   (<c>SalesSync:MinClientVersion</c>) tanımsız ya da ayrıştırılamıyorsa, ya da istemcinin <c>User-Agent</c>'taki sürümü
///   yok ya da eskiyse 409 <c>client-update-required</c> (kapalı-hata).</item>
///   <item><b>Yeniden oynatma.</b> <c>CommandId</c> günlükte varsa ve kanonik isteğin özeti aynıysa komut yeniden
///   uygulanmaz: kayıtlı durum ve sonuç, satırların GÜNCEL hâliyle döner. Yazmayan komutun (<c>reprint</c>) sonucu
///   kayıttan değil, komut yazmadan yeniden çalıştırılarak güncel satırlardan kurulur
///   (<see cref="ISalesCommand.RecomputesOnReplay"/>). Özet farklıysa ya da Id başka lisansın günlüğündeyse 409
///   <c>content-conflict</c>.</item>
///   <item><b>Uygulama</b>, tek işlemde ve tek SaveChanges'te: işleyici görünürlüğü (409 <c>session-live</c>) ve
///   beklenen sürümü (409 <c>stale</c>) denetler, alanları ve birim damgalarını sunucu saatiyle yazar; hat değişen
///   siparişlerin bağlı türetilmiş dosyalarını yeniden türetir (K7; dosyalar bağlı siparişlerden önce yüklenir), stok
///   defterini birleşik durumdan yazar (yalnız ürünü bilinen siparişte) ve günlük satırını ekler. İşleyici işlem kapsamlı
///   uygulama kilidi alabilir (<see cref="SalesCommandScope.AcquireLockAsync"/>).</item>
///   <item><b>Yarış.</b> Jeton çakışması (<see cref="DbUpdateConcurrencyException"/>) işlemi tümüyle geri alır, 409
///   <c>stale</c> ve taze satırlar döner. Aynı <c>CommandId</c>'nin eşzamanlı ikinci kopyası (jeton ya da günlüğün
///   birincil anahtarı) günlük yeniden okunarak oynatılır: komut bir kez uygulanır, iki istek de aynı sonucu alır.
///   Komutun eklediği siparişin Id'sini eşzamanlı başka bir istek aldıysa (<c>add-backup</c>; birincil anahtar ihlali)
///   409 <c>content-conflict</c>.</item>
/// </list>
/// <b>Bildirim yok:</b> komutlar push, WhatsApp etiket kuralı ya da shopper bildirimi üretmez (§10.2). Tek istisna
/// <c>shipment-decision</c>: bir dosyanın durumu gerçekten değiştiyse işlem commit edildikten sonra
/// <c>ShipmentStatusChanged</c> etiket kuralı uygulanır (<c>shipments/sync</c> ile eşlik; hata yutulur, yeniden
/// oynatmada tetiklenmez). Günlüğe yalnız sayı ve kimlik yazılır.
/// </summary>
public sealed class SalesCommandService
{
    /// <summary>Bir komutun en çok sipariş sayısı (öğeler ve beklenen sürümler).</summary>
    public const int MaxOrders = 500;

    public const string CodeInvalidEnvelope = "invalid-envelope";
    public const string CodeInvalidCommand = "invalid-command";
    public const string CodeUnknownKind = "unknown-kind";
    public const string CodeInvalidExpected = "invalid-expected";
    public const string CodeInvalidPayload = "invalid-payload";
    public const string CodeModeOff = "mode-off";
    public const string CodeClientUpdateRequired = "client-update-required";
    public const string CodeContentConflict = "content-conflict";

    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LicenseDbContext _db;
    private readonly SalesReplicaService _replicas;
    private readonly ShipmentAmountDeriver _shipmentAmounts;
    private readonly SessionReleaser _releaser;
    private readonly StockLedgerWriter _ledger;
    private readonly IReadOnlyDictionary<string, ISalesCommandHandler> _handlers;
    private readonly LabelRuleApplier _labels;
    private readonly SalesSyncOptions _options;
    private readonly ILogger<SalesCommandService> _log;

    public SalesCommandService(
        LicenseDbContext db,
        SalesReplicaService replicas,
        ShipmentAmountDeriver shipmentAmounts,
        SessionReleaser releaser,
        StockLedgerWriter ledger,
        IEnumerable<ISalesCommandHandler> handlers,
        LabelRuleApplier labels,
        IOptions<SalesSyncOptions> options,
        ILogger<SalesCommandService> log)
    {
        _db = db;
        _replicas = replicas;
        _shipmentAmounts = shipmentAmounts;
        _releaser = releaser;
        _ledger = ledger;
        _handlers = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);
        _labels = labels;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Bir komutu işler. Lisans sahipliği çağıran uçta denetlenmiş olmalı.</summary>
    /// <param name="userAgent">İsteğin <c>User-Agent</c>'ı: istemci sürümü ve kalp atışı.</param>
    public async Task<SalesCommandReply> ExecuteAsync(
        Guid licenseId, SalesCommandRequest request, string? userAgent, CancellationToken ct)
    {
        if (!SalesEnvelope.TryRead(request, out var envelope, out var envelopeError) || !envelope.IsV2)
            return Fail(StatusCodes.Status400BadRequest, CodeInvalidEnvelope,
                envelopeError ?? "Komutlar v2 isteğidir (replicaId, protocol = 2, seenMode).");
        if (request.CommandId is not { } commandId || commandId == Guid.Empty)
            return Fail(StatusCodes.Status400BadRequest, CodeInvalidCommand, "commandId boş olamaz.");
        if (request.Kind is null || !_handlers.TryGetValue(request.Kind, out var handler))
            return Fail(StatusCodes.Status400BadRequest, CodeUnknownKind, "Bu sunucu bu komut türünü tanımıyor.");
        if (!TryReadExpected(request.Expected, out var expected, out var expectedError))
            return Fail(StatusCodes.Status400BadRequest, CodeInvalidExpected, expectedError);
        if (!handler.TryParse(request.Payload ?? default, expected, out var command, out var payloadError))
            return Fail(StatusCodes.Status400BadRequest, CodeInvalidPayload, payloadError);

        var context = await _replicas.AcceptAsync(licenseId, envelope, userAgent, "sales/commands", ct);
        if (context.Mode != SalesSyncMode.On)
            return Fail(StatusCodes.Status409Conflict, CodeModeOff,
                "Satış senkronu bu lisansta kapalı; değişiklik yerel yoldan yapılır.");
        if (!ClientVersion.IsAtLeast(ClientVersion.FromUserAgent(userAgent), _options.ParseMinClientVersion()))
            return Fail(StatusCodes.Status409Conflict, CodeClientUpdateRequired,
                "Bu işlem için uygulamanın güncel sürümü gerekiyor.");

        var hash = SalesCommandHash.Compute(handler.Kind, envelope.ReplicaId, command.CanonicalPayload, expected);
        if (await FindLogAsync(commandId, ct) is { } logged)
            return await ReplayAsync(licenseId, commandId, logged, hash, expected, command, ct);

        return await ApplyAsync(licenseId, commandId, envelope.ReplicaId, handler.Kind, hash, expected, command, ct);
    }

    private async Task<SalesCommandReply> ApplyAsync(
        Guid licenseId,
        Guid commandId,
        Guid replicaId,
        string kind,
        byte[] hash,
        IReadOnlyList<SalesCommandExpected> expected,
        ISalesCommand command,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        string status;
        string resultJson;
        SalesCommandResult result;
        IReadOnlyList<string> statusChangedCustomers;
        IDbContextTransaction? transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        var scope = new SalesCommandScope(_db, _shipmentAmounts, _releaser, licenseId, replicaId, now, expected);
        try
        {
            var outcome = await command.ExecuteAsync(scope, ct);
            var changes = scope.Changes;

            // K7: değişen birimin bağlı türetilmiş dosyaları aynı yazımda. Dosyalar türetmenin okuduğu bağlı
            // siparişlerden ÖNCE yüklenir (sıra ve jeton gerekçesi ShipmentAmountDeriver'da); hedef siparişler zaten
            // kendi jetonları altında. İşleyicinin bu komutta kendisi türettiği dosya (kargo komutları) yeniden
            // türetilmez: son bağ değişikliğinden sonra türetildi, sürümü komut başına bir kez ilerler.
            var affected = changes
                .SelectMany(c => ShipmentAmountDeriver.AffectedBy(c.ShipmentBefore, c.Order.ShipmentId, c.Units))
                .Except(scope.DerivedShipmentIds)
                .Distinct()
                .ToList();
            var shipments = await _shipmentAmounts.LoadAsync(licenseId, affected, ct);
            foreach (var (id, tracked) in scope.TrackedShipments) shipments.TryAdd(id, tracked);
            var derivedHere = await _shipmentAmounts.DeriveAsync(
                licenseId, shipments, affected, scope.TrackedOrders, now, ct);
            var derived = scope.DerivedShipmentIds.Concat(derivedHere).Distinct().ToList();

            // Defter birleşik durumdan ve siparişlerle aynı SaveChanges'te (orders/sync'in gerekçesi): ürünü bilinmeyen
            // siparişin defterde satırı yoktur.
            var ledgerRows = changes
                .Select(c => c.Order)
                .Where(o => o.ProductId is not null)
                .Select(o => new LedgerOrderInput(
                    new LedgerOrderState(
                        o.Id, o.ProductId, o.ProductVariantId, o.IsShippingFee, o.CancelledAt is not null,
                        o.IsTentativeBackup),
                    o.AddedAt,
                    o.CancelledAt))
                .ToList();
            if (ledgerRows.Count > 0)
                await _ledger.ApplyAsync(licenseId, ledgerRows, DateTimeOffset.UtcNow, ct);

            SalesCommandShipmentAmount AmountOf(Guid id)
            {
                var s = shipments[id];
                return new SalesCommandShipmentAmount(s.Id, s.Version, s.CumulativeAmount);
            }

            result = ResultOf(outcome, changes, derived, AmountOf);
            statusChangedCustomers = outcome.ShipmentStatusChangedCustomers ?? [];
            status = changes.Count > 0 || scope.ShipmentsChanged || scope.EndedSessionIds.Count > 0
                ? SalesCommandLog.StatusApplied
                : SalesCommandLog.StatusUnchanged;
            resultJson = JsonSerializer.Serialize(result, ResultJsonOptions);
            _db.SalesCommandLogs.Add(new SalesCommandLog
            {
                CommandId = commandId,
                LicenseId = licenseId,
                ReplicaId = replicaId,
                Kind = kind,
                RequestHash = hash,
                Status = status,
                ResultJson = resultJson,
                CreatedAt = now,
            });

            await _db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);

            _log.LogInformation(
                "sales/commands: {Kind} {Status} — {Changed} sipariş yazıldı, {Skipped} atlandı, {Derived} dosya "
                + "türetildi, {Ledger} sipariş deftere, {Ended} yayın bitirilip devredildi (license={LicenseId}, "
                + "replica={ReplicaId}, command={CommandId})",
                kind, status, outcome.Changed.Count, outcome.Skipped.Count, derived.Count, ledgerRows.Count,
                scope.EndedSessionIds.Count, licenseId, replicaId, commandId);
        }
        catch (SalesCommandRejection rejection)
        {
            await RollbackAsync(transaction);
            _log.LogInformation(
                "sales/commands: {Kind} reddedildi: {Code} (license={LicenseId}, replica={ReplicaId}, "
                + "command={CommandId})",
                kind, rejection.Code, licenseId, replicaId, commandId);
            return await RejectedAsync(licenseId, rejection, command, ct);
        }
        catch (DbUpdateException ex)
        {
            await RollbackAsync(transaction);

            // Aynı CommandId'nin eşzamanlı kopyası bizden önce kaydetti mi (jeton ya da günlüğün birincil anahtarı)?
            // Kaydettiyse komut bir kez uygulandı: kayıtlı sonuç oynatılır.
            if (await FindLogAsync(commandId, ct) is { } winner)
                return await ReplayAsync(licenseId, commandId, winner, hash, expected, command, ct);

            // Eklenen siparişin Id'sini ön denetimden sonra eşzamanlı başka bir istek (başka bir komut, push ya da
            // başka lisans) aldıysa birincil anahtar ihlali 500 değil, ön denetimin vereceği 409 content-conflict olur.
            var inserted = scope.InsertedOrderIds.ToList();
            if (inserted.Count > 0
                && await _db.Orders.AsNoTracking()
                    .Where(o => inserted.Contains(o.Id))
                    .Select(o => (Guid?)o.Id)
                    .FirstOrDefaultAsync(ct) is { } taken)
            {
                _log.LogInformation(
                    "sales/commands: {Kind} eklediği sipariş Id'si eşzamanlı alındı, 409 content-conflict "
                    + "(license={LicenseId}, replica={ReplicaId}, command={CommandId})",
                    kind, licenseId, replicaId, commandId);
                return await RejectedAsync(licenseId, SalesCommandRejection.ContentConflict(taken), command, ct);
            }

            // Açılan dosyanın Id'si için aynı gerekçe (attach-shipment'ın proposedShipmentId'si).
            var insertedShipments = scope.InsertedShipmentIds.ToList();
            if (insertedShipments.Count > 0
                && await _db.Shipments.AsNoTracking()
                    .Where(s => insertedShipments.Contains(s.Id))
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync(ct) is { } takenShipment)
            {
                _log.LogInformation(
                    "sales/commands: {Kind} açtığı dosyanın Id'si eşzamanlı alındı, 409 content-conflict "
                    + "(license={LicenseId}, replica={ReplicaId}, command={CommandId})",
                    kind, licenseId, replicaId, commandId);
                return await RejectedAsync(
                    licenseId, SalesCommandRejection.ShipmentContentConflict(takenShipment), command, ct);
            }
            if (ex is not DbUpdateConcurrencyException) throw;

            _log.LogInformation(
                "sales/commands: {Kind} eşzamanlı yazıma çarptı, 409 stale (license={LicenseId}, replica={ReplicaId}, "
                + "command={CommandId})",
                kind, licenseId, replicaId, commandId);
            return await RejectedAsync(
                licenseId,
                SalesCommandRejection.Stale("Satırlar eşzamanlı başka bir istekle yazıldı; güncel sürümlerle yeniden deneyin."),
                command,
                ct);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        // İş kaydı commit edildi; etiket kuralı ondan sonra ve hatası yutularak (LabelRuleApplier'ın kuralı: karar
        // etiket yazılamadı diye geri alınmaz).
        if (statusChangedCustomers.Count > 0)
            await _labels.TryApplyAndSaveByWpfCustomersAsync(
                licenseId, WaLabelEvent.ShipmentStatusChanged, statusChangedCustomers, ct);

        return await SucceededAsync(licenseId, status, resultJson, command, result, ct);
    }

    /// <summary>
    /// Yeniden oynatma (§4.6 adım 2, A11 deseni): aynı lisans ve aynı özet → kayıtlı durum ve sonuç, satırların güncel
    /// hâliyle; hiçbir şey yazılmaz. Aksi hâlde 409 <c>content-conflict</c> (istemci hatası; yeniden denenmez). Yazmayan
    /// komutun (<see cref="ISalesCommand.RecomputesOnReplay"/>) sonucu kayıttan değil güncel satırlardan kurulur
    /// (<see cref="RecomputeAsync"/>).
    /// </summary>
    private async Task<SalesCommandReply> ReplayAsync(
        Guid licenseId,
        Guid commandId,
        SalesCommandLog logged,
        byte[] hash,
        IReadOnlyList<SalesCommandExpected> expected,
        ISalesCommand command,
        CancellationToken ct)
    {
        if (logged.LicenseId != licenseId || !logged.RequestHash.AsSpan().SequenceEqual(hash))
        {
            _log.LogWarning(
                "sales/commands: command {CommandId} farklı bir içerikle ya da başka bir lisansta zaten kayıtlı, 409 "
                + "content-conflict (license={LicenseId})",
                commandId, licenseId);
            return Fail(StatusCodes.Status409Conflict, CodeContentConflict,
                "Bu commandId farklı bir içerikle zaten kullanıldı.");
        }

        _log.LogInformation(
            "sales/commands: command {CommandId} yeniden oynatıldı ({Kind}, license={LicenseId})",
            commandId, logged.Kind, licenseId);
        if (command.RecomputesOnReplay)
            return await RecomputeAsync(licenseId, logged, expected, command, ct);

        var result = JsonSerializer.Deserialize<SalesCommandResult>(logged.ResultJson, ResultJsonOptions);
        return await SucceededAsync(licenseId, logged.Status, logged.ResultJson, command, result, ct);
    }

    /// <summary>
    /// Yazmayan komutun yeniden oynatması (<c>reprint</c>, §5.6): komut, günlükteki kopyanın adına, işlemsiz ve
    /// kaydetmeden yeniden çalışır; sonuç güncel satırlardan kurulur, kayıtlı durumla döner. Günlüğe ikinci satır
    /// yazılmaz (ilk satır denetim izidir). Görünürlük reddi ilk uygulamadaki gibi döner. Komut bir şey yazmaya
    /// kalkarsa programlama hatasıdır.
    /// </summary>
    private async Task<SalesCommandReply> RecomputeAsync(
        Guid licenseId,
        SalesCommandLog logged,
        IReadOnlyList<SalesCommandExpected> expected,
        ISalesCommand command,
        CancellationToken ct)
    {
        var scope = new SalesCommandScope(
            _db, _shipmentAmounts, _releaser, licenseId, logged.ReplicaId, DateTimeOffset.UtcNow, expected);
        SalesCommandOutcome outcome;
        try
        {
            outcome = await command.ExecuteAsync(scope, ct);
        }
        catch (SalesCommandRejection rejection)
        {
            return await RejectedAsync(licenseId, rejection, command, ct);
        }
        finally
        {
            // Yalnız okudu; izleyici boşalır, sonraki okumalar veritabanını görür.
            _db.ChangeTracker.Clear();
        }

        if (scope.Changes.Count > 0 || scope.ShipmentsChanged || scope.EndedSessionIds.Count > 0)
            throw new InvalidOperationException("Yeniden oynatmada yeniden çalışan komut yazmamalı.");

        var result = ResultOf(outcome, [], [], id => throw new InvalidOperationException(
            $"Yazmayan komutun sonucunda dosya ({id}) olamaz."));
        return await SucceededAsync(
            licenseId, logged.Status, JsonSerializer.Serialize(result, ResultJsonOptions), command, result, ct);
    }

    /// <summary>Komutun sonucu (<see cref="SalesCommandResult"/>): yazılan ve atlanan siparişler, yazılanların yeni
    /// sürümleri, türetilen dosyalar ve türe özgü listeler.</summary>
    private static SalesCommandResult ResultOf(
        SalesCommandOutcome outcome,
        IReadOnlyList<SalesCommandChange> changes,
        IReadOnlyList<Guid> derived,
        Func<Guid, SalesCommandShipmentAmount> amountOf) =>
        new(
            outcome.Changed,
            outcome.Skipped,
            changes.Select(c => new SalesCommandOrderVersion(c.Order.Id, c.Order.SyncVersion)).ToList(),
            derived.Select(amountOf).ToList())
        {
            Marked = outcome.Marked,
            AlreadyPrinted = outcome.AlreadyPrinted,
            Cancelled = outcome.Cancelled,
            ReprintIds = outcome.ReprintIds,
            CorrectionIds = outcome.CorrectionIds,
            ShipmentId = outcome.ShipmentId,
            Pool = outcome.Pool,
            PoolShipmentIds = outcome.PoolShipmentIds,
            Decided = outcome.DecidedShipmentIds?.Select(amountOf).ToList(),
            AlreadyReleased = outcome.AlreadyReleased,
        };

    /// <summary>
    /// Başarı yanıtı (uygulama ve yeniden oynatma aynı yoldan): siparişler komutunkiler ve ardından komutun yazdığı
    /// öbürleri (ör. kargoya verilirken bağı çözülenler), dosyalar komutunkiler ve sonucun dosyaları (türetilenler,
    /// havuz, kararın dosyaları), yayınlar siparişlerinkiler ve komutunkiler (<c>force-end</c>) — hepsi güncel
    /// hâlleriyle.
    /// </summary>
    private async Task<SalesCommandReply> SucceededAsync(
        Guid licenseId,
        string status,
        string resultJson,
        ISalesCommand command,
        SalesCommandResult? result,
        CancellationToken ct)
    {
        var orderIds = command.OrderIds.Concat(result?.Changed ?? []).Distinct().ToList();
        var shipmentIds = command.ShipmentIds.Concat(result?.ResponseShipmentIds() ?? []).Distinct().ToList();
        var (orders, shipments, sessions) =
            await CurrentRowsAsync(licenseId, orderIds, shipmentIds, command.SessionIds, ct);

        // Havuz (attach-shipment, §7.1): komut anında okunan tutarlardan değil, yanıtın güncel dosyalarından — commit'ten
        // (ve yeniden oynatmada ilk uygulamadan) sonra. Hedef dışındaki açık dosyayı ship kilidini almayan bir yazıcı
        // (cancel komutu, sahibin push'u, K7) komutla eşzamanlı türetebilir; kayıttaki değer o zaman hiç var olmamış bir
        // havuzdur ve eşik kararı onunla verilirdi. Günlükteki değer komut anının denetim izi olarak kalır.
        if (result is { PoolShipmentIds: { } poolIds })
        {
            var pool = shipments.Where(s => poolIds.Contains(s.Id) && IsOpen(s)).Sum(s => s.CumulativeAmount);
            if (pool != result.Pool)
            {
                result = result with { Pool = pool };
                resultJson = JsonSerializer.Serialize(result, ResultJsonOptions);
            }
        }

        using var document = JsonDocument.Parse(resultJson);
        return new SalesCommandSucceeded(
            new SalesCommandResponse(status, orders, shipments, sessions, document.RootElement.Clone()));
    }

    /// <summary>Ret yanıtı; <c>stale</c> ve <c>plan-stale</c> komutun (ya da retin verdiği) siparişlerin taze hâlini
    /// <c>current</c> olarak, komutun (ya da retin verdiği) dosyalarınkini <c>currentShipments</c> olarak taşır.</summary>
    private async Task<SalesCommandReply> RejectedAsync(
        Guid licenseId, SalesCommandRejection rejection, ISalesCommand command, CancellationToken ct)
    {
        var extensions = new Dictionary<string, object?>(rejection.Extensions);
        var shipmentIds = rejection.CurrentShipmentIds ?? (rejection.WithCurrentOrders ? command.ShipmentIds : []);
        if (rejection.WithCurrentOrders || shipmentIds.Count > 0)
        {
            var rows = await CurrentRowsAsync(
                licenseId, rejection.WithCurrentOrders ? rejection.CurrentOrderIds ?? command.OrderIds : [],
                shipmentIds, [], ct);
            if (rejection.WithCurrentOrders) extensions["current"] = rows.Orders;
            if (shipmentIds.Count > 0)
                extensions["currentShipments"] = rows.Shipments.Where(s => shipmentIds.Contains(s.Id)).ToList();
        }
        return new SalesCommandFailed(rejection.StatusCode, rejection.Code, rejection.Message, extensions);
    }

    /// <summary>
    /// Yanıtın satırları, veritabanındaki güncel hâlleriyle ve akış öğesi biçiminde: siparişler istenen sırayla (asıl
    /// kaydın Id'si ve KVKK okuma maskesiyle, <see cref="SalesOrderItem.From"/>), bağlı oldukları ve verilen dosyalar,
    /// siparişlerin oturumları ve verilen oturumlar.
    /// </summary>
    private async Task<(IReadOnlyList<SalesOrderItem> Orders, IReadOnlyList<SalesShipmentItem> Shipments,
        IReadOnlyList<SalesSessionItem> Sessions)> CurrentRowsAsync(
        Guid licenseId,
        IReadOnlyList<Guid> orderIds,
        IReadOnlyList<Guid> shipmentIds,
        IReadOnlyList<Guid> sessionIds,
        CancellationToken ct)
    {
        var rows = orderIds.Count == 0
            ? new Dictionary<Guid, Order>()
            : await _db.Orders.AsNoTracking()
                .Where(o => o.LicenseId == licenseId && orderIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, ct);
        var orders = orderIds.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
        var customers = orders.Count == 0
            ? SalesCustomerDirectory.Empty
            : await SalesCustomerDirectory.LoadAsync(_db, licenseId, orders.Select(o => o.CustomerId), ct);

        var shipmentKeys = orders.Where(o => o.ShipmentId != null).Select(o => o.ShipmentId!.Value)
            .Concat(shipmentIds)
            .Distinct()
            .ToList();
        var shipments = shipmentKeys.Count == 0
            ? new Dictionary<Guid, Shipment>()
            : await _db.Shipments.AsNoTracking()
                .Where(s => s.LicenseId == licenseId && shipmentKeys.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

        var sessionKeys = orders.Where(o => o.SessionId != null).Select(o => o.SessionId!.Value)
            .Concat(sessionIds)
            .Distinct()
            .ToList();
        var sessions = sessionKeys.Count == 0
            ? new Dictionary<Guid, StreamSession>()
            : await _db.StreamSessions.AsNoTracking()
                .Where(s => s.LicenseId == licenseId && sessionKeys.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

        return (
            orders.Select(o => SalesOrderItem.From(o, customers.Resolve(o.CustomerId))).ToList(),
            shipmentKeys.Where(shipments.ContainsKey).Select(id => SalesShipmentItem.From(shipments[id])).ToList(),
            sessionKeys.Where(sessions.ContainsKey).Select(id => SalesSessionItem.From(sessions[id])).ToList());
    }

    /// <summary>Havuza giren açık dosya (§7.1): Pending ya da Held.</summary>
    private static bool IsOpen(SalesShipmentItem shipment) =>
        Enum.TryParse<ShipmentStatus>(shipment.Status, ignoreCase: true, out var status)
        && status is ShipmentStatus.Pending or ShipmentStatus.Held;

    private Task<SalesCommandLog?> FindLogAsync(Guid commandId, CancellationToken ct) =>
        _db.SalesCommandLogs.AsNoTracking().SingleOrDefaultAsync(c => c.CommandId == commandId, ct);

    /// <summary>İşlemi geri alır ve izleyiciyi boşaltır: sonraki okumalar (yeniden oynatma, taze satırlar) veritabanını
    /// görür, bellekteki yarım yazımı değil.</summary>
    private async Task RollbackAsync(IDbContextTransaction? transaction)
    {
        if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
    }

    /// <summary>Beklenen sürümler: sipariş Id'si dolu ve tekil, sürüm ≥ 0, en çok <see cref="MaxOrders"/>.</summary>
    private static bool TryReadExpected(
        List<SalesCommandExpectedDto>? raw,
        out IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(false)] out string? error)
    {
        expected = [];
        error = null;
        if (raw is null) return true;
        if (raw.Count > MaxOrders)
        {
            error = $"expected en çok {MaxOrders} öğe taşır.";
            return false;
        }

        var list = new List<SalesCommandExpected>(raw.Count);
        var seen = new HashSet<Guid>();
        foreach (var item in raw)
        {
            if (item?.OrderId is not { } orderId || orderId == Guid.Empty || item.Version is not { } version
                || version < 0)
            {
                error = "expected öğesi orderId ve 0 ya da daha büyük bir version taşımalı.";
                return false;
            }
            if (!seen.Add(orderId))
            {
                error = "expected bir siparişi iki kez taşıyamaz.";
                return false;
            }
            list.Add(new SalesCommandExpected(orderId, version));
        }

        expected = list;
        return true;
    }

    private static SalesCommandFailed Fail(int statusCode, string code, string detail) =>
        new(statusCode, code, detail, new Dictionary<string, object?>());
}
