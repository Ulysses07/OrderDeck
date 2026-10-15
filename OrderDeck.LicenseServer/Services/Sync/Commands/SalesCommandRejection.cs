using Microsoft.AspNetCore.Http;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>
/// Komut uygulanırken verilen ret (spec 2026-10-13 §4.6, §10.2): işleyici ya da <see cref="SalesCommandScope"/> yardımcısı
/// atar, <see cref="SalesCommandService"/> işlemi geri alır ve ProblemDetails'e çevirir (<c>title</c> = <see cref="Code"/>).
/// Ret hiçbir şey yazmaz: işlem tümüyle geri alınır, komut günlüğe girmez.
/// </summary>
public sealed class SalesCommandRejection : Exception
{
    public const string CodeStale = "stale";
    public const string CodeSessionLive = "session-live";
    public const string CodeUnknownOrder = "unknown-order";
    public const string CodePlanStale = "plan-stale";
    public const string CodeUnknownSession = "unknown-session";

    private SalesCommandRejection(
        int statusCode,
        string code,
        string detail,
        IReadOnlyDictionary<string, object?> extensions,
        bool withCurrent,
        IReadOnlyList<Guid>? currentOrderIds = null)
        : base(detail)
    {
        StatusCode = statusCode;
        Code = code;
        Extensions = extensions;
        WithCurrentOrders = withCurrent;
        CurrentOrderIds = currentOrderIds;
    }

    public int StatusCode { get; }

    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Extensions { get; }

    /// <summary>Ret, komutun siparişlerinin güncel hâlini (<c>current</c>) taşır; servis işlem geri alındıktan sonra
    /// taze okur.</summary>
    public bool WithCurrentOrders { get; }

    /// <summary><see cref="WithCurrentOrders"/>'ın siparişleri komutunkilerden (<c>ISalesCommand.OrderIds</c>) farklıysa
    /// onlar; null = komutunkiler.</summary>
    public IReadOnlyList<Guid>? CurrentOrderIds { get; }

    /// <summary>
    /// 409 <c>stale</c> (§4.6 adım 4, 6): istemcinin gördüğü sürüm güncel değil ya da eşzamanlı bir yazım (jeton, kilit
    /// zaman aşımı) araya girdi. Yanıt güncel satırları taşır; istemci onları uygular, niyeti idempotent olan komutu
    /// (iptal, geri alma) taze sürümlerle bir kez yeniden dener (§5.3).
    /// </summary>
    public static SalesCommandRejection Stale(string detail) =>
        new(StatusCodes.Status409Conflict, CodeStale, detail, new Dictionary<string, object?>(), withCurrent: true);

    /// <summary>
    /// 409 <c>session-live</c> (§4.6 adım 3): fiyat, iptal ya da yedek birimine dokunulan siparişin yayını devredilmemiş
    /// (canlı ya da devrediliyor) ya da siparişin yayını yok. Canlı yayın yalnız sahibinde yazılır (Y1, DM1).
    /// </summary>
    /// <param name="machine">Yayının sahibi bilgisayarın adı; bilinmiyorsa null.</param>
    /// <param name="ownerLastSeenAt">Sahip kopyanın son kalp atışı (<c>WpfReplicas.LastSeenAt</c>); bilinmiyorsa null.</param>
    public static SalesCommandRejection SessionLive(Guid? sessionId, string? machine, DateTimeOffset? ownerLastSeenAt) =>
        new(StatusCodes.Status409Conflict, CodeSessionLive,
            "Bu yayın henüz sunucuya devredilmedi; yalnız onu açan bilgisayar değiştirebilir.",
            new Dictionary<string, object?>
            {
                ["machine"] = machine,
                ["ownerLastSeenAt"] = ownerLastSeenAt,
                ["sessionId"] = sessionId,
            },
            withCurrent: false);

    /// <summary>400 <c>unknown-order</c>: komutun siparişi bu lisansta yok. Akıştan inen ya da bu bilgisayarın
    /// gönderdiği satır her zaman sunucudadır; yoksa istemci hatasıdır.</summary>
    public static SalesCommandRejection UnknownOrders(IReadOnlyList<Guid> orderIds) =>
        new(StatusCodes.Status400BadRequest, CodeUnknownOrder, "Komutun siparişlerinden bazıları bu lisansta yok.",
            new Dictionary<string, object?> { ["orderIds"] = orderIds },
            withCurrent: false);

    /// <summary>
    /// 409 <c>plan-stale</c> (§4.6 <c>set-code-price</c>, §10.1-4): sunucunun kurduğu plan (düzeltilebilir satırlar ve
    /// sürümleri) istemcinin gördüğü plandan (<c>expected</c>) farklı — bir satır eklendi, çıktı ya da sürümü ilerledi.
    /// Bu türde <c>expected</c> planın kendisidir; her uyuşmazlık plan-stale'dir. Yanıt güncel planı (<c>plan</c>) ve
    /// planın, istemcinin beklediği ve seçtiği satırların güncel hâlini (<c>current</c>) taşır. Fiyatta sessiz yeniden
    /// deneme yoktur: istemci planı yeniden gösterir (§5.3).
    /// </summary>
    /// <param name="plan">Sunucunun güncel planı, sipariş Id'sine göre sıralı.</param>
    /// <param name="currentOrderIds">Taze hâli dönecek siparişler.</param>
    public static SalesCommandRejection PlanStale(
        IReadOnlyList<SalesCommandExpected> plan, IReadOnlyList<Guid> currentOrderIds) =>
        new(StatusCodes.Status409Conflict, CodePlanStale,
            "Bu arada başka bir bilgisayarda değişti; plan yenilendi.",
            new Dictionary<string, object?> { ["plan"] = plan },
            withCurrent: true,
            currentOrderIds);

    /// <summary>400 <c>unknown-session</c>: komutun yayını bu lisansta yok (istemci hatası).</summary>
    public static SalesCommandRejection UnknownSession(Guid sessionId) =>
        new(StatusCodes.Status400BadRequest, CodeUnknownSession, "Komutun yayını bu lisansta yok.",
            new Dictionary<string, object?> { ["sessionId"] = sessionId },
            withCurrent: false);
}
