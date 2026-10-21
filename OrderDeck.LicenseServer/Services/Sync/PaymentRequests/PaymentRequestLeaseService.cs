using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.Shared.Sales;
using static OrderDeck.LicenseServer.Services.Sync.PaymentRequests.PaymentRequestRules;

namespace OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

/// <summary>
/// PersonKey kirası — <c>POST payment-requests/begin</c> (spec 2026-10-13 §4.7, §6.4). Yerel <c>PaymentPersonGate</c>'in
/// sunucu ikizi: kişi×kapsamda bilgisayarlar arası tek eşzamanlı ödeme isteği (DM6) ve önceki isteğin sorusu. Sıra:
/// <list type="number">
///   <item><b>İstek.</b> v2 zarfı ve alanlar (400). Lisans sahipliğini uç denetler (404).</item>
///   <item><b>Kalp atışı ve kapılar.</b> İstek kopyanın kalp atışıdır (<see cref="SalesReplicaService.AcceptAsync"/>). Mod
///   Off ise 409 <c>mode-off</c> ve hiçbir şey yazılmaz: kira denetimi yapmadan başarı dönmez (§4.7; Off turunu koşmamış
///   bilgisayarın önbelleğindeki uzak etiketler R1 penceresini açardı). Bugünkü (v0.9.10) davranış değişmez; uç yenidir.
///   Alt sürüm tanımsız ya da istemci eskiyse 409 <c>client-update-required</c> (kapalı-hata, §2 "Ws", §6.5-2).</item>
///   <item><b>Hızlı yeniden oynatma</b> (a), kilitsiz.</item>
///   <item><b>Tek işlem ve uygulama kilitleri.</b> Önce <c>payreq:{lisans}:{PersonKeyHash}:{kapsam}</c>, sonra asıl kayda
///   çözülmüş her üye için <c>payreq-m:{lisans}:{üye}:{kapsam}</c>, tekil ve ordinal sıralı (sıra ölü kilitlenmeyi önler):
///   grup senkronu gecikmesi yüzünden farklı PersonKey hesaplayan iki bilgisayar ortak üyeleri varsa aynı anda geçemez
///   (M10). Kapsam kilit adında küçük harfe katlanır: <c>sp_getapplock</c> adları ikili karşılaştırır, ScopeKey'in SQL
///   eşitliği harf duyarsızdır; kilit en az onun kadar kaba olmalı. Kilit zaman aşımı 409
///   <c>in-progress-elsewhere</c> (makine bilinmiyor).</item>
///   <item>Kilit altında: (a) yeniden, (b) üyeler (409 <c>members-stale</c>), (c) süresi dolmuş ve bu kopyanın önceki
///   <c>Sending</c> kaydının <c>Unconfirmed</c> yapılması, (d) başka kopyanın canlı kirası (409
///   <c>in-progress-elsewhere</c>), (e) onaylanmamış önceki istek (409 <c>already-requested {priors, priorsDigest}</c>;
///   onay Id listesiyle ya da öncül kümesinin özetiyle, <see cref="PaymentRequestPriorsDigest"/>), (f) sunucu toplamı
///   (409 <c>stale-total</c>), (g) kargo ayarı (409 <c>settings-stale</c>), (h) kiranın yazılması. (c)'nin yazımı (d)–(g)
///   retlerinde de kalır; (f) ve (g) hiçbir şey yazmaz.</item>
/// </list>
///
/// <para><b>Eşleme.</b> Kişi×kapsamın kayıtları iki yoldan bulunur: PersonKey özeti (indeks; metin bellekte ordinal yeniden
/// denetlenir) ya da aynı kapsamdaki üye kesişimi (<c>PaymentRequestMembers</c>; SQL harf duyarsız getirir, üye eşitliği
/// bellekte ordinal). SQL'in harf ve sondaki boşluk katlaması iki ayrı kişiyi birbirine bağlamaz. Üye kesişimi GUID'li
/// üyelerin kopyalarını da arar: birleştirme kayıtları yeniden anahtarlamaz, birleştirmeden önce yazılmış kayıt kopyaya
/// dönmüş Id'yle saklanır (<see cref="PaymentPersonResolver.WithCopiesAsync"/>).</para>
///
/// <para><b>Yarışın hakemi</b> filtreli tekil indekstir (DM6). Tekil ihlal 500 değil 409 <c>in-progress-elsewhere</c>
/// olur; işlem tümüyle geri alınır, yarım üye ya da sipariş satırı kalmaz. Aynı <c>recordId</c>'nin eşzamanlı ikinci
/// eklemesi (birincil anahtar) yeniden okunur: yeniden oynatma ya da <c>content-conflict</c>.</para>
///
/// <para><b>Günlük</b> yalnız Id ve sayı taşır (§9, §10.5): PersonKey metni yazılmaz (<c>r:</c> anahtarı ham yerel Id'dir),
/// ad ve makine adı yazılmaz.</para>
/// </summary>
public sealed class PaymentRequestLeaseService
{
    /// <summary>Kira süresi (§6.4-h): <c>LeaseUntil = StartedAt + 120 sn</c>.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(120);

    /// <summary>Id listesiyle onaylanmış önceki kayıtların en çok sayısı. Daha büyük küme öncül kümesi özetiyle onaylanır
    /// (<c>confirmedPriorsDigest</c>, §6.4-e).</summary>
    public const int MaxConfirmedPriors = 500;

    /// <summary>Kayıt tablosunun birincil anahtarının adı: eşzamanlı aynı <c>recordId</c> bununla tanınır.</summary>
    private const string RecordKeyName = "PK_PaymentRequestRecords";

    private const string Endpoint = "payment-requests/begin";

    private readonly LicenseDbContext _db;
    private readonly SalesReplicaService _replicas;
    private readonly CustomerIdResolver _customerIds;
    private readonly PaymentPersonResolver _people;
    private readonly SalesSyncOptions _options;
    private readonly ILogger<PaymentRequestLeaseService> _log;

    public PaymentRequestLeaseService(
        LicenseDbContext db,
        SalesReplicaService replicas,
        CustomerIdResolver customerIds,
        PaymentPersonResolver people,
        IOptions<SalesSyncOptions> options,
        ILogger<PaymentRequestLeaseService> log)
    {
        _db = db;
        _replicas = replicas;
        _customerIds = customerIds;
        _people = people;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Doğrulanmış <c>begin</c> isteği: üyeler ve siparişler ordinal tekil, ham yazımla.</summary>
    private sealed record BeginInput(
        SalesEnvelope Envelope,
        Guid RecordId,
        string OwnerId,
        IReadOnlyList<string> MemberIds,
        string ScopeKey,
        IReadOnlyList<Guid> OrderIds,
        decimal ProductTotal,
        decimal TotalBeforeBalance,
        bool RecipientPays,
        int ShippingSettingsVersion,
        IReadOnlySet<Guid> ConfirmedPriorIds,
        byte[]? ConfirmedPriorsDigest,
        byte[] RequestHash)
    {
        public Guid ReplicaId => Envelope.ReplicaId;
    }

    /// <summary>Kişi×kapsamın bir kaydı, kararın okuduğu alanlarla.</summary>
    private sealed record Related(
        Guid Id,
        Guid ReplicaId,
        string? MachineName,
        PaymentRequestKind Kind,
        PaymentRequestState State,
        decimal? NetAmount,
        decimal? TotalBeforeBalance,
        DateTimeOffset StartedAt,
        DateTimeOffset? LeaseUntil,
        DateTimeOffset? FinishedAt);

    /// <summary><c>begin</c>'i işler. Lisans sahipliği çağıran uçta denetlenmiş olmalı.</summary>
    /// <param name="userAgent">İsteğin <c>User-Agent</c>'ı: istemci sürümü ve kalp atışı.</param>
    public async Task<PaymentRequestReply> BeginAsync(
        Guid licenseId, PaymentRequestBeginRequest request, string? userAgent, CancellationToken ct)
    {
        if (!TryValidate(request, out var begin, out var invalid)) return invalid;

        var context = await _replicas.AcceptAsync(licenseId, begin.Envelope, userAgent, Endpoint, ct);
        if (context.Mode != SalesSyncMode.On)
        {
            _log.LogInformation(
                "payment-requests/begin: mod Off, 409 mode-off (license={LicenseId}, replica={ReplicaId}, "
                + "record={RecordId})",
                licenseId, begin.ReplicaId, begin.RecordId);
            return Fail(StatusCodes.Status409Conflict, CodeModeOff,
                "Satış senkronu bu lisansta kapalı; ödeme isteği yerel kuralla yapılır.");
        }
        if (!ClientVersion.IsAtLeast(ClientVersion.FromUserAgent(userAgent), _options.ParseMinClientVersion()))
        {
            _log.LogInformation(
                "payment-requests/begin: istemci alt sürümün altında ya da alt sürüm tanımsız, 409 "
                + "client-update-required (license={LicenseId}, replica={ReplicaId}, record={RecordId})",
                licenseId, begin.ReplicaId, begin.RecordId);
            return Fail(StatusCodes.Status409Conflict, CodeClientUpdateRequired,
                "Ödeme isteği için uygulamanın güncel sürümü gerekiyor.");
        }

        // (a) kilitsiz: yanıtı kaybolmuş isteğin yeniden gönderimi kilit beklemeden döner.
        if (await ReplayAsync(licenseId, begin, ct) is { } replay) return replay;

        // Kişi (§6.4-b): kilit adı anahtara bağlı olduğu için kilitten önce kurulur.
        var person = await _people.ResolveAsync(licenseId, begin.OwnerId, begin.MemberIds, ct);
        return await BeginLockedAsync(licenseId, begin, person, ct);
    }

    /// <summary>Kilitler ve kilit altındaki adımlar (a–h) tek işlemde; ilişkisel olmayan sağlayıcıda (testler) işlem ve
    /// kilit yoktur.</summary>
    private async Task<PaymentRequestReply> BeginLockedAsync(
        Guid licenseId, BeginInput begin, PaymentPerson person, CancellationToken ct)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            await SalesCommandLock.AcquireAsync(
                _db, PersonResource(licenseId, person.PersonKeyHash, begin.ScopeKey), SalesCommandLock.DefaultTimeout,
                ct);
            foreach (var member in person.Members)
                await SalesCommandLock.AcquireAsync(
                    _db, MemberResource(licenseId, member, begin.ScopeKey), SalesCommandLock.DefaultTimeout, ct);

            var reply = await DecideAsync(licenseId, begin, person, ct);
            // Retler de commit edilir: (c)'nin Unconfirmed yazımı (d)–(g) retlerinde kalmalı (§4.9 "kira süresi için ayrı
            // iş yok"). (a), (b), (f) ve (g) hiçbir şey yazmaz.
            if (transaction is not null) await transaction.CommitAsync(ct);
            return reply;
        }
        catch (SalesCommandRejection)
        {
            // Kilit zaman aşımı ya da kilitlenme kurbanı: aynı kişi×kapsamda başka bir istek sürüyor.
            await RollbackAsync(transaction);
            _log.LogInformation(
                "payment-requests/begin: kilit alınamadı, 409 in-progress-elsewhere (license={LicenseId}, "
                + "replica={ReplicaId}, record={RecordId})",
                licenseId, begin.ReplicaId, begin.RecordId);
            return InProgressElsewhere(null);
        }
        catch (DbUpdateException ex)
        {
            await RollbackAsync(transaction);

            if (IsSendingIndexViolation(ex))
            {
                // DM6'nın hakemi: eşzamanlı başka bir kira bizden önce yazıldı. Kazanan yeniden okunur.
                var winner = await SendingWinnerAsync(licenseId, person, begin.ScopeKey, ct);
                _log.LogInformation(
                    "payment-requests/begin: kişi×kapsamda tekil kira ihlali, 409 in-progress-elsewhere "
                    + "(license={LicenseId}, replica={ReplicaId}, record={RecordId}, winner={WinnerId})",
                    licenseId, begin.ReplicaId, begin.RecordId, winner?.Id);
                return InProgressElsewhere(winner);
            }

            // Aynı recordId eşzamanlı eklendi: kayıtlı içerikle karşılaştırılır.
            if (IsRecordKeyViolation(ex) && await ReplayAsync(licenseId, begin, ct) is { } replay) return replay;
            throw;
        }
    }

    /// <summary>Kilit altındaki karar: (a), (b), (c), (d), (e), (f), (g), (h).</summary>
    private async Task<PaymentRequestReply> DecideAsync(
        Guid licenseId, BeginInput begin, PaymentPerson person, CancellationToken ct)
    {
        // (a) kilit altında yeniden: eşzamanlı aynı istek bizden önce yazdıysa.
        if (await ReplayAsync(licenseId, begin, ct) is { } replay) return replay;

        // (b) Sunucu grubunun CANLI üyeleri istemcinin üye kümesinde olmalı: aksi hâlde istemci kişiyi eksik kurmuştur
        // (grup senkronu gecikmesi) ve başka üyeye yazılmış istekleri göremezdi. Silinmiş üye sayılmaz (ortak bakiye
        // §15-7, §4.0-3'ün kuralı): sunucuda numarası boşalan grubun yeni numarasını izler (PersonGroupSaveHook), WPF'te
        // izlemez — hiçbir müşteri akışı turu onu istemcinin kümesine getirmez, istek kalıcı olarak engellenirdi.
        var serverGroup = await _people.ServerGroupMembersAsync(licenseId, person.GroupId, ct);
        var group = serverGroup.Select(m => m.Id).ToList();
        var clientMembers = person.Members.ToHashSet(StringComparer.Ordinal);
        var missing = serverGroup.Where(m => !m.Purged && !clientMembers.Contains(m.Id)).ToList();
        if (missing.Count > 0)
        {
            _log.LogInformation(
                "payment-requests/begin: istemcinin üye kümesi sunucu grubunu kapsamıyor, 409 members-stale — {Missing} "
                + "eksik üye (license={LicenseId}, replica={ReplicaId}, record={RecordId})",
                missing.Count, licenseId, begin.ReplicaId, begin.RecordId);
            return Fail(StatusCodes.Status409Conflict, CodeMembersStale,
                "Müşteri grupları eşitleniyor; kişi yeniden kurulup tekrar denenmeli.",
                new Dictionary<string, object?> { ["members"] = group });
        }

        // Arama (c–e) ve kaydın üyeleri (h) sunucunun silinmiş üyelerini de taşır: silinmiş sahipli ya da silinmiş üyeye
        // yazılmış kayıtlar bulunur. Toplam (f) ise istemcinin üyeleriyle (aşağıda).
        var members = person.Members.Union(group, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var lookupMembers = await _people.WithCopiesAsync(licenseId, members, ct);
        var now = DateTimeOffset.UtcNow;

        // (c) Süresi dolmuş kiralar ve bu kopyanın aynı kişi×kapsamdaki önceki kirası (süreç çöküp yeniden açılmış; mesaj
        // gitmiş olabilir) Unconfirmed olur.
        var related = await RelatedAsync(licenseId, person, begin.ScopeKey, lookupMembers, ct);
        var stale = related
            .Where(r => r.State == PaymentRequestState.Sending
                && (r.LeaseUntil is null || r.LeaseUntil <= now || r.ReplicaId == begin.ReplicaId))
            .Select(r => r.Id)
            .ToList();
        var expired = 0;
        if (stale.Count > 0)
        {
            expired = await ExpireAsync(licenseId, stale, begin.ReplicaId, now, ct);
            related = await RelatedAsync(licenseId, person, begin.ScopeKey, lookupMembers, ct);
        }

        // (d) Başka kopyanın canlı kirası.
        var elsewhere = related
            .Where(r => r.State == PaymentRequestState.Sending && r.ReplicaId != begin.ReplicaId)
            .OrderBy(r => r.StartedAt)
            .ThenBy(r => r.Id)
            .FirstOrDefault();
        if (elsewhere is not null)
        {
            _log.LogInformation(
                "payment-requests/begin: başka kopyanın canlı kirası, 409 in-progress-elsewhere — {Expired} kira "
                + "Unconfirmed yapıldı (license={LicenseId}, replica={ReplicaId}, record={RecordId}, other={OtherId})",
                expired, licenseId, begin.ReplicaId, begin.RecordId, elsewhere.Id);
            return InProgressElsewhere(elsewhere);
        }

        // (e) Onaylanmamış önceki istek: teslim edilmiş ya da sonucu bilinmeyen, iki türden de. Sıra liste ucununki:
        // kapanış anı (sunucu saati), eşitlikte istek anı, sonra Id. Kirasız kaydın istek anı istemcinin saatidir; sıranın
        // ilk anahtarı olsaydı iki bilgisayarın saati karşılaştırılırdı.
        //
        // İki onay "ya da" ile birleşir: Id listesinin dışında öncül kalmadıysa geçer; kaldıysa onay özeti BÜTÜN öncül
        // kümesinin (Id listesiyle onaylananlar dahil) bugünkü özetine eşit olmalıdır. Özet Id listesine bağlı olmadığı
        // için onay turunda listeyi değiştiren istemci (500'de kesilmiş birikimli liste) tutan özeti bozamaz; soru döngüye
        // girmez. Küme sorudan beri değiştiyse (yeni kapanış, geç commit, birleştirme, grup değişimi) özet tutmaz ve
        // istek yeniden sorulur.
        var open = related
            .Where(r => r.State is PaymentRequestState.Delivered or PaymentRequestState.Unconfirmed)
            .ToList();
        var priors = open
            .Where(r => !begin.ConfirmedPriorIds.Contains(r.Id))
            .Select(r => new PaymentRequestPrior(
                r.Id,
                r.MachineName,
                r.NetAmount,
                r.TotalBeforeBalance,
                r.StartedAt,
                r.FinishedAt,
                r.State == PaymentRequestState.Unconfirmed))
            .OrderBy(p => p.FinishedAt is null)
            .ThenBy(p => p.FinishedAt)
            .ThenBy(p => p.At)
            .ThenBy(p => p.Id)
            .ToList();
        if (priors.Count > 0)
        {
            var digest = PaymentRequestPriorsDigest.Hash(open.Select(r => r.Id));
            if (begin.ConfirmedPriorsDigest is not { } confirmed || !confirmed.AsSpan().SequenceEqual(digest))
            {
                _log.LogInformation(
                    "payment-requests/begin: onaylanmamış önceki istek, 409 already-requested — {Priors} kayıt, onay özeti "
                    + "{DigestOutcome}, {Expired} kira Unconfirmed yapıldı (license={LicenseId}, replica={ReplicaId}, "
                    + "record={RecordId})",
                    priors.Count, begin.ConfirmedPriorsDigest is null ? "gelmedi" : "tutmadı", expired, licenseId,
                    begin.ReplicaId, begin.RecordId);
                return Fail(StatusCodes.Status409Conflict, CodeAlreadyRequested,
                    "Bu kişiden bu kapsamda daha önce ödeme istendi; tekrar gönderilsin mi?",
                    new Dictionary<string, object?>
                    {
                        ["priors"] = priors,
                        ["priorsDigest"] = PaymentRequestPriorsDigest.Format(digest),
                    });
            }

            _log.LogInformation(
                "payment-requests/begin: öncül kümesi özetle onaylandı — {Priors} kayıt Id listesinin dışında "
                + "(license={LicenseId}, replica={ReplicaId}, record={RecordId})",
                priors.Count, licenseId, begin.ReplicaId, begin.RecordId);
        }

        // (f) Sunucu toplamı: istemcinin sipariş kümesi ve ürün toplamı sunucununkiyle aynı olmalı (M11). Üyeler istemcinin
        // üyeleri: (b)'den sonra sunucunun canlı üyeleri zaten onlarda. İstemcide olmayan silinmiş üyenin (§15-7)
        // siparişi sayılsaydı istemci onu hiç saymadığı için stale-total kalıcı olurdu.
        if (await CheckServerTotalAsync(licenseId, begin, person.Members, expired, ct) is { } staleTotal)
            return staleTotal;

        // (g) Kargo ayarı: tutar lisansın güncel ayarıyla hesaplanmış olmalı (§6.2).
        if (await CheckShippingAsync(licenseId, begin, expired, ct) is { } settingsStale) return settingsStale;

        // (h) Kira: kayıt, üyeleri ve kapsadığı siparişler tek SaveChanges'te.
        var stored = PaymentPersonResolver.StorableMembers(members);
        var machine = await _db.WpfReplicas.AsNoTracking()
            .Where(r => r.LicenseId == licenseId && r.ReplicaId == begin.ReplicaId)
            .Select(r => r.MachineName)
            .FirstOrDefaultAsync(ct);
        var record = new PaymentRequestRecord
        {
            Id = begin.RecordId,
            LicenseId = licenseId,
            PersonKey = person.PersonKey,
            PersonKeyHash = person.PersonKeyHash,
            ScopeKey = begin.ScopeKey,
            OwnerWpfCustomerId = person.OwnerWpfCustomerId,
            ReplicaId = begin.ReplicaId,
            MachineName = machine,
            Kind = PaymentRequestKind.Lease,
            State = PaymentRequestState.Sending,
            ProductTotal = begin.ProductTotal,
            TotalBeforeBalance = begin.TotalBeforeBalance,
            ShippingSettingsVersion = begin.ShippingSettingsVersion,
            RequestHash = begin.RequestHash,
            StartedAt = now,
            LeaseUntil = now + LeaseDuration,
            Members = stored
                .Select(m => new PaymentRequestMember { LicenseId = licenseId, WpfCustomerId = m, ScopeKey = begin.ScopeKey })
                .ToList(),
            RecordOrders = begin.OrderIds
                .Select(o => new PaymentRequestRecordOrder { LicenseId = licenseId, OrderId = o })
                .ToList(),
        };
        _db.PaymentRequestRecords.Add(record);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "payment-requests/begin: kira açıldı — {Members} üye, {Orders} sipariş, {Expired} kira Unconfirmed yapıldı "
            + "(license={LicenseId}, replica={ReplicaId}, record={RecordId})",
            stored.Count, begin.OrderIds.Count, expired, licenseId, begin.ReplicaId, begin.RecordId);
        return new PaymentRequestSucceeded(new PaymentRequestBeginResponse(
            record.Id, PaymentRequestWire.State(record.State), record.LeaseUntil, stored));
    }

    // ── (a) yeniden oynatma ────────────────────────────────────────────────

    /// <summary>
    /// (a) Aynı <c>recordId</c> varsa: aynı lisansın kirası ve aynı özet → kaydın GÜNCEL hâli (durum, kira sonu, üyeler),
    /// hiçbir şey yazılmaz. Özet farklı, başka lisansın Id'si ya da kirasız kayıt (<c>Record</c>) → 409
    /// <c>content-conflict</c> (istemci hatası). Kayıt yoksa null.
    ///
    /// <para>Yalnız <c>sending</c> durumu kiradır (W3 sözleşmesi). Kayıt artık canlı değilse 200 başka bir durumla döner.
    /// Süresi dolmuş <c>Sending</c> kira, liste ucundaki gibi sunucu saatiyle <c>unconfirmed</c> bildirilir
    /// (<see cref="PaymentRequestRules.IsExpiredLease"/>). Yoksa yanıtı kaybolan begin'i kira dolduktan sonra yineleyen
    /// kopya <c>sending</c> görür ve kiranın geçerli olup olmadığını ancak kendi saatiyle anlayabilirdi; bu arada başka bir
    /// kopya kirayı Unconfirmed yapıp kendi isteğini açmış olabilir.</para>
    /// </summary>
    private async Task<PaymentRequestReply?> ReplayAsync(Guid licenseId, BeginInput begin, CancellationToken ct)
    {
        var existing = await _db.PaymentRequestRecords.AsNoTracking()
            .Where(r => r.Id == begin.RecordId)
            .Select(r => new { r.LicenseId, r.Kind, r.State, r.LeaseUntil, r.RequestHash })
            .FirstOrDefaultAsync(ct);
        if (existing is null) return null;

        if (existing.LicenseId != licenseId || existing.Kind != PaymentRequestKind.Lease
            || !existing.RequestHash.AsSpan().SequenceEqual(begin.RequestHash))
        {
            _log.LogInformation(
                "payment-requests/begin: record {RecordId} farklı bir içerikle ya da başka bir lisansta zaten kayıtlı, 409 "
                + "content-conflict (license={LicenseId}, replica={ReplicaId})",
                begin.RecordId, licenseId, begin.ReplicaId);
            return Fail(StatusCodes.Status409Conflict, CodeContentConflict,
                "Bu recordId farklı bir içerikle zaten kullanıldı.");
        }

        var members = (await _db.PaymentRequestMembers.AsNoTracking()
                .Where(m => m.RecordId == begin.RecordId)
                .Select(m => m.WpfCustomerId)
                .ToListAsync(ct))
            .Order(StringComparer.Ordinal)
            .ToList();
        var state = IsExpiredLease(existing.State, existing.LeaseUntil, DateTimeOffset.UtcNow)
            ? PaymentRequestState.Unconfirmed
            : existing.State;
        _log.LogInformation(
            "payment-requests/begin: record {RecordId} yeniden oynatıldı, durum {State} (license={LicenseId}, "
            + "replica={ReplicaId})",
            begin.RecordId, state, licenseId, begin.ReplicaId);
        return new PaymentRequestSucceeded(new PaymentRequestBeginResponse(
            begin.RecordId, PaymentRequestWire.State(state), existing.LeaseUntil, members));
    }

    // ── (c–e) kişi×kapsamın kayıtları ───────────────────────────────────────

    /// <summary>
    /// Kişi×kapsamın bütün kayıtları (her tür ve durum): PersonKey özetiyle (metin bellekte ordinal yeniden denetlenir) ya da
    /// aynı kapsamda üye kesişimiyle (SQL harf duyarsız getirir, üye bellekte ordinal eşleşir).
    ///
    /// <para>Kapsam, liste ucunun aksine bellekte ordinal yeniden denetlenmez; bilerek kabadır. <c>begin</c>'in kapsamı
    /// yalnız <c>cumulative</c> ya da <c>session:{GUID}</c>'dir ve yalnız harfle ayrılan iki <c>session:</c> aynı yayındır.
    /// Kilit adı (küçük harfe katlanmış) ve tekil <c>Sending</c> indeksi de onları aynı sayar. İnceltmek, aynı yayının
    /// önceki isteğini <c>already-requested</c> sorusundan düşürürdü.</para>
    /// </summary>
    /// <param name="members">Üyeler ve GUID'li üyelerin kopyaları (<see cref="PaymentPersonResolver.WithCopiesAsync"/>):
    /// birleştirmeden önce yazılmış kayıt, kopyaya dönmüş Id'yle saklanır.</param>
    private async Task<IReadOnlyList<Related>> RelatedAsync(
        Guid licenseId, PaymentPerson person, string scopeKey, IReadOnlyList<string> members, CancellationToken ct)
    {
        var hash = person.PersonKeyHash;
        var byKey = (await _db.PaymentRequestRecords.AsNoTracking()
                .Where(r => r.LicenseId == licenseId && r.PersonKeyHash == hash && r.ScopeKey == scopeKey)
                .Select(r => new
                {
                    r.PersonKey,
                    Row = new Related(
                        r.Id, r.ReplicaId, r.MachineName, r.Kind, r.State, r.NetAmount, r.TotalBeforeBalance, r.StartedAt,
                        r.LeaseUntil, r.FinishedAt),
                })
                .ToListAsync(ct))
            .Where(x => string.Equals(x.PersonKey, person.PersonKey, StringComparison.Ordinal))
            .Select(x => x.Row)
            .ToList();

        var memberSet = members.ToHashSet(StringComparer.Ordinal);
        var known = byKey.Select(r => r.Id).ToHashSet();
        var memberRecordIds = (await _db.PaymentRequestMembers.AsNoTracking()
                .Where(m => m.LicenseId == licenseId && m.ScopeKey == scopeKey && members.Contains(m.WpfCustomerId))
                .Select(m => new { m.RecordId, m.WpfCustomerId })
                .ToListAsync(ct))
            .Where(m => memberSet.Contains(m.WpfCustomerId) && !known.Contains(m.RecordId))
            .Select(m => m.RecordId)
            .Distinct()
            .ToList();
        if (memberRecordIds.Count == 0) return byKey;

        var byMember = await _db.PaymentRequestRecords.AsNoTracking()
            .Where(r => r.LicenseId == licenseId && memberRecordIds.Contains(r.Id))
            .Select(r => new Related(
                r.Id, r.ReplicaId, r.MachineName, r.Kind, r.State, r.NetAmount, r.TotalBeforeBalance, r.StartedAt,
                r.LeaseUntil, r.FinishedAt))
            .ToListAsync(ct);
        return byKey.Concat(byMember).ToList();
    }

    /// <summary>
    /// (c) Verilen <c>Sending</c> kayıtları <c>Unconfirmed</c> yapar, <c>Version + 1</c>. Koşul yazımın içinde yeniden
    /// denetlenir (hâlâ Sending ve süresi dolmuş ya da çağıranın): eşzamanlı bir kapanış ezilmez. İlişkisel sağlayıcıda
    /// tek koşullu UPDATE, açık işlemin içinde; InMemory'de aynı kural izlenen varlıklara (<see cref="SessionReleaser"/>
    /// deseni).
    ///
    /// <para><c>FinishedAt</c>: süresi dolmuş kirada kira sonu (<c>LeaseUntil ?? StartedAt</c>), liste ucunun satır
    /// yazılmadan bildirdiği an (<see cref="PaymentRequestRecordService"/>). Kaydın sırası ve sorudaki anı, başka bir
    /// kopyanın begin'i ne zaman çağırdığına göre kaymaz. Süresi dolmamış kira (aynı kopya yeniden başladı) bu anda
    /// kapanır: <c>now</c>.</para>
    /// </summary>
    /// <returns>Unconfirmed yapılan kayıt sayısı.</returns>
    private async Task<int> ExpireAsync(
        Guid licenseId, IReadOnlyList<Guid> ids, Guid callerReplicaId, DateTimeOffset now, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
            return await _db.PaymentRequestRecords
                .Where(r => r.LicenseId == licenseId && ids.Contains(r.Id) && r.State == PaymentRequestState.Sending
                    && (r.LeaseUntil == null || r.LeaseUntil <= now || r.ReplicaId == callerReplicaId))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.State, PaymentRequestState.Unconfirmed)
                    .SetProperty(r => r.FinishedAt, r => r.LeaseUntil == null || r.LeaseUntil <= now
                        ? r.LeaseUntil ?? r.StartedAt
                        : now)
                    .SetProperty(r => r.Version, r => r.Version + 1), ct);

        var rows = await _db.PaymentRequestRecords
            .Where(r => r.LicenseId == licenseId && ids.Contains(r.Id) && r.State == PaymentRequestState.Sending)
            .ToListAsync(ct);
        var expired = rows
            .Where(r => r.LeaseUntil is null || r.LeaseUntil <= now || r.ReplicaId == callerReplicaId)
            .ToList();
        foreach (var row in expired)
        {
            row.State = PaymentRequestState.Unconfirmed;
            row.FinishedAt = row.LeaseUntil is null || row.LeaseUntil <= now ? row.LeaseUntil ?? row.StartedAt : now;
            row.Version++;
        }
        if (expired.Count > 0) await _db.SaveChangesAsync(ct);
        return expired.Count;
    }

    /// <summary>Tekil indeks ihlalinden sonra kişi×kapsamın kazanan <c>Sending</c> kaydı; bulunamazsa null.</summary>
    private async Task<Related?> SendingWinnerAsync(
        Guid licenseId, PaymentPerson person, string scopeKey, CancellationToken ct)
    {
        var hash = person.PersonKeyHash;
        return (await _db.PaymentRequestRecords.AsNoTracking()
                .Where(r => r.LicenseId == licenseId && r.PersonKeyHash == hash && r.ScopeKey == scopeKey
                    && r.State == PaymentRequestState.Sending)
                .Select(r => new Related(
                    r.Id, r.ReplicaId, r.MachineName, r.Kind, r.State, r.NetAmount, r.TotalBeforeBalance, r.StartedAt,
                    r.LeaseUntil, r.FinishedAt))
                .ToListAsync(ct))
            .OrderBy(r => r.StartedAt)
            .ThenBy(r => r.Id)
            .FirstOrDefault();
    }

    // ── (f) sunucu toplamı ─────────────────────────────────────────────────

    /// <summary>Sunucu kümesinin bir siparişi, kararın okuduğu alanlarla.</summary>
    private sealed record CountedOrder(Guid Id, decimal Price, Guid SessionId);

    /// <summary>
    /// (f) Sunucu toplamı (§6.4-f, M11): kişinin kapsamdaki, toplama giren siparişlerinin kümesi
    /// (<see cref="ServerOrdersAsync"/>) istemcinin <c>orderIds</c>'ine, fiyat toplamı <c>productTotal</c>'a kuruşu
    /// kuruşuna eşit olmalı; aksi hâlde 409 <c>stale-total {serverTotal, liveElsewhere}</c>. Küme karşılaştırması toplamı
    /// eşit iki farklı kümeyi de (biri iptal, biri yeni etiket) yakalar. Hiçbir şey yazmaz.
    /// </summary>
    /// <returns>Ret; tutuyorsa null.</returns>
    private async Task<PaymentRequestFailed?> CheckServerTotalAsync(
        Guid licenseId, BeginInput begin, IReadOnlyList<string> members, int expired, CancellationToken ct)
    {
        var counted = await ServerOrdersAsync(licenseId, begin.ScopeKey, members, ct);
        var serverTotal = counted.Sum(o => o.Price);
        var serverIds = counted.Select(o => o.Id).ToHashSet();
        if (serverTotal == begin.ProductTotal && serverIds.SetEquals(begin.OrderIds)) return null;

        var clientIds = begin.OrderIds.ToHashSet();
        var serverOnly = counted.Where(o => !clientIds.Contains(o.Id)).ToList();
        var clientOnly = clientIds.Count(id => !serverIds.Contains(id));
        var liveElsewhere = await LiveElsewhereAsync(licenseId, begin.ReplicaId, serverOnly, ct);
        _log.LogInformation(
            "payment-requests/begin: sunucu toplamı istemcininkiyle tutmuyor, 409 stale-total — sunucuda {ServerOrders}, "
            + "istemcide {ClientOrders} sipariş; yalnız sunucuda {ServerOnly}, yalnız istemcide {ClientOnly}; başka "
            + "kopyanın devredilmemiş yayını {LiveElsewhere}; {Expired} kira Unconfirmed yapıldı (license={LicenseId}, "
            + "replica={ReplicaId}, record={RecordId})",
            counted.Count, clientIds.Count, serverOnly.Count, clientOnly, liveElsewhere?.Count ?? 0, expired, licenseId,
            begin.ReplicaId, begin.RecordId);
        return Fail(StatusCodes.Status409Conflict, CodeStaleTotal,
            "Bu bilgisayardaki toplam sunucununkiyle tutmuyor; satışlar eşitlenip yeniden denenmeli.",
            new Dictionary<string, object?> { ["serverTotal"] = serverTotal, ["liveElsewhere"] = liveElsewhere });
    }

    /// <summary>
    /// Kişinin kapsamdaki, toplama giren siparişleri (§6.4-f):
    /// <list type="bullet">
    ///   <item>Yayını olan (<c>SessionId</c> dolu). Yayınsız sipariş hiçbir istemciye inmez; sayılsaydı
    ///   <c>stale-total</c> kalıcı olurdu.</item>
    ///   <item>Kapsam: <c>session:{id}</c> o yayının; <c>cumulative</c> bütün yayınların, canlı ve devredilmemiş
    ///   olanlar dahil.</item>
    ///   <item>Müşterisi üyelerden. GUID'li üye asıl kaydın "N" hex'iyle eşleşir: siparişin <c>CustomerId</c>'si
    ///   <see cref="CustomerIdResolver.CanonicalHexOfAsync"/> ile asıl kayda çözülür, kopyanın Id'siyle saklanmış sipariş
    ///   de sayılır. GUID'siz üye ham <c>CustomerId</c>'yle ORDINAL eşleşir (sunucu GUID olmayan değeri aynen saklıyor).
    ///   SQL harf duyarsız ve sondaki boşluğu yok sayarak getirir; eşitlik bellekte.</item>
    ///   <item>Toplama katkısı var (<see cref="LabelRules.Contribution"/>): basılmış, iptal edilmemiş, geçici yedek
    ///   değil.</item>
    /// </list>
    /// </summary>
    private async Task<IReadOnlyList<CountedOrder>> ServerOrdersAsync(
        Guid licenseId, string scopeKey, IReadOnlyList<string> members, CancellationToken ct)
    {
        var guidMembers = new HashSet<Guid>();
        var rawMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (PaymentPersonKey.TryParseGuid(member, out var id)) guidMembers.Add(id);
            else rawMembers.Add(member);
        }

        var copies = await _customerIds.CopiesOfAsync(licenseId, guidMembers, ct);
        var candidates = members
            .Concat(copies.Select(c => c.ToString("N")))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var query = _db.Orders.AsNoTracking()
            .Where(o => o.LicenseId == licenseId && o.SessionId != null && candidates.Contains(o.CustomerId));
        if (SessionOf(scopeKey) is { } sessionId) query = query.Where(o => o.SessionId == sessionId);
        var orders = await query.ToListAsync(ct);

        var canonical = await _customerIds.CanonicalHexOfAsync(licenseId, orders.Select(o => o.CustomerId), ct);
        return orders
            .Where(o => IsMember(canonical[o.CustomerId]) && LabelRules.Contribution(o.ToLabelFacts()).Count == 1)
            .Select(o => new CountedOrder(o.Id, o.Price, o.SessionId!.Value))
            .ToList();

        bool IsMember(string customerId) =>
            PaymentPersonKey.TryParseGuid(customerId, out var id)
                ? guidMembers.Contains(id)
                : rawMembers.Contains(customerId);
    }

    /// <summary>
    /// <c>liveElsewhere</c> (§6.4-f): yalnız sunucuda olan siparişlerin HEPSİ başka kopyanın devredilmemiş (canlı ya da
    /// devrediliyor: <c>ReleasedAt</c> NULL, sahibi çağıran değil) yayınındaysa o yayınlar ve sahip makineleri; istemci
    /// yayın bitip devredilene kadar bekler. Aksi hâlde null: yalnız sunucuda olan sipariş yok (fark yalnız istemcide ya
    /// da fiyatta), ya da bir kısmı devredilmiş yayında ya da çağıranın kendi yayınında — istemci bir akış turuyla
    /// yetişebilir.
    /// </summary>
    private async Task<IReadOnlyList<PaymentRequestLiveSession>?> LiveElsewhereAsync(
        Guid licenseId, Guid callerReplicaId, IReadOnlyList<CountedOrder> serverOnly, CancellationToken ct)
    {
        if (serverOnly.Count == 0) return null;
        var ids = serverOnly.Select(o => o.SessionId).Distinct().ToList();
        var sessions = await _db.StreamSessions.AsNoTracking()
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .Select(s => new { s.Id, s.ReleasedAt, s.OwnerReplicaId, s.OwnerMachineName })
            .ToListAsync(ct);
        if (sessions.Count != ids.Count
            || sessions.Any(s => s.ReleasedAt is not null || s.OwnerReplicaId == callerReplicaId))
            return null;
        return sessions
            .OrderBy(s => s.Id)
            .Select(s => new PaymentRequestLiveSession(s.Id, s.OwnerMachineName))
            .ToList();
    }

    // ── (g) kargo ayarı ────────────────────────────────────────────────────

    /// <summary>
    /// (g) Kargo ayarı (§6.2, §6.4-g): tutarın hesaplandığı ayar sürümü lisansın güncel sürümü olmalı (satır yoksa 0) ve
    /// <c>totalBeforeBalance = productTotal + kargo</c> lisans ayarıyla tutmalı (<see cref="ShippingFee"/>); aksi hâlde
    /// 409 <c>settings-stale {current}</c>. Yerel ayarla hesaplanmış tutar doğru sürüm numarasıyla da geçemez: aynı kişiye
    /// iki bilgisayarın farklı kargo istemesi böyle kapanır. Hiçbir şey yazmaz.
    /// </summary>
    /// <returns>Ret; tutuyorsa null.</returns>
    private async Task<PaymentRequestFailed?> CheckShippingAsync(
        Guid licenseId, BeginInput begin, int expired, CancellationToken ct)
    {
        var current = await LicenseShippingSettingsStore.ReadAsync(_db, licenseId, ct);
        var versionMatches = begin.ShippingSettingsVersion == current.Version;
        if (versionMatches
            && begin.TotalBeforeBalance == begin.ProductTotal + ShippingFee(current, begin.ProductTotal, begin.RecipientPays))
            return null;

        _log.LogInformation(
            "payment-requests/begin: kargo ayarı tutmuyor, 409 settings-stale — istemcinin sürümü {ClientVersion}, güncel "
            + "{CurrentVersion}, sürüm eşit {VersionMatches}; {Expired} kira Unconfirmed yapıldı (license={LicenseId}, "
            + "replica={ReplicaId}, record={RecordId})",
            begin.ShippingSettingsVersion, current.Version, versionMatches, expired, licenseId, begin.ReplicaId,
            begin.RecordId);
        return Fail(StatusCodes.Status409Conflict, CodeSettingsStale,
            "Kargo ayarı değişti; tutar güncel ayarla yeniden hesaplanmalı.",
            new Dictionary<string, object?> { ["current"] = current });
    }

    /// <summary>
    /// İstenecek kargo — WPF <c>PaymentRequestService.ComputeShipping</c>'in kuralı: alıcı ödemeliyse, ayar kapalıysa
    /// (<c>ShippingSettings.IsEnabled</c>: eşik ve ücret ikisi de &gt; 0 değil) ya da ürün toplamı eşik ve üstündeyse 0;
    /// değilse ücret.
    /// </summary>
    private static decimal ShippingFee(
        LicenseShippingSettingsStore.Snapshot settings, decimal productTotal, bool recipientPays)
    {
        if (recipientPays) return 0m;
        if (settings.FreeShippingThreshold is not { } threshold || threshold <= 0
            || settings.Fee is not { } fee || fee <= 0)
            return 0m;
        return productTotal >= threshold ? 0m : fee;
    }

    // ── Kilit adları ──────────────────────────────────────────────────────

    /// <summary>Kişi×kapsamın kilidi: <c>payreq:{lisans}:{PersonKeyHash hex}:{kapsam, küçük harf}</c>.</summary>
    public static string PersonResource(Guid licenseId, byte[] personKeyHash, string scopeKey) =>
        $"payreq:{licenseId:N}:{Convert.ToHexStringLower(personKeyHash)}:{scopeKey.ToLowerInvariant()}";

    /// <summary>Bir üye×kapsamın kilidi: <c>payreq-m:{lisans}:{üye}:{kapsam, küçük harf}</c>. Üye ham (ordinal): üye
    /// kesişimi de ordinal denetlenir.</summary>
    public static string MemberResource(Guid licenseId, string member, string scopeKey) =>
        $"payreq-m:{licenseId:N}:{member}:{scopeKey.ToLowerInvariant()}";

    // ── Doğrulama ─────────────────────────────────────────────────────────

    private static bool TryValidate(
        PaymentRequestBeginRequest request,
        [NotNullWhen(true)] out BeginInput? begin,
        [NotNullWhen(false)] out PaymentRequestFailed? failure)
    {
        begin = null;
        failure = null;

        if (!SalesEnvelope.TryRead(request, out var envelope, out var envelopeError) || !envelope.IsV2)
            return Invalid(CodeInvalidEnvelope,
                envelopeError ?? "Ödeme isteği v2 isteğidir (replicaId, protocol = 2, seenMode).", out failure);
        if (request.RecordId is not { } recordId || recordId == Guid.Empty)
            return Invalid(CodeInvalidRecord, "recordId boş olamaz.", out failure);

        if (request.MemberIds is not { Count: > 0 } rawMembers || rawMembers.Count > MaxMembers)
            return Invalid(CodeInvalidMembers, $"memberIds 1–{MaxMembers} üye taşır.", out failure);
        if (rawMembers.Any(m => !IsMemberId(m)))
            return Invalid(CodeInvalidMembers,
                $"Üye Id'si boş olamaz, en çok {PaymentRequestRecord.WpfCustomerIdMaxLength} karakter.", out failure);
        var members = rawMembers.Select(m => m!).Distinct(StringComparer.Ordinal).ToList();
        if (request.OwnerId is not { } ownerId || !members.Contains(ownerId, StringComparer.Ordinal))
            return Invalid(CodeInvalidMembers, "ownerId memberIds'te olmalı.", out failure);

        if (!IsLeaseScope(request.ScopeKey))
            return Invalid(CodeInvalidScope, "scopeKey 'cumulative' ya da 'session:{id}' olmalı.", out failure);
        var scopeKey = request.ScopeKey!;

        if (request.OrderIds is not { } rawOrders || rawOrders.Count > MaxOrders || rawOrders.Contains(Guid.Empty))
            return Invalid(CodeInvalidOrders, $"orderIds en çok {MaxOrders} dolu Id taşır.", out failure);

        if (!IsMoney(request.ProductTotal) || !IsMoney(request.TotalBeforeBalance))
            return Invalid(CodeInvalidAmount,
                "productTotal ve totalBeforeBalance 0 ya da daha büyük, en çok iki ondalıklı olmalı.", out failure);

        if (request.RecipientPays is not { } recipientPays
            || request.ShippingSettingsVersion is not { } shippingVersion || shippingVersion < 0)
            return Invalid(CodeInvalidShipping,
                "recipientPays ve 0 ya da daha büyük bir shippingSettingsVersion gerekli.", out failure);

        if (request.ConfirmedPriorIds is { } confirmed
            && (confirmed.Count > MaxConfirmedPriors || confirmed.Contains(Guid.Empty)))
            return Invalid(CodeInvalidConfirmedPriors,
                $"confirmedPriorIds en çok {MaxConfirmedPriors} dolu Id taşır.", out failure);

        byte[]? priorsDigest = null;
        if (request.ConfirmedPriorsDigest is { } digestText
            && !PaymentRequestPriorsDigest.TryParse(digestText, out priorsDigest))
            return Invalid(CodeInvalidPriorsDigest,
                "confirmedPriorsDigest already-requested'in priorsDigest'i olmalı (v1. + 43 karakter base64url).",
                out failure);

        var orders = rawOrders.Distinct().ToList();
        var productTotal = request.ProductTotal!.Value;
        var totalBeforeBalance = request.TotalBeforeBalance!.Value;
        begin = new BeginInput(
            envelope,
            recordId,
            ownerId,
            members,
            scopeKey,
            orders,
            productTotal,
            totalBeforeBalance,
            recipientPays,
            shippingVersion,
            (request.ConfirmedPriorIds ?? []).ToHashSet(),
            priorsDigest,
            PaymentRequestHash.Compute(
                envelope.ReplicaId, ownerId, members, scopeKey, orders, productTotal, totalBeforeBalance, recipientPays,
                shippingVersion));
        return true;
    }

    private static bool Invalid(string code, string detail, out PaymentRequestFailed failure)
    {
        failure = Fail(StatusCodes.Status400BadRequest, code, detail);
        return false;
    }

    // ── Yanıtlar ───────────────────────────────────────────────────────────

    /// <summary>409 <c>in-progress-elsewhere {machine, since, recordId}</c>; kilit zaman aşımında ya da kazanan
    /// okunamadığında alanlar boş.</summary>
    private static PaymentRequestFailed InProgressElsewhere(Related? other) =>
        Fail(StatusCodes.Status409Conflict, CodeInProgressElsewhere,
            "Bu kişiye başka bir bilgisayarda ödeme isteniyor.",
            new Dictionary<string, object?>
            {
                ["machine"] = other?.MachineName,
                ["since"] = other?.StartedAt,
                ["recordId"] = other?.Id,
            });

    private static PaymentRequestFailed Fail(
        int statusCode, string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(statusCode, code, detail, extensions ?? new Dictionary<string, object?>());

    // ── Hata sınıflama ─────────────────────────────────────────────────────

    /// <summary>DM6'nın filtreli tekil indeksinin ihlali (2601/2627, indeks adıyla).</summary>
    private static bool IsSendingIndexViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains(PaymentRequestRecord.SendingIndexName, StringComparison.Ordinal);

    /// <summary>Kayıt tablosunun birincil anahtar ihlali: aynı <c>recordId</c> eşzamanlı eklendi.</summary>
    private static bool IsRecordKeyViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 } sql
        && sql.Message.Contains($"'{RecordKeyName}'", StringComparison.Ordinal);

    /// <summary>İşlemi geri alır ve izleyiciyi boşaltır: sonraki okumalar veritabanını görür.</summary>
    private async Task RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction)
    {
        if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
    }
}
