using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Mod Off'ta her teslim ya da belirsiz gönderimden sonra yazılan kirasız ödeme kaydı (spec 2026-10-13 §6.4 "Ödeme
/// geçmişi (mod Off'ta da)", §2 mod kapısı Off satırı, §8.5 tablosu, M17; W3-06 (b)). Kayıt yalnız
/// <c>PendingPaymentRecord</c> kuyruğuna yazılır; <see cref="PaymentRecordSender"/> geri doldurma turunda
/// <c>payment-requests/backfill</c>'e götürür. Gönderim isteği bekletmez, ağa çıkmaz: bugünkü çevrimdışı davranış
/// değişmez. Mod On açılınca öbür bilgisayarın sorusu ve rozetleri bu kayıtları da görür.
///
/// <para><b>Çağıran</b> <c>PaymentRequestService</c>'in gönderim çıkışlarıdır: <c>Sent</c>/<c>Opened</c> → teslim,
/// <c>SendPending</c> ve Cloud API'nin sonucu bilinmeden wa.me'ye düştüğü <c>LaunchFailed</c> → belirsiz; düz
/// <c>LaunchFailed</c> (mesaj kesin gitmedi) → kayıt yok. Mod kapısı (<see cref="IsModeOff"/>) çağıranındır: mod On'un
/// çevrimdışı GUID'siz yerel yolu (W3-07) da aynı kaydı yazar.</para>
///
/// <para><b>Kayıt</b>:
/// <list type="bullet">
///   <item><b>İşli kişi</b> (GUID'li sahip, bakiye akışının işi): ödeme geçmişiyle AYNI türetme
///   (<see cref="PaymentHistoryRecords.FromJob"/>, işin teslimden sonraki satırı) — Id iş ile teslim ya da deneme
///   sayısından; geri doldurma aynı işi sonradan görse de aynı Id'yi üretir. <c>SendPending</c>'de teslim yazılmış olsa
///   da kayıt belirsizdir (Id deneme sayısıyla).</item>
///   <item><b>İşsiz (GUID'siz) kişi</b>: rastgele Id (yükte saklanır, yinelenen gönderim aynı Id'yi taşır); sahip kişinin
///   belirlenimli sahibi (<see cref="PaymentOwnerRule.Order"/>'ın ilki — iki bilgisayar aynı satırı seçer); net
///   mesajdaki tutar, düşüm yok.</item>
///   <item>Üyeler kişinin bütün üyeleri (sahip önce, ≤ 100); <c>orderIds</c> isteğin
///   <see cref="LabelRepository.GetPrintedTotal"/> Id'leri (kişinin o kapsamdaki katkı veren etiketleri) — işli kişide
///   ödeme geçmişinin <c>Covers</c> kuralıyla (<c>PrintedAt ≤ PromptAt</c>, <see cref="PaymentHistoryRecords.PromptAtOf"/>).</item>
/// </list>
/// Lisans anahtarı yoksa yazılmaz (bakiye ve senkron özelliği kapalı). Kapsamı ya da sahibi tel biçimine uymayan istek
/// yazılmaz (sunucunun 400'ü kuyruğu kilitlerdi). Hiçbir hata fırlatılmaz: mesaj zaten gitti, kaydın yazılamaması
/// gönderimi düşürmemeli (uyarı günlüğü). Günlük yalnız Id taşır (tutar, ad yok).</para>
/// </summary>
public sealed class PaymentOffRecordWriter
{
    private readonly PaymentRecordSender _sender;
    private readonly SalesSyncStateRepository _state;
    private readonly LabelRepository _labels;
    private readonly ICurrentLicenseProvider _license;
    private readonly IClock _clock;
    private readonly ILogger<PaymentOffRecordWriter> _log;

    public PaymentOffRecordWriter(
        PaymentRecordSender sender,
        SalesSyncStateRepository state,
        LabelRepository labels,
        ICurrentLicenseProvider license,
        IClock clock,
        ILogger<PaymentOffRecordWriter> log)
    {
        _sender = sender;
        _state = state;
        _labels = labels;
        _license = license;
        _clock = clock;
        _log = log;
    }

    /// <summary>Yerel kip Off mu (<c>SalesSyncState.Mode = 0</c>; satır yoksa Off).</summary>
    public bool IsModeOff() => _state.GetMode() == 0;

    /// <summary>
    /// Gönderimin kirasız kaydını kuyruğa yazar (sınıf belgesi). Kip denetlenmez — çağıran karar verir.
    /// </summary>
    /// <param name="person">İsteğin kişisi (kira altında okunan güncel üyeler).</param>
    /// <param name="job">Bakiye akışının işi, teslim ya da deneme yazıldıktan SONRA yeniden okunmuş hâli; işsiz
    /// (GUID'siz) kişide null.</param>
    /// <param name="scopeKey">İsteğin kapsamı (<c>session:{id}</c> ya da <c>cumulative</c>).</param>
    /// <param name="unconfirmed">Sonuç bilinmiyor: <c>SendPending</c> ya da Cloud API'den sonra açılamayan wa.me.</param>
    /// <param name="netAmount">Mesajdaki net tutar (işsiz kişide kaydın tutarı; işli kişide işin satırı okunur).</param>
    /// <returns>Kuyruğa yazılan kaydın Id'si; yazılmadıysa (lisans yok, biçim uymuyor, zaten kuyrukta, hata) null.</returns>
    public Guid? Record(PaymentPerson person, PaymentJob? job, string scopeKey, bool unconfirmed, decimal netAmount)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_license.CurrentLicenseKey)) return null;
            ArgumentNullException.ThrowIfNull(person);

            var scope = job?.SaleScopeKey ?? scopeKey;
            if (!PaymentHistoryRecords.IsRecordScope(scope))
            {
                _log.LogWarning("Mod Off ödeme kaydı yazılmadı: kapsam kayıt biçimine uymuyor (job={JobId})", job?.Id);
                return null;
            }

            // İşli kişide sahip işin satırı; işsiz kişide kişinin belirlenimli sahibi (GUID'siz üyeler sahip kuralı
            // sırasıyla dizili: ilk üye).
            var owner = job?.CustomerId ?? person.OwnerId ?? person.Members.FirstOrDefault()?.Id;
            if (owner is null) return null;
            var localMembers = new List<string>(person.MemberIds.Count + 1) { owner };
            localMembers.AddRange(person.MemberIds);
            var members = PaymentHistoryRecords.WireMembers(owner, localMembers);
            if (members is null)
            {
                _log.LogWarning("Mod Off ödeme kaydı yazılmadı: sahip Id'si tel biçimine sığmıyor (job={JobId})", job?.Id);
                return null;
            }

            // İşli kişide ödeme geçmişiyle AYNI Covers kuralı (PrintedAt ≤ PromptAt): aynı Id iki yazıcıda aynı kümeyi
            // taşısın — farklı küme sunucuda content-conflict olurdu, kayıttan sonra basılan etiket de "istendi" sayılırdı.
            IReadOnlyList<Guid> orders = [];
            if (PaymentHistoryRecords.IsLeaseScope(scope))
                orders = PaymentHistoryRecords.WireOrders(_labels.GetPrintedTotal(
                    localMembers.Distinct(StringComparer.Ordinal).ToList(), PaymentHistoryRecords.SessionIdOf(scope),
                    job is null ? null : PaymentHistoryRecords.PromptAtOf(job))
                    .LabelIds);

            var item = job is not null
                ? PaymentHistoryRecords.FromJob(job, members, orders, forceUnconfirmed: unconfirmed)
                : new PaymentRequestBackfillItem(
                    RecordId: Guid.NewGuid(),
                    OwnerId: owner,
                    MemberIds: members,
                    ScopeKey: scope,
                    OrderIds: orders,
                    State: unconfirmed ? PaymentRequestStates.Unconfirmed : PaymentRequestStates.Delivered,
                    NetAmount: PaymentHistoryRecords.Money(netAmount),
                    AppliedAmount: null,
                    FullyFromBalance: false,
                    At: DateTimeOffset.FromUnixTimeSeconds(_clock.UnixNow()));

            if (!_sender.EnqueueRecord(item)) return null;
            _log.LogInformation("Mod Off ödeme kaydı kuyruğa alındı (record={RecordId}, job={JobId}, state={State})",
                item.RecordId, job?.Id, item.State);
            return item.RecordId;
        }
        catch (Exception ex)
        {
            // Mesaj gitti; kaydın yazılamaması gönderimi düşürmez.
            _log.LogWarning(ex, "Mod Off ödeme kaydı yazılamadı (job={JobId})", job?.Id);
            return null;
        }
    }
}
