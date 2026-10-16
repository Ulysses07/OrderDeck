using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using static OrderDeck.LicenseServer.Services.Sync.PaymentRequests.PaymentRequestRules;

namespace OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

/// <summary>
/// Ödeme isteği kayıtlarının sonu, okunması ve geri doldurulması — <c>POST payment-requests/{id}/finish</c>,
/// <c>GET payment-requests</c> ve <c>POST payment-requests/backfill</c> (spec 2026-10-13 §4.7, §6.4, M17). Kirayı açan
/// <c>begin</c> <see cref="PaymentRequestLeaseService"/>'tedir.
///
/// <para><b>İki kipte çalışır, alt sürüme bağlı değildir.</b> Üçü de yalnız olgu yazar ya da okur: mod On'da başlamış
/// kiranın sonucu <c>disable</c>'dan sonra da yazılabilmeli (§4.7), Ws'nin ödeme geçmişi mod Off'ta gider (§8.2-6).
/// <c>finish</c>'i sürüme bağlamak kirayı asılı bırakırdı. <c>finish</c> ve <c>backfill</c> v2 isteğidir ve kopyanın kalp
/// atışıdır (<see cref="SalesReplicaService.AcceptAsync"/>); liste salt okunur bir GET'tir.</para>
///
/// <para><b>Günlük</b> yalnız Id ve sayı taşır (§9, §10.5): ad, makine adı ve kişi anahtarı yazılmaz.</para>
/// </summary>
public sealed class PaymentRequestRecordService
{
    public const string StatusApplied = "applied";
    public const string StatusUnchanged = "unchanged";
    public const string StatusContentConflict = CodeContentConflict;

    private const string FinishEndpoint = "payment-requests/finish";
    private const string BackfillEndpoint = "payment-requests/backfill";

    private readonly LicenseDbContext _db;
    private readonly SalesReplicaService _replicas;
    private readonly PaymentPersonResolver _people;
    private readonly ILogger<PaymentRequestRecordService> _log;

    public PaymentRequestRecordService(
        LicenseDbContext db,
        SalesReplicaService replicas,
        PaymentPersonResolver people,
        ILogger<PaymentRequestRecordService> log)
    {
        _db = db;
        _replicas = replicas;
        _people = people;
        _log = log;
    }

    // ── finish ─────────────────────────────────────────────────────────────

    /// <summary>Doğrulanmış <c>finish</c> isteği.</summary>
    private sealed record FinishInput(
        SalesEnvelope Envelope,
        PaymentRequestState Outcome,
        decimal NetAmount,
        decimal? AppliedAmount,
        bool FullyFromBalance)
    {
        public Guid ReplicaId => Envelope.ReplicaId;
    }

    /// <summary>
    /// <c>POST payment-requests/{id}/finish</c> (§6.4 "Akışın sonu", "Teslim kaydı olmadan dolan kira"). Lisans sahipliği
    /// çağıran uçta denetlenmiş olmalı.
    /// <list type="bullet">
    ///   <item>Kayıt bu lisansta değilse 404 <c>record-not-found</c>; kirasız kayıt (<c>Record</c>) 409
    ///   <c>content-conflict</c>.</item>
    ///   <item>Çağıran kaydı yazan kopya ya da onun <c>PreviousReplicaId</c> zincirindeki geri yüklenmiş ardılı olmalı
    ///   (<see cref="SalesReplicaService.LineageAsync"/>; bekleyen <c>finish</c>'i geri yüklemeden sonra yeni kimlik
    ///   gönderir, §8.7); değilse 409 <c>content-conflict</c>.</item>
    ///   <item><c>Sending</c> ve <c>Unconfirmed</c> her sonucu alır: geç <c>delivered</c>, süre dolumunun
    ///   (<c>begin</c>'in c adımı) Unconfirmed'ını yükseltir. <c>Delivered</c> ve <c>Abandoned</c> son hâldir.</item>
    ///   <item>Kayıt istenen sonuç ve tutarlardaysa 200 ve hiçbir şey yazılmaz (yanıtı kaybolan <c>finish</c>'in
    ///   yinelemesi); son hâlde başka sonuç ya da tutar 409 <c>content-conflict</c>.</item>
    ///   <item>Yazım: durum, tutarlar, <c>FinishedAt</c> = sunucu anı, <c>Version + 1</c>, jeton altında. Okumadan sonra
    ///   <c>begin</c>'in süre dolumu (ya da eşzamanlı ikinci <c>finish</c>) satırı yazdıysa jeton çakışır: kayıt yeniden
    ///   okunur ve karar bir kez daha verilir.</item>
    /// </list>
    /// </summary>
    public async Task<PaymentRequestReply> FinishAsync(
        Guid licenseId, Guid recordId, PaymentRequestFinishRequest request, string? userAgent, CancellationToken ct)
    {
        if (!TryValidateFinish(request, out var finish, out var invalid)) return invalid;

        var context = await _replicas.AcceptAsync(licenseId, finish.Envelope, userAgent, FinishEndpoint, ct);
        var lineage = await _replicas.LineageAsync(
            licenseId, finish.ReplicaId, context.ReplicaBefore?.PreviousReplicaId, ct);

        try
        {
            return await FinishOnceAsync(licenseId, recordId, finish, lineage, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Okuduktan sonra satır yazıldı (begin'in süre dolumu ya da eşzamanlı finish): güncel hâl üzerinden bir kez
            // daha. İkinci çakışma yukarı gider; istemci finish'i PendingPaymentRecord'dan yeniden dener.
            _db.ChangeTracker.Clear();
            _log.LogInformation(
                "payment-requests/finish: kayıt okunduktan sonra yazıldı, yeniden değerlendiriliyor (license={LicenseId}, "
                + "replica={ReplicaId}, record={RecordId})",
                licenseId, finish.ReplicaId, recordId);
            return await FinishOnceAsync(licenseId, recordId, finish, lineage, ct);
        }
    }

    private async Task<PaymentRequestReply> FinishOnceAsync(
        Guid licenseId, Guid recordId, FinishInput finish, IReadOnlySet<Guid> lineage, CancellationToken ct)
    {
        var record = await _db.PaymentRequestRecords
            .SingleOrDefaultAsync(r => r.Id == recordId && r.LicenseId == licenseId, ct);
        if (record is null)
        {
            _log.LogInformation(
                "payment-requests/finish: kayıt bu lisansta yok, 404 (license={LicenseId}, replica={ReplicaId}, "
                + "record={RecordId})",
                licenseId, finish.ReplicaId, recordId);
            return Fail(StatusCodes.Status404NotFound, CodeRecordNotFound, "Bu lisansta böyle bir ödeme isteği kaydı yok.");
        }

        if (record.Kind != PaymentRequestKind.Lease)
            return FinishConflict(licenseId, finish, record, "kirasız kayıt bitirilmez");
        if (!lineage.Contains(record.ReplicaId))
            return FinishConflict(licenseId, finish, record, "kaydı başka bir kopya açtı");

        if (record.State == finish.Outcome
            && record.NetAmount == finish.NetAmount
            && record.AppliedAmount == finish.AppliedAmount
            && record.FullyFromBalance == finish.FullyFromBalance)
        {
            _log.LogInformation(
                "payment-requests/finish: kayıt zaten bu sonuçta, yazım yok (license={LicenseId}, replica={ReplicaId}, "
                + "record={RecordId})",
                licenseId, finish.ReplicaId, recordId);
            return Finished(record);
        }

        if (record.State is PaymentRequestState.Delivered or PaymentRequestState.Abandoned)
            return FinishConflict(licenseId, finish, record, "kayıt son hâlde, sonuç ya da tutar farklı");

        var previous = record.State;
        record.State = finish.Outcome;
        record.NetAmount = finish.NetAmount;
        record.AppliedAmount = finish.AppliedAmount;
        record.FullyFromBalance = finish.FullyFromBalance;
        record.FinishedAt = DateTimeOffset.UtcNow;
        record.Version++;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "payment-requests/finish: kayıt {Previous} → {Outcome} (license={LicenseId}, replica={ReplicaId}, "
            + "record={RecordId})",
            previous, record.State, licenseId, finish.ReplicaId, recordId);
        return Finished(record);
    }

    private PaymentRequestFailed FinishConflict(
        Guid licenseId, FinishInput finish, PaymentRequestRecord record, string reason)
    {
        _log.LogInformation(
            "payment-requests/finish: 409 content-conflict — {Reason} (license={LicenseId}, replica={ReplicaId}, "
            + "record={RecordId}, state={State})",
            reason, licenseId, finish.ReplicaId, record.Id, record.State);
        return Fail(StatusCodes.Status409Conflict, CodeContentConflict,
            "Bu kayıt bu sonuçla bitirilemez: kirasız kayıt, başka bilgisayarın kirası ya da farklı bir sonuçla kapanmış.");
    }

    private static PaymentRequestSucceeded Finished(PaymentRequestRecord record) =>
        new(new PaymentRequestFinishResponse(record.Id, PaymentRequestWire.State(record.State), record.FinishedAt));

    private static bool TryValidateFinish(
        PaymentRequestFinishRequest request,
        [NotNullWhen(true)] out FinishInput? finish,
        [NotNullWhen(false)] out PaymentRequestFailed? failure)
    {
        finish = null;
        failure = null;

        if (!SalesEnvelope.TryRead(request, out var envelope, out var envelopeError) || !envelope.IsV2)
            return Invalid(CodeInvalidEnvelope,
                envelopeError ?? "finish v2 isteğidir (replicaId, protocol = 2, seenMode).", out failure);
        if (!PaymentRequestWire.TryParseOutcome(request.Outcome, out var outcome))
            return Invalid(CodeInvalidOutcome, "outcome 'delivered', 'unconfirmed' ya da 'abandoned' olmalı.", out failure);
        if (!IsMoney(request.NetAmount)
            || (request.AppliedAmount is not null && !IsMoney(request.AppliedAmount))
            || request.FullyFromBalance is null)
            return Invalid(CodeInvalidAmount,
                "netAmount (ve verilirse appliedAmount) 0 ya da daha büyük, en çok iki ondalıklı olmalı; "
                + "fullyFromBalance gerekli.", out failure);

        finish = new FinishInput(
            envelope, outcome, request.NetAmount!.Value, request.AppliedAmount, request.FullyFromBalance.Value);
        return true;
    }

    // ── liste ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET payment-requests?scopeKey&amp;memberIds</c> (§6.4 "Teslim kayıtlarının okunması"). Salt okunur; lisans
    /// sahipliği çağıran uçta denetlenmiş olmalı.
    /// <list type="bullet">
    ///   <item>Lisansın, aynı kapsamdaki (ordinal), üyeleri istenen üyelerle kesişen kayıtları. İstenen GUID'li üyeler
    ///   asıl kayda çözülür (kayıtların üyeleri yazıldıkları andaki asıl kayıttır, §6.4-b) ve kopyalarıyla genişletilir:
    ///   birleştirme kayıtları yeniden anahtarlamaz (<see cref="PaymentPersonResolver.WithCopiesAsync"/>). SQL harf
    ///   duyarsız getirir, üye ve kapsam eşitliği bellekte ordinal.</item>
    ///   <item>Vazgeçilmiş (<c>Abandoned</c>) kayıt girmez. Süresi dolmuş <c>Sending</c> kira yazılmadan
    ///   <c>unconfirmed</c> olarak, kapanış anı kiranın dolduğu an ile bildirilir (§6.4 "Teslim kaydı olmadan dolan
    ///   kira"; yazımı <c>begin</c>'in c adımı yapar). Canlı kiranın kapanış anı yoktur, en yeni sayılır.</item>
    ///   <item>Sıra kapanış anı (sunucu saati), eşitlikte başlangıç, sonra Id; en yeni
    ///   <see cref="PaymentRequestRules.MaxListRecords"/> kayıt, eskiden yeniye.</item>
    ///   <item>Çağıranın kendi kopyasının kayıtları süzülmez: istemci kendi yerel işleriyle temsil edilenleri kendisi
    ///   düşer (W3).</item>
    /// </list>
    /// </summary>
    public async Task<PaymentRequestReply> ListAsync(
        Guid licenseId, string? scopeKey, IReadOnlyList<string?> memberIds, CancellationToken ct)
    {
        if (!IsRecordScope(scopeKey))
            return Fail(StatusCodes.Status400BadRequest, CodeInvalidScope,
                "scopeKey 'cumulative', 'session:{id}', 'legacy' ya da 'legacy:{anahtar}' olmalı.");
        if (memberIds.Count is 0 or > MaxMembers || memberIds.Any(m => !IsMemberId(m)))
            return Fail(StatusCodes.Status400BadRequest, CodeInvalidMembers,
                $"memberIds 1–{MaxMembers} üye taşır; üye Id'si boş olamaz, en çok "
                + $"{PaymentRequestRecord.WpfCustomerIdMaxLength} karakter.");
        var scope = scopeKey!;

        // Kopyalar da: birleştirmeden önce yazılmış kayıt, kopyaya dönmüş Id'yle saklanır.
        var members = await _people.WithCopiesAsync(
            licenseId,
            await _people.CanonicalMembersAsync(licenseId, memberIds.Select(m => m!).Distinct(StringComparer.Ordinal), ct),
            ct);
        var memberSet = members.ToHashSet(StringComparer.Ordinal);
        var recordIds = (await _db.PaymentRequestMembers.AsNoTracking()
                .Where(m => m.LicenseId == licenseId && m.ScopeKey == scope && members.Contains(m.WpfCustomerId))
                .Select(m => new { m.RecordId, m.WpfCustomerId })
                .ToListAsync(ct))
            .Where(m => memberSet.Contains(m.WpfCustomerId))
            .Select(m => m.RecordId)
            .Distinct()
            .ToList();
        if (recordIds.Count == 0)
            return new PaymentRequestSucceeded(new PaymentRequestListResponse([]));

        var now = DateTimeOffset.UtcNow;
        var rows = (await _db.PaymentRequestRecords.AsNoTracking()
                .Where(r => r.LicenseId == licenseId && recordIds.Contains(r.Id)
                    && r.State != PaymentRequestState.Abandoned)
                .ToListAsync(ct))
            .Where(r => string.Equals(r.ScopeKey, scope, StringComparison.Ordinal))
            .Select(r => Effective(r, now))
            .OrderByDescending(x => x.FinishedAt is null)
            .ThenByDescending(x => x.FinishedAt)
            .ThenByDescending(x => x.Record.StartedAt)
            .ThenByDescending(x => x.Record.Id)
            .Take(MaxListRecords)
            .Reverse()
            .ToList();

        var selected = rows.Select(x => x.Record.Id).ToList();
        var orders = (await _db.PaymentRequestRecordOrders.AsNoTracking()
                .Where(o => selected.Contains(o.RecordId))
                .Select(o => new { o.RecordId, o.OrderId })
                .ToListAsync(ct))
            .ToLookup(o => o.RecordId, o => o.OrderId);

        var items = rows
            .Select(x => new PaymentRequestListItem(
                x.Record.Id,
                x.Record.ReplicaId,
                x.Record.MachineName,
                PaymentRequestWire.Kind(x.Record.Kind),
                PaymentRequestWire.State(x.State),
                x.Record.NetAmount,
                x.Record.AppliedAmount,
                x.Record.FullyFromBalance,
                x.Record.ProductTotal,
                x.Record.TotalBeforeBalance,
                x.Record.StartedAt,
                x.FinishedAt,
                orders[x.Record.Id].Order().ToList()))
            .ToList();
        return new PaymentRequestSucceeded(new PaymentRequestListResponse(items));
    }

    /// <summary>Kaydın okuyucuya görünen hâli: süresi dolmuş kira, satırı yazılmadan sonucu bilinmeyen.</summary>
    private sealed record EffectiveRecord(PaymentRequestRecord Record, PaymentRequestState State, DateTimeOffset? FinishedAt);

    /// <summary>
    /// Süresi dolmuş <c>Sending</c> kira (<c>LeaseUntil ≤ şimdi</c>; <c>begin</c>'in c adımıyla aynı kural)
    /// <c>Unconfirmed</c>, kapanış anı kiranın dolduğu an; başka her kayıt olduğu gibi.
    /// </summary>
    private static EffectiveRecord Effective(PaymentRequestRecord record, DateTimeOffset now) =>
        record.State == PaymentRequestState.Sending && (record.LeaseUntil is null || record.LeaseUntil <= now)
            ? new EffectiveRecord(record, PaymentRequestState.Unconfirmed, record.LeaseUntil ?? record.StartedAt)
            : new EffectiveRecord(record, record.State, record.FinishedAt);

    // ── backfill ───────────────────────────────────────────────────────────

    /// <summary>Doğrulanmış geri doldurma öğesi: üyeler ve siparişler tekil, ham yazımla.</summary>
    private sealed record BackfillInput(
        Guid RecordId,
        string OwnerId,
        IReadOnlyList<string> MemberIds,
        string ScopeKey,
        IReadOnlyList<Guid> OrderIds,
        PaymentRequestState State,
        decimal NetAmount,
        decimal? AppliedAmount,
        bool FullyFromBalance,
        DateTimeOffset At);

    /// <summary>
    /// <c>POST payment-requests/backfill</c> (§4.7, §6.4 "Ödeme geçmişi (mod Off'ta da)", M17): Ws'nin ilk açılışındaki
    /// ödeme geçmişi ve mod Off'ta her teslim ya da belirsiz gönderimden sonra kuyruğa alınan kayıt. Lisans sahipliği
    /// çağıran uçta denetlenmiş olmalı.
    /// <list type="bullet">
    ///   <item>Kirasız kayıt (<c>Kind = Record</c>) yazar. PersonKey ve üyeler <c>begin</c>'deki yoldan
    ///   (<see cref="PaymentPersonResolver"/>): öbür bilgisayarın <c>begin</c>'i kaydı PersonKey'den ya da üye
    ///   kesişiminden bulur ve <c>already-requested</c> sorusuna koyar. Üyeler = istemcinin asıl kayda çözülmüş kümesi ∪
    ///   sunucunun grubu.</item>
    ///   <item>Kilit, <c>members-stale</c>, toplam ve kargo denetimi yok: olmuş bir isteğin olgusudur, yeni bir istek
    ///   değil.</item>
    ///   <item>Anlar: <c>StartedAt</c> istemcinin <c>at</c>'i, şimdiye kırpılmış (bilgi ve gösterim); <c>FinishedAt</c>
    ///   sunucunun alış anı — sıralama sunucu saatiyle kalır, iki bilgisayarın saati karşılaştırılmaz.</item>
    ///   <item>Idempotent (<c>recordId</c>; özet <see cref="PaymentRequestHash.ComputeRecord"/>): aynı içerik
    ///   <c>unchanged</c> ve yeni üyeler eklenir (grup senkronu denemeler arasında üyeleri değiştirebilir); farklı içerik,
    ///   başka lisansın ya da kiranın Id'si öğe başına <c>content-conflict</c>, hiçbir şey yazılmaz. Aynı partide aynı
    ///   Id ikinci kez gelirse ilkiyle karşılaştırılır.</item>
    ///   <item>Parti tek <c>SaveChanges</c>'tir. Aynı Id'nin eşzamanlı eklemesi (birincil anahtar) partiyi bir kez
    ///   baştan değerlendirir: kazananın satırı artık vardır.</item>
    /// </list>
    /// Parti en çok <see cref="PaymentRequestRules.MaxBackfillBatch"/> kayıt taşır: Ws'nin geçmiş yüklemesi aynı NAT
    /// arkasındaki PC'lerin 100 istek/dk/IP bütçesinde kalır (risk 9).
    /// </summary>
    public async Task<PaymentRequestReply> BackfillAsync(
        Guid licenseId, PaymentRequestBackfillRequest request, string? userAgent, CancellationToken ct)
    {
        if (!TryValidateBackfill(request, out var envelope, out var items, out var invalid)) return invalid;

        await _replicas.AcceptAsync(licenseId, envelope, userAgent, BackfillEndpoint, ct);

        try
        {
            return await BackfillOnceAsync(licenseId, envelope.ReplicaId, items, ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Aynı recordId (ya da aynı kayda aynı üye) eşzamanlı yazıldı: parti bütünüyle geri alındı. Bir kez daha
            // değerlendirilir; kazananın satırı artık yeniden oynatma ya da içerik çakışmasıdır.
            _db.ChangeTracker.Clear();
            _log.LogInformation(
                "payment-requests/backfill: eşzamanlı aynı kayıt, parti yeniden değerlendiriliyor (license={LicenseId}, "
                + "replica={ReplicaId})",
                licenseId, envelope.ReplicaId);
            return await BackfillOnceAsync(licenseId, envelope.ReplicaId, items, ct);
        }
    }

    private async Task<PaymentRequestReply> BackfillOnceAsync(
        Guid licenseId, Guid replicaId, IReadOnlyList<BackfillInput> items, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var machine = await _db.WpfReplicas.AsNoTracking()
            .Where(r => r.LicenseId == licenseId && r.ReplicaId == replicaId)
            .Select(r => r.MachineName)
            .FirstOrDefaultAsync(ct);

        // Var olan Id'ler BÜTÜN lisanslardan okunur: birincil anahtar yalnız Id; başka lisansın Id'si çakışmadır.
        var ids = items.Select(i => i.RecordId).Distinct().ToList();
        var records = await _db.PaymentRequestRecords
            .Include(r => r.Members)
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);

        var results = new List<PaymentRequestBackfillResult>(items.Count);
        var applied = 0;
        var unchanged = 0;
        var membersAdded = 0;
        var conflicts = new List<Guid>();
        foreach (var item in items)
        {
            var person = await _people.ResolveAsync(licenseId, item.OwnerId, item.MemberIds, ct);
            var group = await _people.ServerGroupAsync(licenseId, person.GroupId, ct);
            var members = PaymentPersonResolver.StorableMembers(person.Members.Union(group, StringComparer.Ordinal));
            var hash = PaymentRequestHash.ComputeRecord(
                person.OwnerWpfCustomerId, item.ScopeKey, item.State, item.NetAmount, item.AppliedAmount,
                item.FullyFromBalance, item.OrderIds);

            if (records.TryGetValue(item.RecordId, out var existing))
            {
                if (existing.LicenseId != licenseId || existing.Kind != PaymentRequestKind.Record
                    || !existing.RequestHash.AsSpan().SequenceEqual(hash))
                {
                    conflicts.Add(item.RecordId);
                    results.Add(new PaymentRequestBackfillResult(item.RecordId, StatusContentConflict));
                    continue;
                }

                membersAdded += UnionMembers(existing, members);
                unchanged++;
                results.Add(new PaymentRequestBackfillResult(item.RecordId, StatusUnchanged));
                continue;
            }

            var record = new PaymentRequestRecord
            {
                Id = item.RecordId,
                LicenseId = licenseId,
                PersonKey = person.PersonKey,
                PersonKeyHash = person.PersonKeyHash,
                ScopeKey = item.ScopeKey,
                OwnerWpfCustomerId = person.OwnerWpfCustomerId,
                ReplicaId = replicaId,
                MachineName = machine,
                Kind = PaymentRequestKind.Record,
                State = item.State,
                NetAmount = item.NetAmount,
                AppliedAmount = item.AppliedAmount,
                FullyFromBalance = item.FullyFromBalance,
                RequestHash = hash,
                StartedAt = item.At < now ? item.At : now,
                FinishedAt = now,
                Members = members
                    .Select(m => new PaymentRequestMember
                    {
                        LicenseId = licenseId, WpfCustomerId = m, ScopeKey = item.ScopeKey,
                    })
                    .ToList(),
                RecordOrders = item.OrderIds
                    .Select(o => new PaymentRequestRecordOrder { LicenseId = licenseId, OrderId = o })
                    .ToList(),
            };
            _db.PaymentRequestRecords.Add(record);
            records[record.Id] = record;
            applied++;
            results.Add(new PaymentRequestBackfillResult(item.RecordId, StatusApplied));
        }

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "payment-requests/backfill: {Applied} kayıt yazıldı, {Unchanged} kayıt zaten vardı ({MembersAdded} üye "
            + "eklendi), {Conflicts} içerik çakışması (license={LicenseId}, replica={ReplicaId})",
            applied, unchanged, membersAdded, conflicts.Count, licenseId, replicaId);
        if (conflicts.Count > 0)
            _log.LogWarning(
                "payment-requests/backfill: aynı Id'de farklı içerik, başka lisansın ya da kiranın kaydı — {RecordIds} "
                + "(license={LicenseId}, replica={ReplicaId})",
                string.Join(",", conflicts.Distinct()), licenseId, replicaId);
        return new PaymentRequestSucceeded(new PaymentRequestBackfillResponse(results));
    }

    /// <summary>Yeniden oynatmanın yeni üyelerini kayda ekler; birincil anahtarın (varsayılan harman) eşit saydığı üye
    /// yeniden eklenmez (<see cref="PaymentPersonResolver.StorageKey"/>).</summary>
    /// <returns>Eklenen üye sayısı.</returns>
    private static int UnionMembers(PaymentRequestRecord record, IReadOnlyList<string> members)
    {
        var known = record.Members
            .Select(m => PaymentPersonResolver.StorageKey(m.WpfCustomerId))
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var member in members)
        {
            if (!known.Add(PaymentPersonResolver.StorageKey(member))) continue;
            record.Members.Add(new PaymentRequestMember
            {
                LicenseId = record.LicenseId, WpfCustomerId = member, ScopeKey = record.ScopeKey,
            });
            added++;
        }
        return added;
    }

    private static bool TryValidateBackfill(
        PaymentRequestBackfillRequest request,
        [NotNullWhen(true)] out SalesEnvelope? envelope,
        [NotNullWhen(true)] out IReadOnlyList<BackfillInput>? items,
        [NotNullWhen(false)] out PaymentRequestFailed? failure)
    {
        envelope = null;
        items = null;
        failure = null;

        if (!SalesEnvelope.TryRead(request, out var read, out var envelopeError) || !read.IsV2)
            return Invalid(CodeInvalidEnvelope,
                envelopeError ?? "backfill v2 isteğidir (replicaId, protocol = 2, seenMode).", out failure);
        if (request.Records is not { Count: > 0 } records || records.Count > MaxBackfillBatch)
            return Invalid(CodeInvalidBatch, $"records 1–{MaxBackfillBatch} kayıt taşır.", out failure);

        var inputs = new List<BackfillInput>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            if (!TryValidateItem(records[i], i, out var input, out failure)) return false;
            inputs.Add(input);
        }

        envelope = read;
        items = inputs;
        return true;
    }

    private static bool TryValidateItem(
        PaymentRequestBackfillItem? item,
        int index,
        [NotNullWhen(true)] out BackfillInput? input,
        [NotNullWhen(false)] out PaymentRequestFailed? failure)
    {
        input = null;
        failure = null;
        var at = $"records[{index}]";

        if (item?.RecordId is not { } recordId || recordId == Guid.Empty)
            return Invalid(CodeInvalidRecord, $"{at}: recordId boş olamaz.", out failure);

        if (item.MemberIds is not { Count: > 0 } rawMembers || rawMembers.Count > MaxMembers)
            return Invalid(CodeInvalidMembers, $"{at}: memberIds 1–{MaxMembers} üye taşır.", out failure);
        if (rawMembers.Any(m => !IsMemberId(m)))
            return Invalid(CodeInvalidMembers,
                $"{at}: üye Id'si boş olamaz, en çok {PaymentRequestRecord.WpfCustomerIdMaxLength} karakter.", out failure);
        var members = rawMembers.Select(m => m!).Distinct(StringComparer.Ordinal).ToList();
        if (item.OwnerId is not { } ownerId || !members.Contains(ownerId, StringComparer.Ordinal))
            return Invalid(CodeInvalidMembers, $"{at}: ownerId memberIds'te olmalı.", out failure);

        if (!IsRecordScope(item.ScopeKey))
            return Invalid(CodeInvalidScope,
                $"{at}: scopeKey 'cumulative', 'session:{{id}}', 'legacy' ya da 'legacy:{{anahtar}}' olmalı.", out failure);

        if (item.OrderIds is not { } rawOrders || rawOrders.Count > MaxOrders || rawOrders.Contains(Guid.Empty))
            return Invalid(CodeInvalidOrders, $"{at}: orderIds en çok {MaxOrders} dolu Id taşır.", out failure);

        if (!PaymentRequestWire.TryParseOutcome(item.State, out var state) || state == PaymentRequestState.Abandoned)
            return Invalid(CodeInvalidState, $"{at}: state 'delivered' ya da 'unconfirmed' olmalı.", out failure);

        if (!IsMoney(item.NetAmount)
            || (item.AppliedAmount is not null && !IsMoney(item.AppliedAmount))
            || item.FullyFromBalance is null)
            return Invalid(CodeInvalidAmount,
                $"{at}: netAmount (ve verilirse appliedAmount) 0 ya da daha büyük, en çok iki ondalıklı olmalı; "
                + "fullyFromBalance gerekli.", out failure);

        if (item.At is not { } localAt)
            return Invalid(CodeInvalidAt, $"{at}: at gerekli.", out failure);

        input = new BackfillInput(
            recordId,
            ownerId,
            members,
            item.ScopeKey!,
            rawOrders.Distinct().ToList(),
            state,
            item.NetAmount!.Value,
            item.AppliedAmount,
            item.FullyFromBalance.Value,
            localAt);
        return true;
    }

    // ── Ortak ──────────────────────────────────────────────────────────────

    /// <summary>Birincil anahtar ya da tekil indeks ihlali (2601/2627).</summary>
    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    private static bool Invalid(string code, string detail, out PaymentRequestFailed failure)
    {
        failure = Fail(StatusCodes.Status400BadRequest, code, detail);
        return false;
    }

    private static PaymentRequestFailed Fail(int statusCode, string code, string detail) =>
        new(statusCode, code, detail, new Dictionary<string, object?>());
}
