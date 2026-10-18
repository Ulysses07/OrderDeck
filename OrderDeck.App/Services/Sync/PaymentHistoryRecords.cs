using OrderDeck.Core.Payments;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Kirasız ödeme kaydının (<c>payment-requests/backfill</c> öğesi, <see cref="PaymentRequestBackfillItem"/>) kurulumu
/// (spec 2026-10-13 §4.7, §6.4 "Ödeme geçmişi (mod Off'ta da)", M17; W3-06). Ödeme geçmişi geri doldurması
/// (<see cref="PaymentHistoryBackfill"/>) ve mod Off'taki teslim kaydı (<see cref="PaymentOffRecordWriter"/>) AYNI
/// kuralla kurar: aynı iş aynı sayıda aynı Id'yi ve aynı içeriği verir, sunucu yinelemeyi <c>unchanged</c> sayar.
///
/// <para><b>İşten kayıt</b> (<see cref="FromJob"/>):
/// <list type="bullet">
///   <item>Id: sonucu bilinmeyen gönderimde <see cref="PaymentRecordIds.Unconfirmed"/> (deneme sayısıyla), aksi hâlde
///   <see cref="PaymentRecordIds.Delivered"/> (teslim sayısıyla) — iki ayrı ad uzayı.</item>
///   <item>Durum <c>unconfirmed</c> (= <see cref="PaymentJob.HasUnconfirmedSend"/>) ya da <c>delivered</c>.</item>
///   <item>Sahip işin satırı (<see cref="PaymentJob.CustomerId"/>); kapsam işin ASIL kapsamı
///   (<see cref="PaymentJob.SaleScopeKey"/>) — <c>legacy</c>/<c>legacy:…</c> yalnız asıl kapsamı bilinmeyen işte.</item>
///   <item>Tutarlar: <c>netAmount</c> = <see cref="PaymentJob.PromptNet"/> (sorunun gösterdiği net), <c>appliedAmount</c>
///   = <see cref="PaymentJob.AppliedAmount"/>, <c>fullyFromBalance</c> = uygulanmış ∧ düşüm &gt; 0 ∧ net ≤ 0.</item>
///   <item>An: <see cref="PaymentJob.PromptAt"/> (yalnız gösterim; sunucu sırayı kendi saatiyle tutar).</item>
/// </list></para>
///
/// <para><b>Sunucu sınırları</b> istemcide uygulanır (<c>PaymentRequestRules</c>): sunucunun 400 vereceği öğe partinin
/// tamamını düşürür ve kuyruk hiç boşalmazdı — <c>paymentHistoryCompleted</c> kalıcı olarak gitmezdi. Kapsamı
/// tanınmayan ya da sahibi tel biçimine sığmayan iş kayda çevrilmez (<see cref="IsRecordScope"/>,
/// <see cref="WireMembers"/>); üye ve sipariş listeleri sınırlara kırpılır, tutarlar iki ondalığa yuvarlanır ve 0'ın
/// altına inmez.</para>
/// </summary>
public static class PaymentHistoryRecords
{
    public const string CumulativeScope = "cumulative";
    public const string SessionScopePrefix = "session:";
    public const string LegacyScope = "legacy";
    public const string LegacyScopePrefix = "legacy:";

    /// <summary>Kira kapsamı (sunucunun <c>IsLeaseScope</c>'u): <c>cumulative</c> ya da <c>session:</c> + yayının "N"
    /// biçimli Id'si. Kapsadığı siparişler yerel etiketlerden kurulabilir.</summary>
    public static bool IsLeaseScope(string? scopeKey) =>
        scopeKey is not null
        && scopeKey.Length <= PaymentRequestLimits.MaxScopeKeyLength
        && (string.Equals(scopeKey, CumulativeScope, StringComparison.Ordinal)
            || (scopeKey.StartsWith(SessionScopePrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(scopeKey.AsSpan(SessionScopePrefix.Length), "N", out _)));

    /// <summary>Kayıt kapsamı (sunucunun <c>IsRecordScope</c>'u): kira kapsamı ya da miras biçimi (<c>legacy</c>,
    /// <c>legacy:{anahtar}</c>, anahtar boş değil); en çok <see cref="PaymentRequestLimits.MaxScopeKeyLength"/>.</summary>
    public static bool IsRecordScope(string? scopeKey) =>
        IsLeaseScope(scopeKey)
        || (scopeKey is not null
            && scopeKey.Length <= PaymentRequestLimits.MaxScopeKeyLength
            && (string.Equals(scopeKey, LegacyScope, StringComparison.Ordinal)
                || (scopeKey.StartsWith(LegacyScopePrefix, StringComparison.Ordinal)
                    && scopeKey.Length > LegacyScopePrefix.Length)));

    /// <summary><c>session:{id}</c> kapsamının yerel yayın Id'si; başka kapsamda null.</summary>
    public static string? SessionIdOf(string scopeKey) =>
        scopeKey.StartsWith(SessionScopePrefix, StringComparison.Ordinal) ? scopeKey[SessionScopePrefix.Length..] : null;

    /// <summary>
    /// Teldeki üye listesi: sahip önce, sonra öbür üyeler verilen sırayla; tekil (ordinal), boş ya da
    /// <see cref="PaymentRequestLimits.MaxMemberIdLength"/>'ten uzun Id'ler atlanır, en çok
    /// <see cref="PaymentRequestLimits.MaxMembers"/>. Sahip tel biçimine sığmıyorsa null — kayıt kurulamaz (sunucu
    /// sahibin üyelerde olmasını ister).
    /// </summary>
    public static IReadOnlyList<string>? WireMembers(string ownerId, IEnumerable<string> memberIds)
    {
        if (!IsWireMember(ownerId)) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal) { ownerId };
        var result = new List<string> { ownerId };
        foreach (var id in memberIds)
        {
            if (result.Count >= PaymentRequestLimits.MaxMembers) break;
            if (IsWireMember(id) && seen.Add(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>Teldeki sipariş listesi: Guid olarak çözülen etiket Id'leri, tekil, verilen sırayla, en çok
    /// <see cref="PaymentRequestLimits.MaxOrders"/>.</summary>
    public static IReadOnlyList<Guid> WireOrders(IEnumerable<string> labelIds)
    {
        var seen = new HashSet<Guid>();
        var result = new List<Guid>();
        foreach (var id in labelIds)
        {
            if (result.Count >= PaymentRequestLimits.MaxOrders) break;
            if (Guid.TryParse(id, out var guid) && guid != Guid.Empty && seen.Add(guid)) result.Add(guid);
        }
        return result;
    }

    /// <summary>Teldeki tutar: iki ondalık (yukarı yarım), 0'ın altına inmez (sunucu negatif tutarı reddeder).</summary>
    public static decimal Money(decimal value) =>
        value <= 0m ? 0m : decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>İsteğin anı (unix saniye): <see cref="PaymentJob.PromptAt"/>; bilinmiyorsa son teslim, o da yoksa işin
    /// son güncellenmesi. <c>Covers</c> kuralının sınırı (<c>PrintedAt ≤ PromptAt</c>).</summary>
    public static long PromptAtOf(PaymentJob job) => job.PromptAt ?? job.LastDeliveredAt ?? job.UpdatedAt;

    /// <summary>İşin kirasız kaydı (sınıf belgesi).</summary>
    /// <param name="wireMembers"><see cref="WireMembers"/>'ın sonucu: sahip önce.</param>
    /// <param name="orderIds">Kaydın kapsadığı siparişler (<see cref="WireOrders"/>); miras kapsamda boş.</param>
    /// <param name="forceUnconfirmed">Teslim yazılmış olsa da sonuç bilinmiyor (Cloud API <c>in_progress</c>,
    /// <c>SendPending</c>): kayıt <c>unconfirmed</c>, Id deneme sayısıyla.</param>
    public static PaymentRequestBackfillItem FromJob(
        PaymentJob job, IReadOnlyList<string> wireMembers, IReadOnlyList<Guid> orderIds, bool forceUnconfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        var unconfirmed = forceUnconfirmed || job.HasUnconfirmedSend;
        var net = job.PromptNet;
        var applied = job.AppliedAmount;
        return new PaymentRequestBackfillItem(
            RecordId: unconfirmed
                ? PaymentRecordIds.Unconfirmed(job.Id, job.SendAttempts)
                : PaymentRecordIds.Delivered(job.Id, job.Deliveries),
            OwnerId: job.CustomerId,
            MemberIds: wireMembers,
            ScopeKey: job.SaleScopeKey,
            OrderIds: orderIds,
            State: unconfirmed ? PaymentRequestStates.Unconfirmed : PaymentRequestStates.Delivered,
            NetAmount: Money(net),
            AppliedAmount: applied is { } a ? Money(a) : null,
            FullyFromBalance: job.State == PaymentJobState.Applied && applied is > 0m && net <= 0m,
            At: DateTimeOffset.FromUnixTimeSeconds(PromptAtOf(job)));
    }

    private static bool IsWireMember(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= PaymentRequestLimits.MaxMemberIdLength;
}
