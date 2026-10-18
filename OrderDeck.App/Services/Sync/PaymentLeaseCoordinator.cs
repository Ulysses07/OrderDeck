using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>Ödeme isteği kirasının (spec 2026-10-13 §6.4; W3) operatöre görünen satır içi metinleri.</summary>
public static class PaymentLeaseMessages
{
    /// <summary>§6.4-1: bu kişinin bu kapsamdaki bir etiketinin bekleyen komutu (kalıntı kuyruğun iptali ya da ilk baskısı)
    /// ön gönderimde gönderilemedi.</summary>
    public const string PendingCommandBlocked =
        "Bekleyen baskı ya da iptal onayı gönderilemedi; bağlantı gelince tekrar dene.";
}

/// <summary>Kapsamdan yeniden okunan tutar (spec 2026-10-13 §6.4 istemci adımı 2).</summary>
/// <param name="ProductTotal">Kişinin bütün üyelerinin kapsamdaki katkı veren etiketlerinin toplamı
/// (<see cref="LabelRepository.GetPrintedTotal"/>).</param>
/// <param name="OrderIds">Toplama giren etiketler — <c>begin</c>'in <c>orderIds</c>'i.</param>
/// <param name="Stop">Doluysa akış durur ve bu sonuç döner (hiçbir şey gönderilmedi).</param>
public sealed record PaymentLeaseAmount(decimal ProductTotal, IReadOnlyList<Guid> OrderIds, PaymentRequestOutcome? Stop)
{
    public static PaymentLeaseAmount Stopped(PaymentRequestOutcome outcome) => new(0m, [], outcome);
}

/// <summary>
/// Ödeme isteğinin mod On sarmalı (spec 2026-10-13 §6.4 "İstemci tarafı", §6.5-2, M9, M11, §12 risk 14; W3-07).
/// <c>PaymentRequestService</c> bunu İSTEĞE BAĞLI alır ve yalnız girişte ve çıkışta çağırır; ödeme işinin durum makinesi
/// (<c>PaymentJobRepository</c>) değişmez.
///
/// <para><b>Kapı:</b> yalnız yerel <c>SalesSyncState.Mode = 1</c> (<see cref="EnterAsync"/> null değilse). Mod Off'ta
/// (bugün her lisans) hiçbir şey yapılmaz: ön gönderim yok, kira ucu yok; akış bugünkü yerel yoldan gider ve mod Off kaydı
/// (W3-06) yazılır.</para>
///
/// <para><b>Ön gönderim</b> (§6.4-1, §5.5): akışın EN BAŞINDA, hiçbir <c>CustomerBusySet</c> kirası alınmadan — bekleyen
/// komutların yanıt satırları (<see cref="SalesFeedRepository.ApplyItem"/>) ve gönderimlerin yönlendirmeleri kişinin
/// kendi kirasını beklemesin (U13/U17). Sıra: bekleyen satış komutları
/// (<see cref="SalesChangesPullService.FlushPendingSalesCommandsAsync"/>) → bekleyen <c>finish</c>'ler
/// (<see cref="PaymentRecordSender"/>; gitmezse sunucu bu kopyanın kuyruktaki <c>abandoned</c> kirasını
/// <c>Unconfirmed</c> yapar, §6.4-c, ve sahte bir "gitmiş olabilir" sorusu çıkar) → müşteri → oturum ve etiket (gönderim
/// kapısından). Toplam en çok <see cref="PrePushBudget"/>, iptal edilebilir; zaman aşımı ya da ilgisiz bir kuyruktaki
/// takılma tek başına engel DEĞİLDİR (karar sunucu toplamınındır, §6.4-f). Her adım arka plan iş parçacığında; bir adımın
/// hatası günlüğe düşer, sıradakine geçilir.</para>
///
/// <para><b>Tek doğrudan engel</b> bekleyen komuttur (<see cref="PaymentLeaseFlow.ReadAmount"/>): kişi okunduktan sonra bu
/// kişinin bu kapsamdaki bir etiketini hâlâ bekleyen bir komut örtüyorsa istek durur
/// (<see cref="PaymentLeaseMessages.PendingCommandBlocked"/>) — yerel toplam onaylanmamış baskıyı saymaz, sunucu da onu
/// bilmez; iki toplam eşit çıkar ve kâğıdı basılmış ürün istenmezdi.</para>
///
/// <para>Günlük yalnız Id ve sayı taşır (ad, telefon, tutar yok).</para>
/// </summary>
public sealed class PaymentLeaseCoordinator
{
    /// <summary>Ön gönderimin toplam süresi (§6.4-1).</summary>
    public static readonly TimeSpan PrePushBudget = TimeSpan.FromSeconds(5);

    private readonly SalesSyncStateRepository _state;
    private readonly SalesChangesPullService _pull;
    private readonly PaymentRecordSender _records;
    private readonly WpfCustomerProjectionSyncService _customerPush;
    private readonly SessionOrderSyncService _salesPush;
    private readonly PendingSalesCommandRepository _pendingCommands;
    private readonly LabelRepository _labels;
    private readonly ILogger<PaymentLeaseCoordinator> _log;

    public PaymentLeaseCoordinator(
        SalesSyncStateRepository state,
        SalesChangesPullService pull,
        PaymentRecordSender records,
        WpfCustomerProjectionSyncService customerPush,
        SessionOrderSyncService salesPush,
        PendingSalesCommandRepository pendingCommands,
        LabelRepository labels,
        ILogger<PaymentLeaseCoordinator> log)
    {
        _state = state;
        _pull = pull;
        _records = records;
        _customerPush = customerPush;
        _salesPush = salesPush;
        _pendingCommands = pendingCommands;
        _labels = labels;
        _log = log;
    }

    /// <summary>Ön gönderimin süresi; testler kısaltır.</summary>
    internal TimeSpan PrePushTimeout { get; set; } = PrePushBudget;

    /// <summary>
    /// Akışın girişi. Yerel kip On değilse null — bugünkü yol. On ise ön gönderim koşar ve akışın nesnesi döner. Yerel
    /// kip okunamazsa yerel hata fırlar (çağıran mesajı engeller); iptal <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<PaymentLeaseFlow?> EnterAsync(CancellationToken ct)
    {
        if (_state.GetMode() != 1) return null;
        await PrePushAsync(ct).ConfigureAwait(false);
        return new PaymentLeaseFlow(this);
    }

    private async Task PrePushAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PrePushTimeout);
        var steps = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("bekleyen satış komutları", t => _pull.FlushPendingSalesCommandsAsync(t)),
            ("bekleyen finish kayıtları", t => _records.FlushAsync(t, finishesOnly: true)),
            ("müşteri", t => _customerPush.SyncOnceAsync(t)),
            ("oturum ve etiket", t => _salesPush.SyncOnceAsync(t)),
        };
        foreach (var (name, run) in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested)
            {
                _log.LogInformation("Ödeme isteği ön gönderimi: süre doldu, '{Step}' adımı atlandı; karar sunucunun", name);
                return;
            }
            var step = Task.Run(() => run(budget.Token), CancellationToken.None);
            try
            {
                await step.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Bütçe doldu (§6.4-1: zaman aşımı engel değil). Bırakılan adım arka planda biter; hatası gözlenir.
                _ = step.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _log.LogInformation(
                    "Ödeme isteği ön gönderimi: '{Step}' adımı {Budget} sn içinde bitmedi; kalan adımlar atlandı, karar "
                  + "sunucunun", name, PrePushTimeout.TotalSeconds);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Ödeme isteği ön gönderimi: '{Step}' adımı yerel hatayla düştü; sıradakine geçiliyor",
                    name);
            }
        }
    }

    /// <summary><see cref="PaymentLeaseFlow.ReadAmount"/>'ın gövdesi.</summary>
    internal PaymentLeaseAmount ReadAmount(PaymentPerson person, string scopeKey)
    {
        try
        {
            var sessionId = PaymentHistoryRecords.SessionIdOf(scopeKey);
            var covered = _pendingCommands.GetCoveredLabelIdsOfCustomers(person.MemberIds, sessionId);
            if (covered.Count > 0)
            {
                _log.LogWarning(
                    "Ödeme isteği engellendi: kişinin bu kapsamdaki {Count} etiketini gönderilemeyen bekleyen komut örtüyor "
                  + "(customer={CustomerId})", covered.Count, person.LeasedId);
                return PaymentLeaseAmount.Stopped(
                    new PaymentRequestOutcome(PaymentRequestResult.Blocked, Message: PaymentLeaseMessages.PendingCommandBlocked));
            }

            var total = _labels.GetPrintedTotal(person.MemberIds.ToList(), sessionId);
            return new PaymentLeaseAmount(total.Total, PaymentHistoryRecords.WireOrders(total.LabelIds), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Ödeme isteğinin tutarı kapsamdan okunamadı — mesaj engellendi (customer={CustomerId})",
                person.LeasedId);
            return PaymentLeaseAmount.Stopped(new PaymentRequestOutcome(PaymentRequestResult.BalanceUncertain));
        }
    }
}

/// <summary>
/// Bir "Ödeme iste" akışının mod On durumu (<see cref="PaymentLeaseCoordinator.EnterAsync"/>). Akış başına bir örnek;
/// eşzamanlı kullanılmaz.
/// </summary>
public sealed class PaymentLeaseFlow
{
    private readonly PaymentLeaseCoordinator _owner;

    internal PaymentLeaseFlow(PaymentLeaseCoordinator owner) => _owner = owner;

    /// <summary>
    /// Kişi okunduktan sonra (kira altında): bekleyen komut bu kişinin bu kapsamdaki bir etiketini örtüyorsa engel; değilse
    /// tutar ve <c>orderIds</c> kapsamdan yeniden okunur (§6.4 istemci adımları 1–2) — ön gönderimde basılmış etiketler de
    /// girer. Yerel hata engeldir (fail-closed).
    /// </summary>
    public PaymentLeaseAmount ReadAmount(PaymentPerson person, string scopeKey) => _owner.ReadAmount(person, scopeKey);
}
