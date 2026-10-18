using System.Text.Json.Serialization;

namespace OrderDeck.Licensing.Api.Models;

// Ödeme isteği uçları (spec 2026-10-13 §4.7, §6.4; S3 ve PR #523'ün onay özeti) ile lisans geneli kargo ayarı (§4.8, §6.2).
// Sunucudaki karşılıkları: Services/Sync/PaymentRequests/PaymentRequestContracts.cs (begin, finish, liste ve backfill
// kayıtları), PaymentRequestRules (sınırlar ve kodlar), LicensesPaymentRequestsController (ret = ProblemDetails, title = kod,
// ek üyeler), LicensesShippingSettingsController.PutRequest ve LicenseShippingSettingsStore.Snapshot. Alan adları ve tipleri
// onların aynası. Uçların hepsi v2 konuşur: zarf (replicaId, protocol = 2, seenMode) her istekte dolu (§4.0).

/// <summary>Sunucunun istek sınırları (<c>PaymentRequestRules</c>, <c>PaymentRequestLeaseService.MaxConfirmedPriors</c>).
/// <see cref="LicenseApiClient"/> bunları aşan isteği göndermeden fırlatır: sunucu 400 verirdi.</summary>
public static class PaymentRequestLimits
{
    /// <summary><c>confirmedPriorIds</c>'in en çok Id sayısı. Daha büyük öncül kümesini onay özeti onaylar
    /// (<see cref="PaymentRequestBeginRequest.ConfirmedPriorsDigest"/>, §6.4 "409'ların istemcideki karşılığı"); fazlası
    /// gönderilmez.</summary>
    public const int MaxConfirmedPriorIds = 500;

    /// <summary>Bir isteğin (ve liste sorgusunun) en çok üye sayısı.</summary>
    public const int MaxMembers = 100;

    /// <summary>Bir geri doldurma partisinin en çok kayıt sayısı.</summary>
    public const int MaxBackfillBatch = 200;
}

/// <summary>Kaydın teldeki durumu (<c>PaymentRequestWire.State</c>); <c>finish</c>'in sonucu ve geri doldurmanın durumu da
/// bu adlarla gider.</summary>
public static class PaymentRequestStates
{
    public const string Sending = "sending";
    public const string Delivered = "delivered";
    public const string Unconfirmed = "unconfirmed";
    public const string Abandoned = "abandoned";
}

/// <summary>Kaydın türü (<c>PaymentRequestWire.Kind</c>): kira ya da kirasız kayıt.</summary>
public static class PaymentRequestKinds
{
    public const string Lease = "lease";
    public const string Record = "record";
}

/// <summary>Ödeme isteği ve kargo ayarı uçlarının ret kodları (ProblemDetails <c>title</c>'ı).</summary>
public static class PaymentRequestProblemCodes
{
    // 409 — begin (PaymentRequestLeaseService)
    public const string ModeOff = "mode-off";
    public const string ClientUpdateRequired = "client-update-required";
    public const string ContentConflict = "content-conflict";
    public const string MembersStale = "members-stale";
    public const string InProgressElsewhere = "in-progress-elsewhere";
    public const string AlreadyRequested = "already-requested";
    public const string StaleTotal = "stale-total";
    public const string SettingsStale = "settings-stale";

    // 404 — finish: kayıt bu lisansta yok (uç yok değil)
    public const string RecordNotFound = "record-not-found";

    // 409 — PUT shipping-settings: beklenen sürüm güncel değil (ek üye current)
    public const string Stale = "stale";

    // 400 — istemci hatası
    public const string InvalidEnvelope = "invalid-envelope";
    public const string InvalidBatch = "invalid-batch";
    public const string InvalidRecord = "invalid-record";
    public const string InvalidOutcome = "invalid-outcome";
    public const string InvalidState = "invalid-state";
    public const string InvalidAt = "invalid-at";
    public const string InvalidMembers = "invalid-members";
    public const string InvalidScope = "invalid-scope";
    public const string InvalidOrders = "invalid-orders";
    public const string InvalidAmount = "invalid-amount";
    public const string InvalidShipping = "invalid-shipping";
    public const string InvalidConfirmedPriors = "invalid-confirmed-priors";
    public const string InvalidPriorsDigest = "invalid-priors-digest";
    public const string InvalidExpectedVersion = "invalid-expected-version";
}

// ── begin ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>POST payment-requests/begin</c> gövdesi (§6.4 istemci adımı 3, #523 onay özeti).
/// </summary>
/// <param name="RecordId">İstemcinin ürettiği kayıt Id'si; aynı Id ve aynı içerik yeniden oynatmadır (§6.4-a).</param>
/// <param name="OwnerId">Sahip satırın Id'si; <paramref name="MemberIds"/>'te olmalı.</param>
/// <param name="MemberIds">Kişinin BÜTÜN üyeleri, GUID'siz olanlar dahil (ham Id); 1–100.</param>
/// <param name="ScopeKey"><c>cumulative</c> ya da <c>session:{"N" hex}</c>.</param>
/// <param name="OrderIds">Toplama giren siparişler; sunucunun kümesiyle eşit olmalı (§6.4-f).</param>
/// <param name="TotalBeforeBalance"><paramref name="ProductTotal"/> + lisans ayarıyla kargo (§6.2, §6.4-g).</param>
/// <param name="ShippingSettingsVersion">Tutarın hesaplandığı lisans kargo ayarı sürümü (satır yoksa 0).</param>
/// <param name="ConfirmedPriorIds">"Tekrar gönderilsin mi?" sorusunda onaylanan öncüller; en çok
/// <see cref="PaymentRequestLimits.MaxConfirmedPriorIds"/>, boş olabilir.</param>
/// <param name="ConfirmedPriorsDigest">Onaylanan sorunun <c>already-requested.priorsDigest</c>'i, aynen. Boşken telde hiç
/// yazılmaz: sunucu onayı yalnız Id listesinden okur.</param>
public sealed record PaymentRequestBeginRequest(
    Guid RecordId,
    string OwnerId,
    IReadOnlyList<string> MemberIds,
    string ScopeKey,
    IReadOnlyList<Guid> OrderIds,
    decimal ProductTotal,
    decimal TotalBeforeBalance,
    bool RecipientPays,
    int ShippingSettingsVersion,
    IReadOnlyList<Guid> ConfirmedPriorIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConfirmedPriorsDigest,
    Guid ReplicaId,
    int Protocol,
    int SeenMode);

/// <summary><c>begin</c>'in 200 yanıtı (ilk kayıt ve yeniden oynatma): kaydın GÜNCEL hâli.</summary>
/// <param name="State"><see cref="PaymentRequestStates"/>; yeniden oynatmada kapanmış bir durum da olabilir.</param>
/// <param name="LeaseUntil">Kiranın sonu (sunucu saati).</param>
/// <param name="Members">Kaydın üyeleri: istemcinin kümesi asıl kayda çözülmüş, sunucunun grubuyla birleşmiş; kapsam
/// uzlaştırmasının adayları (§6.4 "Kapsam uzlaştırması").</param>
public sealed record PaymentRequestBeginResponse(
    Guid RecordId,
    string State,
    DateTimeOffset? LeaseUntil,
    IReadOnlyList<string> Members);

/// <summary><c>already-requested</c>'ın bir öncül kaydı (§6.4-e); liste ucunun sırasıyla, eskiden yeniye.</summary>
/// <param name="Machine">Kaydı yazan bilgisayar; bilinmiyorsa null.</param>
/// <param name="NetAmount">İstenen net tutar; teslim kaydı olmadan dolan kirada null.</param>
/// <param name="TotalBeforeBalance">Kiranın bakiyeden önceki tutarı; kirasız kayıtta null. <paramref name="NetAmount"/>
/// null iken sorunun gösterdiği tutar budur (§6.8).</param>
/// <param name="At">İsteğin anı, yalnız gösterim için.</param>
/// <param name="FinishedAt">Kapanış anı, sunucu saati.</param>
/// <param name="Unconfirmed">Mesaj gitmiş olabilir, sonucu bilinmiyor.</param>
public sealed record PaymentRequestPrior(
    Guid Id,
    string? Machine,
    decimal? NetAmount,
    decimal? TotalBeforeBalance,
    DateTimeOffset At,
    DateTimeOffset? FinishedAt,
    bool Unconfirmed);

/// <summary><c>stale-total</c>'ın <c>liveElsewhere</c> öğesi (§6.4-f): farkı açıklayan, başka kopyanın devredilmemiş
/// yayını.</summary>
/// <param name="Machine">Yayının sahibi bilgisayar; bilinmiyorsa null.</param>
public sealed record PaymentRequestLiveSession(Guid SessionId, string? Machine);

// ── finish ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary><c>POST payment-requests/{id}/finish</c> gövdesi (§6.4 "Akışın sonu"); iki kipte çalışır.</summary>
/// <param name="Outcome"><see cref="PaymentRequestStates.Delivered"/>, <see cref="PaymentRequestStates.Unconfirmed"/> ya
/// da <see cref="PaymentRequestStates.Abandoned"/>.</param>
/// <param name="AppliedAmount">Bakiyeden düşülen tutar; bilinmiyorsa null.</param>
public sealed record PaymentRequestFinishRequest(
    string Outcome,
    decimal NetAmount,
    decimal? AppliedAmount,
    bool FullyFromBalance,
    Guid ReplicaId,
    int Protocol,
    int SeenMode);

/// <summary><c>finish</c>'in 200 yanıtı (ilk yazım ve yeniden oynatma): kaydın GÜNCEL hâli.</summary>
public sealed record PaymentRequestFinishResponse(Guid RecordId, string State, DateTimeOffset? FinishedAt);

// ── liste ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary><c>GET payment-requests?scopeKey&amp;memberIds</c>'in bir öğesi (§6.4 "Teslim kayıtlarının okunması"). Süresi
/// dolmuş canlı kira <c>unconfirmed</c> olarak, <paramref name="FinishedAt"/> = kiranın dolduğu an ile gelir.</summary>
/// <param name="Kind"><see cref="PaymentRequestKinds"/>.</param>
/// <param name="OrderIds">Kaydın kapsadığı siparişler: uzak kayıtta <c>Covers</c> bununla cevaplanır, iki bilgisayarın
/// saati karşılaştırılmaz.</param>
public sealed record PaymentRequestListItem(
    Guid Id,
    Guid ReplicaId,
    string? Machine,
    string Kind,
    string State,
    decimal? NetAmount,
    decimal? AppliedAmount,
    bool? FullyFromBalance,
    decimal? ProductTotal,
    decimal? TotalBeforeBalance,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<Guid> OrderIds);

/// <summary>Liste ucunun 200 yanıtı: kapanış anına göre eskiden yeniye, en yeni 200 kayıt. Kendi kopyanın kayıtları
/// süzülmez; onları çağıran düşer.</summary>
public sealed record PaymentRequestListResponse(IReadOnlyList<PaymentRequestListItem> Records);

// ── backfill ─────────────────────────────────────────────────────────────────────────────────────────

/// <summary><c>POST payment-requests/backfill</c>'in bir öğesi (§6.4 "Ödeme geçmişi"): yerel <c>PaymentJob</c>'un kirasız
/// kaydı. Moddan bağımsızdır.</summary>
/// <param name="RecordId">İş Id'si ile teslim sayısından türetilmiş Id; yineleme zararsız.</param>
/// <param name="ScopeKey">İşin ASIL kapsamı (<c>cumulative</c>, <c>session:{"N" hex}</c>); asıl kapsamı bilinmeyen miras
/// işte <c>legacy</c> ya da <c>legacy:{anahtar}</c>.</param>
/// <param name="OrderIds">Yerelde <c>Covers</c> kuralıyla hesaplanmış, kaydın kapsadığı siparişler.</param>
/// <param name="State"><see cref="PaymentRequestStates.Delivered"/> ya da
/// <see cref="PaymentRequestStates.Unconfirmed"/>.</param>
/// <param name="At">Kaydın yerel anı (istemci saati); bilgi ve gösterim içindir.</param>
public sealed record PaymentRequestBackfillItem(
    Guid RecordId,
    string OwnerId,
    IReadOnlyList<string> MemberIds,
    string ScopeKey,
    IReadOnlyList<Guid> OrderIds,
    string State,
    decimal NetAmount,
    decimal? AppliedAmount,
    bool FullyFromBalance,
    DateTimeOffset At);

/// <summary><c>backfill</c> gövdesi: en çok <see cref="PaymentRequestLimits.MaxBackfillBatch"/> kayıt ve v2 zarfı.</summary>
public sealed record PaymentRequestBackfillRequest(
    IReadOnlyList<PaymentRequestBackfillItem> Records,
    Guid ReplicaId,
    int Protocol,
    int SeenMode);

/// <summary>Geri doldurmanın öğe sonucu.</summary>
/// <param name="Status"><see cref="StatusApplied"/>, <see cref="StatusUnchanged"/> ya da
/// <see cref="StatusContentConflict"/>.</param>
public sealed record PaymentRequestBackfillResult(Guid RecordId, string Status)
{
    /// <summary>Yazıldı.</summary>
    public const string StatusApplied = "applied";

    /// <summary>Aynı içerik zaten kayıtlı; yeni üyeler eklendi.</summary>
    public const string StatusUnchanged = "unchanged";

    /// <summary>Aynı Id'de başka içerik (başka lisansın ya da kiranın Id'si). Son durum — yeniden gönderilmez (§4.7).</summary>
    public const string StatusContentConflict = "content-conflict";
}

/// <summary><c>backfill</c>'in 200 yanıtı: istekteki sırayla, öğe başına bir sonuç.</summary>
public sealed record PaymentRequestBackfillResponse(IReadOnlyList<PaymentRequestBackfillResult> Records);

// ── kargo ayarı ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>Lisans geneli kargo ayarı (<c>LicenseShippingSettingsStore.Snapshot</c>, §4.8): <c>GET</c> ve <c>PUT</c>
/// yanıtı, <c>PUT</c>'un <c>stale</c>'i ve <c>begin</c>'in <c>settings-stale</c>'i bunu taşır. Satır yoksa sürüm 0 ve
/// değerler boş.</summary>
/// <param name="Fee">Kargo ücreti; <paramref name="FreeShippingThreshold"/> ile birlikte boş = kapalı.</param>
/// <param name="UpdatedBy"><c>cli</c> ya da yazan kopyanın ReplicaId'si.</param>
public sealed record ShippingSettingsSnapshot(
    int Version,
    decimal? FreeShippingThreshold,
    decimal? Fee,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary><c>PUT shipping-settings</c> gövdesi (<c>LicensesShippingSettingsController.PutRequest</c>): yalnız mod On'da
/// (§4.8). Boş değerler de yazılır: ikisi de boş = kargo kapalı.</summary>
/// <param name="ExpectedVersion">İstemcinin gördüğü sürüm (satır yoksa 0); güncel değilse 409 <c>stale {current}</c>.</param>
public sealed record ShippingSettingsPutRequest(
    int ExpectedVersion,
    decimal? FreeShippingThreshold,
    decimal? Fee,
    Guid ReplicaId,
    int Protocol,
    int SeenMode);

// ── taşıma sonucu ────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Ödeme isteği ve kargo ayarı uçlarının reddi (400/404/409 ProblemDetails): <c>title</c> = <see cref="Code"/>; ek üyeler
/// sunucunun yazdıkları. Yalnız ilgili ret doldurur; öbürleri null.
/// </summary>
/// <param name="Priors"><c>already-requested</c>: <c>confirmedPriorIds</c> dışındaki öncüller.</param>
/// <param name="PriorsDigest"><c>already-requested</c>: öncül kümesinin özeti; onay turunda
/// <see cref="PaymentRequestBeginRequest.ConfirmedPriorsDigest"/> olarak aynen geri gider. #523 öncesi sunucuda null.</param>
/// <param name="Members"><c>members-stale</c>: sunucunun grubu.</param>
/// <param name="ServerTotal"><c>stale-total</c>: sunucunun toplamı.</param>
/// <param name="LiveElsewhere"><c>stale-total</c>: farkı açıklayan, başka kopyanın devredilmemiş yayınları; yoksa null.</param>
/// <param name="Current"><c>settings-stale</c> ve kargo ayarı <c>stale</c>'i: güncel ayar.</param>
/// <param name="Machine"><c>in-progress-elsewhere</c>: kirayı tutan bilgisayar; kilit zaman aşımında null.</param>
/// <param name="Since"><c>in-progress-elsewhere</c>: kiranın başlangıcı; kilit zaman aşımında null.</param>
/// <param name="RecordId"><c>in-progress-elsewhere</c>: öbür kopyanın kaydı; kilit zaman aşımında null.</param>
public sealed record PaymentRequestProblem(
    [property: JsonPropertyName("title")] string? Code,
    string? Detail,
    int? Status,
    IReadOnlyList<PaymentRequestPrior>? Priors,
    string? PriorsDigest,
    IReadOnlyList<string>? Members,
    decimal? ServerTotal,
    IReadOnlyList<PaymentRequestLiveSession>? LiveElsewhere,
    ShippingSettingsSnapshot? Current,
    string? Machine,
    DateTimeOffset? Since,
    Guid? RecordId);

/// <summary>Ödeme isteği ve kargo ayarı çağrılarının taşıma sonucu (<see cref="SalesCommandTransport"/> deseni).</summary>
public enum PaymentRequestTransport
{
    /// <summary>200 ve eksiksiz gövde: <see cref="PaymentRequestHttpResult{T}.Response"/> dolu.</summary>
    Ok,

    /// <summary>400 ya da 409 başlıklı, ek üyeleri eksiksiz ProblemDetails; ya da <c>finish</c>'in 404
    /// <c>record-not-found</c>'u: <see cref="PaymentRequestHttpResult{T}.Problem"/> dolu.</summary>
    Problem,

    /// <summary>Öbür 404'ler: uç yok (S2/S3 öncesi imaj ya da otomatik geri alma penceresi, §8.6) ya da lisans bu hesabın
    /// değil.</summary>
    EndpointMissing,

    /// <summary>Ağ hatası ya da zaman aşımı: sunucu isteği almış olabilir — aynı istek aynen yinelenebilir.</summary>
    Offline,

    /// <summary>5xx, beklenmeyen kod, başlıksız ya da ek üyesi eksik ret, çözümlenemeyen ya da eksik 200 gövdesi. İstek
    /// uygulanmış olabilir; aynı istek aynen yinelenebilir.</summary>
    Failed,
}

/// <summary>Bir ödeme isteği ya da kargo ayarı çağrısının sonucu.</summary>
/// <param name="StatusCode">HTTP kodu; ağ hatasında 0.</param>
/// <param name="Error">Günlük için kısa açıklama (kişisel veri yok).</param>
public sealed record PaymentRequestHttpResult<T>(
    PaymentRequestTransport Transport,
    int StatusCode,
    T? Response = null,
    PaymentRequestProblem? Problem = null,
    string? Error = null)
    where T : class;
