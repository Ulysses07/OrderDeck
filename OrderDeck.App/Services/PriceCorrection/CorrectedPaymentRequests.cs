using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;

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

    /// <param name="shipping">Yeni toplamın kargosu (spec 2026-10-13 §6.2): ödeme akışıyla aynı kaynak — mod Off'ta
    /// yerel ayar, mod On'da lisans geneli ayar. Plan, akışın isteyeceği tutarı göstermeli.</param>
    /// <param name="balanceOffline">Bakiye sunucusuna ulaşılamıyor mu
    /// (<see cref="PaymentRequestService.IsBalanceOfflineAsync"/>); null = denetlenmez.</param>
    public CorrectedPaymentRequests(
        CustomerRepository customers, LabelRepository labels, IPaymentJobStore jobs,
        IShippingSettingsProvider shipping, RequestPayment request, Func<Task<bool>>? balanceOffline = null)
    {
        _customers = customers;
        _labels = labels;
        _jobs = jobs;
        _shipping = shipping;
        _request = request;
        _balanceOffline = balanceOffline;
    }

    /// <summary>DI kurucusu: gönderim gerçek ödeme servisinden.</summary>
    public CorrectedPaymentRequests(
        CustomerRepository customers, LabelRepository labels, IPaymentJobStore jobs,
        IShippingSettingsProvider shipping, PaymentRequestService payments)
        : this(customers, labels, jobs, shipping,
            (c, total, date, scope, tokens) => payments.RequestPaymentAsync(c, total, date, scope, tokens),
            () => payments.IsBalanceOfflineAsync())
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
    /// SONRA gitmiş olmalı. Etiketlerden önce gitmiş istek (ör. günler önceki genel istek) yanlış fiyatı hiç
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
                ? LastDeliveredRequests(sessionId, person).ToList()
                : new List<(string Scope, PaymentJob Last)>();
            // Bakiyeden ödenmiş etiketler: onları içeren son istek tamamen bakiyeden düşülmüş.
            var byBalance = counted.Where(i => PaidByBalance(i.Label, lasts)).ToList();
            var delivered = new List<(string Scope, PaymentJob Last)>();
            if (counted.Count > 0)
            {
                var earliestPrint = counted.Min(i => i.Label.PrintedAt!.Value);
                foreach (var (scope, last) in lasts)
                    if (Covers(last, earliestPrint) && !IsPaidByBalance(last)) delivered.Add((scope, last));
            }

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
                var priorBalance = last.AppliedAmount is > 0m ? last.AppliedAmount.Value : 0m;
                if (newTotal == PriorTotal(last)) continue;
                toResend.Add(new CorrectedRequestCandidate(
                    person.LeasedId, display, scope, last.PromptNet,
                    last.PromptAt is long at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
                    last.HasUnconfirmedSend, newTotal, PriorHadBalance: priorBalance > 0m));
            }
        }

        return new PaymentFollowUpPlan(requestedPersons, toResend, paid);
    }

    /// <summary>Kişinin yayın ve genel kapsamdaki SON teslim edilmiş (ya da gitmiş olabilecek) isteği —
    /// ödeme sorusuyla aynı plan (<see cref="PersonScopePlan.PromptJobs"/>): soruda "istendi" denen istek.</summary>
    private IEnumerable<(string Scope, PaymentJob Last)> LastDeliveredRequests(string sessionId, PaymentPerson person)
    {
        foreach (var scope in new[] { SessionScope(sessionId), CumulativeScope })
        {
            var (jobs, legacies) = _jobs.GetPersonScopeJobs(person.MemberIds, scope);
            if (jobs.Count + legacies.Count == 0) continue;
            var prompts = PersonScopePlan.For(jobs, legacies, person.OwnerId).PromptJobs;
            if (prompts.Count > 0) yield return (scope, prompts[^1]);
        }
    }

    /// <summary>İstek, <paramref name="printedAt"/>'te basılmış etiketi içeriyor mu: etiket basıldıktan sonra
    /// (aynı saniye dahil) gitti. Anı bilinmeyen gönderim temkinle içeriyor sayılır.</summary>
    internal static bool Covers(PaymentJob request, long printedAt)
        => request.PromptAt is not long at || at >= printedAt;

    /// <summary>İsteğin tamamı bakiyeden düşüldü — müşteriden hiçbir şey istenmedi. Kısmi düşüm değil.</summary>
    internal static bool IsPaidByBalance(PaymentJob request)
        => request.State == PaymentJobState.Applied && request.AppliedAmount is > 0m && request.PromptNet <= 0m;

    /// <summary>Etiket bakiyeden ödendi: basılmış, onaylı, dosyaya bağlı değil ve kişinin bir kapsamdaki son
    /// isteği onu içeriyor (<see cref="Covers"/>) ve tamamı bakiyeden düşülmüş.</summary>
    private static bool PaidByBalance(Label label, IEnumerable<(string Scope, PaymentJob Last)> lasts)
        => label.PrintedAt is long printedAt && !label.IsTentativeBackup && label.ShipmentId is null
           && lasts.Any(x => IsPaidByBalance(x.Last) && Covers(x.Last, printedAt));

    /// <summary>Verilen etiketlerden bakiyeden ödenmiş olanlar (<see cref="PaidByBalance"/>, <see cref="Plan"/> ile
    /// aynı kural) — düzeltme yazılmadan önce okunur: ödendiği fiyat ilk düzeltmede saklansın
    /// (<see cref="LabelService.CorrectPrices"/>).</summary>
    public IReadOnlySet<string> BalancePaidLabels(string sessionId, IReadOnlyCollection<Label> labels)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var byPerson = new Dictionary<string, List<(string Scope, PaymentJob Last)>>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label.PrintedAt is null || label.IsTentativeBackup || label.ShipmentId is not null) continue;
            var person = _customers.GetPaymentPerson(label.CustomerId);
            if (!byPerson.TryGetValue(person.MemberKey, out var lasts))
                byPerson[person.MemberKey] = lasts = LastDeliveredRequests(sessionId, person).ToList();
            if (PaidByBalance(label, lasts)) result.Add(label.Id);
        }
        return result;
    }

    /// <summary>İsteğin bakiye ÖNCESİ toplamı (ödeme sorusuyla aynı — PaymentRequestService.PromptOf): mesajdaki
    /// net + hâlâ duran düşüm. Tamamı bakiyeden düşülmüş istekte bu, müşterinin bakiyeden ödediği tutardır.</summary>
    private static decimal PriorTotal(PaymentJob request)
        => request.PromptNet + (request.AppliedAmount is > 0m ? request.AppliedAmount.Value : 0m);

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
        var byPerson = new Dictionary<string, IReadOnlyList<(string Scope, PaymentJob Last)>>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label.PrintedAt is not long printedAt || label.IsTentativeBackup) continue;
            var person = _customers.GetPaymentPerson(label.CustomerId);
            if (!byPerson.TryGetValue(person.MemberKey, out var lasts))
                byPerson[person.MemberKey] = lasts = LastDeliveredRequests(sessionId, person).ToList();
            foreach (var (_, last) in lasts)
            {
                if (!Covers(last, printedAt)) continue;
                if (IsPaidByBalance(last)) balancePaid.Add(label.Id);
                else requested.Add(label.Id);
            }
        }
        return new PaymentBadges(requested, balancePaid);
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
            // soruyorsa (birleştirme, farklı son istek) onay ona verilmiş sayılmaz.
            if (outcome.Result == PaymentRequestResult.AlreadyRequested
                && outcome.Prompt is { Kind: PersonScopePromptKind.Resend } prompt
                && prompt.Requests.Count > 0 && prompt.Requests[^1].Amount == c.PriorAmount)
                outcome = await _request(customer, Total, streamDate, c.ScopeKey,
                    new HashSet<string>(StringComparer.Ordinal) { prompt.Token });

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
                PaymentRequestResult.PaymentInProgress => new(c, ResendStatus.Manual, "başka bir pencerede ödeme isteği sürüyor"),
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

    /// <summary>Ödeme akışının isteyeceği ürün toplamı: yayın kapsamında kişinin bu yayındaki basılmış
    /// toplamı, genel kapsamda üyelerin müşteri toplamı (CustomerSearchViewModel.CurrentAmount ile aynı).</summary>
    private decimal ProductTotal(string sessionId, string scopeKey, PaymentPerson person)
        => scopeKey.StartsWith("session:", StringComparison.Ordinal)
            ? _labels.GetSessionPrintedTotal(sessionId, person.MemberIds.ToList())
            : person.Members.Sum(m => m.TotalAmount);

    private static string DisplayOf(Label l) => string.IsNullOrWhiteSpace(l.DisplayName) ? l.Username : l.DisplayName!;
}
