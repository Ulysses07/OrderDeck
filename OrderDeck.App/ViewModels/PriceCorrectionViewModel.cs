using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.App.Services.Sales;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.ViewModels;

/// <summary>Düzeltmede bir kod (sahip kararı F3): kodun yayındaki BÜTÜN düzeltilebilir etiketleri, mevcut
/// fiyat(lar)ı ve adediyle. Operatör düzelteceği kodları işaretler; fiyat grubu işaretleme (kısmi düzeltme)
/// YOK — bir yayında bir kodun tek fiyatı vardır.</summary>
public sealed partial class CodeOption : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public CodeOption(CodeCorrectionPlan plan, bool isChecked)
    {
        Plan = plan;
        _isChecked = isChecked;
    }

    public CodeCorrectionPlan Plan { get; }

    /// <summary>Eşleştirme anahtarı (harf duyarsız karşılaştırılır); kodsuz grup boş.</summary>
    internal string Key => Plan.Code ?? "";

    public string Title => Plan.Code ?? "Kodsuz (yalnız seçilenler)";

    public int Count => Plan.Plan.Correctable.Count;

    public bool IsMixed => Plan.Plan.PriceGroups.Count > 1;

    /// <summary>"250,00 TL × 18"; karışıksa her fiyat ayrı ("250,00 TL × 17, 230,00 TL × 1").</summary>
    public string Text => PricesText(Plan.Plan.PriceGroups) + (IsMixed ? " — karışık" : "");

    internal static string PricesText(IEnumerable<PriceGroup> groups)
        => string.Join(", ", groups.Select(g => $"{g.Price.ToString("N2", Tr)} TL × {g.Count}"));

    [ObservableProperty] private bool _isChecked;
}

/// <summary>Düzeltilmiş isteği gönder listesinin satırı.</summary>
public sealed partial class ResendRowViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public ResendRowViewModel(CorrectedRequestCandidate candidate) => Candidate = candidate;

    public CorrectedRequestCandidate Candidate { get; }

    public string Text
    {
        get
        {
            var c = Candidate;
            var scope = c.IsSessionScope ? "yayın isteği" : "genel istek";
            // Genel istek günler önce gitmiş olabilir: bugün değilse tarihli (ödeme sorusuyla aynı kural).
            var at = c.PriorAt is { } t
                ? Services.PaymentResultPresenter.WhenText(t, DateTimeOffset.Now) + Services.PaymentResultPresenter.Locative(t) + " "
                : "";
            var was = c.PriorUnconfirmed ? "istenmiş olabilir" : "istendi";
            // Önceki istekten bakiye düşülmüştü: istenen net bakiye SONRASI, yeni toplam bakiye ÖNCESİ —
            // ödeme sorusuyla aynı ifade; yoksa düşüş artış gibi görünürdü ("200 istendi; yeni 205").
            var now = c.PriorHadBalance
                ? $"yeni toplam {c.NewTotal.ToString("N2", Tr)} TL (bakiye düşülmeden önce)"
                : $"yeni tutar {c.NewTotal.ToString("N2", Tr)} TL";
            return $"{c.Display} — {scope}: {at}{c.PriorAmount.ToString("N2", Tr)} TL {was}; {now}";
        }
    }

    /// <summary>Gönderimden sonra: "gönderildi", "elle bakılmalı: …". Gönderilmeden önce boş.</summary>
    [ObservableProperty] private string? _outcomeText;
    [ObservableProperty] private bool _needsManual;
}

/// <summary>
/// "Fiyatı düzelt" (sahip kararları K4/K5, F2/F3) — pop-up YOK: form çekmecede, onaylar satır içi.
///
/// <para><b>Kod düzeyinde</b> (F3, "açık artırma değil, sabit fiyat"): bir yayında bir kodun TEK fiyatı vardır.
/// Seçimin KODLARI listelenir (her biri mevcut fiyatı ve adediyle); operatör düzelteceği kodları işaretler (tek
/// kod varsa işaretli gelir) ve yeni fiyatı yazar. İşaretli kodun yayındaki BÜTÜN düzeltilebilir etiketleri
/// değişir — yalnız seçilenler değil; kuyruk, basılmış ve yedekler dahil, iptal edilmiş ve kargoya verilmiş
/// dosyadakiler hariç. Kodda karışık fiyat varsa (eski veri, pazarlıklı yedek) hepsi yeni fiyata geçer; özet
/// söyler. Kodsuz seçilen etiketlerde genişletilecek kod yok: yalnız onlar.</para>
///
/// <para>Özet (onaydan önce): N etiket / M müşteri, kuyruk/basılmış, seçimde olmayıp düzelecekler, eski tutarla
/// istek gitmiş K kişi, ödemesi alınmış P kişi, kargoya verilmiş S etiket ve başka bilgisayarın dosyasındaki E etiket
/// (ikisi de değişmez; ikincisi spec 2026-10-13 §7.4). Onay tek pakette yazar
/// (<see cref="LabelService.CorrectPrices"/>); kabuk kuyruktaki satırları yerinde günceller. Sonra: basılmış
/// etiket düzeldiyse "yeniden basılsın mı?" (varsayılan Evet), teslim edilmiş isteği değişen kişiler için
/// "düzeltilmiş isteği gönder". Aynı akış FİYAT kutusundaki fiyat değişikliğinin şeridinden de koşar
/// (<see cref="ForCode"/>): sonuç görünümü orada satır içi.</para>
///
/// <para><b>Uçuştaki baskı:</b> düzelecek etiketlerden biri o an yazıcıdaysa düzeltme reddedilir — kâğıda
/// eski fiyat çıkar, ciro yeni fiyatla yazılırdı. Onay anında yeniden denetlenir. <b>Arada değişen
/// etiketler:</b> operatörün GÖRDÜĞÜ etiketlerden biri basıldı, iptal edildi ya da fiyatı değiştiyse özet
/// yenilenir, yeniden onay istenir. Arada yazılan YENİ etiket kodun etiketidir: özeti bayatlatmadan düzeltmeye
/// girer — yayında yorumlar yağarken onay hiç tutmazdı.</para>
/// </summary>
public sealed partial class PriceCorrectionViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly LabelService _labels;
    private readonly ILabelEditor _editor;
    private readonly CorrectedPaymentRequests? _payments;
    private readonly string _sessionId;
    private readonly Func<IReadOnlyList<CodeCorrectionPlan>> _planner;
    private readonly IReadOnlySet<string> _selection;
    private readonly DateTime _streamDate;
    private readonly Func<IReadOnlySet<string>?> _inFlight;
    private readonly Action<PriceCorrectionResult> _applied;
    private readonly Func<IReadOnlyList<string>, Task<string?>> _reprint;
    private readonly bool _cloudApi;

    /// <summary>FİYAT kutusunun düzeltmesi (<see cref="ForCode"/>): bu bilgisayarın canlı yayını. Uzak kayıt ön okuması
    /// yapılmaz — canlı yol ağ beklemez (spec 2026-10-13 §5.1), beklemesi de bir şey değiştirmez: devredilmemiş satış başka
    /// bilgisayarda istenemez (Y1; §6.3 <c>stale-total liveElsewhere</c>), yani hiçbir uzak kayıt bu etiketleri kapsamaz.
    /// Kabuk onayın sonucunu zaman uyumlu okur.</summary>
    private readonly bool _liveStream;

    private IReadOnlyList<CodeCorrectionPlan> _plans = Array.Empty<CodeCorrectionPlan>();
    private IReadOnlySet<string> _shownIds = new HashSet<string>();
    private string _shownFingerprint = "";
    private IReadOnlyList<CorrectedLabel> _correctedPrinted = Array.Empty<CorrectedLabel>();

    /// <summary>Sonucun kodları ("A1", "A1, B2") — <see cref="UnansweredWork"/> metninin başı.</summary>
    private string _resultCodes = "";

    /// <summary>Ödemesi alınmış kişilerin adları (<see cref="PaidRows"/>'un farksız hâli).</summary>
    private readonly List<string> _paidNames = new();

    /// <summary>Seçimden açılan düzeltme (kuyruk ya da "Basılanlar"): <paramref name="labelIds"/> seçili
    /// etiketler — düzeltme onların KODLARI üzerinden çalışır.</summary>
    /// <param name="reprint">Kabuğun yeniden baskı yolu (yazıcı kapısı paylaşılır); hata metni ya da null.</param>
    /// <param name="cloudApiEnabled">WhatsApp Cloud API açık mı — kapalıysa her kişi için WhatsApp
    /// penceresi açılacağı söylenir.</param>
    /// <param name="editor">Düzenleme yönlendiricisi (spec 2026-10-13 §5.3 "Fiyatı düzelt"): mod On'da devredilmiş
    /// yayının etiketleri <c>set-code-price</c> komutuyla. Verilmezse (testlerin çoğu) yalnız yerel — bugünkü yol.</param>
    public PriceCorrectionViewModel(
        LabelService labels, CorrectedPaymentRequests? payments, string sessionId,
        IReadOnlyList<string> labelIds, DateTime streamDate,
        Func<IReadOnlySet<string>?> inFlight, Action<PriceCorrectionResult> applied,
        Func<IReadOnlyList<string>, Task<string?>> reprint, bool cloudApiEnabled,
        ILabelEditor? editor = null)
        : this(labels, payments, sessionId, () => labels.PlanCodeCorrection(sessionId, labelIds),
            labelIds.ToHashSet(StringComparer.Ordinal), streamDate, inFlight, applied, reprint, cloudApiEnabled, editor,
            liveStream: false)
    {
    }

    private PriceCorrectionViewModel(
        LabelService labels, CorrectedPaymentRequests? payments, string sessionId,
        Func<IReadOnlyList<CodeCorrectionPlan>> planner, IReadOnlySet<string> selection, DateTime streamDate,
        Func<IReadOnlySet<string>?> inFlight, Action<PriceCorrectionResult> applied,
        Func<IReadOnlyList<string>, Task<string?>> reprint, bool cloudApiEnabled, ILabelEditor? editor, bool liveStream)
    {
        _liveStream = liveStream;
        _labels = labels;
        _editor = editor ?? new LocalLabelEditor(labels);
        _payments = payments;
        _sessionId = sessionId;
        _planner = planner;
        _selection = selection;
        _streamDate = streamDate;
        _inFlight = inFlight;
        _applied = applied;
        _reprint = reprint;
        _cloudApi = cloudApiEnabled;

        LoadCodes(_planner(), checkedKeys: null);
        RecomputeSummary();
        _summaryRefresh = RefreshSummaryAsync(force: true);
    }

    /// <summary>Özetin uzak kayıt ön okuması (W3-10) — testler bekler.</summary>
    internal Task SummaryRefresh => _summaryRefresh;

    private Task _summaryRefresh = Task.CompletedTask;

    /// <summary>
    /// Özetin ödeme satırları mod On'da başka bilgisayarların isteklerini de sayar (spec 2026-10-13 §6.4 "Teslim kayıtlarının
    /// okunması"; W3-10): özet önce yerel işlerle hemen kurulur, uzak kayıtlar arka planda okunur, yeni bir şey geldiyse özet
    /// yeniden hesaplanır. Bekletmez; okuma düşerse özet yerel kalır. Açılışta zorlanır (planın tutarı güncel olsun); plan
    /// yeniden okununca yalnız eksikler. FİYAT kutusunun düzeltmesinde (<see cref="_liveStream"/>) hiç okunmaz.
    /// </summary>
    private async Task RefreshSummaryAsync(bool force)
    {
        if (_payments is null || _liveStream) return;
        var labels = DistinctLabels(_plans.SelectMany(p => p.Plan.Correctable)).Where(l => l.ShipmentId is null).ToList();
        if (labels.Count == 0) return;
        if (await _payments.PrefetchAsync(_sessionId, labels, force) && !IsDone) RecomputeSummary();
    }

    /// <summary>FİYAT kutusundaki fiyat değişikliğinin düzeltmesi (F2): tek kod, işaretli — kodun yayındaki
    /// bütün düzeltilebilir etiketleri. Yalnız canlı yayın, her zaman yerel (spec 2026-10-13 §5.3 "FİYAT kutusu"). Onay
    /// beklemeden biter (uzak kayıt ön okuması yok, <see cref="_liveStream"/>): kabuk sonucu
    /// <c>ConfirmCommand.Execute</c>'tan hemen sonra okur.</summary>
    public static PriceCorrectionViewModel ForCode(
        LabelService labels, CorrectedPaymentRequests? payments, string sessionId, string code, DateTime streamDate,
        Func<IReadOnlySet<string>?> inFlight, Action<PriceCorrectionResult> applied,
        Func<IReadOnlyList<string>, Task<string?>> reprint, bool cloudApiEnabled)
        => new(labels, payments, sessionId, () => new[] { labels.PlanCodeCorrection(sessionId, code) },
            new HashSet<string>(StringComparer.Ordinal), streamDate, inFlight, applied,
            reprint, cloudApiEnabled, editor: null, liveStream: true);

    /// <summary>Çekmeceyi (ya da FİYAT kutusunun altındaki satır içi sonucu) kapatır; testte boş.</summary>
    public Action? Close { get; set; }

    /// <summary>Yeniden baskı ya da toplu gönderim BİTTİ; argümanlar o işin sonucu ("Fiyat düzeltmesi: …" —
    /// baskı hatası, "elle bakılmalı" ve "WhatsApp'ta doğrulanmalı" adları) ve sonucun operatöre iş bırakıp
    /// bırakmadığı (baskı hatası; gönderilmiş sayılmayan istek) — iş bırakan kayıt kalıcı şeritten sınırla
    /// düşmez (<see cref="PrintedLabelsViewModel.AddOutcome"/>). Her iş kendi sonucunu bir kez
    /// bildirir. Çekmece iş sürerken de kapatılabilir (yayında kod/fiyat kutusunu ve kuyruğu örtmesin) — iş arka
    /// planda sürer, kabuk sonucu bitince "Basılanlar"daki kalıcı sonuç şeridine ekler: çekmece kapandıysa da
    /// operatöre ulaşır. Testte boş.</summary>
    public Action<string, bool>? WorkFinished { get; set; }

    /// <summary>Düzeltmenin yazımı (test dikişi; varsayılan <see cref="LabelService.CorrectPrices"/>) —
    /// paket içindeki çakışma (<see cref="PriceCorrectionConflictException"/>) testte başka türlü
    /// üretilemiyor.</summary>
    internal Func<IReadOnlyCollection<string>, decimal, PriceCorrectionResult>? CorrectPricesSeam { get; set; }

    /// <summary>Seçimin kodları (düzeltilebilir etiketi olanlar).</summary>
    public ObservableCollection<CodeOption> Codes { get; } = new();

    public bool HasMultipleCodes => Codes.Count > 1;

    public bool HasCorrectable => Codes.Count > 0;

    [ObservableProperty] private string _newPriceText = "";
    [ObservableProperty] private string? _newPriceError;

    /// <summary>0 TL satır içi onayı bekleniyor (onay düğmesi "Evet, 0 TL yap" olur).</summary>
    [ObservableProperty] private bool _isZeroConfirmPending;

    /// <summary>Özet satırları (onaydan önce).</summary>
    public ObservableCollection<string> Summary { get; } = new();

    /// <summary>Satır içi uyarı: uçuştaki baskı, arada değişen etiketler, hata.</summary>
    [ObservableProperty] private string? _blockingMessage;

    /// <summary>Onaylandı: sonuç görünümüne geçildi.</summary>
    [ObservableProperty] private bool _isDone;

    [ObservableProperty] private string _resultText = "";

    /// <summary>Basılmış etiket düzeltildi — "yeniden basılsın mı?" sorusu açık.</summary>
    [ObservableProperty] private bool _isReprintAsked;
    [ObservableProperty] private string _reprintQuestion = "";
    [ObservableProperty] private string? _reprintResult;

    public ObservableCollection<ResendRowViewModel> ResendRows { get; } = new();
    public ObservableCollection<string> PaidRows { get; } = new();

    public bool HasResendRows => ResendRows.Count > 0;
    public bool HasPaidRows => PaidRows.Count > 0;

    [ObservableProperty] private bool _isResendAvailable;
    [ObservableProperty] private string _resendButtonText = "";
    [ObservableProperty] private string? _resendNote;
    [ObservableProperty] private bool _isSending;

    public string ConfirmButtonText => IsZeroConfirmPending ? "Evet, 0 TL yap" : "Fiyatı düzelt";

    partial void OnIsZeroConfirmPendingChanged(bool value) => OnPropertyChanged(nameof(ConfirmButtonText));

    partial void OnNewPriceTextChanged(string value)
    {
        NewPriceError = null;
        IsZeroConfirmPending = false;
    }

    /// <summary>Kod seçeneklerini kurar. <paramref name="checkedKeys"/> null = ilk açılış: tek kod varsa işaretli,
    /// birden çoksa hiçbiri (operatör seçer — bir baskıda farklı ürünler olur, yeni fiyat hepsine yanlışlıkla
    /// uygulanmasın). Doluysa operatörün işaretleri koda göre korunur.</summary>
    private void LoadCodes(IReadOnlyList<CodeCorrectionPlan> plans, IReadOnlySet<string>? checkedKeys)
    {
        foreach (var c in Codes) c.PropertyChanged -= OnCodeChanged;
        Codes.Clear();
        _plans = plans;
        var options = plans.Where(p => p.Plan.Correctable.Count > 0).ToList();
        foreach (var p in options)
        {
            var option = new CodeOption(p, checkedKeys is null ? options.Count == 1 : checkedKeys.Contains(p.Code ?? ""));
            option.PropertyChanged += OnCodeChanged;
            Codes.Add(option);
        }
        OnPropertyChanged(nameof(HasMultipleCodes));
        OnPropertyChanged(nameof(HasCorrectable));
    }

    private IReadOnlySet<string> CheckedKeys()
        => Codes.Where(c => c.IsChecked).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Planı yeniden okunmuş hâliyle değiştirir; operatörün işaretleri koda göre korunur.</summary>
    private void ReloadPlan(IReadOnlyList<CodeCorrectionPlan> fresh)
    {
        LoadCodes(fresh, CheckedKeys());
        RecomputeSummary();
        _summaryRefresh = RefreshSummaryAsync(force: false);
    }

    private void OnCodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeOption.IsChecked)) RecomputeSummary();
    }

    /// <summary>İşaretli kodların planları (<paramref name="plans"/> içinden).</summary>
    private static List<CodeCorrectionPlan> Chosen(IEnumerable<CodeCorrectionPlan> plans, IReadOnlySet<string> keys)
        => plans.Where(p => keys.Contains(p.Code ?? "")).ToList();

    /// <summary>"+45,00" / "−60,00" (tutar farkı).</summary>
    private static string Signed(decimal amount)
        => $"{(amount >= 0 ? "+" : "−")}{Math.Abs(amount).ToString("N2", Tr)}";

    private static List<Label> DistinctLabels(IEnumerable<Label> labels)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return labels.Where(l => seen.Add(l.Id)).ToList();
    }

    private void RecomputeSummary()
    {
        var chosen = Chosen(_plans, CheckedKeys());
        var targets = DistinctLabels(chosen.SelectMany(p => p.Plan.Correctable));
        var unchangedPlans = chosen.Concat(_plans.Where(p => p.Plan.Correctable.Count == 0)).ToList();
        var shipped = DistinctLabels(unchangedPlans.SelectMany(p => p.Plan.Shipped));
        var elsewhere = DistinctLabels(unchangedPlans.SelectMany(p => p.Plan.Elsewhere));
        var shown = targets.Concat(DistinctLabels(chosen.SelectMany(p => p.Plan.Shipped.Concat(p.Plan.Elsewhere))))
            .ToList();
        _shownIds = shown.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        _shownFingerprint = PriceCorrectionPlan.FingerprintOf(_shownIds, shown);

        Summary.Clear();
        if (targets.Count == 0 && HasMultipleCodes)
        {
            Summary.Add("Düzelteceğin kodu işaretle.");
        }
        else
        {
            var customers = targets.Select(l => l.CustomerId).Distinct(StringComparer.Ordinal).Count();
            var queued = targets.Count(l => !l.PrintedAt.HasValue);
            Summary.Add($"{targets.Count} etiket / {customers} müşteri");
            Summary.Add($"{queued} kuyrukta, {targets.Count - queued} basılmış");
            var notSelected = _selection.Count == 0 ? 0 : targets.Count(l => !_selection.Contains(l.Id));
            if (notSelected > 0)
                Summary.Add($"{notSelected} etiket seçimde değil ama aynı kodda — onlar da düzelir");
            foreach (var mixed in Codes.Where(c => c.IsChecked && c.IsMixed))
                Summary.Add($"{mixed.Title}: karışık fiyat ({CodeOption.PricesText(mixed.Plan.Plan.PriceGroups)}) — " +
                            "hepsi yeni fiyata geçer");
            if (_payments is not null && targets.Count > 0)
            {
                var follow = _payments.Plan(_sessionId, targets.Select(l => (l, 0m)));
                if (follow.RequestedPersons > 0)
                    Summary.Add($"{follow.RequestedPersons} kişiye eski tutarla ödeme isteği gitmiş");
                if (follow.Paid.Count > 0)
                    Summary.Add($"{follow.Paid.Count} kişinin ödemesi alınmış (dekont/bakiye) — onlara otomatik bir şey gitmez");
            }
        }
        if (shipped.Count > 0)
            Summary.Add($"{shipped.Count} etiket kargoya verildi, değişmeyecek");
        if (elsewhere.Count > 0)
            Summary.Add($"{elsewhere.Count} etiket başka bilgisayarın dosyasında, değişmeyecek");
        var excluded = DistinctLabels(_plans.SelectMany(p => p.Plan.Excluded)).Count;
        if (excluded > 0)
            Summary.Add($"{excluded} etiket (iptal ya da kargo ücreti) düzeltmeye girmez");
        var uncheckedCodes = Codes.Count(c => !c.IsChecked);
        if (uncheckedCodes > 0 && targets.Count > 0)
            Summary.Add($"{uncheckedCodes} kod işaretsiz, değişmeyecek");
    }

    /// <summary>Onay. Yeni fiyat okunur (FİYAT kutusuyla aynı kural); 0 TL ikinci tıklamayla onaylanır.
    /// Uçuştaki baskı ya da gösterilen etiketlerin arada değişmesi düzeltmeyi durdurur (satır içi).
    ///
    /// <para>Yazım yönlendiriciden geçer (spec 2026-10-13 §5.3 "Fiyatı düzelt"): mod Off'ta ve canlı yayında bugünkü
    /// <see cref="LabelService.CorrectPrices"/> (ya da <see cref="CorrectPricesSeam"/>), beklemeden. Mod On'da
    /// devredilmiş yayının etiketleri yerel aynadan kurulan planla <c>set-code-price</c> komutuna gider (beklenen sürüm
    /// planın kendisi); fiyat, <c>PriceCorrectedAt</c> ve <c>PaidPrice</c> yanıttan yazılır. <c>plan-stale</c>'de
    /// güncel satırlar yazılmıştır: plan yeniden okunur, sebep satır içinde, sessiz yeniden deneme yok. Karışık seçimde
    /// (canlı yayın + kalıntı kuyruk) önce yazılan parça sonraki onayın sonucuna katılır.</para></summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsDone) return;
        BlockingMessage = null;

        var parsed = PriceInput.Parse(NewPriceText);
        if (!parsed.IsOk)
        {
            NewPriceError = parsed.Status switch
            {
                PriceInputStatus.Negative => "Fiyat eksi olamaz.",
                PriceInputStatus.Empty => "Yeni fiyatı yaz.",
                _ => "Geçerli bir fiyat gir (örn: 250 veya 99,50).",
            };
            return;
        }
        if (parsed.Value == 0m && !IsZeroConfirmPending)
        {
            IsZeroConfirmPending = true;
            NewPriceError = "Yeni fiyat 0 TL. Doğruysa onayla.";
            return;
        }

        // Mod On (spec 2026-10-13 §6.4; W3-10): yazımdan önceki "bakiyeden ödenmiş" kümesi (BalancePaidLabels) zaman uyumlu ve
        // yalnız önbellekten okur. Çekmece açıldıktan sonra senkron önbelleği boşaltmış ya da açılışın ön okuması henüz
        // bitmemiş olabilir: eksik anahtarlar burada ön okunur (süre ve istek bütçesiyle sınırlı; düşerse yerel kalır).
        // Yoksa başka bilgisayarın "tamamı bakiyeden" isteği sayılmaz, etiketin ödendiği fiyat (PaidPrice) saklanmazdı.
        // Plan denetiminden ÖNCE: denetimle yazım arasına ağ beklemesi girmesin. FİYAT kutusunun canlı yolunda okunmaz
        // (_liveStream): onay orada beklemeden biter — kabuk sonucu zaman uyumlu okur.
        await RefreshSummaryAsync(force: false);

        // Çekmece açıkken baskı başlamış/bitmiş olabilir: plan yeniden okunur; gösterilen etiketlerden biri
        // değiştiyse operatör yeni özeti görüp yeniden onaylar. Arada yazılmış yeni etiket kodun etiketidir —
        // gösterilenleri değiştirmez, düzeltmeye girer.
        var fresh = _planner();
        var freshLabels = fresh.SelectMany(p => p.Plan.Correctable.Concat(p.Plan.Shipped).Concat(p.Plan.Elsewhere));
        if (!string.Equals(PriceCorrectionPlan.FingerprintOf(_shownIds, freshLabels), _shownFingerprint,
                StringComparison.Ordinal))
        {
            ReloadPlan(fresh);
            BlockingMessage = "Etiketler bu arada değişti (basıldı, iptal edildi ya da fiyatı değişti). " +
                              "Özeti kontrol edip yeniden onayla.";
            return;
        }

        var chosen = Chosen(fresh, CheckedKeys());
        var targets = DistinctLabels(chosen.SelectMany(p => p.Plan.Correctable));
        if (targets.Count == 0)
        {
            BlockingMessage = HasMultipleCodes
                ? "Düzeltilecek etiket yok — en az bir kodu işaretle."
                : "Düzeltilecek etiket yok.";
            return;
        }

        var inFlight = _inFlight();
        if (inFlight is not null && targets.Any(l => inFlight.Contains(l.Id)))
        {
            BlockingMessage = "Düzelecek etiketlerden bazıları şu an yazıcıda. Baskı bitince tekrar deneyin.";
            return;
        }

        var newPrice = parsed.Value;
        SalesCodePriceResult outcome;
        try
        {
            // Kargoya verilmiş ve başka bilgisayarın dosyasındakiler de verilir: servis onlara dokunmaz, sonuçta
            // "değişmedi" diye döner.
            var ids = targets.Concat(chosen.SelectMany(p => p.Plan.Shipped.Concat(p.Plan.Elsewhere))).Select(l => l.Id)
                .Distinct(StringComparer.Ordinal).ToList();
            // Bakiyeden ödenmiş etiketler yazımdan ÖNCE okunur: ödendikleri fiyat ilk düzeltmede saklanır.
            var paidFromBalance = _payments?.BalancePaidLabels(_sessionId, targets);
            outcome = await _editor.CorrectCodePricesAsync(
                new SalesCodePriceRequest(_sessionId, chosen, _selection, ids, newPrice, paidFromBalance),
                local => CorrectPricesSeam?.Invoke(local, newPrice)
                         ?? _labels.CorrectPrices(local, newPrice, paidFromBalance));
        }
        catch (PriceCorrectionConflictException ex)
        {
            ReloadPlan(_planner());
            BlockingMessage = ex.Message + " Özeti kontrol edip yeniden onayla.";
            return;
        }
        catch (Exception ex)
        {
            BlockingMessage = $"Fiyat düzeltilemedi: {ex.Message}";
            return;
        }

        var result = Compose(outcome, chosen, targets, newPrice);
        if (outcome.Problem is { } problem)
        {
            // Yazılmış parça (karışık seçimin yerel parçası, önceki komutlar) kabukta tazelenir ve sonraki onayın
            // sonucuna katılır: yeniden baskı sorusu ve düzeltilmiş istek onları da kapsasın.
            if (result.Corrected.Count > 0)
            {
                _carried = Carry(_carried, result.Corrected);
                _applied(result);
            }
            if (outcome.Status is not null || result.Corrected.Count > 0) ReloadPlan(_planner());
            BlockingMessage = problem;
            return;
        }

        if (_carried.Count > 0)
        {
            result = result with { Corrected = Carry(_carried, result.Corrected) };
            _carried = Array.Empty<CorrectedLabel>();
        }
        _applied(result);
        ShowResult(result, newPrice);
        await ShowPaymentFollowUpAsync(result);
    }

    /// <summary>Önceki başarısız onaylarda yazılmış etiketler (karışık seçim): sonraki başarılı onayın sonucuna
    /// katılır.</summary>
    private IReadOnlyList<CorrectedLabel> _carried = Array.Empty<CorrectedLabel>();

    /// <summary>Yönlendiricinin sonucundan düzeltmenin sonucu. Yalnız yerel yazımsa bugünkü sonucun kendisi; komut
    /// varsa komutun değiştirdiği etiketler (önceki hâlleriyle) yerel parçaya eklenir, değişmeyenler planlardan.</summary>
    private static PriceCorrectionResult Compose(
        SalesCodePriceResult outcome, IReadOnlyList<CodeCorrectionPlan> chosen, IReadOnlyList<Label> targets,
        decimal newPrice)
    {
        if (!outcome.Commanded && outcome.Local is { } local) return local;

        var commanded = outcome.CommandChanged.ToHashSet(StringComparer.Ordinal);
        var byCommand = targets.Where(l => commanded.Contains(l.Id)).Select(l => new CorrectedLabel(l, newPrice)).ToList();
        var corrected = (outcome.Local?.Corrected ?? Array.Empty<CorrectedLabel>()).Concat(byCommand)
            .DistinctBy(c => c.Before.Id, StringComparer.Ordinal).ToList();
        var customerDeltas = new Dictionary<string, decimal>(
            outcome.Local?.CustomerDeltas ?? new Dictionary<string, decimal>(), StringComparer.Ordinal);
        // Müşteri toplamındaki fark (bilgi için): yalnız basılmış ve onaylı satırlar (CorrectPrices ile aynı kural).
        foreach (var c in byCommand.Where(c => c.WasPrinted && !c.Before.IsTentativeBackup))
            customerDeltas[c.Before.CustomerId] = customerDeltas.GetValueOrDefault(c.Before.CustomerId) + c.Delta;
        return new PriceCorrectionResult(
            corrected,
            DistinctLabels(chosen.SelectMany(p => p.Plan.Shipped)),
            customerDeltas,
            outcome.Local?.ShipmentDeltas ?? new Dictionary<string, decimal>(StringComparer.Ordinal),
            DistinctLabels(chosen.SelectMany(p => p.Plan.Elsewhere)));
    }

    /// <summary>Taşınan düzeltmeler + yenileri: aynı etiket yeniden düzeltildiyse önceki hâli (ilk fiyatı) korunur,
    /// yeni fiyatı yazılır.</summary>
    private static IReadOnlyList<CorrectedLabel> Carry(IReadOnlyList<CorrectedLabel> carried, IReadOnlyList<CorrectedLabel> fresh)
    {
        if (carried.Count == 0) return fresh;
        var again = fresh.ToDictionary(c => c.Before.Id, StringComparer.Ordinal);
        var known = carried.Select(c => c.Before.Id).ToHashSet(StringComparer.Ordinal);
        return carried.Select(c => again.TryGetValue(c.Before.Id, out var newer) ? c with { NewPrice = newer.NewPrice } : c)
            .Concat(fresh.Where(c => !known.Contains(c.Before.Id)))
            .ToList();
    }

    private void ShowResult(PriceCorrectionResult result, decimal newPrice)
    {
        IsDone = true;
        var queued = result.Corrected.Count(c => !c.WasPrinted);
        var printed = result.Corrected.Count - queued;
        var codes = result.Corrected.Select(c => c.Before.Code?.Trim()).OfType<string>()
            .Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _resultCodes = string.Join(", ", codes);
        var head = codes.Count == 0 ? "" : _resultCodes + ": ";
        var lines = new List<string>
        {
            result.Corrected.Count == 0
                ? "Etiketler zaten bu fiyatta; hiçbir şey değişmedi."
                : $"{head}{result.Corrected.Count} etiket {newPrice.ToString("N2", Tr)} TL'ye düzeltildi" + (queued, printed) switch
                {
                    (> 0, > 0) => $" ({queued} kuyrukta — yeni fiyatla basılacak, {printed} basılmış)",
                    (> 0, _) => $" ({queued} kuyrukta — yeni fiyatla basılacak)",
                    _ => $" ({printed} basılmış — kâğıtlarında eski fiyat yazıyor).",
                },
        };
        if (result.Shipped.Count > 0)
            lines.Add($"{result.Shipped.Count} etiket kargoya verildi, değişmedi: " +
                      string.Join(", ", result.Shipped.Select(l => l.DisplayName ?? l.Username).Distinct().Take(10)));
        if (result.Elsewhere.Count > 0)
            lines.Add($"{result.Elsewhere.Count} etiket başka bilgisayarın dosyasında, değişmedi: " +
                      string.Join(", ", result.Elsewhere.Select(l => l.DisplayName ?? l.Username).Distinct().Take(10)));
        ResultText = string.Join("\n", lines);

        // Soru nereden açıldığına (kuyruk, "Basılanlar", FİYAT şeridi) değil sonuca bağlı (K5: "basılmış etiket
        // varsa"): kodun basılmış etiketi düzeldiyse her yerde sorulur — kâğıtlarında eski fiyat kalır.
        _correctedPrinted = result.CorrectedPrinted;
        if (_correctedPrinted.Count > 0)
        {
            IsReprintAsked = true;
            ReprintQuestion = $"{_correctedPrinted.Count} basılmış etiket düzeltildi — yeniden basılsın mı?";
        }
    }

    /// <summary>
    /// Sonucun ödeme satırları (K5): eski tutarla istek gitmiş kişiler ve ödemesi alınmışlar. Mod On'da başka bilgisayarların
    /// istekleri de sayılır (spec 2026-10-13 §6.4; W3-10): önce düzeltilen etiketlerin kişilerinin uzak kayıtları ön okunur
    /// (yalnız eksikler — özet açılışta okudu; arada senkron geçersizleştirdiyse yeniden), sonra plan kurulur. Ön okuma süre ve
    /// istek bütçesiyle sınırlı; düşerse plan yerel kalır. FİYAT kutusunun canlı yolunda (<see cref="_liveStream"/>) okunmaz:
    /// liste beklemeden kurulur.
    /// </summary>
    private async Task ShowPaymentFollowUpAsync(PriceCorrectionResult result)
    {
        if (_payments is null || result.Corrected.Count == 0) return;
        if (!_liveStream)
            await _payments.PrefetchAsync(_sessionId, result.Corrected.Select(c => c.Before).Where(l => l.ShipmentId is null));

        var follow = _payments.Plan(_sessionId, result.Corrected.Select(c => (c.Before, c.Delta)));
        foreach (var c in follow.ToResend) ResendRows.Add(new ResendRowViewModel(c));
        foreach (var p in follow.Paid)
        {
            var how = (p.ByDekont, p.ByBalance) switch
            {
                (true, true) => "dekont + bakiye",
                (true, _) => "dekont",
                _ => "bakiye",
            };
            // Ödenene göre fark; bu düzeltmenin kendi farkı ondan ayrıysa (aynı etiket ikinci kez düzeltildi)
            // o da yazılır.
            PaidRows.Add($"{p.Display} — ödendi ({how}), fark {Signed(p.Delta)} TL" +
                         (p.StepDelta != p.Delta ? $" (bu düzeltmede {Signed(p.StepDelta)} TL)" : ""));
            _paidNames.Add(p.Display);
        }
        OnPropertyChanged(nameof(HasResendRows));
        OnPropertyChanged(nameof(HasPaidRows));
        IsResendAvailable = ResendRows.Count > 0;
        // Aynı kişiye yayın ve genel istek için iki mesaj gidebilir: sayı mesajları da söyler.
        var persons = ResendRows.Select(r => r.Candidate.CustomerId).Distinct(StringComparer.Ordinal).Count();
        ResendButtonText = ResendRows.Count == persons
            ? $"Düzeltilmiş isteği gönder ({persons} kişi)"
            : $"Düzeltilmiş isteği gönder ({persons} kişi, {ResendRows.Count} istek)";
        if (!_cloudApi && ResendRows.Count > 0)
            ResendNote = "WhatsApp Cloud API kapalı: her kişi için WhatsApp penceresi açılır, gönder tuşuna sen basarsın.";
    }

    /// <summary>"Yeniden basılsın mı?" → Evet. Kâğıtta "DÜZELTME" işareti; ciro ve ilk baskı anı
    /// değişmez. Basılamazsa (başka baskı sürüyor, yazıcı hatası) soru hata metniyle geri gelir —
    /// "tekrar dene" düğmesi yerinde kalır; yoksa düzeltilen satırları Basılanlar'da yeniden seçmek
    /// gerekirdi. Çekmece baskı sürerken kapatılabilir: sonuç bitince kabuğa da bildirilir
    /// (<see cref="WorkFinished"/>).</summary>
    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (!IsReprintAsked) return;
        IsReprintAsked = false;
        ReprintResult = "Yeniden basılıyor…";
        var error = await _reprint(_correctedPrinted.Select(c => c.Before.Id).ToList());
        var outcome = error ?? $"{_correctedPrinted.Count} etiket \"DÜZELTME\" işaretiyle yeniden basıldı.";
        ReprintResult = outcome;
        if (error is not null) IsReprintAsked = true;
        ReportWorkFinished(outcome, needsAction: error is not null);
    }

    [RelayCommand]
    private void SkipReprint()
    {
        if (!IsReprintAsked) return;
        IsReprintAsked = false;
        ReprintResult = "Yeniden basılmadı. Sonra \"Basılanlar\"dan seçip \"Yeniden bas\" diyebilirsin " +
                        "(kâğıtta \"DÜZELTME\" işareti olur).";
    }

    /// <summary>TEK onay: listedekilere düzeltilmiş isteği gönderir; ek soru gerektiren atlanır. Çekmece
    /// gönderim sürerken kapatılabilir: sonuç bitince kabuğa da bildirilir (<see cref="WorkFinished"/>).</summary>
    [RelayCommand]
    private async Task ResendAsync()
    {
        if (_payments is null || !IsResendAvailable || IsSending) return;
        // Başka bir düzeltmenin gönderimi sürüyor (çekmecesi kapatılmış olabilir): aynı kişiye iki istek gitmesin.
        // Düğme yerinde kalır; bitince tekrar denenir.
        if (_payments.IsResending)
        {
            ResendNote = "Önceki bir fiyat düzeltmesinin düzeltilmiş istek gönderimi sürüyor; bitince tekrar dene.";
            return;
        }
        IsSending = true;
        IsResendAvailable = false;
        string report;
        bool needsAction;
        try
        {
            var rows = ResendRows.ToList();
            // İlerleme bildirimi (Progress) bağlama POSTALANIR: gönderim baştan sona senkron bittiyse (her aday
            // beklemeden döndü — telefon gerekli, liste bayat, müşteri kaydı yok) son bildirim özetten SONRA
            // koşup notu "Gönderiliyor… n/n"e çevirirdi. Bittikten sonra gelen bildirim yok sayılır (kilit:
            // bağlam yoksa bildirim iş parçacığı havuzundan gelir).
            var progressGate = new object();
            var finished = false;
            var progress = new Progress<int>(n =>
            {
                lock (progressGate)
                    if (!finished) ResendNote = $"Gönderiliyor… {n}/{rows.Count}";
            });
            var outcomes = await _payments.ResendAsync(_sessionId, _streamDate, rows.Select(r => r.Candidate).ToList(), progress);
            lock (progressGate) finished = true;
            for (var i = 0; i < outcomes.Count; i++)
            {
                var o = outcomes[i];
                rows[i].NeedsManual = o.Status == ResendStatus.Manual;
                rows[i].OutcomeText = o.Status == ResendStatus.Manual ? "elle bakılmalı: " + o.Note : o.Note;
            }
            var summary = OutcomeSummary(outcomes);
            ResendNote = summary;
            report = "Düzeltilmiş istek: " + summary + OutcomeNames(outcomes);
            needsAction = outcomes.Any(o => o.Status != ResendStatus.Sent);
        }
        finally
        {
            IsSending = false;
        }
        ReportWorkFinished(report, needsAction);
    }

    private void ReportWorkFinished(string outcome, bool needsAction)
        => WorkFinished?.Invoke("Fiyat düzeltmesi: " + outcome, needsAction);

    /// <summary>Düzeltmenin yayını.</summary>
    internal string SessionId => _sessionId;

    /// <summary>Düzeltme bu yayındaki bu kodu kapsıyor mu (kod harf duyarsız, kod kutusuyla aynı kural). Kodlar
    /// her yayında yeniden kullanılıyor ("A1"): yalnız koda bakmak önceki yayının A1 sonucunu bu yayının A1'iyle
    /// kapsanmış sayıyordu — oysa yeni sonuç önceki yayının etiketlerini ve kişilerini içermez.</summary>
    internal bool Covers(string sessionId, string code)
        => string.Equals(_sessionId, sessionId, StringComparison.Ordinal)
           && Codes.Any(c => string.Equals(c.Key, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sonuçta CEVAPLANMADAN kalan işler, adlarıyla: yanıtlanmamış yeniden baskı sorusu, gönderilmemiş düzeltilmiş
    /// istekler, ödemesi alınmış kişiler (fark müşteriyle çözülecek). Yoksa null. Süren yeniden baskı ya da gönderim
    /// sayılmaz — o iş bitince sonucunu zaten bildirir (<see cref="WorkFinished"/>).
    ///
    /// <para>Kabuk FİYAT kutusunun altındaki sonucu operatör kapatmadan kaldırırken (yerine başka bir kodun ya da başka
    /// bir yayının sonucu geçiyor, yayın değişti) bunu "Basılanlar"daki kalıcı sonuç şeridine yazar: bekleyen kâğıt
    /// ve para işi sessizce kaybolmasın. <paramref name="reason"/> neden kaldırıldığını söyler. Tutar YAZILMAZ —
    /// sonraki bir düzeltme kişinin toplamını yeniden değiştirmiş olabilir; güncel eski/yeni tutarı müşteri
    /// kartındaki ödeme sorusu söyler.</para>
    /// </summary>
    internal string? UnansweredWork(string reason)
    {
        static string Names(IEnumerable<string> names)
            => string.Concat(names.Distinct(StringComparer.Ordinal).Select(n => $"\n• {n}"));

        var text = "";
        if (IsReprintAsked)
            text += $"\n{_correctedPrinted.Count} basılmış etiket yeniden basılmadı — kâğıtlarında eski fiyat var " +
                    "(\"Basılanlar\"dan seçip \"Yeniden bas\").";
        if (IsResendAvailable && ResendRows.Count > 0)
            text += "\nEski tutarla istek gitmiş, düzeltilmiş istek gönderilmedi (müşteri kartından gönder):" +
                    Names(ResendRows.Select(r => r.Candidate.Display));
        if (_paidNames.Count > 0)
            text += "\nÖdemesi alınmış — farkı müşteriyle çöz:" + Names(_paidNames);
        if (text.Length == 0) return null;
        var codes = _resultCodes.Length == 0 ? "" : $" ({_resultCodes})";
        return $"Fiyat düzeltmesi{codes}: {reason}; cevaplanmayanlar:{text}";
    }

    /// <summary>Sonuç şeridi için ele alınması gereken kişiler, adı ve sebebiyle ayrı satırda: "elle bakılmalı"
    /// olanlar ve WhatsApp'ta doğrulanması gerekenler (sahip kararı F5 — pencere açıldı ama gönder tuşuna basıldı
    /// mı bilinmiyor ya da gönderimin sonucu belirsiz). Sayı yetmez: kime bakılacağı bilinmezse o kişilerde eski
    /// tutarlı istek kalır.</summary>
    internal static string OutcomeNames(IReadOnlyCollection<ResendOutcome> outcomes)
    {
        static string Lines(IEnumerable<ResendOutcome> group)
            => string.Concat(group.Select(o => $"\n• {o.Candidate.Display} ({o.Note})"));
        var manual = outcomes.Where(o => o.Status == ResendStatus.Manual).ToList();
        var verify = outcomes.Where(o => o.Status is ResendStatus.Opened or ResendStatus.Pending).ToList();
        var text = "";
        if (manual.Count > 0) text += "\nElle bakılmalı:" + Lines(manual);
        if (verify.Count > 0) text += "\nWhatsApp'ta doğrulanmalı:" + Lines(verify);
        return text;
    }

    /// <summary>Toplu gönderimin özeti — sonuçlar AYRI sayılır: WhatsApp penceresi açılan istek gitmiş
    /// değildir (operatör gönder tuşuna basmadan kapatırsa müşteride eski tutarlı mesaj kalır), sonucu
    /// belirsiz olan WhatsApp'ta doğrulanmalı. Hepsini "gönderildi" saymak satırlardaki "gönder tuşuna
    /// bas" ile çelişiyordu.</summary>
    internal static string OutcomeSummary(IReadOnlyCollection<ResendOutcome> outcomes)
    {
        int Count(ResendStatus s) => outcomes.Count(o => o.Status == s);
        var parts = new List<string>();
        if (Count(ResendStatus.Sent) is > 0 and var sent) parts.Add($"{sent} istek gönderildi");
        if (Count(ResendStatus.Opened) is > 0 and var opened)
            parts.Add($"{opened} WhatsApp penceresi açıldı — her birinde gönder tuşuna bas");
        if (Count(ResendStatus.Pending) is > 0 and var pending) parts.Add($"{pending} istek WhatsApp'ta doğrulanmalı");
        if (Count(ResendStatus.Manual) is > 0 and var manual) parts.Add($"{manual} istek elle bakılmalı");
        return parts.Count == 0 ? "Gönderilecek istek yok." : string.Join("; ", parts) + ".";
    }

    [RelayCommand]
    private void Done() => Close?.Invoke();
}
