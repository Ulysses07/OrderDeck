using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OrderDeck.LicenseServer.Services.Sync.Commands;

/// <summary>Doğrulanmış beklenen sürüm (<see cref="SalesCommandExpectedDto"/>): sipariş Id'si başına bir kez.</summary>
public sealed record SalesCommandExpected(Guid OrderId, int Version);

/// <summary>
/// Bir komut türünün işleyicisi (spec 2026-10-13 §4.6 tablosunun bir satırı). <see cref="SalesCommandService"/> zarfı,
/// kipi, sürüm kapısını, yeniden oynatmayı, işlemi, defteri, dosya türetmesini ve günlüğü yürütür; işleyici yalnız yükü
/// okur ve türün kuralını uygular. Kayıt DI'da (<c>Program.cs</c>), tür başına bir sınıf; kayıtlı olmayan tür 400
/// <c>unknown-kind</c>.
/// </summary>
public interface ISalesCommandHandler
{
    /// <summary>Teldeki tür adı (<c>cancel</c>); en çok <c>SalesCommandLog.KindMaxLength</c> karakter.</summary>
    string Kind { get; }

    /// <summary>
    /// Yükü tipli okur, doğrular ve normalleştirir. Başarısızsa <paramref name="error"/> 400 <c>invalid-payload</c>'ın
    /// açıklamasıdır. Beklenen sürümlerin kapsamı türe özgüdür ve burada denetlenir.
    /// </summary>
    /// <param name="payload">Zarfın <c>payload</c>'ı; yoksa <see cref="JsonValueKind.Undefined"/>.</param>
    bool TryParse(
        JsonElement payload,
        IReadOnlyList<SalesCommandExpected> expected,
        [NotNullWhen(true)] out ISalesCommand? command,
        [NotNullWhen(false)] out string? error);
}

/// <summary>Doğrulanmış, uygulanmaya hazır bir komut.</summary>
public interface ISalesCommand
{
    /// <summary>İstek özetine giren kanonik yük (<see cref="SalesCommandHash"/>); tipli, normalleştirilmiş.</summary>
    object CanonicalPayload { get; }

    /// <summary>Yanıtta (ve yeniden oynatmada) güncel hâliyle dönen siparişler, bu sırayla. Komutun yazdığı başka
    /// siparişler (<see cref="SalesCommandOutcome.Changed"/>) ardından eklenir.</summary>
    IReadOnlyList<Guid> OrderIds { get; }

    /// <summary>Komutun hedef kargo dosyaları (<c>shipment-decision</c>): yanıtta ve <c>stale</c> retinde
    /// (<c>currentShipments</c>) güncel hâlleriyle döner. Çoğu tür için boş.</summary>
    IReadOnlyList<Guid> ShipmentIds => [];

    /// <summary>Komutun hedef yayınları (<c>force-end</c>): yanıtta (ve yeniden oynatmada) siparişlerin yayınlarıyla
    /// birlikte güncel hâlleriyle döner. Çoğu tür için boş.</summary>
    IReadOnlyList<Guid> SessionIds => [];

    /// <summary>
    /// Hiçbir şey yazmayan komut (<c>reprint</c>): yeniden oynatmada sonuç günlükteki kayıttan değil, komut yeniden
    /// (yazmadan) çalıştırılarak güncel satırlardan kurulur — kayıtlı liste arada değişen satırları bayat gösterirdi
    /// (§5.6: çevrimiçiyken güncel sunucu satırları basılır). Günlük satırı yalnız denetim izidir. Varsayılan
    /// <c>false</c>: yazan komut bir kez uygulanır, yeniden oynatma kayıtlı sonucu döner (§4.6 adım 2).
    /// </summary>
    bool RecomputesOnReplay => false;

    /// <summary>
    /// Türün kuralını uygular: <paramref name="scope"/> ile siparişleri yükler, görünürlüğü ve beklenen sürümü denetler,
    /// alanları ve birim damgalarını yazar, değişen siparişi <see cref="SalesCommandScope.MarkChanged"/> ile bildirir.
    /// Kaydetmez. Ret <see cref="SalesCommandRejection"/> atar; servis işlemi geri alır.
    /// </summary>
    Task<SalesCommandOutcome> ExecuteAsync(SalesCommandScope scope, CancellationToken ct);
}

/// <summary>
/// İşleyicinin sonucu: yazılan ve atlanan siparişler ve türe özgü listeler (<see cref="SalesCommandResult"/>'a aynen
/// girer; boş bırakılan liste sonuçta yazılmaz).
/// </summary>
public sealed record SalesCommandOutcome(IReadOnlyList<Guid> Changed, IReadOnlyList<Guid> Skipped)
{
    /// <inheritdoc cref="SalesCommandResult.Marked"/>
    public IReadOnlyList<Guid>? Marked { get; init; }

    /// <inheritdoc cref="SalesCommandResult.AlreadyPrinted"/>
    public IReadOnlyList<Guid>? AlreadyPrinted { get; init; }

    /// <inheritdoc cref="SalesCommandResult.Cancelled"/>
    public IReadOnlyList<Guid>? Cancelled { get; init; }

    /// <inheritdoc cref="SalesCommandResult.ReprintIds"/>
    public IReadOnlyList<Guid>? ReprintIds { get; init; }

    /// <inheritdoc cref="SalesCommandResult.CorrectionIds"/>
    public IReadOnlyList<Guid>? CorrectionIds { get; init; }

    /// <inheritdoc cref="SalesCommandResult.ShipmentId"/>
    public Guid? ShipmentId { get; init; }

    /// <inheritdoc cref="SalesCommandResult.Pool"/>
    public decimal? Pool { get; init; }

    /// <inheritdoc cref="SalesCommandResult.PoolShipmentIds"/>
    public IReadOnlyList<Guid>? PoolShipmentIds { get; init; }

    /// <inheritdoc cref="SalesCommandResult.AlreadyReleased"/>
    public bool? AlreadyReleased { get; init; }

    /// <summary><c>shipment-decision</c>: kararın uygulandığı dosyalar (hedef ve U12 ile öbür açık dosyalar); servis
    /// sonuca sürüm ve tutarlarıyla yazar (<see cref="SalesCommandResult.Decided"/>).</summary>
    public IReadOnlyList<Guid>? DecidedShipmentIds { get; init; }

    /// <summary>
    /// Kargo durumu GERÇEKTEN değişen müşteriler (<c>shipment-decision</c>): servis işlem commit edildikten sonra
    /// <c>ShipmentStatusChanged</c> WhatsApp etiket kuralını bunlara uygular (<c>shipments/sync</c> ile eşlik, §4.6).
    /// Sonuca yazılmaz; yeniden oynatmada tetiklenmez.
    /// </summary>
    public IReadOnlyList<string>? ShipmentStatusChangedCustomers { get; init; }
}
