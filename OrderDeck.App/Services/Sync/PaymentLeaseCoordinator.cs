using Microsoft.Extensions.Logging;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Ödeme isteği kirasının (spec 2026-10-13 §6.4; W3) operatöre görünen satır içi metinleri.</summary>
public static class PaymentLeaseMessages
{
    private static readonly System.Globalization.CultureInfo Tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>§6.4-1: bu kişinin bu kapsamdaki bir etiketinin bekleyen komutu (kalıntı kuyruğun iptali ya da ilk baskısı)
    /// ön gönderimde gönderilemedi.</summary>
    public const string PendingCommandBlocked =
        "Bekleyen baskı ya da iptal onayı gönderilemedi; bağlantı gelince tekrar dene.";

    /// <summary><c>in-progress-elsewhere</c> (§6.4): "PC-A'da bu kişiye ödeme isteniyor". Makine adı bilinmiyorsa
    /// (kilit zaman aşımı) null — bugünkü metin gösterilir.</summary>
    public static string? InProgressElsewhere(string? machine) =>
        string.IsNullOrWhiteSpace(machine)
            ? null
            : $"{SalesCommandMessages.Locative(machine)} bu kişiye ödeme isteniyor — biraz sonra tekrar deneyin.";

    /// <summary><c>members-stale</c> (§6.4).</summary>
    public const string MembersStale = "Müşteri grupları eşitleniyor; biraz sonra tekrar dene.";

    /// <summary><c>stale-total</c> (§6.4): "Bu bilgisayar güncel değil (sunucu X TL, burada Y TL)".</summary>
    public static string NotCurrent(decimal serverTotal, decimal localTotal) =>
        $"Bu bilgisayar güncel değil (sunucu {Money(serverTotal)}, burada {Money(localTotal)}).";

    /// <summary><c>stale-total</c>'ın <c>liveElsewhere</c>'i (§6.4): "Bu kişinin PC-A'da süren yayında etiketi var; istek
    /// yayın bitip devredilince yapılabilir."</summary>
    public static string LiveElsewhere(string? machine) =>
        $"Bu kişinin {(string.IsNullOrWhiteSpace(machine) ? "başka bir bilgisayarda" : SalesCommandMessages.Locative(machine))} "
      + "süren yayında etiketi var; istek yayın bitip devredilince yapılabilir.";

    /// <summary><c>settings-stale</c> (§6.2, §6.4).</summary>
    public const string SettingsStale = "Kargo ayarı bu arada değişti; biraz sonra tekrar dene.";

    /// <summary>Mod Off yolunda yayın kapsamının oturumu bu bilgisayarda yok ya da başka bilgisayarın (uzak) yayını: Off turu
    /// uzak yayınları sildi, açık kalmış rapor ya da liste o yayını gösteriyor (<see cref="PaymentLeaseCoordinator.CheckModeOffScope"/>).</summary>
    public const string SessionNotLocal = "Senkron kapatıldı; bu yayın bu bilgisayarda değil.";

    /// <summary>Uygulamanın öbür tutar metinleriyle aynı biçim: iki kuruş hanesi + "TL".</summary>
    private static string Money(decimal amount) => amount.ToString("N2", Tr) + " TL";
}

/// <summary>
/// Kiranın yeniden denenen retleri (spec 2026-10-13 §6.4 "409'ların istemcideki karşılığı"; W3-08): akış kişiyi bırakır,
/// bir tur koşar ve bütün akış AYNI onay belirteçleriyle bir kez yeniden koşar
/// (<see cref="PaymentLeaseCoordinator.RunRetryAsync"/>). Bayraklar bir isteğin harcadığı yeniden denemeleri de tutar.
/// </summary>
[Flags]
public enum PaymentLeaseRetry
{
    None = 0,

    /// <summary><c>members-stale</c>: bir müşteri akışı turu, kişi yeniden kurulur.</summary>
    CustomerTour = 1,

    /// <summary><c>stale-total</c>: bir sınırlı satış akışı turu, tutar yeniden okunur. Hakkı <see cref="CustomerTour"/>'dan
    /// ayrıdır (§6.4: her biri "bir tur ve bir kez yeniden deneme").</summary>
    SalesTour = 2,

    /// <summary><c>mode-off</c>: Off turu (§8.5) sınırlı süre beklenir; bittiyse istek mod Off yolunda yeniden kurulur
    /// (yerel toplam, kira yok, kirasız kayıt).</summary>
    ModeOffRound = 4,
}

/// <summary>
/// "Tekrar gönderilsin mi?" sorusunun UZAK öncüller için onayı (spec 2026-10-13 §6.4 "409'ların istemcideki karşılığı",
/// PR #523 eki): sunucunun <c>already-requested</c>'ındaki öncül kümesinin özeti (<c>priorsDigest</c>) ve öncül Id'leri.
/// <see cref="PaymentPrompt.Token"/> olarak taşınır (<see cref="Token"/>); operatör onaylayınca belirteç kümesiyle geri gelir
/// (<see cref="From"/>) ve <c>begin</c>'e iki alanla gider: <c>confirmedPriorsDigest</c> aynen, <c>confirmedPriorIds</c> en
/// çok <see cref="PaymentRequestLimits.MaxConfirmedPriorIds"/> Id (fazlası gönderilmez — bütün kümeyi özet onaylar).
///
/// <para>Akışın yeniden koşuları (<c>members-stale</c>, <c>stale-total</c>, <c>settings-stale</c>) aynı belirteç kümesiyle
/// koştuğu için özet de Id listesi de korunur; sunucu her istekte kümeyi yeniden karşılaştırır, küme değiştiyse yeniden
/// sorar. Birden çok uzak onay varsa (soru onaydan sonra yeniden geldi) en yenisi geçerlidir: belirteç süreç içi bir sıra
/// numarası taşır. Belirteci soru kurar (W3-09); bozuk belirteç onay sayılmaz.</para>
/// </summary>
/// <param name="PriorsDigest">409'un <c>priorsDigest</c>'i, aynen; #523 öncesi sunucuda null.</param>
/// <param name="PriorIds">Sorunun öncülleri (sunucunun sırasıyla).</param>
/// <param name="Sequence">Süreç içi sıra: büyük olan yenidir.</param>
public sealed record PaymentLeaseConfirmation(string? PriorsDigest, IReadOnlyList<Guid> PriorIds, long Sequence = 0)
{
    private const string Prefix = "kira-onay:";
    private static long _lastSequence;

    public static PaymentLeaseConfirmation None { get; } = new(null, []);

    /// <summary>Yeni onay; sıra numarası süreç içinde artar. Id'lerin en çok
    /// <see cref="PaymentRequestLimits.MaxConfirmedPriorIds"/>'ü tutulur — sorunun sırasıyla ilkleri (en eskiler): onay
    /// turunda küme değişirse sunucunun taze sorusu listenin dışındaki, yani en yeni öncülleri gösterir.</summary>
    public static PaymentLeaseConfirmation Create(string? priorsDigest, IEnumerable<Guid> priorIds) =>
        new(string.IsNullOrEmpty(priorsDigest) ? null : priorsDigest,
            priorIds.Distinct().Take(PaymentRequestLimits.MaxConfirmedPriorIds).ToList(),
            Interlocked.Increment(ref _lastSequence));

    /// <summary>Onay belirteci (<see cref="PaymentPrompt.Token"/>): <c>kira-onay:{sıra}:{özet}:{Id,…}</c>. Özet base64url
    /// olduğu için ':' taşımaz; Id'ler "N" hex.</summary>
    public string Token =>
        $"{Prefix}{Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{PriorsDigest}:"
      + string.Join(',', PriorIds.Select(id => id.ToString("N")));

    /// <summary><c>begin</c>'in <c>confirmedPriorIds</c>'i: en çok <see cref="PaymentRequestLimits.MaxConfirmedPriorIds"/>
    /// Id, fazlası kesilir.</summary>
    public IReadOnlyList<Guid> WirePriorIds =>
        PriorIds.Count <= PaymentRequestLimits.MaxConfirmedPriorIds
            ? PriorIds
            : PriorIds.Take(PaymentRequestLimits.MaxConfirmedPriorIds).ToList();

    /// <summary>Onaylanan belirteçlerdeki en yeni uzak onay; yoksa <see cref="None"/>.</summary>
    public static PaymentLeaseConfirmation From(IReadOnlySet<string>? confirmedTokens)
    {
        var newest = None;
        if (confirmedTokens is null) return newest;
        foreach (var token in confirmedTokens)
            if (TryParse(token, out var parsed) && parsed.Sequence > newest.Sequence) newest = parsed;
        return newest;
    }

    private static bool TryParse(string token, out PaymentLeaseConfirmation confirmation)
    {
        confirmation = None;
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var body = token.AsSpan(Prefix.Length);
        var first = body.IndexOf(':');
        var last = body.LastIndexOf(':');
        if (first < 0 || last <= first) return false;
        if (!long.TryParse(body[..first], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence <= 0)
            return false;
        var digest = body[(first + 1)..last].ToString();
        var ids = new List<Guid>();
        foreach (var part in body[(last + 1)..].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParseExact(part, "N", out var id)) return false;
            ids.Add(id);
        }
        confirmation = new PaymentLeaseConfirmation(digest.Length == 0 ? null : digest, ids, sequence);
        return true;
    }
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
/// <param name="Confirmation">Operatörün onayladığı uzak öncüller (<see cref="PaymentLeaseConfirmation.From"/>); yoksa
/// onay alanları boş gider.</param>
public sealed record PaymentLeaseBeginInput(
    PaymentPerson Person,
    string ScopeKey,
    IReadOnlyList<Guid> OrderIds,
    decimal ProductTotal,
    decimal TotalBeforeBalance,
    bool RecipientPays,
    int ShippingSettingsVersion,
    PaymentLeaseConfirmation? Confirmation = null);

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
/// saklanır (<see cref="PaymentLeaseFlow.ServerMembers"/>). Kira alınamazsa mesaj GİTMEZ (M9, fail-closed):
/// <list type="bullet">
///   <item>Sunucuya ulaşılamadı (çevrimdışı, sunucu hatası, lisans çözülemedi), v2 zarfı hazır değil → bugünkü
///   belirsizlik sunumu (<see cref="PaymentRequestResult.BalanceUncertain"/>); kira ucu yok (404, S3 geri alındı, §8.6) →
///   satır içi "Sunucu güncelleniyor…". İstisna (§5.8, §6.5-2): GUID'li üyesi olmayan VE kapsamdaki hiçbir etiketi uzak
///   (akıştan inmiş) bir yayında olmayan kişi bugünkü yerel yoldan gider; kaydı bağlantı gelince yazılır
///   (<see cref="PaymentLeaseFlow.RecordsLocally"/>, W3-06'nın kirasız kaydı).</item>
///   <item><c>in-progress-elsewhere</c> → <see cref="PaymentRequestResult.PaymentInProgress"/>, makine adıyla.</item>
///   <item><c>client-update-required</c> → güncelleme sorusu (süreç başına bir kez) ve satır içi engel.</item>
///   <item><c>content-conflict</c> ve 400 → satır içi "İşlem kaydedilemedi (iç hata); tekrar dene." (günlüğe).</item>
///   <item><c>members-stale</c> ve <c>stale-total</c> (W3-08) → akış kişiyi BIRAKIR ve yeniden başlar
///   (<see cref="PaymentLeaseFlow.PendingRetry"/>): kira tutulurken tur işe yaramaz — müşteri ve satış akışının
///   <c>ApplyItem</c>'ı meşgul müşteriyi erteler. Kişi kilitleri ve tıklanan müşterinin kirası bırakıldıktan sonra
///   <see cref="RunRetryAsync"/> bir tur koşar (<c>members-stale</c> → bir müşteri akışı turu, <c>stale-total</c> → bir
///   sınırlı satış akışı turu; ikisi de en çok <see cref="RetryTourBudget"/>), ardından bütün akış AYNI onay
///   belirteçleriyle (<see cref="PaymentLeaseConfirmation"/>: özet + Id listesi) ve YENİ bir <c>recordId</c> ile BİR KEZ
///   yeniden koşar. Her birinin hakkı ayrıdır ve bir kezdir. Ret sürerse satır içi engel: "Müşteri grupları
///   eşitleniyor…" ya da "Bu bilgisayar güncel değil (sunucu X, burada Y)" — tutarlar ikinci denemeden; <c>liveElsewhere</c> doluysa
///   "Bu kişinin PC-A'da süren yayında etiketi var…".</item>
///   <item><c>settings-stale</c> (§6.2) → 409'un <c>current</c>'ı lisans geneli kargo önbelleğine yazılır, kargo yeniden
///   hesaplanır ve kira YERİNDE bir kez daha istenir (<see cref="PaymentLeaseFlow.TakeShippingRecompute"/>; kişi
///   bırakılmaz, onay alanları aynı); mesaj yeni ücretle gider. Sürerse satır içi engel.</item>
///   <item><c>mode-off</c> (§4.7, §5.3, §8.5) → mesaj gitmez; akış kişiyi bırakır, <see cref="RunRetryAsync"/> Off turunu
///   ister (<see cref="SalesChangesPullService.QueueModeOffRound"/>; kabuk "Senkron kapatıldı; ekran yenileniyor."
///   şeridini açar) ve en çok <see cref="ModeOffRoundBudget"/> bekler. Tur bittiyse (yerel kip 0, bekleyen komut yok;
///   uzak etiketler silindi) istek mod Off yolunda bir kez yeniden kurulur: yerel toplam, kira yok, kirasız kayıt
///   (W3-06) — kapsamın yayını artık bu bilgisayarda değilse ya da kişinin kapsamdaki bütün etiketleri uzaktıysa (yerel
///   toplam 0'a düştü) mesaj gitmez, satır içi engel (<see cref="CheckModeOffScope"/>). Bitmediyse satır içi engel
///   "Senkron kapatıldı; ekran yenileniyor.".</item>
///   <item><c>already-requested</c> (W3-09) → mesaj gitmez; bugünkü "tekrar gönderilsin mi?" sorusu
///   (<see cref="PaymentRequestResult.AlreadyRequested"/>), sunucunun öncülleri makine adıyla; yerel planın sorusu aynı
///   diyaloğa katılır. Onay belirteci (<see cref="PaymentLeaseConfirmation"/>) 409'un <c>priorsDigest</c>'ini aynen ve
///   en çok 500 öncül Id'sini taşır, akış onunla yeniden koşar: <c>confirmedPriorsDigest</c> + <c>confirmedPriorIds</c>
///   (#523). Yeni bir <c>already-requested</c> ancak operatör yeniden onaylarsa özeti değiştirir (en yeni belirteç
///   geçerli); öbür retlerin yeniden denemelerinde özet de Id listesi de korunur.</item>
///   <item><c>invalid-priors-digest</c> (400, beklenmez) → sert hata yok: özet atılır, <c>begin</c> bir kez özetsiz
///   (yalnız Id listesiyle) yinelenir; sunucunun taze 409'u soruyu yeniden getirir.</item>
/// </list></para>
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

    /// <summary><c>members-stale</c>'in müşteri akışı turunun ve <c>stale-total</c>'ın satış akışı turunun süresi (§6.4);
    /// dolarsa beklenmez, akış yine bir kez yeniden koşar (karar ikinci <c>begin</c>'in).</summary>
    public static readonly TimeSpan RetryTourBudget = TimeSpan.FromSeconds(5);

    /// <summary><c>begin</c>'in <c>mode-off</c>'undan sonra Off turunun beklendiği süre (§8.5): ön gönderim, son akış ve yerel
    /// geçiş. Dolarsa istek engellenir; tur arka planda sürer, operatörün sonraki tıklaması mod Off yolundan gider.</summary>
    public static readonly TimeSpan ModeOffRoundBudget = TimeSpan.FromSeconds(10);

    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly ICurrentLicenseProvider _license;
    private readonly SalesSyncStateRepository _state;
    private readonly SalesChangesPullService _pull;
    private readonly CustomerChangesPullService _customerPull;
    private readonly PaymentRecordSender _records;
    private readonly WpfCustomerProjectionSyncService _customerPush;
    private readonly SessionOrderSyncService _salesPush;
    private readonly PendingSalesCommandRepository _pendingCommands;
    private readonly LabelRepository _labels;
    private readonly SessionRepository _sessions;
    private readonly IAppUpdatePrompt _updatePrompt;
    private readonly ILogger<PaymentLeaseCoordinator> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    /// <summary>Bu süreçte güncelleme sorusu kira reddinden açıldı (<see cref="OpenUpdatePromptOnce"/>).</summary>
    private int _updatePromptOpened;

    public PaymentLeaseCoordinator(
        LicenseApiClient api,
        SalesSyncEnvelope envelope,
        ICurrentLicenseProvider license,
        SalesSyncStateRepository state,
        SalesChangesPullService pull,
        CustomerChangesPullService customerPull,
        PaymentRecordSender records,
        WpfCustomerProjectionSyncService customerPush,
        SessionOrderSyncService salesPush,
        PendingSalesCommandRepository pendingCommands,
        LabelRepository labels,
        SessionRepository sessions,
        IAppUpdatePrompt updatePrompt,
        ILogger<PaymentLeaseCoordinator> log)
    {
        _sessions = sessions;
        _updatePrompt = updatePrompt;
        _api = api;
        _envelope = envelope;
        _license = license;
        _state = state;
        _pull = pull;
        _customerPull = customerPull;
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

    /// <summary>Yeniden denemenin turunun süresi; testler kısaltır.</summary>
    internal TimeSpan RetryTourTimeout { get; set; } = RetryTourBudget;

    /// <summary>Off turunun bekleme süresi; testler kısaltır.</summary>
    internal TimeSpan ModeOffRoundTimeout { get; set; } = ModeOffRoundBudget;

    /// <summary>
    /// Akışın girişi. Yerel kip On değilse null — bugünkü yol. On ise ön gönderim koşar ve akışın nesnesi döner. Yerel
    /// kip okunamazsa yerel hata fırlar (çağıran mesajı engeller); iptal <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="retried">Bu isteğin daha önceki koşularında harcanmış yeniden denemeler (<see cref="RunRetryAsync"/>):
    /// harcanmış bir hak yeniden istenmez, ret satır içi engel olur.</param>
    public async Task<PaymentLeaseFlow?> EnterAsync(PaymentLeaseRetry retried, CancellationToken ct)
    {
        if (_state.GetMode() != 1) return null;
        await PrePushAsync(ct).ConfigureAwait(false);
        return new PaymentLeaseFlow(this, retried);
    }

    /// <summary>
    /// Kiranın yeniden denenen reddinden sonra, akış kişiyi ve tıklanan müşteriyi BIRAKTIKTAN sonra (§6.4): müşteri ve satış
    /// akışının <c>ApplyItem</c>'ı meşgul müşteriyi erteler, kira tutulurken tur işe yaramazdı.
    /// <list type="bullet">
    ///   <item><see cref="PaymentLeaseRetry.CustomerTour"/> (<c>members-stale</c>): bir müşteri akışı turu
    ///   (<see cref="CustomerChangesPullService.PullOnceAsync"/>) — eksik grup üyeleri ve yönlendirmeler iner.</item>
    ///   <item><see cref="PaymentLeaseRetry.SalesTour"/> (<c>stale-total</c>): bir sınırlı satış akışı turu
    ///   (<see cref="SalesChangesPullService.RefreshAsync"/>) — devredilmiş yayınların etiketleri ve komut sonuçları iner.</item>
    ///   <item><see cref="PaymentLeaseRetry.ModeOffRound"/> (<c>mode-off</c>): Off turu istenir ve en çok
    ///   <see cref="ModeOffRoundTimeout"/> beklenir. Tur TAMAMEN bittiyse (yerel kip 0 ∧ bekleyen komut yok —
    ///   <see cref="SalesSyncStateRepository.GetSeenMode"/>; uzak etiketler silindi, bekleyen baskılar yerele çevrildi) akış
    ///   yeniden koşar ve yerel kip 0 olduğu için mod Off yolundan gider. Bitmediyse engel; tur arka planda sürer.</item>
    /// </list>
    /// Turlar en çok <see cref="RetryTourTimeout"/> beklenir; dolarsa arka planda biter. Turun hatası ya da zaman aşımı engel
    /// değildir: akış yine bir kez yeniden koşar, karar ikinci <c>begin</c>'in. <c>null</c> = akış yeniden koşsun; doluysa
    /// akış bu sonuçla biter (mesaj gitmez). Ağ işi arka plan iş parçacığında; iptal fırlar.
    /// </summary>
    internal async Task<PaymentRequestOutcome?> RunRetryAsync(PaymentLeaseRetry retry, CancellationToken ct)
    {
        switch (retry)
        {
            case PaymentLeaseRetry.CustomerTour:
                await RunBoundedAsync("müşteri akışı turu", t => _customerPull.PullOnceAsync(t), ct).ConfigureAwait(false);
                return null;
            case PaymentLeaseRetry.SalesTour:
                await RunBoundedAsync("satış akışı turu", t => _pull.RefreshAsync(RetryTourTimeout, t), ct)
                    .ConfigureAwait(false);
                return null;
            case PaymentLeaseRetry.ModeOffRound:
                return await AwaitModeOffRoundAsync(ct).ConfigureAwait(false);
            default:
                _log.LogError("Ödeme isteği kirası: tanınmayan yeniden deneme {Retry} — mesaj engellendi", retry);
                return FailClosed;
        }
    }

    /// <summary><see cref="PaymentLeaseRetry.ModeOffRound"/>: Off turu istenir ve sınırlı süre beklenir (tur iptal edilmez).</summary>
    private async Task<PaymentRequestOutcome?> AwaitModeOffRoundAsync(CancellationToken ct)
    {
        var round = _pull.QueueModeOffRound();
        try
        {
            await round.WaitAsync(ModeOffRoundTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogInformation("Ödeme isteği kirası: Off turu {Budget} sn içinde bitmedi; tur arka planda sürüyor",
                ModeOffRoundTimeout.TotalSeconds);
        }

        int seenMode;
        try { seenMode = _state.GetSeenMode(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Ödeme isteği kirası: Off turundan sonra yerel kip okunamadı — mesaj engellendi");
            seenMode = 1;
        }
        if (seenMode == 0)
        {
            _log.LogInformation("Ödeme isteği kirası: Off turu bitti; istek mod Off yolunda yerel toplamla yeniden kuruluyor");
            return null;
        }
        _log.LogInformation("Ödeme isteği kirası: Off turu tamamlanmadı — mesaj engellendi");
        return Blocked(SalesCommandMessages.ModeOff);
    }

    /// <summary>
    /// Mod Off yolunun kapsam denetimi (spec 2026-10-13 §5.3, §6.4 <c>mode-off</c>; §8.5): akış mod Off yolundan gidecekse
    /// kişi ve yerel toplam okunduktan sonra, telefon, bakiye ve mesajdan ÖNCE. Off turu uzak (başka bilgisayarın) yayınları
    /// ve etiketlerini siler; "yerel toplamla yeniden kurma" yalnız bu bilgisayarda kalan etiketler için anlamlıdır.
    /// <list type="bullet">
    ///   <item><c>session:{id}</c> kapsamının oturumu yerelde yok ya da uzak (Off turu sildi; açık kalmış rapor ya da liste
    ///   onu gösteriyor) → engel (<see cref="PaymentLeaseMessages.SessionNotLocal"/>). Gitseydi yerel toplam 0 olurdu: istek
    ///   yalnız kargo ücretiyle gider, kapsam uzlaştırması başka bilgisayarın teslim edilmiş isteğinin düşümünü benimseyip
    ///   tutar farkı yüzünden geri alırdı.</item>
    ///   <item><paramref name="modeOnTotal"/> doluysa (bu istek <c>begin</c>'in <c>mode-off</c>'undan sonra yeniden kuruluyor)
    ///   ve ilk koşunun toplamı &gt; 0 iken yerel toplam 0 → engel ("Senkron kapatıldı; ekran yenileniyor."): kişinin bu
    ///   kapsamdaki bütün etiketleri uzaktı, operatörün gördüğü tutarın hiçbir parçası bu bilgisayarda değil. Toplam yalnız
    ///   düştüyse (yerel ve uzak etiket karışık) istek spec gereği yerel toplamla gider.</item>
    /// </list>
    /// Bugünkü mod Off'ta oturum hiç silinmez: denetim yalnız Off turundan sonra tetiklenir. <c>null</c> = sürer. Okuma hatası
    /// engeldir (fail-closed). Günlük yalnız Id taşır.
    /// </summary>
    internal PaymentRequestOutcome? CheckModeOffScope(
        PaymentPerson person, string scopeKey, decimal localTotal, decimal? modeOnTotal)
    {
        if (PaymentHistoryRecords.SessionIdOf(scopeKey) is { Length: > 0 } sessionId)
        {
            StreamSession? session;
            try { session = _sessions.GetById(sessionId); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Ödeme isteği (mod Off): kapsamın yayını okunamadı — mesaj engellendi (customer={CustomerId})",
                    person.LeasedId);
                return FailClosed;
            }
            if (session is not { Origin: StreamSessionOrigin.Local })
            {
                _log.LogInformation(
                    "Ödeme isteği (mod Off): kapsamın yayını bu bilgisayarda değil ({State}) — mesaj engellendi "
                  + "(customer={CustomerId}, session={SessionId})",
                    session is null ? "yok" : "uzak", person.LeasedId, sessionId);
                return Blocked(PaymentLeaseMessages.SessionNotLocal);
            }
        }
        if (modeOnTotal is > 0m && localTotal <= 0m)
        {
            _log.LogInformation(
                "Ödeme isteği (mod Off): mode-off'tan sonra kişinin bu kapsamda yerel etiketi kalmadı — mesaj engellendi "
              + "(customer={CustomerId})", person.LeasedId);
            return Blocked(SalesCommandMessages.ModeOff);
        }
        return null;
    }

    /// <summary>Yeniden denemenin turu: arka plan iş parçacığında, en çok <see cref="RetryTourTimeout"/> beklenir; süre
    /// dolarsa ya da tur düşerse günlüğe yazılır ve geçilir (tur arka planda biter, hatası gözlenir).</summary>
    private async Task RunBoundedAsync(string name, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        var tour = Task.Run(() => run(ct), CancellationToken.None);
        try
        {
            await tour.WaitAsync(RetryTourTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = tour.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _log.LogInformation("Ödeme isteği kirası: yeniden denemenin {Tour} {Budget} sn içinde bitmedi; akış yine de "
                              + "bir kez yeniden koşuyor", name, RetryTourTimeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Ödeme isteği kirası: yeniden denemenin {Tour} düştü; akış yine de bir kez yeniden koşuyor",
                name);
        }
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
        flow.RecordsLocally = false;
        flow.BegunProductTotal = input.ProductTotal;
        if (!PaymentHistoryRecords.IsLeaseScope(input.ScopeKey) || person.MemberIds.Count == 0)
        {
            _log.LogError("Ödeme isteği kirası kurulamadı: kapsam ya da üye kümesi kira biçimine uymuyor — mesaj engellendi "
                        + "(customer={CustomerId})", person.LeasedId);
            return FailClosed;
        }
        if (_envelope.ForPush() is not { IsV2: true } envelope)
            return Unreachable(flow, input, endpointMissing: false, "v2 zarfı hazır değil");
        var licenseId = await ResolveLicenseIdAsync(ct).ConfigureAwait(false);
        if (licenseId is null)
            return Unreachable(flow, input, endpointMissing: false, "lisans çözülemedi");

        // Her begin (yeniden koşular ve settings-stale'in yerinde yinelemesi dahil) YENİ bir Id: önceki deneme kira
        // yazmadı (ret) ya da kapandı. Onay alanları operatörün onayladığı belirteçlerden — yeniden koşularda aynı: özet
        // 409'un priorsDigest'i aynen, Id listesi en çok 500 (§6.4, #523).
        var confirmation = input.Confirmation ?? PaymentLeaseConfirmation.None;
        PaymentRequestBeginRequest NewRequest(string? digest) => new(
            RecordId: Guid.NewGuid(),
            OwnerId: person.OwnerId ?? person.MemberIds[0],
            MemberIds: person.MemberIds,
            ScopeKey: input.ScopeKey,
            OrderIds: input.OrderIds,
            ProductTotal: PaymentHistoryRecords.Money(input.ProductTotal),
            TotalBeforeBalance: PaymentHistoryRecords.Money(input.TotalBeforeBalance),
            RecipientPays: input.RecipientPays,
            ShippingSettingsVersion: input.ShippingSettingsVersion,
            ConfirmedPriorIds: confirmation.WirePriorIds,
            ConfirmedPriorsDigest: digest,
            ReplicaId: envelope.ReplicaId!.Value,
            Protocol: SalesSyncEnvelope.Protocol,
            SeenMode: envelope.SeenMode);
        var request = NewRequest(confirmation.PriorsDigest);

        PaymentRequestHttpResult<PaymentRequestBeginResponse> http;
        try
        {
            http = await SendBeginAsync(flow, licenseId.Value, request, ct).ConfigureAwait(false);
            if (request.ConfirmedPriorsDigest is not null
                && http is { Transport: PaymentRequestTransport.Problem,
                             Problem.Code: PaymentRequestProblemCodes.InvalidPriorsDigest })
            {
                // §6.4: özeti aynen yankılayan istemcide beklenmez (biçim değişikliği genişlet/daralt ile çıkar). Sert hata
                // gösterilmez: 400 hiçbir şey yazmadı; özet atılır ve begin bir kez özetsiz (yalnız Id listesiyle) yinelenir
                // — küme Id listesini aşıyorsa sunucunun taze 409'u soruyu yeniden getirir.
                _log.LogWarning("Ödeme isteği kirası {RecordId}: invalid-priors-digest — özet atıldı, istek bir kez "
                              + "özetsiz yineleniyor", request.RecordId);
                flow.Clear();
                request = NewRequest(null);
                http = await SendBeginAsync(flow, licenseId.Value, request, ct).ConfigureAwait(false);
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // İstemcinin göndermeden reddettiği istek (üye sayısı sunucu sınırını aşıyor): sunucunun 400'ü gibi.
            _log.LogError(ex, "Ödeme isteği kirası {RecordId} gönderilemedi: istek sunucu sınırlarını aşıyor — mesaj "
                            + "engellendi", request.RecordId);
            flow.Clear();
            return Blocked(SalesCommandMessages.ContentConflict);
        }

        switch (http.Transport)
        {
            case PaymentRequestTransport.Ok when http.Response!.State == PaymentRequestStates.Sending:
                flow.Begun(request.RecordId, http.Response.Members);
                _log.LogInformation(
                    "Ödeme isteği kirası alındı {RecordId} (customer={CustomerId}, {Members} üye, {Orders} sipariş)",
                    request.RecordId, person.LeasedId, http.Response.Members.Count, request.OrderIds.Count);
                return null;
            case PaymentRequestTransport.Ok when http.Response!.State == PaymentRequestStates.Unconfirmed:
                // Yanıtı kaybolan ilk begin'in kirası, aynı Id'li yineleme yanıt alana kadar doldu (§6.4-a): kayıt bu akışın,
                // mesaj gitmedi. Kira tutulur ve çıkışta abandoned ile kapanır (sunucunun finish'i Unconfirmed'ı kabul eder);
                // yoksa kayıt kalıcı olarak "istenmiş olabilir" kalır, öbür bilgisayarların her sorusunda görünürdü.
                flow.Begun(request.RecordId);
                _log.LogWarning("Ödeme isteği kirası {RecordId}: yinelemede kira dolmuş döndü (unconfirmed) — mesaj "
                              + "engellendi, kayıt abandoned ile kapatılacak", request.RecordId);
                return FailClosed;
            case PaymentRequestTransport.Ok:
                // Yeniden oynatma son hâldeki bir kaydı döndürdü: bu akışın kirası yok, kapatılacak bir şey de yok.
                flow.Settled(request.RecordId);
                _log.LogWarning("Ödeme isteği kirası {RecordId}: kayıt {State} durumunda döndü — mesaj engellendi",
                    request.RecordId, http.Response!.State);
                return FailClosed;
            case PaymentRequestTransport.Offline or PaymentRequestTransport.Failed:
                return Unreachable(flow, input, endpointMissing: false,
                    $"{http.Transport} HTTP {http.StatusCode} (kira {request.RecordId})");
            case PaymentRequestTransport.EndpointMissing:
                // Uç yok: önceki deneme de kira yazamadı.
                flow.Settled(request.RecordId);
                return Unreachable(flow, input, endpointMissing: true, $"kira ucu yok (kira {request.RecordId})");
            default:
                // Ret kira yazmaz. Önceki denemenin sonucu bilinmiyorsa (yanıtı alınamadı) Id akışta kalır ve çıkışta
                // abandoned ile kapanır: yineleme, sunucu ilk denemeyi hâlâ işlerken kişi kilidinde zaman aşımına
                // uğrayıp in-progress-elsewhere almış olabilir — ilk deneme sonradan Sending yazılırsa kapanmazdı.
                flow.Clear();
                return Rejected(flow, request.RecordId, http.StatusCode, http.Problem!, input);
        }
    }

    /// <summary><c>begin</c>'i gönderir; yanıt alınamazsa (çevrimdışı, sunucu hatası) akış içinde AYNI Id ve gövdeyle bir
    /// kez yineler — sunucu yeniden oynatır (§6.4-a). Kira yazılmış olabileceği için ilk denemenin Id'si akışa sonucu
    /// bilinmeyen deneme olarak yazılır (<see cref="PaymentLeaseFlow.Uncertain"/>): yinelemenin sonucu onu çözmedikçe
    /// (kira alındı ya da kayıt son hâlde döndü) çıkışta abandoned ile kapanır — kayıt yoksa finish record-not-found alır ve
    /// zararsızca düşer.</summary>
    private async Task<PaymentRequestHttpResult<PaymentRequestBeginResponse>> SendBeginAsync(
        PaymentLeaseFlow flow, Guid licenseId, PaymentRequestBeginRequest request, CancellationToken ct)
    {
        var http = await _api.PostPaymentRequestBeginAsync(licenseId, request, ct).ConfigureAwait(false);
        if (http.Transport is not (PaymentRequestTransport.Offline or PaymentRequestTransport.Failed)) return http;
        flow.Uncertain(request.RecordId);
        _log.LogInformation("Ödeme isteği kirası {RecordId}: yanıt alınamadı ({Transport}); aynı istek bir kez yineleniyor",
            request.RecordId, http.Transport);
        return await _api.PostPaymentRequestBeginAsync(licenseId, request, ct).ConfigureAwait(false);
    }

    /// <summary>Kira alınamadı, sunucunun kararı da yok: mesaj gitmez (M9), bugünkü belirsizlik sunumu.</summary>
    private static PaymentRequestOutcome FailClosed => new(PaymentRequestResult.BalanceUncertain);

    private static PaymentRequestOutcome Blocked(string message) => new(PaymentRequestResult.Blocked, Message: message);

    /// <summary>
    /// Kiraya ulaşılamadı (çevrimdışı, sunucu hatası, lisans çözülemedi, zarf hazır değil, uç yok; §5.8, §6.5-2, §8.6).
    /// İstisna: GUID'li üyesi olmayan VE kapsamdaki hiçbir etiketi uzak (akıştan inmiş) bir yayında olmayan kişi bugünkü
    /// yerel yoldan gider (null, <see cref="PaymentLeaseFlow.RecordsLocally"/>) — çevrimdışı canlı yayın sürebilsin (Y3);
    /// uzak etiketi olan hiçbir kişi kirayı atlayamaz. Öbürlerinde mesaj gitmez.
    /// </summary>
    private PaymentRequestOutcome? Unreachable(
        PaymentLeaseFlow flow, PaymentLeaseBeginInput input, bool endpointMissing, string reason)
    {
        var person = input.Person;
        bool local;
        try
        {
            local = person.OwnerId is null
                    && !_labels.HasRemoteLabels(person.MemberIds, PaymentHistoryRecords.SessionIdOf(input.ScopeKey));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Ödeme isteği kirası: uzak etiket denetimi okunamadı (customer={CustomerId})",
                person.LeasedId);
            local = false;
        }

        if (local)
        {
            flow.RecordsLocally = true;
            _log.LogInformation(
                "Ödeme isteği kirasına ulaşılamadı ({Reason}); GUID'siz ve yalnız kendi etiketleri olan kişi yerel yoldan "
              + "gidiyor, kayıt bağlantı gelince (customer={CustomerId})", reason, person.LeasedId);
            return null;
        }

        _log.LogInformation("Ödeme isteği kirasına ulaşılamadı ({Reason}) — mesaj engellendi (customer={CustomerId})",
            reason, person.LeasedId);
        return endpointMissing ? Blocked(SalesCommandMessages.EndpointMissing) : FailClosed;
    }

    /// <summary>Sunucunun reddi (§6.4 "409'ların istemcideki karşılığı"; §5.3). Hiçbir ret kira yazmaz. Yeniden denenen
    /// retlerde (<see cref="PaymentLeaseFlow.PendingRetry"/>) dönen sonuç akışı durdurur; hak harcanmışsa engelin
    /// kendisidir.</summary>
    private PaymentRequestOutcome Rejected(
        PaymentLeaseFlow flow, Guid recordId, int status, PaymentRequestProblem problem, PaymentLeaseBeginInput input)
    {
        switch (problem.Code)
        {
            case PaymentRequestProblemCodes.InProgressElsewhere:
                _log.LogInformation(
                    "Ödeme isteği kirası {RecordId}: başka bilgisayarda bu kişiye istek sürüyor (other={OtherRecordId}) — "
                  + "mesaj engellendi", recordId, problem.RecordId);
                return new PaymentRequestOutcome(PaymentRequestResult.PaymentInProgress,
                    Message: PaymentLeaseMessages.InProgressElsewhere(problem.Machine));
            case PaymentRequestProblemCodes.ClientUpdateRequired:
                _log.LogWarning("Ödeme isteği kirası {RecordId}: güncelleme gerekli — mesaj engellendi", recordId);
                OpenUpdatePromptOnce(recordId);
                return Blocked(SalesCommandMessages.ClientUpdateRequired);
            case PaymentRequestProblemCodes.MembersStale:
                if (flow.RequestRetry(PaymentLeaseRetry.CustomerTour))
                    _log.LogInformation(
                        "Ödeme isteği kirası {RecordId}: members-stale ({Members} sunucu üyesi) — kişi bırakılıyor, müşteri "
                      + "akışı turundan sonra akış bir kez yeniden koşacak", recordId, problem.Members?.Count);
                else
                    _log.LogInformation(
                        "Ödeme isteği kirası {RecordId}: members-stale yeniden denemede sürüyor ({Members} sunucu üyesi) — "
                      + "mesaj engellendi", recordId, problem.Members?.Count);
                return Blocked(PaymentLeaseMessages.MembersStale);
            case PaymentRequestProblemCodes.StaleTotal:
                if (flow.RequestRetry(PaymentLeaseRetry.SalesTour))
                    _log.LogInformation(
                        "Ödeme isteği kirası {RecordId}: stale-total ({Live} başka bilgisayarda süren yayın) — kişi "
                      + "bırakılıyor, satış akışı turundan sonra akış bir kez yeniden koşacak",
                        recordId, problem.LiveElsewhere?.Count ?? 0);
                else
                    _log.LogInformation(
                        "Ödeme isteği kirası {RecordId}: stale-total yeniden denemede sürüyor ({Live} başka bilgisayarda "
                      + "süren yayın) — mesaj engellendi", recordId, problem.LiveElsewhere?.Count ?? 0);
                // Tutarlar bu denemeden: sunucunun toplamı bu 409'dan, yerel toplam bu begin'in kapsamdan okuduğu.
                return Blocked(problem.LiveElsewhere is { Count: > 0 } live
                    ? PaymentLeaseMessages.LiveElsewhere(live[0].Machine)
                    : PaymentLeaseMessages.NotCurrent(problem.ServerTotal ?? 0m, input.ProductTotal));
            case PaymentRequestProblemCodes.SettingsStale:
                return SettingsStale(flow, recordId, problem.Current!, input);
            case PaymentRequestProblemCodes.ModeOff:
                if (flow.RequestRetry(PaymentLeaseRetry.ModeOffRound))
                    _log.LogInformation("Ödeme isteği kirası {RecordId}: mode-off — mesaj gitmedi, kişi bırakılıyor, Off "
                                      + "turundan sonra istek mod Off yolunda yeniden kurulacak", recordId);
                else
                    _log.LogInformation("Ödeme isteği kirası {RecordId}: Off turundan sonra yine mode-off — mesaj engellendi",
                        recordId);
                return Blocked(SalesCommandMessages.ModeOff);
            case PaymentRequestProblemCodes.AlreadyRequested:
                return AlreadyRequested(recordId, problem, input);
            default:
                // content-conflict, 400 ve tanınmayan ret: istemci hatası, yeniden denenmez.
                _log.LogError("Ödeme isteği kirası {RecordId} reddedildi (HTTP {Status}, {Code}): istemci hatası — mesaj "
                            + "engellendi", recordId, status, problem.Code);
                return Blocked(SalesCommandMessages.ContentConflict);
        }
    }

    /// <summary>
    /// <c>already-requested</c> (§6.4-e, "409'ların istemcideki karşılığı"; sahip kararı 2, #506; W3-09): sunucuda bu kişi ×
    /// kapsamın onaylanmamış önceki isteği var — mesaj gitmez, operatöre bugünkü "tekrar gönderilsin mi?" sorusu sorulur
    /// (<see cref="PaymentRequestResult.AlreadyRequested"/>). Ret kira yazmaz; kapatılacak bir şey yok.
    /// <list type="bullet">
    ///   <item>İstekler sunucunun öncülleridir, sunucunun sırasıyla (kapanış anı); makine adıyla. Tutar teslim edilmiş net,
    ///   yoksa (teslim kaydı olmadan dolan kira) kiranın bakiye öncesi tutarı — o istek "gitmiş olabilir"dir (§6.8).</item>
    ///   <item>Son öncül kiraysa ve bakiye öncesi tutarı bu isteğinkinden farklıysa yeni toplam da sorulur (K5).</item>
    ///   <item>Onay belirteci (<see cref="PaymentLeaseConfirmation"/>) 409'un <c>priorsDigest</c>'ini aynen ve en çok 500
    ///   öncül Id'sini taşır; operatör onaylayınca akış onunla yeniden koşar ve <c>begin</c> onayı taşır. 500'ü aşan
    ///   küme de tek onay turunda geçer: bütün kümeyi özet onaylar.</item>
    /// </list>
    /// Yerel planın sorusu çağıranda katılır (<c>PaymentRequestService.WithLocalPlan</c>). Öncülsüz ret beklenmez: istemci
    /// hatası sayılır.
    /// </summary>
    private PaymentRequestOutcome AlreadyRequested(Guid recordId, PaymentRequestProblem problem, PaymentLeaseBeginInput input)
    {
        var priors = problem.Priors ?? [];
        if (priors.Count == 0)
        {
            _log.LogError("Ödeme isteği kirası {RecordId}: öncülsüz already-requested — mesaj engellendi", recordId);
            return Blocked(SalesCommandMessages.ContentConflict);
        }

        var requests = priors.Select(p => new PriorPaymentRequest(
            p.NetAmount ?? p.TotalBeforeBalance ?? 0m,
            p.At,
            Unconfirmed: p.Unconfirmed || p.NetAmount is null,
            Machine: p.Machine)).ToList();
        // Onay öncül kümesinin özetiyle taşınır (#523): Id listesi en çok 500 — fazlası gönderilmez, bütün kümeyi özet
        // onaylar. Özetsiz (#523 öncesi) sunucuda onay yalnız Id listesidir.
        var confirmation = PaymentLeaseConfirmation.Create(problem.PriorsDigest, priors.Select(p => p.Id));

        // K5: tutarın değiştiğini yalnız bakiye öncesi tutarı bilinen öncülde söyleyebiliriz (kira; kirasız kaydın neti
        // düşümü içermez).
        var last = priors[^1];
        var total = PaymentHistoryRecords.Money(input.TotalBeforeBalance);
        var changed = last.TotalBeforeBalance is { } before && PaymentHistoryRecords.Money(before) != total;
        var prompt = new PaymentPrompt(PersonScopePromptKind.Resend, input.ScopeKey, requests, confirmation.Token,
            NewTotal: changed ? total : null,
            PriorHadBalance: changed && last.NetAmount is { } net && net < last.TotalBeforeBalance!.Value);

        _log.LogInformation(
            "Ödeme isteği kirası {RecordId}: already-requested ({Priors} öncül, {Unconfirmed} sonucu bilinmeyen) — mesaj "
          + "gitmedi, operatöre soruluyor", recordId, priors.Count, requests.Count(r => r.Unconfirmed));
        return new PaymentRequestOutcome(PaymentRequestResult.AlreadyRequested, prompt);
    }

    /// <summary>
    /// <c>settings-stale</c> (§6.2, §6.4-g): sunucunun güncel lisans geneli kargo ayarı (409'un <c>current</c>'ı) önbelleğe
    /// yazılır — sağlayıcı her çağrıda önbellekten okur, mesajın ve kiranın tutarı aynı yeni anlık görüntüden çıkar. Hak
    /// varsa çağıran kargoyu yeniden hesaplar ve kirayı YERİNDE bir kez daha ister
    /// (<see cref="PaymentLeaseFlow.TakeShippingRecompute"/>; kişi bırakılmaz). Önbellek yazılamazsa ya da hak harcandıysa
    /// engel. Sürüm aynıysa (tutar ayarla tutmadı) önbellek değişmez; yinelenen istek yine reddedilir ve engel olur.
    /// </summary>
    private PaymentRequestOutcome SettingsStale(
        PaymentLeaseFlow flow, Guid recordId, ShippingSettingsSnapshot current, PaymentLeaseBeginInput input)
    {
        try
        {
            _state.SaveShippingCache(current.Version, current.FreeShippingThreshold, current.Fee);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Ödeme isteği kirası {RecordId}: settings-stale — kargo önbelleği yazılamadı, mesaj engellendi",
                recordId);
            return Blocked(PaymentLeaseMessages.SettingsStale);
        }
        if (flow.RequestShippingRecompute())
        {
            _log.LogInformation(
                "Ödeme isteği kirası {RecordId}: settings-stale (sürüm {Sent} → {Current}) — kargo yeniden hesaplanıp kira "
              + "yerinde bir kez yineleniyor", recordId, input.ShippingSettingsVersion, current.Version);
            return Blocked(PaymentLeaseMessages.SettingsStale);
        }
        _log.LogInformation("Ödeme isteği kirası {RecordId}: settings-stale yinelemede sürüyor (sürüm {Sent}, sunucu "
                          + "{Current}) — mesaj engellendi", recordId, input.ShippingSettingsVersion, current.Version);
        return Blocked(PaymentLeaseMessages.SettingsStale);
    }

    /// <summary><c>client-update-required</c>'ın güncelleme sorusu (§5.3) — süreç başına BİR KEZ (Velopack sorusu
    /// modaldır; "Hayır" sonraki açılışa bırakır). Satır içi metin her retle döner.</summary>
    private void OpenUpdatePromptOnce(Guid recordId)
    {
        if (Interlocked.Exchange(ref _updatePromptOpened, 1) == 1)
        {
            _log.LogDebug("Ödeme isteği kirası {RecordId}: güncelleme sorusu bu süreçte zaten açıldı", recordId);
            return;
        }
        try { _updatePrompt.CheckAndPrompt(); }
        catch (Exception ex) { _log.LogWarning(ex, "Güncelleme sorusu açılamadı"); }
    }

    /// <summary><see cref="PaymentLeaseFlow.FinishAsync"/>'in gövdesi: önce kalıcı kuyruk, sonra süreli gönderim. Fırlatmaz.</summary>
    /// <param name="recordId">Akışın kirası; yoksa yalnız <paramref name="abandoned"/> kapanır.</param>
    /// <param name="abandoned">Sonucu bilinmeyen önceki <c>begin</c> denemeleri: mesajları gitmedi, <c>abandoned</c> ile
    /// kapanır (kayıt yoksa sunucu record-not-found der, kuyruktan düşer).</param>
    internal async Task FinishAsync(
        Guid? recordId, string outcome, decimal netAmount, decimal? appliedAmount, IReadOnlyList<Guid> abandoned)
    {
        var enqueued = false;
        foreach (var orphan in abandoned)
            enqueued |= Enqueue(new PendingPaymentFinish(
                orphan, PaymentRequestStates.Abandoned, PaymentHistoryRecords.Money(netAmount), null, FullyFromBalance: false),
                orphan, PaymentRequestStates.Abandoned);
        if (recordId is { } id)
            enqueued |= Enqueue(new PendingPaymentFinish(
                id,
                outcome,
                PaymentHistoryRecords.Money(netAmount),
                appliedAmount is { } applied ? PaymentHistoryRecords.Money(applied) : null,
                FullyFromBalance: appliedAmount is > 0m && netAmount <= 0m), id, outcome);
        if (!enqueued) return;

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

    /// <summary>Finish'i kalıcı kuyruğa yazar; yazılamazsa false (kira sunucuda dolar ve "gitmiş olabilir" sayılır —
    /// fail-closed yönü).</summary>
    private bool Enqueue(PendingPaymentFinish finish, Guid recordId, string outcome)
    {
        try
        {
            _records.EnqueueFinish(finish);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ödeme isteği kirası {RecordId}: finish ({Outcome}) kuyruğa yazılamadı", recordId, outcome);
            return false;
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

    /// <summary>Bu isteğin önceki koşularında harcanmış yeniden denemeler.</summary>
    private readonly PaymentLeaseRetry _retried;

    /// <summary>Kapanmamış kira (ya da sonucu bilinmeyen <c>begin</c>): çıkışta <c>finish</c> bununla gider.</summary>
    private Guid? _recordId;

    internal PaymentLeaseFlow(PaymentLeaseCoordinator owner, PaymentLeaseRetry retried)
    {
        _owner = owner;
        _retried = retried;
    }

    /// <summary>
    /// Kira yeniden denenen bir retle durdu (W3-08, §6.4): çağıran kişiyi ve tıklanan müşteriyi BIRAKIR,
    /// <see cref="PaymentLeaseCoordinator.RunRetryAsync"/>'i çağırır ve bütün akışı aynı onay belirteçleriyle bir kez yeniden
    /// koşar (bu bayrak o koşunun <see cref="PaymentLeaseCoordinator.EnterAsync"/>'ine eklenir). Yoksa
    /// <see cref="PaymentLeaseRetry.None"/>.
    /// </summary>
    public PaymentLeaseRetry PendingRetry { get; private set; }

    /// <summary>Yeniden deneme hakkı varsa onu ister (<see cref="PendingRetry"/>); hak bu isteğin önceki bir koşusunda
    /// harcandıysa false — ret engeldir. Her retin hakkı ayrıdır (§6.4): bir istek en çok bir müşteri turu, bir satış turu
    /// ve bir Off turuyla yeniden koşar — döngü yok.</summary>
    internal bool RequestRetry(PaymentLeaseRetry retry)
    {
        if ((_retried & retry) != 0) return false;
        PendingRetry = retry;
        return true;
    }

    /// <summary><c>settings-stale</c>'in yerinde yinelemesi bu koşuda harcandı.</summary>
    private bool _shippingRecomputeUsed;

    /// <summary>Kargo önbelleği yenilendi, yineleme bekliyor (<see cref="TakeShippingRecompute"/>).</summary>
    private bool _shippingRecomputePending;

    /// <summary>
    /// <c>settings-stale</c>'den sonra (§6.2): kargo önbelleği sunucunun güncel ayarıyla yenilendi — çağıran kargoyu yeniden
    /// hesaplar ve <see cref="BeginAsync"/>'i YERİNDE bir kez daha çağırır (kişi bırakılmaz; yeni <c>recordId</c>, aynı onay
    /// alanları). Okununca sıfırlanır; koşu başına en çok bir kez true.
    /// </summary>
    public bool TakeShippingRecompute()
    {
        var pending = _shippingRecomputePending;
        _shippingRecomputePending = false;
        return pending;
    }

    /// <summary>Yerinde yineleme hakkı varsa ister; bu koşuda harcandıysa false (ret engeldir).</summary>
    internal bool RequestShippingRecompute()
    {
        if (_shippingRecomputeUsed) return false;
        _shippingRecomputeUsed = true;
        _shippingRecomputePending = true;
        return true;
    }

    /// <summary>Son kiranın sunucu üyeleri (<c>begin</c> yanıtı): istemcinin kümesi asıl kayda çözülmüş, sunucunun grubuyla
    /// birleşmiş — kapsam uzlaştırmasının adayları (§6.4 "Kapsam uzlaştırması"). Kira yokken boş.</summary>
    public IReadOnlyList<string> ServerMembers { get; private set; } = [];

    /// <summary>
    /// Kiraya ulaşılamadı ve kişi yerel yolun istisnasında (GUID'li üye yok, kapsamda uzak etiket yok; §5.8, §6.5-2): akış
    /// bugünkü yerel yoldan gider ve teslim ya da belirsiz gönderimden sonra kirasız kayıt yazılır (W3-06'nın kaydı,
    /// bağlantı gelince gider).
    /// </summary>
    public bool RecordsLocally { get; internal set; }

    /// <summary>Son <c>begin</c>'in ürün toplamı (kapsamdan okunan, uzak etiketler dahil). <c>mode-off</c>'tan sonra istek
    /// yeniden kurulurken yerel toplamla karşılaştırılır (<see cref="PaymentLeaseCoordinator.CheckModeOffScope"/>).</summary>
    public decimal? BegunProductTotal { get; internal set; }

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
    /// <remarks>Yerel yolda (<see cref="RecordsLocally"/>) sonucu bilinmeyen kira her zaman <c>abandoned</c> kapanır:
    /// teslimi kirasız kayıt taşır; kira da teslim deseydi aynı mesaj sunucuda iki öncül olurdu.</remarks>
    public Task FinishAsync(string outcome, decimal netAmount, decimal? appliedAmount)
    {
        var current = _recordId;
        var orphans = _uncertain.Where(id => id != current).ToList();
        _uncertain.Clear();
        _recordId = null;
        ServerMembers = [];
        if (current is null && orphans.Count == 0) return Task.CompletedTask;
        return _owner.FinishAsync(current, RecordsLocally ? PaymentRequestStates.Abandoned : outcome, netAmount,
            appliedAmount, orphans);
    }

    /// <summary>Yanıtı alınamamış <c>begin</c> denemeleri (kira yazılmış olabilir): sonucu öğrenilmedikçe çıkışta
    /// <c>abandoned</c> ile kapanır — sonraki bir deneme (yeni Id) akışın kirası olsa da.</summary>
    private readonly HashSet<Guid> _uncertain = new();

    internal void Begun(Guid recordId, IReadOnlyList<string>? serverMembers = null)
    {
        _recordId = recordId;
        _uncertain.Remove(recordId);
        if (serverMembers is not null) ServerMembers = serverMembers;
    }

    /// <summary>Denemenin yanıtı alınamadı: kira yazılmış olabilir — akışın kirası sayılır (yineleme de düşerse çıkışta
    /// kapanır) ve sonucu öğrenilmedikçe (<see cref="Begun"/>, <see cref="Settled"/>) çıkışta abandoned ile kapanır.</summary>
    internal void Uncertain(Guid recordId)
    {
        _recordId = recordId;
        _uncertain.Add(recordId);
    }

    /// <summary>Denemenin sonucu öğrenildi ve kapatılacak kira yok (kayıt son hâlde döndü ya da uç yok).</summary>
    internal void Settled(Guid recordId)
    {
        _uncertain.Remove(recordId);
        Clear();
    }

    /// <summary>Akışın kirası yok (ret). Sonucu bilinmeyen önceki denemeler (<see cref="Uncertain"/>) unutulmaz.</summary>
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
