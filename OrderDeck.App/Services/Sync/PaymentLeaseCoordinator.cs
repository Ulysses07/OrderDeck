using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

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

/// <summary><c>begin</c>'in girdisi (spec 2026-10-13 §6.4 istemci adımı 3): tutar okunduktan ve kargo hesaplandıktan sonra,
/// bakiyeden ÖNCE.</summary>
/// <param name="Person">Kira altında okunan kişi: <c>memberIds</c> bütün üyeleri (GUID'siz ham Id'ler dahil), sahip
/// <see cref="PaymentPerson.OwnerId"/> ya da GUID'li üye yoksa sahip kuralının belirlenimli ilk üyesi.</param>
/// <param name="OrderIds">Toplama giren etiketler (<see cref="PaymentLeaseAmount.OrderIds"/>).</param>
/// <param name="ProductTotal">Kapsamdan yeniden okunan ürün toplamı.</param>
/// <param name="TotalBeforeBalance"><paramref name="ProductTotal"/> + kargo, mesajla AYNI kargo ayarı anlık görüntüsünden
/// (<c>ComputeShipping</c>).</param>
/// <param name="RecipientPays">Kişi düzeyinde alıcı ödemeli.</param>
/// <param name="ShippingSettingsVersion">Anlık görüntünün lisans geneli ayar sürümü (önbellek boşsa 0).</param>
public sealed record PaymentLeaseBeginInput(
    PaymentPerson Person,
    string ScopeKey,
    IReadOnlyList<Guid> OrderIds,
    decimal ProductTotal,
    decimal TotalBeforeBalance,
    bool RecipientPays,
    int ShippingSettingsVersion);

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
/// <para><b>Kira</b> (<see cref="PaymentLeaseFlow.BeginAsync"/>, §6.4 istemci adımı 3): tutar okunup kargo hesaplandıktan
/// sonra, bakiyeden ÖNCE (GUID'siz kişide de). Akış başına yeni <c>recordId</c>; yalnız yanıtı kaybolan isteğin akış içi
/// tek yinelemesi aynı Id'yi ve aynı gövdeyi taşır (sunucu yeniden oynatır, §6.4-a). 200 → akış sürer, sunucunun üyeleri
/// saklanır (<see cref="PaymentLeaseFlow.ServerMembers"/>). Kira alınamazsa mesaj GİTMEZ (M9, fail-closed).</para>
///
/// <para><b>Akışın sonu</b> (<see cref="PaymentLeaseFlow.FinishAsync"/>, §6.4 "Akışın sonu"): kira alındıysa (ya da
/// sonucu bilinmiyorsa) her çıkışta <c>finish</c> — <c>delivered</c>, <c>unconfirmed</c> ya da <c>abandoned</c>; net,
/// düşüm ve tamamı bakiyeden akıştan. Önce kalıcı kuyruğa yazılır (<see cref="PaymentRecordSender.EnqueueFinish"/>),
/// sonra en çok <see cref="FinishBudget"/> gönderilir: gidemezse kuyrukta kalır, sonraki gönderim (geri doldurma turu,
/// sonraki isteğin ön gönderimi) götürür. Süreç arada kapansa da kayıt kaybolmaz — kira kendiliğinden dolup sahte
/// "gitmiş olabilir" olmaz.</para>
///
/// <para>Günlük yalnız Id ve sayı taşır (ad, telefon, tutar yok).</para>
/// </summary>
public sealed class PaymentLeaseCoordinator
{
    /// <summary>Ön gönderimin toplam süresi (§6.4-1).</summary>
    public static readonly TimeSpan PrePushBudget = TimeSpan.FromSeconds(5);

    /// <summary><c>finish</c>'in akış içindeki gönderim süresi; dolarsa kayıt kuyrukta kalır.</summary>
    public static readonly TimeSpan FinishBudget = TimeSpan.FromSeconds(3);

    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly ICurrentLicenseProvider _license;
    private readonly SalesSyncStateRepository _state;
    private readonly SalesChangesPullService _pull;
    private readonly PaymentRecordSender _records;
    private readonly WpfCustomerProjectionSyncService _customerPush;
    private readonly SessionOrderSyncService _salesPush;
    private readonly PendingSalesCommandRepository _pendingCommands;
    private readonly LabelRepository _labels;
    private readonly ILogger<PaymentLeaseCoordinator> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public PaymentLeaseCoordinator(
        LicenseApiClient api,
        SalesSyncEnvelope envelope,
        ICurrentLicenseProvider license,
        SalesSyncStateRepository state,
        SalesChangesPullService pull,
        PaymentRecordSender records,
        WpfCustomerProjectionSyncService customerPush,
        SessionOrderSyncService salesPush,
        PendingSalesCommandRepository pendingCommands,
        LabelRepository labels,
        ILogger<PaymentLeaseCoordinator> log)
    {
        _api = api;
        _envelope = envelope;
        _license = license;
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

    /// <summary><c>finish</c>'in akış içindeki gönderim süresi; testler değiştirir.</summary>
    internal TimeSpan FinishTimeout { get; set; } = FinishBudget;

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

    /// <summary><see cref="PaymentLeaseFlow.BeginAsync"/>'in gövdesi.</summary>
    internal async Task<PaymentRequestOutcome?> BeginAsync(
        PaymentLeaseFlow flow, PaymentLeaseBeginInput input, CancellationToken ct)
    {
        var person = input.Person;
        if (!PaymentHistoryRecords.IsLeaseScope(input.ScopeKey) || person.MemberIds.Count == 0)
        {
            _log.LogError("Ödeme isteği kirası kurulamadı: kapsam ya da üye kümesi kira biçimine uymuyor — mesaj engellendi "
                        + "(customer={CustomerId})", person.LeasedId);
            return FailClosed;
        }
        if (_envelope.ForPush() is not { IsV2: true } envelope)
        {
            _log.LogInformation("Ödeme isteği kirası: v2 zarfı hazır değil — mesaj engellendi (customer={CustomerId})",
                person.LeasedId);
            return FailClosed;
        }
        var licenseId = await ResolveLicenseIdAsync(ct).ConfigureAwait(false);
        if (licenseId is null)
        {
            _log.LogInformation("Ödeme isteği kirası: lisans çözülemedi (sunucuya ulaşılamıyor) — mesaj engellendi "
                              + "(customer={CustomerId})", person.LeasedId);
            return FailClosed;
        }

        var request = new PaymentRequestBeginRequest(
            RecordId: Guid.NewGuid(),
            OwnerId: person.OwnerId ?? person.MemberIds[0],
            MemberIds: person.MemberIds,
            ScopeKey: input.ScopeKey,
            OrderIds: input.OrderIds,
            ProductTotal: PaymentHistoryRecords.Money(input.ProductTotal),
            TotalBeforeBalance: PaymentHistoryRecords.Money(input.TotalBeforeBalance),
            RecipientPays: input.RecipientPays,
            ShippingSettingsVersion: input.ShippingSettingsVersion,
            ConfirmedPriorIds: [],
            ConfirmedPriorsDigest: null,
            ReplicaId: envelope.ReplicaId!.Value,
            Protocol: SalesSyncEnvelope.Protocol,
            SeenMode: envelope.SeenMode);

        PaymentRequestHttpResult<PaymentRequestBeginResponse> http;
        try
        {
            http = await _api.PostPaymentRequestBeginAsync(licenseId.Value, request, ct).ConfigureAwait(false);
            if (http.Transport is PaymentRequestTransport.Offline or PaymentRequestTransport.Failed)
            {
                // Sunucu isteği almış olabilir: kira yazıldıysa çıkışta abandoned ile kapanır (yoksa finish
                // record-not-found alır ve düşer). Akış içi tek yineleme AYNI Id ve gövdeyle: sunucu yeniden oynatır.
                flow.Begun(request.RecordId);
                _log.LogInformation("Ödeme isteği kirası {RecordId}: yanıt alınamadı ({Transport}); aynı istek bir kez "
                                  + "yineleniyor", request.RecordId, http.Transport);
                http = await _api.PostPaymentRequestBeginAsync(licenseId.Value, request, ct).ConfigureAwait(false);
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // İstemcinin göndermeden reddettiği istek (üye sayısı sınırı aşıyor).
            _log.LogError(ex, "Ödeme isteği kirası {RecordId} gönderilemedi: istek sunucu sınırlarını aşıyor — mesaj "
                            + "engellendi", request.RecordId);
            flow.Clear();
            return FailClosed;
        }

        switch (http.Transport)
        {
            case PaymentRequestTransport.Ok when http.Response!.State == PaymentRequestStates.Sending:
                flow.Begun(request.RecordId, http.Response.Members);
                _log.LogInformation(
                    "Ödeme isteği kirası alındı {RecordId} (customer={CustomerId}, {Members} üye, {Orders} sipariş)",
                    request.RecordId, person.LeasedId, http.Response.Members.Count, request.OrderIds.Count);
                return null;
            case PaymentRequestTransport.Ok:
                // Yeniden oynatma kapanmış bir kaydı döndürdü: bu akışın kirası yok.
                flow.Clear();
                _log.LogWarning("Ödeme isteği kirası {RecordId}: kayıt {State} durumunda döndü — mesaj engellendi",
                    request.RecordId, http.Response!.State);
                return FailClosed;
            case PaymentRequestTransport.Offline or PaymentRequestTransport.Failed:
                _log.LogInformation("Ödeme isteği kirası {RecordId}: sunucuya ulaşılamadı ({Transport}, HTTP {Status}) — "
                                  + "mesaj engellendi", request.RecordId, http.Transport, http.StatusCode);
                return FailClosed;
            case PaymentRequestTransport.EndpointMissing:
                flow.Clear();
                _log.LogWarning("Ödeme isteği kirası {RecordId}: kira ucu yok (404) — mesaj engellendi", request.RecordId);
                return FailClosed;
            default:
                flow.Clear();
                _log.LogInformation("Ödeme isteği kirası {RecordId} reddedildi: {Code} — mesaj engellendi",
                    request.RecordId, http.Problem?.Code);
                return FailClosed;
        }
    }

    /// <summary>Kira alınamadı: mesaj gitmez (M9), bugünkü belirsizlik sunumu.</summary>
    private static PaymentRequestOutcome FailClosed => new(PaymentRequestResult.BalanceUncertain);

    /// <summary><see cref="PaymentLeaseFlow.FinishAsync"/>'in gövdesi: önce kalıcı kuyruk, sonra süreli gönderim. Fırlatmaz.</summary>
    internal async Task FinishAsync(Guid recordId, string outcome, decimal netAmount, decimal? appliedAmount)
    {
        var finish = new PendingPaymentFinish(
            recordId,
            outcome,
            PaymentHistoryRecords.Money(netAmount),
            appliedAmount is { } applied ? PaymentHistoryRecords.Money(applied) : null,
            FullyFromBalance: appliedAmount is > 0m && netAmount <= 0m);
        try
        {
            _records.EnqueueFinish(finish);
        }
        catch (Exception ex)
        {
            // Kayıt yazılamadı: kira sunucuda dolar ve "gitmiş olabilir" sayılır (fail-closed yönü).
            _log.LogWarning(ex, "Ödeme isteği kirası {RecordId}: finish ({Outcome}) kuyruğa yazılamadı", recordId, outcome);
            return;
        }

        using var budget = new CancellationTokenSource(FinishTimeout);
        try
        {
            var flush = await _records.FlushAsync(budget.Token, finishesOnly: true).ConfigureAwait(false);
            _log.LogInformation("Ödeme isteği kirası {RecordId}: finish ({Outcome}); {Sent} kayıt sunucuya geçti, kuyrukta "
                              + "{Remaining}", recordId, outcome, flush.Sent, flush.Remaining);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Ödeme isteği kirası {RecordId}: finish ({Outcome}) süresinde gönderilemedi; kuyrukta",
                recordId, outcome);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ödeme isteği kirası {RecordId}: finish ({Outcome}) gönderimi yerel hatayla düştü; kuyrukta",
                recordId, outcome);
        }
    }

    /// <summary>Geçerli lisans anahtarının LicenseId'si; çözülemezse (anahtar yok, sunucuya ulaşılamıyor) null.</summary>
    private async Task<Guid?> ResolveLicenseIdAsync(CancellationToken ct)
    {
        var key = _license.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_cachedLicenseId is { } cached && _cachedLicenseKey == key) return cached;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == key);
            if (match?.Id is null) return null;
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = key;
            return match.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Ödeme isteği kirası: LicenseId çözülemedi");
            return null;
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

    /// <summary>Kapanmamış kira (ya da sonucu bilinmeyen <c>begin</c>): çıkışta <c>finish</c> bununla gider.</summary>
    private Guid? _recordId;

    internal PaymentLeaseFlow(PaymentLeaseCoordinator owner) => _owner = owner;

    /// <summary>Son kiranın sunucu üyeleri (<c>begin</c> yanıtı): istemcinin kümesi asıl kayda çözülmüş, sunucunun grubuyla
    /// birleşmiş — kapsam uzlaştırmasının adayları (§6.4 "Kapsam uzlaştırması"). Kira yokken boş.</summary>
    public IReadOnlyList<string> ServerMembers { get; private set; } = [];

    /// <summary>
    /// Kişi × kapsamın kirası (§6.4 istemci adımı 3). <c>null</c> = kira alındı, akış sürer; değilse akış durur ve bu sonuç
    /// döner (mesaj gitmez). Yerel kip On'da kira zorunludur ve fail-closed çalışır (M9).
    /// </summary>
    public Task<PaymentRequestOutcome?> BeginAsync(PaymentLeaseBeginInput input, CancellationToken ct)
        => _owner.BeginAsync(this, input, ct);

    /// <summary>
    /// Akışın sonu (§6.4 "Akışın sonu"): kira alındıysa ya da <c>begin</c>'in sonucu bilinmiyorsa <c>finish</c>
    /// (<paramref name="outcome"/> = <see cref="PaymentRequestStates.Delivered"/>, <see cref="PaymentRequestStates.Unconfirmed"/>
    /// ya da <see cref="PaymentRequestStates.Abandoned"/>); yoksa hiçbir şey. Bir kira bir kez kapanır. Fırlatmaz.
    /// </summary>
    /// <param name="netAmount">Mesajın neti (bakiye düşüldükten sonra).</param>
    /// <param name="appliedAmount">Bakiyeden düşülen; bakiye adımı sonuçlanmadıysa ya da kişide bakiye işi yoksa null.</param>
    public Task FinishAsync(string outcome, decimal netAmount, decimal? appliedAmount)
    {
        if (_recordId is not { } recordId) return Task.CompletedTask;
        _recordId = null;
        ServerMembers = [];
        return _owner.FinishAsync(recordId, outcome, netAmount, appliedAmount);
    }

    internal void Begun(Guid recordId, IReadOnlyList<string>? serverMembers = null)
    {
        _recordId = recordId;
        if (serverMembers is not null) ServerMembers = serverMembers;
    }

    internal void Clear()
    {
        _recordId = null;
        ServerMembers = [];
    }

    /// <summary>
    /// Kişi okunduktan sonra (kira altında): bekleyen komut bu kişinin bu kapsamdaki bir etiketini örtüyorsa engel; değilse
    /// tutar ve <c>orderIds</c> kapsamdan yeniden okunur (§6.4 istemci adımları 1–2) — ön gönderimde basılmış etiketler de
    /// girer. Yerel hata engeldir (fail-closed).
    /// </summary>
    public PaymentLeaseAmount ReadAmount(PaymentPerson person, string scopeKey) => _owner.ReadAmount(person, scopeKey);
}
