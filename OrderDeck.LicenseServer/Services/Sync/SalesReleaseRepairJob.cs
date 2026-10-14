using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.Shared.Sales;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// <c>sales-release-repair</c> — spec 2026-10-13 §4.9, DM3, DM7. Göç veri geri doldurmaz (§3.1); devrin ve
/// türetilmiş dosyanın kaçırdığı satırları bu iş kapatır. Açılışta bir kez kuyruğa alınır ve günde bir koşar
/// (04:50 UTC, yayın penceresi dışında; <see cref="Program.ScheduleSalesSyncJobs"/>). Dört adım, bu sırayla:
/// <list type="number">
///   <item><b>(a) Miras oturumların devri.</b> <c>EndedAt</c> dolu ∧ <c>ReleasedAt</c> NULL ∧
///   <c>OwnerReplicaId</c> NULL olan oturumlar <see cref="SessionReleaser.ReleaseUnownedAsync"/> ile devredilir:
///   oturum ve siparişleri aynı işlemde, devrin tek yazım yolundan. Sahipsizlik koşulu koşullu UPDATE'in içinde
///   olduğu için iş hiçbir zaman bir v2 oturumunu (sahibi olan) devretmez — devrediliyor hâlindeki v2 oturumunu
///   devretmek "etiket kuyruğu boşalmadan devir yok" kuralını (M2) bozardı; onun kaybolan kapanışını istemci
///   kendisi yeniden gönderir (§5.2). İşlem başına en çok <see cref="SessionBatchSize"/> oturum: ilk koşu
///   S1'den önce bitmiş bütün yayınları devreder ve tek uzun işlem canlı gönderimlerin satır kilitlerini
///   beklerdi.</item>
///   <item><b>(b) Kaçmış sipariş damgaları.</b> Oturumu devredilmiş ama <c>SessionReleasedAt</c>'i NULL kalmış
///   siparişler — S1 öncesi imajın geri alma penceresinde eklediği satırlar ve devirle yarışan ekleme
///   (<see cref="SessionReleaser"/>'ın "kalan pencere" notu) — oturumun <c>ReleasedAt</c>'iyle,
///   <see cref="OrderBatchSize"/>'lık partilerle damgalanır. Birime ve <c>SyncVersion</c>'a dokunulmaz (devrin
///   damgasıyla aynı); <c>ChangeSeq</c> ilerler, siparişler akışa girer.</item>
///   <item><b>(c) Türetilmiş dosyalar, yalnız mod On.</b> <c>AmountDerivedAt</c> dolu ve <b>Shipped olmayan</b>
///   dosyanın saklanan tutarı bağlı siparişlerden türetilenden (<see cref="LabelRules.DerivedShipmentAmount"/>)
///   farklıysa yeniden türetilir; <c>Version</c> ve <c>UpdatedAt</c> ilerler. Shipped dosyanın tutarı kargoya
///   verildiği anda donmuştur (§7.2), <c>--accept-shipment</c> ile kabul edilmiş dosya (<c>AmountDerivedAt</c>
///   NULL) saklanan tutarıyla kalır: ikisine de dokunulmaz. Mod Off'ta tutarın yazıcısı dosyanın kopyasıdır
///   (§7.4); iş o lisanslara bakmaz.</item>
///   <item><b>(d) Devredilmiş oturumun boş EndedAt'i.</b> <c>ReleasedAt</c> dolu ∧ <c>EndedAt</c> NULL olan
///   oturumlarda <c>EndedAt = ReleasedAt</c>, <c>UpdatedAt</c> ilerler. S1 ve sonrası bu hâli hiç yazmaz (devir
///   yalnız EndedAt doluyken, EndedAt tekdüze); yalnız S1 öncesi imaja dönüşte kalır: eski kodun oturum
///   güncellemesi v0.9.10'un bayat gönderimiyle (EndedAt boş) devredilmiş oturumun EndedAt'ini NULL'lar ve istemci
///   satırı senkron saydığı için o oturumu bir daha göndermez. Yayın panelde sürekli canlı görünür, akış onu boş
///   EndedAt'le öbür bilgisayarlara taşırdı. Asıl kapanış anı kaybolmuştur; devir anı onu izleyen ilk sunucu
///   anıdır. Asıl değer sonradan gelirse <c>sessions/sync</c> onu ancak bu adımdan önce alır.</item>
/// </list>
///
/// <para><b>İdempotent.</b> Her adım yalnız eksik olanı yazar: ikinci koşu hiçbir satırı (ve <c>ChangeSeq</c>'i)
/// değiştirmez. (c) tutarı aynı çıkan dosyaya yazmaz; <see cref="ShipmentAmountDeriver"/> ise her türetmede
/// sürümü artırır, bu yüzden burada kullanılmaz — kural yine tek yerde (<see cref="LabelRules"/>).</para>
///
/// <para><b>Eşzamanlılık (c).</b> Dosyalar bağlı siparişlerden ÖNCE okunur ve sürüm jetonuyla yazılır
/// (<see cref="ShipmentAmountDeriver"/>'ın sıra gerekçesi): bağlı bir siparişi aynı anda değiştiren her yazıcı
/// dosyayı da türetip sürümünü artırır, iki yazımdan biri jetona çarpar. Çarpan parti atlanır, sayılır; sonraki
/// koşu yeniden dener.</para>
///
/// <para><b>Günlük.</b> Yalnız sayılar (§10.5): ad, kullanıcı adı ya da metin yazılmaz.</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class SalesReleaseRepairJob
{
    /// <summary>Hangfire işinin adı (recurring Id).</summary>
    public const string JobId = "sales-release-repair";

    /// <summary>04:50 UTC: yayın penceresi (TR 20:00–01:00) dışında; 04:45 kimlik anahtarı onarımından sonra,
    /// 04:47 ve 04:52 İYS işlerinin arasında, 5 dakikalık ızgaranın dışında.</summary>
    public const string Cron = "50 4 * * *";

    /// <summary>Bir devir işleminde en çok bu kadar oturum (bkz. sınıf dokümanı, adım a).</summary>
    public const int SessionBatchSize = 50;

    /// <summary>(c)'de bir partide okunan dosya sayısı.</summary>
    public const int ShipmentBatchSize = 500;

    private readonly LicenseDbContext _db;
    private readonly SessionReleaser _releaser;
    private readonly ILogger<SalesReleaseRepairJob> _log;

    public SalesReleaseRepairJob(LicenseDbContext db, SessionReleaser releaser, ILogger<SalesReleaseRepairJob> log)
    {
        _db = db;
        _releaser = releaser;
        _log = log;
    }

    /// <summary>(b)'nin parti boyu (spec §4.9: 5 bin). Testler küçültüp parti döngüsünü sınar.</summary>
    public int OrderBatchSize { get; set; } = 5_000;

    /// <summary>Bir koşunun sayıları.</summary>
    /// <param name="SessionsReleased">(a) devredilen miras oturum.</param>
    /// <param name="OrdersStamped">(b) damgalanan sipariş.</param>
    /// <param name="OrderBatches">(b) satır yazan parti sayısı.</param>
    /// <param name="ShipmentsRederived">(c) tutarı düzeltilen dosya.</param>
    /// <param name="ShipmentsConflicted">(c) eşzamanlı yazım yüzünden bu koşuda atlanan dosya.</param>
    /// <param name="ReleasedEndedAtFilled">(d) EndedAt'i devir anıyla doldurulan devredilmiş oturum.</param>
    public sealed record Report(
        int SessionsReleased, int OrdersStamped, int OrderBatches, int ShipmentsRederived, int ShipmentsConflicted,
        int ReleasedEndedAtFilled);

    public async Task<Report> RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var released = await ReleaseLegacySessionsAsync(now, ct);
        var (stamped, batches) = await StampMissedOrdersAsync(ct);
        var (rederived, conflicted, licenses) = await RederiveShipmentsAsync(now, ct);
        var endedAtFilled = await FillReleasedEndedAtAsync(now, ct);

        var report = new Report(released, stamped, batches, rederived, conflicted, endedAtFilled);
        if (released > 0 || stamped > 0 || rederived > 0 || conflicted > 0 || endedAtFilled > 0)
            _log.LogWarning(
                "sales-release-repair: {Sessions} miras oturum devredildi, {Orders} siparişe devir damgası basıldı "
                + "({Batches} parti), {Shipments} türetilmiş dosyanın tutarı düzeltildi ({Licenses} lisans, mod On), "
                + "{Conflicts} dosya eşzamanlı yazım yüzünden atlandı, {EndedAtFilled} devredilmiş oturumun boş "
                + "EndedAt'i devir anıyla dolduruldu (S1 öncesi imaja dönüş kalıntısı)",
                released, stamped, batches, rederived, licenses, conflicted, endedAtFilled);
        else
            _log.LogInformation("sales-release-repair: düzeltilecek satır yok");
        return report;
    }

    /// <summary>(a) Miras oturumlar; bkz. sınıf dokümanı.</summary>
    private async Task<int> ReleaseLegacySessionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Filtreli indeks (ReleasedAt IS NULL): devredilmemiş satırlar lisansların yayınlarının küçük bir kısmı.
        var candidates = await _db.StreamSessions.AsNoTracking()
            .Where(s => s.ReleasedAt == null && s.EndedAt != null && s.OwnerReplicaId == null)
            .OrderBy(s => s.LicenseId).ThenBy(s => s.Id)
            .Select(s => new { s.LicenseId, s.Id })
            .ToListAsync(ct);

        var released = 0;
        foreach (var license in candidates.GroupBy(c => c.LicenseId))
        foreach (var chunk in license.Select(c => c.Id).Chunk(SessionBatchSize))
            released += (await _releaser.ReleaseUnownedAsync(license.Key, chunk, now, ct)).Count;
        return released;
    }

    /// <summary>(b) Kaçmış damgalar; bkz. sınıf dokümanı. Her parti kendi deyimi (otomatik işlem): parti
    /// yazıldıkça kalıcıdır, kesilen koşu kaldığı yerden devam eder.</summary>
    private async Task<(int Stamped, int Batches)> StampMissedOrdersAsync(CancellationToken ct)
    {
        var batchSize = Math.Max(1, OrderBatchSize);
        var stamped = 0;
        var batches = 0;
        while (true)
        {
            // Oturumun lisansı da eşleşmeli: orders/sync başka lisansın oturumunu reddeder (unknown-session), bu
            // koşul yalnız bozuk bir satırın başka lisansın devir anını almasına karşı.
            var rows = await _db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE TOP ({batchSize}) o
                SET o.SessionReleasedAt = s.ReleasedAt
                FROM Orders AS o
                INNER JOIN StreamSessions AS s ON s.Id = o.SessionId AND s.LicenseId = o.LicenseId
                WHERE o.SessionReleasedAt IS NULL AND s.ReleasedAt IS NOT NULL
                """, ct);
            if (rows == 0) break;
            stamped += rows;
            batches++;
            if (rows < batchSize) break;
        }
        return (stamped, batches);
    }

    /// <summary>(d) Devredilmiş oturumun boş EndedAt'i; bkz. sınıf dokümanı. Tek koşullu deyim: koşul veritabanında,
    /// satır ancak hâlâ boşsa yazılır (eşzamanlı bir <c>sessions/sync</c> asıl değeri önce yazdıysa dokunulmaz). Aday
    /// yalnız geri dönüş kalıntısı olduğu için parti gerekmez.</summary>
    private Task<int> FillReleasedEndedAtAsync(DateTimeOffset now, CancellationToken ct) =>
        _db.StreamSessions
            .Where(s => s.ReleasedAt != null && s.EndedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.EndedAt, s => s.ReleasedAt)
                .SetProperty(s => s.UpdatedAt, now), ct);

    /// <summary>(c) Türetilmiş dosyalar, yalnız mod On; bkz. sınıf dokümanı.</summary>
    private async Task<(int Rederived, int Conflicted, int Licenses)> RederiveShipmentsAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var licenses = await _db.Licenses.AsNoTracking()
            .Where(l => l.SalesSyncMode == SalesSyncMode.On)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var rederived = 0;
        var conflicted = 0;
        var touchedLicenses = 0;
        foreach (var licenseId in licenses)
        {
            var ids = await _db.Shipments.AsNoTracking()
                .Where(s => s.LicenseId == licenseId && s.AmountDerivedAt != null && s.Status != ShipmentStatus.Shipped)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(ct);

            var before = rederived;
            foreach (var chunk in ids.Chunk(ShipmentBatchSize))
            {
                var (fixedCount, conflict) = await RederiveChunkAsync(licenseId, chunk, now, ct);
                rederived += fixedCount;
                conflicted += conflict;
            }
            if (rederived > before) touchedLicenses++;
        }
        return (rederived, conflicted, touchedLicenses);
    }

    private async Task<(int Fixed, int Conflicted)> RederiveChunkAsync(
        Guid licenseId, Guid[] shipmentIds, DateTimeOffset now, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();

        // Önce dosyalar (izlenen, sürüm jetonuyla), sonra bağlı siparişler — sıra gerekçesi sınıf dokümanında.
        // Durum ve türetme bayrağı yeniden okunur: liste okunduktan sonra dosya kargolanmış olabilir.
        var shipments = await _db.Shipments
            .Where(s => s.LicenseId == licenseId && shipmentIds.Contains(s.Id)
                && s.AmountDerivedAt != null && s.Status != ShipmentStatus.Shipped)
            .ToListAsync(ct);
        if (shipments.Count == 0) return (0, 0);

        var targetIds = shipments.Select(s => s.Id).ToList();
        var facts = (await _db.Orders.AsNoTracking()
                .Where(o => o.LicenseId == licenseId && o.ShipmentId != null && targetIds.Contains(o.ShipmentId.Value))
                .ToListAsync(ct))
            .Select(o => o.ToLabelFacts())
            .ToList();

        var changed = 0;
        foreach (var shipment in shipments)
        {
            var derived = LabelRules.DerivedShipmentAmount(shipment.Id.ToString("N"), facts);
            if (derived == shipment.CumulativeAmount) continue;
            shipment.CumulativeAmount = derived;
            shipment.UpdatedAt = now;
            shipment.Version++;
            changed++;
        }
        if (changed == 0) return (0, 0);

        try
        {
            await _db.SaveChangesAsync(ct);
            return (changed, 0);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Bir bağlı sipariş ya da dosya aynı anda yazıldı; o yazıcı dosyayı kendisi türetti. Parti atlanır,
            // sonraki koşu yeniden bakar.
            _db.ChangeTracker.Clear();
            return (0, changed);
        }
    }
}
