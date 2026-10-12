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
/// <param name="NewTotal">Şimdi istenecek toplam (kargo dahil).</param>
public sealed record CorrectedRequestCandidate(
    string CustomerId, string Display, string ScopeKey,
    decimal PriorAmount, DateTimeOffset? PriorAt, bool PriorUnconfirmed, decimal NewTotal)
{
    public bool IsSessionScope => ScopeKey.StartsWith("session:", StringComparison.Ordinal);
}

/// <summary>Ödemesi alınmış kişi (dekont kaydedilmiş ya da bakiyeden düşülmüş): OTOMATİK bir şey gitmez,
/// "ödendi, fark ±X TL" olarak gösterilir (K5). <see cref="Delta"/> kişinin düzeltilen basılmış ve onaylı
/// etiketlerinin fark toplamı.</summary>
public sealed record PaidPerson(string Display, decimal Delta, bool ByDekont, bool ByBalance);

/// <summary>Düzeltmeden etkilenen kişilerin ödeme durumu.</summary>
/// <param name="RequestedPersons">Teslim edilmiş isteği olan, ödemesi alınmamış kişi sayısı.</param>
/// <param name="ToResend">Bunlardan isteğinin tutarı yeni toplamdan farklı olan kişi × kapsamlar.</param>
public sealed record PaymentFollowUpPlan(
    int RequestedPersons, IReadOnlyList<CorrectedRequestCandidate> ToResend, IReadOnlyList<PaidPerson> Paid);

public enum ResendStatus
{
    /// <summary>Gönderildi (Cloud API) ya da WhatsApp penceresi açıldı.</summary>
    Sent,

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
/// <para><b>Ödemesi alınmış</b> (etiketi dekontla kargo dosyasına bağlanmış ya da bu kapsamlarda bakiyeden
/// düşülmüş) kişiye otomatik bir şey gitmez: dekont ve bakiyeye dokunulmaz, fark yalnız gösterilir.</para>
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
    private readonly Func<AppSettings> _settings;
    private readonly RequestPayment _request;

    public CorrectedPaymentRequests(
        CustomerRepository customers, LabelRepository labels, IPaymentJobStore jobs,
        Func<AppSettings> settings, RequestPayment request)
    {
        _customers = customers;
        _labels = labels;
        _jobs = jobs;
        _settings = settings;
        _request = request;
    }

    /// <summary>DI kurucusu: gönderim gerçek ödeme servisinden.</summary>
    public CorrectedPaymentRequests(
        CustomerRepository customers, LabelRepository labels, IPaymentJobStore jobs,
        SettingsStore settings, PaymentRequestService payments)
        : this(customers, labels, jobs, settings.Load,
            (c, total, date, scope, tokens) => payments.RequestPaymentAsync(c, total, date, scope, tokens))
    {
    }

    public static string SessionScope(string sessionId) => $"session:{sessionId}";

    public const string CumulativeScope = "cumulative";

    /// <summary>Etkilenen etiketlerin kişilerinin ödeme durumu. Düzeltmeden ÖNCE çağrılırsa sayılar
    /// özet içindir (yeni toplam henüz yazılmadı); SONRA çağrılırsa <see cref="PaymentFollowUpPlan.ToResend"/>
    /// güncel toplamlarla kurulur.</summary>
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

        var settings = _settings();
        var scopes = new[] { SessionScope(sessionId), CumulativeScope };
        var toResend = new List<CorrectedRequestCandidate>();
        var paid = new List<PaidPerson>();
        var requestedPersons = 0;

        foreach (var (person, items) in persons.Values)
        {
            var display = DisplayOf(items[0].Label);
            var byDekont = items.Any(i => i.Label.ShipmentId is not null);
            var byBalance = false;
            var delivered = new List<(string Scope, PaymentJob Last)>();
            foreach (var scope in scopes)
            {
                var (jobs, legacies) = _jobs.GetPersonScopeJobs(person.MemberIds, scope);
                if (jobs.Count + legacies.Count == 0) continue;
                if (jobs.Any(j => j.State == PaymentJobState.Applied && j.AppliedAmount is > 0m)) byBalance = true;
                var prompts = PersonScopePlan.For(jobs, legacies, person.OwnerId).PromptJobs;
                if (prompts.Count > 0) delivered.Add((scope, prompts[^1]));
            }

            if (byDekont || byBalance)
            {
                var delta = items.Where(i => i.Label.PrintedAt.HasValue && !i.Label.IsTentativeBackup)
                    .Sum(i => i.Delta);
                paid.Add(new PaidPerson(display, delta, byDekont, byBalance));
                continue;
            }
            if (delivered.Count == 0) continue;
            requestedPersons++;

            var contact = _customers.GetById(person.LeasedId);
            if (contact is null) continue;
            contact = contact with { RecipientPaysActive = person.RecipientPaysActive };
            foreach (var (scope, last) in delivered)
            {
                var productTotal = ProductTotal(sessionId, scope, person);
                var newTotal = PaymentRequestService.ComputeShipping(contact, productTotal, settings).Total;
                if (newTotal == last.PromptNet) continue;
                toResend.Add(new CorrectedRequestCandidate(
                    person.LeasedId, display, scope, last.PromptNet,
                    last.PromptAt is long at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
                    last.HasUnconfirmedSend, newTotal));
            }
        }

        return new PaymentFollowUpPlan(requestedPersons, toResend, paid);
    }

    /// <summary>"Basılanlar" rozeti ("ödeme istendi"): verilen müşterilerden KİŞİSİNE bu yayın ya da genel
    /// toplam için istek gitmiş (ya da gitmiş olabilir) olanlar — soruyla aynı tanım
    /// (<see cref="PersonScopePlan.PromptJobs"/>).</summary>
    public IReadOnlySet<string> CustomersWithDeliveredRequest(string sessionId, IReadOnlyCollection<string> customerIds)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var byPerson = new Dictionary<string, bool>(StringComparer.Ordinal);
        var scopes = new[] { SessionScope(sessionId), CumulativeScope };
        foreach (var id in customerIds)
        {
            var person = _customers.GetPaymentPerson(id);
            if (!byPerson.TryGetValue(person.MemberKey, out var requested))
            {
                requested = scopes.Any(scope =>
                {
                    var (jobs, legacies) = _jobs.GetPersonScopeJobs(person.MemberIds, scope);
                    return jobs.Count + legacies.Count > 0
                           && PersonScopePlan.For(jobs, legacies, person.OwnerId).PromptJobs.Count > 0;
                });
                byPerson[person.MemberKey] = requested;
            }
            if (requested) result.Add(id);
        }
        return result;
    }

    /// <summary>Listedekilere düzeltilmiş isteği gönderir — sırayla, her biri ödeme akışından. Ek soru
    /// gerektiren atlanır (<see cref="ResendStatus.Manual"/>).</summary>
    public async Task<IReadOnlyList<ResendOutcome>> ResendAsync(
        string sessionId, DateTime streamDate, IReadOnlyList<CorrectedRequestCandidate> candidates,
        IProgress<int>? progress = null)
    {
        var outcomes = new List<ResendOutcome>(candidates.Count);
        foreach (var c in candidates)
        {
            outcomes.Add(await ResendOneAsync(sessionId, streamDate, c));
            progress?.Report(outcomes.Count);
        }
        return outcomes;
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
                PaymentRequestResult.Opened => new(c, ResendStatus.Sent, "WhatsApp'ta açıldı — gönder tuşuna bas"),
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
