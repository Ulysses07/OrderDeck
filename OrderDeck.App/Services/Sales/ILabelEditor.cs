using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Services.Sales;

/// <summary>Bir etiketin düzenleme yolu (spec 2026-10-13 §5.3).</summary>
public enum SalesEditRoute
{
    /// <summary>Bugünkü yerel servis (<see cref="LabelService"/>), kapısız. Mod Off'ta her etiket; mod On'da bu
    /// bilgisayarın açık (canlı) yayını.</summary>
    Local,

    /// <summary>Yerel servis, devir kapısı altında (§5.2): mod On'da, ilk yetişmeden sonra, kendi bitmiş ama
    /// devredilmemiş yayını. Kilit sıfır beklemeyle alınır ve yerel yazım boyunca tutulur; alınamazsa
    /// <see cref="SalesCommandMessages.HandoverInProgress"/>.</summary>
    LocalGated,

    /// <summary>Düzenleme kapalı (§5.3, §8.4-2): mod On'un ilk yetişmesi bitmedi ve kendi bitmiş yayınının devri
    /// yerelde bilinmiyor — sunucuda devredilmiş olabilir, yerel yazım push'ta reddedilip kaybolurdu.</summary>
    Blocked,

    /// <summary>Sunucu komutu (<see cref="SalesCommandClient"/>): yayın sunucuya devredilmiş (<c>Authority =
    /// 'server'</c>).</summary>
    Command,

    /// <summary>Komut gerekir ama sunucu şu an kullanılamaz: çevrimdışı ya da komut ucu yok (§5.7, §5.8). Düğmeler
    /// pasif, araç ipucu.</summary>
    Unavailable,
}

/// <summary>Bir etiketin yönlendirme kararı.</summary>
/// <param name="SessionId">Etiketin yayını; mod Off'ta okunmaz (null).</param>
/// <param name="Reason">Blocked ve Unavailable'da operatöre görünen metin; öbürlerinde null.</param>
public sealed record SalesEditDecision(string LabelId, string? SessionId, SalesEditRoute Route, string? Reason);

/// <summary>Uygulanamayan etiketler ve sebebi (Türkçe, operatöre görünen).</summary>
public sealed record SalesEditProblem(string Message, IReadOnlyList<string> LabelIds);

/// <summary>
/// Bir düzenlemenin birleşik sonucu: karışık seçim bölünür (yerel yazım + komut), sonuçlar tek satır içi iletide
/// birleşir (§5.3, §5.7).
/// </summary>
/// <param name="Applied">Yazımı gerçekleşen etiketler (yerel ya da komutla; zaten o hâlde olan dahil).</param>
/// <param name="Problems">Uygulanamayanlar, sebebe göre gruplu.</param>
/// <param name="ChangedLabelIds">Yerelde değeri değişmiş olabilecek etiketler (yerel yazım ve komut yanıtının
/// satırları, 409'un güncel satırları dahil); ekran bunları tazeler.</param>
/// <param name="Pending">Kuyruk iptalinde (§5.5) bekleyen komuta giren kalıntı etiketler: sunucuya henüz işlenmedi ama
/// yerel okuyucular uygulanmış sayar (örtü) — kuyruktan çıkarlar. Öbür düzenlemelerde boş.</param>
/// <param name="Notices">Bekleyen komutun anlık gönderiminde kalıcı ret gibi, belirli bir etiket grubuna bağlı olmayan
/// satır içi uyarılar (<see cref="SalesCommandMessages.PendingRejected"/>); yoksa boş.</param>
public sealed record SalesEditResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<SalesEditProblem> Problems,
    IReadOnlyList<string> ChangedLabelIds,
    IReadOnlyList<string>? Pending = null,
    IReadOnlyList<string>? Notices = null)
{
    public static SalesEditResult Empty { get; } = new([], [], []);

    /// <summary>Bekleyen komuta giren etiketler (örtülü); yoksa boş.</summary>
    public IReadOnlyList<string> PendingLabelIds => Pending ?? [];

    /// <summary>Satır içi uyarılar; yoksa boş.</summary>
    public IReadOnlyList<string> NoticeTexts => Notices ?? [];

    /// <summary>Bugünkü yol: hepsi yerelde yazıldı.</summary>
    public static SalesEditResult AllLocal(IReadOnlyList<string> labelIds) => new(labelIds, [], labelIds);

    public bool HasProblems => Problems.Count > 0;

    public int NotAppliedCount => Problems.Sum(p => p.LabelIds.Count);

    /// <summary>
    /// Satır içi metin; sorun yoksa null. Tek sebep ve hiç uygulanan yoksa yalnız sebep; karışıkta her sebep kendi
    /// sayısıyla ("1 etiket değiştirilemedi: Bu değişiklik için sunucu bağlantısı gerekli.").
    /// </summary>
    public string? ProblemText =>
        Problems.Count == 0 ? null
        : Problems.Count == 1 && Applied.Count == 0 ? Sentence(Problems[0].Message)
        : string.Join(" ", Problems.Select(p => $"{p.LabelIds.Count} etiket değiştirilemedi: {Sentence(p.Message)}"));

    private static string Sentence(string text)
    {
        var t = text.Trim();
        return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') ? t : t + ".";
    }
}

/// <summary>Kuyruk baskısının işaretlenmesi (spec 2026-10-13 §5.6-4, §5.5): kâğıt basıldıktan sonra.</summary>
/// <param name="Marked">Basılmış işaretlenen etiketler (yerel yazım ya da uygulanan komut; zaten basılmış olan dahil).</param>
/// <param name="Pending">Kâğıdı basılan ama henüz işaretlenmeyen etiketler: sunucuya işlenmeyen kalıntılar bekleyen
/// <c>mark-printed</c>'de, örtülü — yeniden açılışta da teklif edilmez; kendi bitmiş yayınının satırları, devir kilidi
/// süresinde alınamadıysa (kapanış gönderimi sürüyor), kilit açılınca arka planda kilit altında işaretlenir.</param>
/// <param name="DiscardLabelIds">Sunucunun <c>cancelled[]</c> ve <c>alreadyPrinted[]</c>'i: "kâğıtları imha et".</param>
/// <param name="Notices">Satır içi uyarılar (işaretlenemeyen sunucu satırı, kalıcı ret); yoksa boş.</param>
/// <param name="ChangedLabelIds">Yerelde değeri değişmiş olabilecek etiketler.</param>
/// <param name="AlreadyPrintedIds"><see cref="DiscardLabelIds"/>'in <c>alreadyPrinted[]</c>'ten gelen (iptal edilmemiş)
/// kısmı: şerit "zaten basılmıştı" der.</param>
public sealed record SalesPrintMarkResult(
    IReadOnlyList<string> Marked,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> DiscardLabelIds,
    IReadOnlyList<string> Notices,
    IReadOnlyList<string> ChangedLabelIds,
    IReadOnlyList<string>? AlreadyPrintedIds = null)
{
    public static SalesPrintMarkResult Empty { get; } = new([], [], [], [], []);

    /// <summary><see cref="DiscardLabelIds"/>'in başka yerde zaten basılmış kısmı (boşsa hepsi iptal).</summary>
    public IReadOnlyList<string> AlreadyPrintedLabelIds => AlreadyPrintedIds ?? [];

    /// <summary>Bugünkü yol: hepsi yerelde damgalandı.</summary>
    public static SalesPrintMarkResult AllLocal(IReadOnlyList<string> labelIds) => new(labelIds, [], [], [], labelIds);
}

/// <summary>Yeniden baskının planı (§5.6): basılacak satırlar (istenen sırayla, basılmış ve iptal edilmemiş),
/// "DÜZELTME" işaretlileri ve — sunucu satırı önbellekten basıldıysa — ekran notu.</summary>
/// <param name="Error">Komut reddedildi: hiçbir şey basılmaz, metin satır içi.</param>
/// <param name="FromCache">Sunucu satırları çevrimdışı olduğu için yerel önbellekten okundu.</param>
/// <param name="LastSyncAt">Önbelleğin tazeliği: son satış akışı eşitlemesi (bu süreçte); bilinmiyorsa null.</param>
public sealed record SalesReprintPlan(
    IReadOnlyList<Label> Labels,
    IReadOnlySet<string> CorrectionIds,
    string? Error = null,
    bool FromCache = false,
    DateTimeOffset? LastSyncAt = null)
{
    public static SalesReprintPlan From(Reprint reprint) => new(reprint.Labels, reprint.CorrectionIds);

    /// <summary>Ekran notu ("son eşitleme SS:DD"); önbellekten basılmadıysa null.</summary>
    public string? CacheNote => FromCache ? SalesCommandMessages.LastSync(LastSyncAt) : null;
}

/// <summary>"Fiyatı düzelt"in onayı (spec 2026-10-13 §4.6 <c>set-code-price</c>, §5.3 "Fiyatı düzelt"): planlar yerel
/// aynadan, bugünkü planlayıcıyla (<see cref="LabelService.PlanCodeCorrection(string, IReadOnlyCollection{string})"/>).</summary>
/// <param name="SessionId">Düzeltmenin yayını.</param>
/// <param name="Plans">İşaretli kodların planları.</param>
/// <param name="Selection">Operatörün seçtiği etiketler: kodsuz grubun ve yayın×kod dışında kalan seçilenlerin kaynağı
/// (<c>alsoSelectedIds</c>).</param>
/// <param name="LabelIds">Yerel yazımın etiketleri — planların düzeltilebilir, kargoya verilmiş ve başka bilgisayarın
/// dosyasındaki etiketleri (bugünkü <see cref="LabelService.CorrectPrices"/> girdisi).</param>
/// <param name="NewPrice">Yeni fiyat (≥ 0).</param>
/// <param name="PaidFromBalance">Bakiyeden ödenmiş etiketler (yazımdan önce okunur); null = yok.</param>
public sealed record SalesCodePriceRequest(
    string SessionId,
    IReadOnlyList<CodeCorrectionPlan> Plans,
    IReadOnlySet<string> Selection,
    IReadOnlyList<string> LabelIds,
    decimal NewPrice,
    IReadOnlySet<string>? PaidFromBalance);

/// <summary>Fiyat düzeltmesinin sonucu. Karışık seçimde (canlı yayın + kalıntı kuyruk) yerel yazım ve komut(lar)
/// birlikte; biri başarısızsa öncekiler yazılmış kalır (<see cref="Problem"/> dolu).</summary>
/// <param name="Local">Yerel yazımın sonucu (bugünkü <see cref="LabelService.CorrectPrices"/>); yerel etiket yoksa
/// null.</param>
/// <param name="Commanded">Planda devredilmiş yayının etiketi vardı (komut yolu); false = bugünkü yerel yazım.</param>
/// <param name="CommandChanged">Komutların fiyatını değiştirdiği etiketler (yanıtın <c>changed</c>'i).</param>
/// <param name="Problem">Uygulanamadı (satır içi metin, §5.3); başarıda null.</param>
/// <param name="Status">Sorun komuttan geldiyse son komutun durumu.</param>
/// <param name="ChangedLabelIds">Yerelde değeri değişmiş olabilecek etiketler (yanıtın ve retin güncel satırları).</param>
public sealed record SalesCodePriceResult(
    PriceCorrectionResult? Local,
    bool Commanded,
    IReadOnlyList<string> CommandChanged,
    string? Problem,
    SalesCommandStatus? Status,
    IReadOnlyList<string> ChangedLabelIds)
{
    /// <summary>Bugünkü yol: hepsi yerelde yazıldı.</summary>
    public static SalesCodePriceResult AllLocal(PriceCorrectionResult local) => new(local, false, [], null, null, []);

    /// <summary>Hiçbir şey yazılmadan reddedildi (kapalı, bağlantısız, devir sürüyor).</summary>
    public static SalesCodePriceResult Refused(string problem) => new(null, false, [], problem, null, []);
}

/// <summary>Yedek seçimi (spec 2026-10-13 §4.6 <c>add-backup</c>, §5.3 "Yedek seçimi"): ebeveyn etiket ve sohbetten
/// seçilen kişi — bugünkü <see cref="LabelService.AddBackup"/> girdisi.</summary>
public sealed record SalesBackupRequest(
    string ParentLabelId,
    string Platform,
    string Username,
    string DisplayName,
    string? MessageText);

/// <summary>Yedek eklemenin sonucu.</summary>
/// <param name="Backup">Kuyruğa girecek yeni geçici yedek (yerel yazım ya da komut yanıtının satırı); eklenemediyse ya
/// da yanıtın satırı yerele yazılamadıysa null.</param>
/// <param name="Problem">Eklenemedi: satır içi metin (§5.3); başarıda null.</param>
/// <param name="Resend">Sonucu bilinmeyen komut (zaman aşımı, sunucu hatası): "Tekrar dene" AYNI niyeti — aynı
/// <c>CommandId</c> ve <c>newOrderId</c> — gönderir; sunucu ikinci kez eklemez. Yoksa null.</param>
/// <param name="AddedRemotely">Komut uygulandı ama yanıtın satırı yerele yazılamadı (akışa kaldı).</param>
public sealed record SalesBackupResult(
    Label? Backup,
    string? Problem,
    SalesCommandIntent? Resend = null,
    bool AddedRemotely = false)
{
    public static SalesBackupResult Added(Label backup) => new(backup, null);

    public static SalesBackupResult Failed(string problem, SalesCommandIntent? resend = null) => new(null, problem, resend);
}

/// <summary>Dekontun kargo adımının sonucu (spec 2026-10-13 §5.3 "Dekont kargo adımı", §5.8 "Dekont kaydı").</summary>
/// <param name="Threshold">Normal yönergede havuz eşiğe ulaştı: eşik çekmecesi bununla açılır; yoksa null.</param>
/// <param name="Warning">Kargo adımı yapılamadı ya da yarım kaldı (dekont yine kaydedildi): satır içi uyarı; başarıda
/// null. Bir sonraki dekont bütün bağlanmamış etiketleri alır.</param>
/// <param name="ChangedLabelIds">Yerelde kargo bağı değişmiş olabilecek etiketler (komut yanıtının ve retin
/// satırları); mod Off'ta boş.</param>
public sealed record SalesShipmentStepResult(
    ShipmentDecisionContext? Threshold,
    string? Warning,
    IReadOnlyList<string> ChangedLabelIds)
{
    public static SalesShipmentStepResult Local(ShipmentDecisionContext? threshold) => new(threshold, null, []);

    public static SalesShipmentStepResult Skipped(string warning, IReadOnlyList<string>? changed = null) =>
        new(null, warning, changed ?? []);
}

/// <summary>Kargo kararının sonucu (§5.3 "Eşik çekmecesi kararı").</summary>
/// <param name="Shipment">Kararın uygulandığı dosya (yerel satır ya da ayna); uygulanamadıysa null.</param>
/// <param name="PooledAmount">Kararın kapsadığı dosyaların toplamı (U12) — "ücretsiz kargo kazandın" mesajının tutarı.</param>
/// <param name="Problem">Uygulanamadı: satır içi metin (§5.3); başarıda null.</param>
/// <param name="ChangedLabelIds">Yerelde kargo bağı değişmiş olabilecek etiketler.</param>
/// <param name="Replan">Mod On'da karar <c>stale</c> aldı ve güncel aynada dosya hâlâ açık, havuz hâlâ eşikte: plan
/// bununla yeniden gösterilir (§5.3 "sessiz yeniden deneme yok … plan yeniden gösterilir"); yoksa null.</param>
public sealed record SalesShipmentDecisionResult(
    Shipment? Shipment,
    decimal PooledAmount,
    string? Problem,
    IReadOnlyList<string> ChangedLabelIds,
    ShipmentDecisionContext? Replan = null);

/// <summary>
/// Kargo dosyası işlemleri (spec 2026-10-13 §5.3 "Dekont kargo adımı" ve "Eşik çekmecesi kararı", §7.1). Mod Off'ta
/// bugünkü yerel <see cref="ShipmentService"/> sırası, çağıranın iş parçacığında, beklemeden — dönen görev tamamlanmış
/// olarak döner. Mod On'da dosya ve etiket–dosya bağı sunucunundur: her zaman komut (<c>attach-shipment</c>,
/// <c>shipment-decision</c>), sessiz yeniden deneme yok; yerel <c>Shipment</c>'a hiç yazılmaz.
/// </summary>
public interface IShipmentEditor
{
    /// <summary>
    /// Dekont kaydedildikten sonra müşterinin bağlanmamış etiketlerini açık dosyasına bağlar ve yönergeyi uygular:
    /// Hold ve RecipientPays kararı çekmecesiz verilir, Normal'de eşik havuzla değerlendirilir. Mod On'da
    /// çevrimdışıyken, komut ucu yokken ya da komut reddedilince adım atlanır ve sonuç uyarı taşır (§5.8); dekont
    /// kaydı etkilenmez.
    /// </summary>
    /// <param name="customerId">Dekontun müşterisi (yerel Id).</param>
    Task<SalesShipmentStepResult> RunDekontShipmentStepAsync(
        string customerId, ShipmentDirective directive, CancellationToken ct = default);

    /// <summary>Eşik çekmecesinin kararı: mod Off'ta <see cref="ShipmentService.ApplyDecision(string, ShipmentDecision, out decimal)"/>,
    /// mod On'da <c>shipment-decision</c> (beklenen sürüm aynanın <see cref="Shipment.ServerVersion"/>'ı).</summary>
    Task<SalesShipmentDecisionResult> DecideShipmentAsync(
        string shipmentId, ShipmentDecision decision, CancellationToken ct = default);
}

/// <summary>
/// Etiket düzenlemelerinin tek karar noktası (spec 2026-10-13 §5.3). Mod Off'ta (<c>SalesSyncState.Mode = 0</c>, bugün
/// her lisans) her çağrı bugünkü yerel servise gider: kapı yok, ağ yok, bekleme yok. Mod On'da etiket başına
/// yayının <c>Authority</c>'si, kendi yayınının bitiş ve devir bilgisi, ilk yetişme ve bağlantı yolu seçer
/// (<see cref="SalesEditRoute"/>). Kargo işlemleri de buradan geçer (<see cref="IShipmentEditor"/>). Çağıranlar sonucu
/// satır içi gösterir; pop-up yok.
/// </summary>
public interface ILabelEditor : IShipmentEditor
{
    /// <summary>Etiket başına karar; yazmaz, ağa gitmez. Kip her çağrıda okunur.</summary>
    IReadOnlyList<SalesEditDecision> Route(IReadOnlyCollection<string> labelIds);

    /// <summary>Sunucu şu an komut alamıyorsa araç ipucu metni (çevrimdışı ya da komut ucu yok); kullanılabiliyorsa
    /// null. Yalnız bellekten okur — kipten bağımsız; çağıran seçimin sunucu satırı taşıyıp taşımadığını
    /// <see cref="Route"/> ile bilir.</summary>
    string? ServerUnavailableReason { get; }

    /// <summary>Düğme pasif mi: verilen etiketlerin HEPSİ sunucu ister ve sunucu kullanılamıyorsa araç ipucu metni,
    /// değilse null (karışık seçim etkin kalır; bölünür ve sonucu satır içi söylenir).</summary>
    string? EditBlockedReason(IReadOnlyCollection<string> labelIds);

    /// <summary>İptal (<see cref="LabelService.Cancel"/> ya da <c>cancel</c> komutu).</summary>
    Task<SalesEditResult> CancelAsync(IReadOnlyList<string> labelIds, string reason, CancellationToken ct = default);

    /// <summary>
    /// Kuyruktan çıkarma ve kuyruğu temizleme (spec 2026-10-13 §5.3 "Kuyruktan çıkar", "Kuyruğu temizle"; §5.5). Mod
    /// Off'ta bugünkü <see cref="LabelService.Cancel"/>, beklemeden. Mod On'da canlı ve yerel yayınların etiketleri
    /// yerelde; devredilmiş KENDİ yayınının kalıntı etiketleri sabit kimlikli <c>cancel</c> komutuyla — önce bekleyen
    /// komut olarak kalıcı yazılır (örtü), çevrimiçiyse hemen gönderilir; çevrimdışıyken ya da komut ucu yokken kuyrukta
    /// bekler (<see cref="SalesEditResult.PendingLabelIds"/>). Başka bilgisayarın yayını bekleyen komuta girmez.
    /// </summary>
    Task<SalesEditResult> CancelQueueAsync(IReadOnlyList<string> labelIds, string reason, CancellationToken ct = default);

    /// <summary>
    /// Kuyruk baskısının işaretlenmesi, kâğıt basıldıktan SONRA (§5.6-4). Mod Off'ta bugünkü
    /// <see cref="LabelService.MarkPrintedAndRecord(IReadOnlyList{string})"/>, beklemeden. Mod On'da tek basış kimliği ve
    /// kâğıt sırası: yerel satırlar <see cref="LabelService.MarkPrintedAndRecord(IReadOnlyList{string}, string, IReadOnlyList{int})"/>
    /// ile, devredilmiş yayınların satırları — kendi kalıntısı da başka bilgisayarınki de (sahip kararı Y11) — bekleyen
    /// <c>mark-printed</c> komutuyla (kalıcı önce; çevrimiçiyse hemen gönderilir; sonucu bilinmezse tur aynı kimlikle
    /// yineler). Canlı yayının satırları devir kilidine uğramadan hemen; kendi bitmiş yayınının satırları kilidi en çok
    /// 3 sn bekler, süre dolunca kilit açılınca arka planda işaretlenir (<see cref="SalesPrintMarkResult.Pending"/>).
    /// </summary>
    /// <param name="paperOrder">Basılan etiketler, kâğıt sırasıyla.</param>
    Task<SalesPrintMarkResult> MarkPrintedAsync(IReadOnlyList<string> paperOrder, CancellationToken ct = default);

    /// <summary>
    /// <see cref="MarkPrintedAsync"/>'in süresinde beklenemeyen satırlarının işareti arka planda bitti (kilit açıldı):
    /// değişen etiketler, imha edilecek kâğıtlar ve uyarılar — işlenemeyen sunucu satırı, kalıcı ret ya da işaret hiç
    /// yazılamadıysa <see cref="SalesCommandMessages.PrintNotWritten"/>. Çağıranın şeridi bunu
    /// <see cref="MarkPrintedAsync"/>'in kendi sonucu gibi gösterir; sessizce kaybolmaz. Arka plan iş parçacığından gelir;
    /// mod Off'ta hiç gelmez.
    /// </summary>
    event EventHandler<PendingSalesCommandsSettled>? DeferredPrintSettled;

    /// <summary>İptali geri alma (<see cref="LabelService.Uncancel"/> ya da <c>uncancel</c> komutu).</summary>
    Task<SalesEditResult> UncancelAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default);

    /// <summary>Yedek onayı (<see cref="LabelService.ConfirmBackup"/> ya da <c>confirm-backup</c> komutu).</summary>
    Task<SalesEditResult> ConfirmBackupAsync(string backupLabelId, decimal? newPrice, CancellationToken ct = default);

    /// <summary>Yeniden baskı (§5.6): yerel satırlar <see cref="LabelService.PrepareReprint"/>; sunucu satırları
    /// çevrimiçiyken <c>reprint</c> komutunun güncel satırları, çevrimdışıyken önbellek (notla).</summary>
    Task<SalesReprintPlan> PrepareReprintAsync(IReadOnlyList<string> labelIds, CancellationToken ct = default);

    /// <summary>
    /// "Fiyatı düzelt"in onayı (§4.6 <c>set-code-price</c>). Yerel etiketler <paramref name="writeLocal"/> ile — bugünkü
    /// <see cref="LabelService.CorrectPrices"/> çağrısı, çağıranın iş parçacığında, beklemeden; mod Off'ta her zaman
    /// yalnız bu. Devredilmiş yayının etiketleri planla (beklenen sürüm = planın düzeltilebilir etiketleri) komutla;
    /// fiyatta sessiz yeniden deneme yok (<c>plan-stale</c>: güncel satırlar yazılır, sonuç sorunu taşır). Yerel
    /// yazımın çakışması (<see cref="PriceCorrectionConflictException"/>) çağırana bugünkü gibi fırlar.
    /// </summary>
    /// <param name="writeLocal">Yerel yazım: verilen etiketlerle <see cref="LabelService.CorrectPrices"/> (ya da testin
    /// dikişi).</param>
    Task<SalesCodePriceResult> CorrectCodePricesAsync(
        SalesCodePriceRequest request,
        Func<IReadOnlyList<string>, PriceCorrectionResult> writeLocal,
        CancellationToken ct = default);

    /// <summary>
    /// Yedek seçimi (§4.6 <c>add-backup</c>): yerel yolda bugünkü <see cref="LabelService.AddBackup"/> (çağıranın iş
    /// parçacığında, beklemeden; mod Off'ta her zaman); ebeveynin yayını devredilmişse <c>add-backup</c> komutu.
    /// <paramref name="resend"/> verilirse önceki sonucu bilinmeyen niyet aynen yeniden gönderilir.
    /// </summary>
    Task<SalesBackupResult> AddBackupAsync(
        SalesBackupRequest request, SalesCommandIntent? resend = null, CancellationToken ct = default);
}
