using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.PriceCorrection;

/// <summary>Teslim edilmiş isteğinin tutarı yeni toplamdan farklı bir kişi × kapsam — "düzeltilmiş isteği
/// gönder" listesinin satırı.</summary>
/// <param name="CustomerId">İsteğin gönderileceği satır (kişinin kiralanan satırı).</param>
/// <param name="PriorAmount">Son istenen net (sorudaki tutarın aynısı — <see cref="PaymentJob.PromptNet"/>).</param>
/// <param name="PriorUnconfirmed">Son gönderimin sonucu yazılamamıştı (gitmiş olabilir).</param>
/// <param name="NewTotal">Şimdi istenecek toplam (kargo dahil, bakiye düşülmeden önce).</param>
/// <param name="PriorHadBalance">Son isteğin netinden bakiye düşülmüştü: <see cref="PriorAmount"/> bakiye
/// SONRASI net, <see cref="NewTotal"/> bakiye ÖNCESİ toplam — satır bunu söyler (ödeme sorusundaki
/// <see cref="PaymentPrompt.PriorHadBalance"/> ile aynı).</param>
public sealed record CorrectedRequestCandidate(
    string CustomerId, string Display, string ScopeKey,
    decimal PriorAmount, DateTimeOffset? PriorAt, bool PriorUnconfirmed, decimal NewTotal,
    bool PriorHadBalance = false)
{
    public bool IsSessionScope => ScopeKey.StartsWith("session:", StringComparison.Ordinal);
}

/// <summary>Bu düzeltmede ödenmiş etiketi (dekontla dosyaya bağlı ya da onu içeren isteğin tamamı bakiyeden düşülmüş)
/// düzelen kişi: OTOMATİK bir şey gitmez, "ödendi, fark ±X TL" olarak gösterilir (K5). İkisi de YALNIZ bu
/// düzeltmenin ödenmiş etiketleri üzerinden (<see cref="CorrectedPaymentRequests.Plan"/>).</summary>
/// <param name="Delta">Σ (yeni fiyat − ödendiği fiyat): 250 → 200 → 190'da ikinci düzeltme −60 der.</param>
/// <param name="StepDelta">Σ (yeni fiyat − bu düzeltmeden önceki fiyat): aynı örnekte −10. <see cref="Delta"/>'dan
/// farklıysa ikisi birlikte gösterilir.</param>
public sealed record PaidPerson(string Display, decimal Delta, decimal StepDelta, bool ByDekont, bool ByBalance);

/// <summary>Düzeltmeden etkilenen kişilerin ödeme durumu.</summary>
/// <param name="RequestedPersons">Düzeltilen etiketleri içeren teslim edilmiş isteği olan, ödemesi
/// alınmamış kişi sayısı.</param>
/// <param name="ToResend">Bunlardan isteğinin tutarı yeni toplamdan farklı olan kişi × kapsamlar.</param>
public sealed record PaymentFollowUpPlan(
    int RequestedPersons, IReadOnlyList<CorrectedRequestCandidate> ToResend, IReadOnlyList<PaidPerson> Paid);

/// <summary>"Basılanlar" rozetlerinin ödeme tarafı (<see cref="CorrectedPaymentRequests.Badges"/>) — ikisi de
/// ETİKET düzeyinde.</summary>
/// <param name="RequestedLabels">Kendisini içeren (basıldıktan sonra gitmiş) teslim edilmiş isteği olan
/// etiketler ("ödeme istendi").</param>
/// <param name="BalancePaidLabels">İçeren son isteğin tamamı bakiyeden düşülmüş etiketler ("ödendi").</param>
public sealed record PaymentBadges(IReadOnlySet<string> RequestedLabels, IReadOnlySet<string> BalancePaidLabels)
{
    public static readonly PaymentBadges None =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
}

public enum ResendStatus
{
    /// <summary>Gönderildi (Cloud API).</summary>
    Sent,

    /// <summary>WhatsApp penceresi açıldı (Cloud API kapalı) — gönder tuşuna operatör basar; basmadan
    /// kapatırsa istek gitmemiş olur. "Gönderildi" sayılmaz.</summary>
    Opened,

    /// <summary>Sonuç bilinmiyor — WhatsApp'ta doğrulanmalı.</summary>
    Pending,

    /// <summary>Ek soru ya da engel var; gönderilmedi — elle bakılmalı.</summary>
    Manual,
}

public sealed record ResendOutcome(CorrectedRequestCandidate Candidate, ResendStatus Status, string Note);

/// <summary>
/// Fiyat düzeltmesinin ödeme tarafı (sahip kararı K5).
///
/// <para><b>Kim listelenir:</b> düzeltilen etiketlerin KİŞİLERİ (#506: kişi düzeyi — aynı kişinin bütün
/// satırları), yayın kapsamı (<c>session:{id}</c>) ve genel toplam (<c>cumulative</c>) ikisi de. Teslim
/// edilmiş istek ödeme sorusuyla AYNI planla bulunur (<see cref="PersonScopePlan.PromptJobs"/>): soruda
/// "istendi" denen tutar listede de aynı. Yeni toplam ödeme akışının isteyeceği tutardır (kişinin yayın
/// toplamı ya da üyelerinin toplamı + kargo).</para>
///
/// <para><b>Ödemesi alınmış</b> (etiketi dekontla kargo dosyasına bağlanmış ya da düzeltilen etiketleri
/// içeren isteğin tamamı bakiyeden düşülmüş) kişiye otomatik bir şey gitmez: dekont ve bakiyeye
/// dokunulmaz, fark yalnız gösterilir. Karar ETİKET düzeyinde: kişinin dosyaya bağlı olmayan etiketleri
/// için gitmiş istek ayrıca listelenir. Yalnız düzeltilen etiketleri içeren istekler sayılır
/// (<see cref="Plan"/>).</para>
///
/// <para><b>Başka bilgisayarların istekleri</b> (mod On; spec 2026-10-13 §6.4 "Teslim kayıtlarının okunması", §6.7, M17;
/// W3-10): son istek yerel işlerle sunucunun kayıtlarından birlikte bulunur — PC-A'nın isteği PC-B'nin rozetlerinde ve
/// planında da görünür. Uzak kaydın bir etiketi içerip içermediği sipariş kümesiyle cevaplanır; iki bilgisayarın saati
/// karşılaştırılmaz. Hesaplar zaman uyumlu ve önbellekten okur (<see cref="PrefetchAsync"/> önce bekletilir); okunamayan kişi
/// yalnız yerel kalır, hiçbir şey engellenmez — para güvenliği <c>begin</c>'in "zaten istendi" sorusundadır.</para>
///
/// <para><b>Gönderim:</b> her kişi mevcut <see cref="PaymentRequestService.RequestPaymentAsync"/> yolundan
/// geçer (kişi kilidi, revizyon, iade). Operatörün TEK onayı listedeki "tekrar gönder" sorusunun cevabıdır —
/// yalnız soru listede gösterilen isteği soruyorsa. Ek soru gerektiren (birleştirme, telefon, bakiye
/// belirsiz / çevrimdışı, başka akış sürüyor, liste bayat) ATLANIR ve "elle bakılmalı" olarak döner.</para>
/// </summary>
public sealed class CorrectedPaymentRequests
{
    public delegate Task<PaymentRequestOutcome> RequestPayment(
        Customer customer, Func<PaymentPerson, decimal?> productTotalFor, DateTime streamDate,
        string scopeKey, IReadOnlySet<string>? confirmedTokens);

    private readonly CustomerRepository _customers;
    private readonly LabelRepository _labels;
    private readonly IPaymentJobStore _jobs;
    private readonly IShippingSettingsProvider _shipping;
    private readonly RequestPayment _request;
    private readonly Func<Task<bool>>? _balanceOffline;
    private readonly RemotePaymentRecords? _remote;

    /// <param name="shipping">Yeni toplamın kargosu (spec 2026-10-13 §6.2): ödeme akışıyla aynı kaynak — mod Off'ta
    /// yerel ayar, mod On'da lisans geneli ayar. Plan, akışın isteyeceği tutarı göstermeli.</param>
    /// <param name="balanceOffline">Bakiye sunucusuna ulaşılamıyor mu
    /// (<see cref="PaymentRequestService.IsBalanceOfflineAsync"/>); null = denetlenmez.</param>
    /// <param name="remote">Başka bilgisayarların ödeme kayıtları (spec 2026-10-13 §6.4 "Teslim kayıtlarının okunması";
    /// W3-10): mod On'da rozetler, <see cref="BalancePaidLabels"/> ve <see cref="Plan"/> yerel işleri bunlarla birleştirir.
    /// null = yalnız yerel (bugünkü davranış).</param>
    public CorrectedPaymentRequests(
        CustomerRepository customers, LabelRepository labels, IPaymentJobStore jobs,
        IShippingSettingsProvider shipping, RequestPayment request, Func<Task<bool>>? balanceOffline = null,
        RemotePaymentRecords? remote = null)
    {
        _customers = customers;
        _labels = labels;
        _jobs = jobs;
        _shipping = shipping;
        _request = request;
        _balanceOffline = balanceOffline;
        _remote = remote;
    }

    /// <summary>DI kurucusu: gönderim gerçek ödeme servisinden, uzak kayıtlar tekil önbellekten.</summary>
    public CorrectedPaymentRequests(
        CustomerRepository customers, LabelRepository labels, IPaymentJobStore jobs,
        IShippingSettingsProvider shipping, PaymentRequestService payments, RemotePaymentRecords? remote = null)
        : this(customers, labels, jobs, shipping,
            (c, total, date, scope, tokens) => payments.RequestPaymentAsync(c, total, date, scope, tokens),
            () => payments.IsBalanceOfflineAsync(), remote)
    {
    }

    public static string SessionScope(string sessionId) => $"session:{sessionId}";

    public const string CumulativeScope = "cumulative";

    /// <summary>Etkilenen etiketlerin kişilerinin ödeme durumu. Düzeltmeden ÖNCE çağrılırsa sayılar
    /// özet içindir (yeni toplam henüz yazılmadı); SONRA çağrılırsa <see cref="PaymentFollowUpPlan.ToResend"/>
    /// güncel toplamlarla kurulur.
    ///
    /// <para><b>Yalnız düzeltilen etiketleri İÇEREN istek sayılır</b> (<see cref="Covers"/>): kapsamın son
    /// teslim edilmiş isteği, kişinin düzeltilen basılmış ve onaylı etiketlerinden en erkeninin basılmasından
    /// SONRA gitmiş olmalı (mod On'da başka bilgisayarın kaydıysa en az birini sipariş kümesinde taşımalı). Etiketlerden önce gitmiş istek (ör. günler önceki genel istek) yanlış fiyatı hiç
    /// içermedi: ne "eski tutarla istek gitmiş" listesine girer ne de "ödendi" kararını verir. Kuyruktaki
    /// etiket henüz hiçbir istekte yok, geçici yedek toplamlara girmez — ikisi de isteği etkilemez.</para>
    ///
    /// <para><b>"Bakiyeden ödendi" kapsam başına</b> (<see cref="IsPaidByBalance"/>): o kapsamın etiketleri
    /// içeren son isteğinin tamamı bakiyeden düşülmüş (müşteriden 0 TL istenmiş) olmalı. Kısmi düşüm
    /// ödendi sayılmaz — müşteriden kalan tutar istenmişti, o istek listeye girer (soru önceki düşümü
    /// zaten söyler). Bir kapsamdaki düşüm öbür kapsamın isteğini listeden düşürmez.</para>
    ///
    /// <para><b>"Dekontla ödendi" etiket başına:</b> dekont kaydı etiketleri (kuyruktakiler dahil) açık
    /// kargo dosyasına bağlar. Kişinin bağlı OLMAYAN basılmış etiketleri istek denetiminden ayrıca geçer: tek
    /// etiketi dekontla ödenmiş kişinin öbür etiketi için eski tutarla gitmiş istek listeden düşmez.</para>
    ///
    /// <para><b>"Ödendi, fark" ETİKET düzeyinde, yalnız bu düzeltmenin ödenmiş etiketleri:</b> etiket ödenmiş =
    /// dosyaya bağlı (dekont) ya da onu içeren (basıldıktan sonra gitmiş) son istek tamamen bakiyeden düşülmüş
    /// (<see cref="PaidByBalance"/>, rozetlerle aynı kural). Kişinin farkı Σ (yeni fiyat − ödendiği fiyat); ödendiği
    /// fiyat ödenmişken ilk düzeltmede saklanır (Label.PaidPrice — <see cref="LabelService.CorrectPrices"/>), yoksa
    /// düzeltmeden önceki fiyat. 250 → 200 → 190'da ikinci sonuç −60 der; bu düzeltmenin kendi farkı (−10)
    /// <see cref="PaidPerson.StepDelta"/>. Düzeltmeye girmeyen etiketler (başka kodlar, istekten sonra yapılmış
    /// ödenmemiş alımlar, başka yayınlar) farka hiç girmez: kişi ya da kapsam toplamından hesaplansaydı ödenmiş
    /// ama fiyatı düşen müşteri, sonra aldığı ödenmemiş ürün yüzünden "+" farkla görünürdü; operatörün önceki
    /// sonuçta çözdüğü fark da başka kodun sonucunda yeniden çıkardı.</para>
    ///
    /// <para><b>Tutar değişti mi</b> ödeme sorusuyla AYNI karşılaştırmayla (PaymentRequestService.PromptOf):
    /// son isteğin bakiye ÖNCESİ toplamı (mesajdaki net + düşüm) yeni toplamla. Net (bakiye sonrası)
    /// brüt toplamla karşılaştırılsaydı toplamı değişmeyen kişi listeye girer, aynı istek "düzeltilmiş"
    /// diye yeniden giderdi.</para></summary>
    /// <param name="affected">Düzeltilen (ya da düzeltilecek) etiketler ve fark.</param>
    public PaymentFollowUpPlan Plan(string sessionId, IEnumerable<(Label Label, decimal Delta)> affected)
    {
        var persons = new Dictionary<string, (PaymentPerson Person, List<(Label Label, decimal Delta)> Items)>(
            StringComparer.Ordinal);
        foreach (var item in affected)
        {
            var person = _customers.GetPaymentPerson(item.Label.CustomerId);
            if (!persons.TryGetValue(person.MemberKey, out var entry))
                persons[person.MemberKey] = entry = (person, new List<(Label, decimal)>());
            entry.Items.Add(item);
        }

        var shipping = _shipping.Current().Settings;
        var remote = _remote?.Snapshot();
        var toResend = new List<CorrectedRequestCandidate>();
        var paid = new List<PaidPerson>();
        var requestedPersons = 0;

        foreach (var (person, items) in persons.Values)
        {
            var display = DisplayOf(items[0].Label);
            // Dekontla ödenmiş etiketler: dosyaya bağlı (kuyruktakiler dahil).
            var attached = items.Where(i => i.Label.ShipmentId is not null).ToList();
            // İsteklerin tutarına giren, dekontla ödenmemiş etiketler: basılmış ve onaylı (müşteri toplamı
            // ve yayın toplamı yalnız bunlardan beslenir).
            var counted = items
                .Where(i => i.Label.ShipmentId is null && i.Label.PrintedAt.HasValue && !i.Label.IsTentativeBackup)
                .ToList();

            var lasts = counted.Count > 0
                ? LastDeliveredRequests(sessionId, person, remote).ToList()
                : new List<(string Scope, LastRequest Last)>();
            // Bakiyeden ödenmiş etiketler: onları içeren son istek tamamen bakiyeden düşülmüş.
            var byBalance = counted.Where(i => PaidByBalance(i.Label, lasts)).ToList();
            // Düzeltilen etiketlerden en az birini içeren son istek (yerel işte: en erkeni basıldıktan sonra gitti; uzak
            // kayıtta: sipariş kümesinde).
            var delivered = lasts
                .Where(x => counted.Any(i => x.Last.Covers(i.Label)) && !x.Last.IsPaidByBalance)
                .ToList();

            var paidItems = attached.Concat(byBalance).ToList();
            if (paidItems.Count > 0)
            {
                var paidPrices = _labels.GetPaidPrices(paidItems.Select(i => i.Label.Id).ToList());
                // Ödendiği fiyat: ödenmişken ilk düzeltmede saklanan; saklanmamışsa bu düzeltmeden önceki fiyat.
                var delta = paidItems.Sum(i =>
                    i.Label.Price + i.Delta - paidPrices.GetValueOrDefault(i.Label.Id, i.Label.Price));
                paid.Add(new PaidPerson(display, delta, StepDelta: paidItems.Sum(i => i.Delta),
                    ByDekont: attached.Count > 0, ByBalance: byBalance.Count > 0));
            }
            if (delivered.Count == 0) continue;
            requestedPersons++;

            var contact = _customers.GetById(person.LeasedId);
            if (contact is null) continue;
            contact = contact with { RecipientPaysActive = person.RecipientPaysActive };
            foreach (var (scope, last) in delivered)
            {
                // Ödeme akışının şimdi isteyeceği toplam (kargo dahil, bakiye düşülmeden önce).
                var newTotal = PaymentRequestService.ComputeShipping(
                    contact, ProductTotal(sessionId, scope, person), shipping).Total;
                // Son isteğin bakiye öncesi toplamı (ödeme sorusuyla aynı): net + hâlâ duran düşüm.
                if (newTotal == last.TotalBeforeBalance) continue;
                toResend.Add(new CorrectedRequestCandidate(
                    person.LeasedId, display, scope, last.Amount, last.At, last.Unconfirmed, newTotal,
                    PriorHadBalance: last.Applied > 0m));
            }
        }

        return new PaymentFollowUpPlan(requestedPersons, toResend, paid);
    }

    /// <summary>
    /// Kişinin yayın ve genel kapsamdaki SON teslim edilmiş (ya da gitmiş olabilecek) isteği — ödeme sorusuyla aynı plan
    /// (<see cref="PersonScopePlan.PromptJobs"/>): soruda "istendi" denen istek.
    ///
    /// <para><b>Mod On'da başka bilgisayarların kayıtlarıyla birleşik</b> (spec 2026-10-13 §6.4 "Teslim kayıtlarının
    /// okunması", §6.7; W3-10; <see cref="RemotePaymentSnapshot.For"/>): sıra sunucunun kapanış sırasıdır, yerel işin son
    /// teslimi listede bu kopyanın en yeni kaydının yerindedir (yoksa ya da yerel kaydı kuyruktaysa en yeni). İki bilgisayarın
    /// saati karşılaştırılmaz. Kişi × kapsam okunmamışsa (mod Off, bütçe, okuma düştü) yalnız yerel iş.</para>
    /// </summary>
    private IEnumerable<(string Scope, LastRequest Last)> LastDeliveredRequests(
        string sessionId, PaymentPerson person, RemotePaymentSnapshot? remote)
    {
        foreach (var scope in new[] { SessionScope(sessionId), CumulativeScope })
        {
            var (jobs, legacies) = _jobs.GetPersonScopeJobs(person.MemberIds, scope);
            PaymentJob? local = null;
            if (jobs.Count + legacies.Count > 0
                && PersonScopePlan.For(jobs, legacies, person.OwnerId).PromptJobs is { Count: > 0 } prompts)
                local = prompts[^1];
            if (remote?.For(scope, person)?.LastWith(local is not null) is { } other)
                yield return (scope, LastRequest.Of(other));
            else if (local is not null)
                yield return (scope, LastRequest.Of(local));
        }
    }

    /// <summary>İstek, <paramref name="printedAt"/>'te basılmış etiketi içeriyor mu: etiket basıldıktan sonra
    /// (aynı saniye dahil) gitti. Anı bilinmeyen gönderim temkinle içeriyor sayılır.</summary>
    internal static bool Covers(PaymentJob request, long printedAt)
        => request.PromptAt is not long at || at >= printedAt;

    /// <summary>İsteğin tamamı bakiyeden düşüldü — müşteriden hiçbir şey istenmedi. Kısmi düşüm değil.</summary>
    internal static bool IsPaidByBalance(PaymentJob request)
        => request.State == PaymentJobState.Applied && request.AppliedAmount is > 0m && request.PromptNet <= 0m;

    /// <summary>
    /// Kişi × kapsamın son isteği: bu bilgisayarın işi ya da (mod On) başka bilgisayarın kaydı. Sorular aynı tanımla cevaplanır:
    /// <list type="bullet">
    ///   <item><see cref="Covers"/> — yerel işte isteğin anı etiketin basılma anıyla (ikisi de bu bilgisayarın saati); uzak
    ///   kayıtta etiket kaydın sipariş kümesinde mi (<c>PaymentRequestRecordOrders</c>; saat karşılaştırılmaz).</item>
    ///   <item><see cref="IsPaidByBalance"/> — uzak kayıtta teslim edilmiş ∧ tamamı bakiyeden.</item>
    ///   <item>Tutar sorudaki gibi: uzak kayıtta net, yoksa (teslim kaydı olmadan dolan kira) kiranın bakiye öncesi tutarı —
    ///   o istek "gitmiş olabilir"dir.</item>
    /// </list>
    /// </summary>
    private sealed class LastRequest
    {
        private readonly PaymentJob? _local;
        private readonly PaymentRequestListItem? _remote;
        private readonly HashSet<Guid>? _orders;

        private LastRequest(PaymentJob? local, PaymentRequestListItem? remote)
        {
            _local = local;
            _remote = remote;
            if (remote is not null) _orders = remote.OrderIds.ToHashSet();
        }

        public static LastRequest Of(PaymentJob local) => new(local, null);

        public static LastRequest Of(PaymentRequestListItem remote) => new(null, remote);

        /// <summary>İstek basılmış <paramref name="label"/>'ı içeriyor mu.</summary>
        public bool Covers(Label label) => _local is not null
            ? label.PrintedAt is long printedAt && CorrectedPaymentRequests.Covers(_local, printedAt)
            : Guid.TryParse(label.Id, out var orderId) && _orders!.Contains(orderId);

        /// <summary>İsteğin tamamı bakiyeden düşüldü (müşteriden 0 TL istendi).</summary>
        public bool IsPaidByBalance => _local is not null
            ? CorrectedPaymentRequests.IsPaidByBalance(_local)
            : _remote!.State == PaymentRequestStates.Delivered && _remote.FullyFromBalance == true;

        /// <summary>Müşteriden istenen net (sorudaki tutar).</summary>
        public decimal Amount => _local?.PromptNet ?? _remote!.NetAmount ?? _remote.TotalBeforeBalance ?? 0m;

        /// <summary>İsteğin anı (yalnız gösterim): yerel işte teslim ya da deneme anı, uzak kayıtta isteğin anı.</summary>
        public DateTimeOffset? At => _local is not null
            ? _local.PromptAt is long at ? DateTimeOffset.FromUnixTimeSeconds(at) : null
            : _remote!.StartedAt;

        /// <summary>Gönderimin sonucu bilinmiyor (gitmiş olabilir).</summary>
        public bool Unconfirmed => _local?.HasUnconfirmedSend
                                   ?? (_remote!.State == PaymentRequestStates.Unconfirmed || _remote.NetAmount is null);

        /// <summary>İsteğin netinden düşülmüş, hâlâ duran bakiye.</summary>
        public decimal Applied
        {
            get
            {
                var applied = _local is not null ? _local.AppliedAmount : _remote!.AppliedAmount;
                return applied is > 0m ? applied.Value : 0m;
            }
        }

        /// <summary>İsteğin bakiye ÖNCESİ toplamı (ödeme sorusuyla aynı — PaymentRequestService.PromptOf): mesajdaki net +
        /// hâlâ duran düşüm; kirada sunucunun bildiği bakiye öncesi tutar. Tamamı bakiyeden düşülmüş istekte bu, müşterinin
        /// bakiyeden ödediği tutardır.</summary>
        public decimal TotalBeforeBalance => _local is not null
            ? _local.PromptNet + Applied
            : _remote!.TotalBeforeBalance ?? (_remote.NetAmount ?? 0m) + Applied;
    }

    /// <summary>Etiket bakiyeden ödendi: basılmış, onaylı, dosyaya bağlı değil ve kişinin bir kapsamdaki son
    /// isteği onu içeriyor (<see cref="LastRequest.Covers"/>) ve tamamı bakiyeden düşülmüş.</summary>
    private static bool PaidByBalance(Label label, IEnumerable<(string Scope, LastRequest Last)> lasts)
        => label.PrintedAt.HasValue && !label.IsTentativeBackup && label.ShipmentId is null
           && lasts.Any(x => x.Last.IsPaidByBalance && x.Last.Covers(label));

    /// <summary>Verilen etiketlerden bakiyeden ödenmiş olanlar (<see cref="PaidByBalance"/>, <see cref="Plan"/> ile
    /// aynı kural) — düzeltme yazılmadan önce okunur: ödendiği fiyat ilk düzeltmede saklansın
    /// (<see cref="LabelService.CorrectPrices"/>).</summary>
    public IReadOnlySet<string> BalancePaidLabels(string sessionId, IReadOnlyCollection<Label> labels)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var remote = _remote?.Snapshot();
        var byPerson = new Dictionary<string, List<(string Scope, LastRequest Last)>>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label.PrintedAt is null || label.IsTentativeBackup || label.ShipmentId is not null) continue;
            var person = _customers.GetPaymentPerson(label.CustomerId);
            if (!byPerson.TryGetValue(person.MemberKey, out var lasts))
                byPerson[person.MemberKey] = lasts = LastDeliveredRequests(sessionId, person, remote).ToList();
            if (PaidByBalance(label, lasts)) result.Add(label.Id);
        }
        return result;
    }

    /// <summary>"Basılanlar" rozetleri — <see cref="Plan"/> ile AYNI tanımlar, ETİKET düzeyinde: istek kişinin
    /// son isteğidir (#506, kişi düzeyi) ama rozeti yalnız o isteğin İÇERDİĞİ (etiket basıldıktan sonra
    /// gitmiş — <see cref="Covers"/>) etiket alır. "ödeme istendi" — tamamı bakiyeden düşülmemiş böyle bir
    /// istek; "ödendi" (bakiye) — tamamı bakiyeden düşülmüş böyle bir istek.
    ///
    /// <para>Kişi düzeyinde verilseydi isteğin gitmesinden SONRA basılan etiket ve günler önceki genel
    /// isteği olan müşterinin bugünkü her etiketi "ödeme istendi" görünürdü — oysa fiyat düzeltme
    /// çekmecesi aynı etiket için "istek gitmemiş" der; operatör yeni ürün için istek atmayabilirdi.
    /// Kuyruktaki etiket hiçbir istekte yok, geçici yedek toplamlara girmez: ikisi rozet almaz.</para></summary>
    public PaymentBadges Badges(string sessionId, IReadOnlyCollection<Label> labels)
    {
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var balancePaid = new HashSet<string>(StringComparer.Ordinal);
        var remote = _remote?.Snapshot();
        var byPerson = new Dictionary<string, IReadOnlyList<(string Scope, LastRequest Last)>>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label.PrintedAt is null || label.IsTentativeBackup) continue;
            var person = _customers.GetPaymentPerson(label.CustomerId);
            if (!byPerson.TryGetValue(person.MemberKey, out var lasts))
                byPerson[person.MemberKey] = lasts = LastDeliveredRequests(sessionId, person, remote).ToList();
            foreach (var (_, last) in lasts)
            {
                if (!last.Covers(label)) continue;
                if (last.IsPaidByBalance) balancePaid.Add(label.Id);
                else requested.Add(label.Id);
            }
        }
        return new PaymentBadges(requested, balancePaid);
    }

    /// <summary>
    /// Uzak kayıtların ön okuması (spec 2026-10-13 §6.4 "Teslim kayıtlarının okunması"; W3-10): <see cref="Badges"/>,
    /// <see cref="BalancePaidLabels"/> ve <see cref="Plan"/> zaman uyumlu ve yalnız önbellekten okur — çağıran önce bunu bekler,
    /// sonra (sonuç true ise) yeniden hesaplar. Verilen etiketlerin (basılmış, geçici yedek olmayan; en yeni baskı önce)
    /// kişileri, yayın ve genel kapsamda. Kişi araması da ağ işi de arka planda; istek bütçesi ve süresi
    /// <see cref="RemotePaymentRecords"/>'ta. Mod Off'ta ya da uzak kayıt kaynağı yoksa hemen false. Fırlatmaz (iptal hariç).
    /// </summary>
    /// <param name="force">Önbellekte olanları da yeniden oku (fiyat düzeltme çekmecesinin açılışı).</param>
    /// <returns>Uzak kayıtlardan biri yeni geldi ya da değişti.</returns>
    public Task<bool> PrefetchAsync(string sessionId, IEnumerable<Label> labels, bool force = false,
        CancellationToken ct = default)
    {
        if (_remote is null || !_remote.IsModeOn) return Task.FromResult(false);
        var ordered = labels.Where(l => l.PrintedAt.HasValue && !l.IsTentativeBackup)
            .OrderByDescending(l => l.PrintedAt)
            .ToList();
        if (ordered.Count == 0) return Task.FromResult(false);
        return _remote.RefreshAsync(KeysOf(sessionId, ordered), force, ct);
    }

    /// <summary>Etiketlerin kişileri, kişi başına yayın ve genel kapsam; tembel (ön okumanın arka plan iş parçacığında ve bütçe
    /// bitince durur).</summary>
    private IEnumerable<RemotePaymentKey> KeysOf(string sessionId, IReadOnlyList<Label> labels)
    {
        var seenCustomers = new HashSet<string>(StringComparer.Ordinal);
        var seenPersons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (!seenCustomers.Add(label.CustomerId)) continue;
            var person = _customers.GetPaymentPerson(label.CustomerId);
            if (!seenPersons.Add(person.MemberKey)) continue;
            yield return new RemotePaymentKey(SessionScope(sessionId), person);
            yield return new RemotePaymentKey(CumulativeScope, person);
        }
    }

    /// <summary>Toplu gönderim sürüyor mu. Aynı anda TEK toplu gönderim: fiyat düzeltme çekmecesi gönderim
    /// sürerken kapatılabilir (iş arka planda sürer); başka bir düzeltmeden başlatılan gönderim çekmecede satır
    /// içi reddedilir — aynı kişiye iki düzeltilmiş istek gitmesin.</summary>
    public bool IsResending => Volatile.Read(ref _resending) != 0;

    private int _resending;

    /// <summary>Listedekilere düzeltilmiş isteği gönderir — sırayla, her biri ödeme akışından. Ek soru
    /// gerektiren atlanır (<see cref="ResendStatus.Manual"/>). Bakiye sunucusuna ulaşılamıyorsa
    /// (çevrimdışı) HİÇBİRİ gönderilmez: akış bu durumda bakiyeyi denemeden tam tutarı isteyebilirdi.
    /// Başka bir toplu gönderim sürüyorsa (<see cref="IsResending"/>) <see cref="InvalidOperationException"/>.</summary>
    public async Task<IReadOnlyList<ResendOutcome>> ResendAsync(
        string sessionId, DateTime streamDate, IReadOnlyList<CorrectedRequestCandidate> candidates,
        IProgress<int>? progress = null)
    {
        if (Interlocked.CompareExchange(ref _resending, 1, 0) != 0)
            throw new InvalidOperationException("Başka bir düzeltilmiş istek gönderimi sürüyor.");
        try
        {
            if (_balanceOffline is not null && candidates.Count > 0 && await _balanceOffline())
                return candidates.Select(c => new ResendOutcome(c, ResendStatus.Manual,
                    "çevrimdışı — sunucuya ulaşılamıyor, bakiye denenemez")).ToList();

            var outcomes = new List<ResendOutcome>(candidates.Count);
            foreach (var c in candidates)
            {
                outcomes.Add(await ResendOneAsync(sessionId, streamDate, c));
                progress?.Report(outcomes.Count);
            }
            return outcomes;
        }
        finally
        {
            Volatile.Write(ref _resending, 0);
        }
    }

    private async Task<ResendOutcome> ResendOneAsync(string sessionId, DateTime streamDate, CorrectedRequestCandidate c)
    {
        try
        {
            var customer = _customers.GetById(c.CustomerId);
            if (customer is null) return new(c, ResendStatus.Manual, "müşteri kaydı bulunamadı");

            // Ödeme kartı/raporuyla AYNI tutar tanımı (kişinin toplamı); kiralanan satır listeninki değilse
            // (arada yerel taşıma) istek reddedilir — liste bayat.
            decimal? Total(PaymentPerson p) =>
                string.Equals(p.LeasedId, customer.Id, StringComparison.Ordinal)
                    ? ProductTotal(sessionId, c.ScopeKey, p)
                    : null;

            var outcome = await _request(customer, Total, streamDate, c.ScopeKey, null);
            // Operatörün tek onayı: listede gösterilen istek için "tekrar gönder". Soru BAŞKA bir şeyi
            // soruyorsa (birleştirme, farklı son istek) onay ona verilmiş sayılmaz. Onay sorunun BÜTÜN belirteçlerini
            // verir: mod On'da sunucunun sorusuna katılan yerel planın kapısı da geçer (W3-09).
            if (outcome.Result == PaymentRequestResult.AlreadyRequested
                && outcome.Prompt is { Kind: PersonScopePromptKind.Resend } prompt
                && prompt.Requests.Count > 0 && prompt.Requests[^1].Amount == c.PriorAmount)
                outcome = await _request(customer, Total, streamDate, c.ScopeKey,
                    new HashSet<string>(prompt.Tokens, StringComparer.Ordinal));

            return outcome.Result switch
            {
                PaymentRequestResult.Sent => new(c, ResendStatus.Sent, "gönderildi"),
                PaymentRequestResult.Opened => new(c, ResendStatus.Opened, "WhatsApp'ta açıldı — gönder tuşuna bas"),
                PaymentRequestResult.SendPending => new(c, ResendStatus.Pending,
                    "gönderim işleniyor — WhatsApp'ta ulaştığını doğrula"),
                PaymentRequestResult.MergeRequired => new(c, ResendStatus.Manual, "birden çok istek var, birleştirme sorusu gerekiyor"),
                PaymentRequestResult.AlreadyRequested => new(c, ResendStatus.Manual, "istek bu arada değişti"),
                PaymentRequestResult.PhoneRequired => new(c, ResendStatus.Manual, "WhatsApp numarası gerekli"),
                PaymentRequestResult.BalanceUncertain => new(c, ResendStatus.Manual,
                    "bakiye doğrulanamadı (sunucuya ulaşılamıyor olabilir)"),
                PaymentRequestResult.PaymentInProgress => new(c, ResendStatus.Manual,
                    outcome.Message ?? "başka bir pencerede ödeme isteği sürüyor"),
                // W3-07 (spec 2026-10-13 §6.4): mod On'un engeli — elle bakılmalı, neden satırda.
                PaymentRequestResult.Blocked => new(c, ResendStatus.Manual,
                    outcome.Message ?? "ödeme isteği engellendi"),
                PaymentRequestResult.ListStale => new(c, ResendStatus.Manual, "müşteri kaydı bilgisayarlar arası birleştirildi"),
                PaymentRequestResult.LaunchFailed => new(c, ResendStatus.Manual, "WhatsApp açılamadı"),
                _ => new(c, ResendStatus.Manual, outcome.Result.ToString()),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(c, ResendStatus.Manual, $"gönderilemedi: {ex.Message}");
        }
    }

    /// <summary>Ödeme akışının isteyeceği ürün toplamı: yayın kapsamında kişinin bu yayındaki, genel kapsamda bütün
    /// yayınlardaki basılmış etiketleri (CustomerSearchViewModel.CurrentAmount ile aynı). İkisi de etiketlerden okunur
    /// (W3, spec 2026-10-13 §6.1): Customer.TotalAmount önbelleği okunmaz.</summary>
    private decimal ProductTotal(string sessionId, string scopeKey, PaymentPerson person)
        => _labels.GetPrintedTotal(person.MemberIds.ToList(),
            scopeKey.StartsWith("session:", StringComparison.Ordinal) ? sessionId : null).Total;

    private static string DisplayOf(Label l) => string.IsNullOrWhiteSpace(l.DisplayName) ? l.Username : l.DisplayName!;
}
