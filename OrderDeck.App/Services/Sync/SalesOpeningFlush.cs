using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>Mod On açılışının 1. adımının sonucu (<see cref="ISalesOpeningFlush.FlushAsync"/>).</summary>
/// <param name="Flushed">Bütün giden kuyruklar boşaldı.</param>
/// <param name="BlockedQueue">Boşalmayan ilk kuyruğun günlükteki adı (kişisel veri yok); boşaldıysa <c>null</c>.</param>
/// <param name="Remaining">O kuyrukta kalan kayıt sayısı.</param>
public sealed record SalesOpeningFlushResult(bool Flushed, string? BlockedQueue, int Remaining)
{
    public static SalesOpeningFlushResult Ok { get; } = new(true, null, 0);

    public static SalesOpeningFlushResult Blocked(string queue, int remaining) => new(false, queue, remaining);
}

/// <summary>Mod On açılışının 1. adımı (spec 2026-10-13 §8.4-1): bütün giden kuyrukları görülen kip 0 ile boşaltır.
/// <see cref="SalesModeCoordinator"/>'ın tek dikişi (testler kendi kuyruklarını verebilir).</summary>
public interface ISalesOpeningFlush
{
    /// <summary>Kuyrukları sırayla boşaltır ve her birinin boşaldığını kendi gönderiminden HEMEN sonra denetler.
    /// Yerel hata çağırana fırlar; iptal <see cref="OperationCanceledException"/>.</summary>
    Task<SalesOpeningFlushResult> FlushAsync(string licenseKey, CancellationToken ct);
}

/// <summary>
/// Mod On açılışının 1. adımı (spec 2026-10-13 §8.4-1; W2-10). Sunucu modu 1 gösterirken yerel kip hâlâ 0'dır: her
/// gönderim zarfı görülen kip 0 taşır (<see cref="SalesSyncEnvelope.SeenMode"/>), sunucu açılış penceresindeki kopyanın
/// gönderimini Off kurallarıyla alır (§4.0) — modu görmeden verilmiş kargo kararı ve bağ kaybolmaz.
///
/// <para><b>Sıra</b> (her kuyruk kendi gönderiminden hemen sonra sayılır, <see cref="SalesSyncStateRepository.GetOpeningBacklog"/>):
/// <list type="number">
///   <item>Bekleyen ödeme kayıtları (<see cref="PaymentRecordSender"/>, W3-05; §8.4-1'in ilk kuyruğu): gönderilemeyen
///   <c>finish</c>'ler ve mod Off kirasız kayıtları — mod On'da öbür bilgisayarın <c>begin</c>'i (e) bu bilgisayarın
///   önceki isteklerini görmeli. Gönderim bütçelidir (<see cref="PaymentRecordSender.MaxRequestsPerFlush"/>); kalan
///   varsa açılış burada durur, sonraki tur sürdürür. Boşaltma sürerken yazılan kayıt sonda yeniden sayılır.</item>
///   <item>Müşteri (<see cref="WpfCustomerProjectionSyncService"/>; tur kuyruğu kendisi parti parti boşaltır). Tavan
///   gönderimden ÖNCE okunur: gönderim sırasında doğan müşteri (yayında sohbet) açılışı bekletmez.</item>
///   <item>Oturum ve etiket (<see cref="SessionOrderSyncService"/>, gönderim kapısından; tur bir şey gönderdikçe
///   yinelenir). Canlı yayının oturumu ve etiketleri sayılmaz: iki kipte aynı yargılanırlar.</item>
///   <item>Kargo (<see cref="ShipmentSyncService"/>, yinelenir).</item>
///   <item>Dekont (<see cref="PaymentSyncService"/>, yinelenir).</item>
/// </list>
/// Boşalmayan ilk kuyrukta durulur: yerel kip 0 kalır, sonraki tur baştan dener. Kuyruğun boşaldığı ile 2. adımın
/// kilidi arasında yazılan kargo ya da etiket (ör. dekontun kargo adımı) 2. adımın kilit altındaki yeniden denetimine
/// takılır (<see cref="SalesSyncStateRepository.TryEnableMode"/>).</para>
///
/// <para>Gönderim servisleri HTTP hatasını kendileri yutar; başarının ölçüsü kuyruğun gerçekten boşalmasıdır. Her
/// <c>await</c> <c>ConfigureAwait(false)</c>; SQLite bağlantısı ağ isteğini beklemez.</para>
/// </summary>
public sealed class SalesOpeningFlush : ISalesOpeningFlush
{
    /// <summary>Kuyruk adları (günlük).</summary>
    internal const string CustomersQueue = "müşteri";
    internal const string SalesQueue = "oturum ve etiket";
    internal const string ShipmentsQueue = "kargo";
    internal const string PaymentsQueue = "dekont";
    internal const string PaymentRecordsQueue = "ödeme kaydı";

    private readonly PaymentRecordSender _paymentRecords;
    private readonly WpfCustomerProjectionSyncService _customers;
    private readonly SessionOrderSyncService _sales;
    private readonly ShipmentSyncService _shipments;
    private readonly PaymentSyncService _payments;
    private readonly SalesSyncStateRepository _state;

    public SalesOpeningFlush(
        PaymentRecordSender paymentRecords,
        WpfCustomerProjectionSyncService customers,
        SessionOrderSyncService sales,
        ShipmentSyncService shipments,
        PaymentSyncService payments,
        SalesSyncStateRepository state)
    {
        _paymentRecords = paymentRecords;
        _customers = customers;
        _sales = sales;
        _shipments = shipments;
        _payments = payments;
        _state = state;
    }

    public async Task<SalesOpeningFlushResult> FlushAsync(string licenseKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);

        var ceiling = _state.MaxCustomerSyncSeq();
        await _paymentRecords.FlushAsync(ct).ConfigureAwait(false);
        var backlog = Backlog(licenseKey, ceiling, ct);
        if (backlog.PaymentRecords > 0)
            return SalesOpeningFlushResult.Blocked(PaymentRecordsQueue, backlog.PaymentRecords);

        await _customers.SyncOnceAsync(ct).ConfigureAwait(false);
        backlog = Backlog(licenseKey, ceiling, ct);
        if (backlog.Customers > 0) return SalesOpeningFlushResult.Blocked(CustomersQueue, backlog.Customers);

        await DrainAsync(async t =>
        {
            var r = await _sales.SyncOnceAsync(t).ConfigureAwait(false);
            return r.SessionsPushed + r.OrdersPushed;
        }, ct).ConfigureAwait(false);
        backlog = Backlog(licenseKey, ceiling, ct);
        if (backlog.EndedSessions + backlog.EndedSessionLabels > 0)
            return SalesOpeningFlushResult.Blocked(SalesQueue, backlog.EndedSessions + backlog.EndedSessionLabels);

        await DrainAsync(async t => (await _shipments.SyncOnceAsync(t).ConfigureAwait(false)).Pushed, ct)
            .ConfigureAwait(false);
        backlog = Backlog(licenseKey, ceiling, ct);
        if (backlog.Shipments > 0) return SalesOpeningFlushResult.Blocked(ShipmentsQueue, backlog.Shipments);

        await DrainAsync(async t => (await _payments.SyncOnceAsync(t).ConfigureAwait(false)).Pushed, ct)
            .ConfigureAwait(false);
        backlog = Backlog(licenseKey, ceiling, ct);
        if (backlog.Payments > 0) return SalesOpeningFlushResult.Blocked(PaymentsQueue, backlog.Payments);
        // Boşaltma sürerken yazılan ödeme kaydı (yerel kip hâlâ 0: istek bugünkü yoldan, kirasız kayıt): sonraki tur
        // onu da ilk sırada gönderir.
        if (backlog.PaymentRecords > 0)
            return SalesOpeningFlushResult.Blocked(PaymentRecordsQueue, backlog.PaymentRecords);
        return SalesOpeningFlushResult.Ok;
    }

    /// <summary>Turda tek parti gönderen servis: bir şey gönderdikçe yinelenir (<see cref="SyncFlushService.Drain"/>,
    /// en çok <see cref="SyncFlushService.MaxDrainRounds"/> tur).</summary>
    private static Task DrainAsync(Func<CancellationToken, Task<int>> pushOnce, CancellationToken ct) =>
        SyncFlushService.Drain(pushOnce)(ct);

    /// <summary>Kuyruk kalanı; iptal edildiyse sayılmaz (yarım boşaltma "dolu" sanılmasın).</summary>
    private SalesOpeningBacklog Backlog(string licenseKey, long ceiling, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _state.GetOpeningBacklog(_customers.Watermark(licenseKey), ceiling);
    }
}
