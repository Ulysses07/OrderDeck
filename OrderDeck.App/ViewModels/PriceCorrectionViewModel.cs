using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.ViewModels;

/// <summary>Düzeltmenin nereden açıldığı: kuyruk (basılmamış satırlar, K4) ya da "Basılanlar" sekmesi. Yeniden
/// baskı sorusu kipe değil sonuca bağlıdır: kuyruk kipinde de çekmece açıkken basılan satır düzeldiyse
/// sorulur.</summary>
public enum PriceCorrectionMode
{
    Queue,
    Printed,
}

/// <summary>Seçimdeki bir fiyat grubu — operatör hangilerinin düzeleceğini işaretler.</summary>
public sealed partial class PriceGroupOption : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public PriceGroupOption(PriceGroup group) => Group = group;

    public PriceGroup Group { get; }
    public decimal Price => Group.Price;
    public string Text => $"{Group.Price.ToString("N2", Tr)} TL × {Group.Count}";

    [ObservableProperty] private bool _isChecked = true;
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
            return $"{c.Display} — {scope}: {at}{c.PriorAmount.ToString("N2", Tr)} TL {was}; yeni tutar {c.NewTotal.ToString("N2", Tr)} TL";
        }
    }

    /// <summary>Gönderimden sonra: "gönderildi", "elle bakılmalı: …". Gönderilmeden önce boş.</summary>
    [ObservableProperty] private string? _outcomeText;
    [ObservableProperty] private bool _needsManual;
}

/// <summary>
/// "Fiyatı düzelt" çekmecesi (sahip kararları K4/K5) — pop-up YOK: form çekmecede, onaylar satır içi.
///
/// <para>Akış: seçimdeki fiyat grupları gösterilir (varsayılan hepsi işaretli; birden çok fiyat varsa
/// uyarı), operatör yeni fiyatı yazar, özet görünür (N etiket / M müşteri, kuyruk/basılmış, eski tutarla
/// istek gitmiş K kişi, ödemesi alınmış P kişi, kargoya verilmiş S etiket — değişmez). Onay tek pakette
/// yazar (<see cref="LabelService.CorrectPrices"/>); kabuk kuyruktaki satırları yerinde günceller. Sonra:
/// basılmış etiket varsa "yeniden basılsın mı?" (varsayılan Evet), teslim edilmiş isteği değişen kişiler
/// için "düzeltilmiş isteği gönder".</para>
///
/// <para><b>Uçuştaki baskı:</b> seçimde o an yazıcıda olan etiket varsa düzeltme reddedilir — kâğıda
/// eski fiyat çıkar, ciro yeni fiyatla yazılırdı. Onay anında yeniden denetlenir (çekmece açıkken baskı
/// başlamış olabilir).</para>
/// </summary>
public sealed partial class PriceCorrectionViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly LabelService _labels;
    private readonly CorrectedPaymentRequests? _payments;
    private readonly string _sessionId;
    private readonly IReadOnlyList<string> _labelIds;
    private readonly DateTime _streamDate;
    private readonly Func<IReadOnlySet<string>?> _inFlight;
    private readonly Action<PriceCorrectionResult> _applied;
    private readonly Func<IReadOnlyList<string>, Task<string?>> _reprint;
    private readonly bool _cloudApi;

    private PriceCorrectionPlan _plan;
    private string _shownFingerprint = "";
    private IReadOnlyList<CorrectedLabel> _correctedPrinted = Array.Empty<CorrectedLabel>();

    /// <param name="reprint">Kabuğun yeniden baskı yolu (yazıcı kapısı paylaşılır); hata metni ya da null.</param>
    /// <param name="cloudApiEnabled">WhatsApp Cloud API açık mı — kapalıysa her kişi için WhatsApp
    /// penceresi açılacağı söylenir.</param>
    public PriceCorrectionViewModel(
        LabelService labels, CorrectedPaymentRequests? payments, string sessionId,
        IReadOnlyList<string> labelIds, PriceCorrectionMode mode, DateTime streamDate,
        Func<IReadOnlySet<string>?> inFlight, Action<PriceCorrectionResult> applied,
        Func<IReadOnlyList<string>, Task<string?>> reprint, bool cloudApiEnabled)
    {
        _labels = labels;
        _payments = payments;
        _sessionId = sessionId;
        _labelIds = labelIds;
        Mode = mode;
        _streamDate = streamDate;
        _inFlight = inFlight;
        _applied = applied;
        _reprint = reprint;
        _cloudApi = cloudApiEnabled;

        _plan = _labels.PlanPriceCorrection(labelIds);
        LoadGroups();
        RecomputeSummary();
    }

    public PriceCorrectionMode Mode { get; }

    /// <summary>Çekmeceyi kapatır (fabrika bağlar); testte boş.</summary>
    public Action? Close { get; set; }

    /// <summary>Düzeltmenin yazımı (test dikişi; varsayılan <see cref="LabelService.CorrectPrices"/>) —
    /// paket içindeki çakışma (<see cref="PriceCorrectionConflictException"/>) testte başka türlü
    /// üretilemiyor.</summary>
    internal Func<IReadOnlyCollection<string>, decimal, PriceCorrectionResult>? CorrectPricesSeam { get; set; }

    public ObservableCollection<PriceGroupOption> PriceGroups { get; } = new();

    public bool HasMultiplePrices => PriceGroups.Count > 1;

    public bool HasCorrectable => PriceGroups.Count > 0;

    [ObservableProperty] private string _newPriceText = "";
    [ObservableProperty] private string? _newPriceError;

    /// <summary>0 TL satır içi onayı bekleniyor (onay düğmesi "Evet, 0 TL yap" olur).</summary>
    [ObservableProperty] private bool _isZeroConfirmPending;

    /// <summary>Özet satırları (onaydan önce).</summary>
    public ObservableCollection<string> Summary { get; } = new();

    /// <summary>Satır içi uyarı: uçuştaki baskı, arada değişen etiketler, hata.</summary>
    [ObservableProperty] private string? _blockingMessage;

    /// <summary>Onaylandı: çekmece sonuç görünümüne geçti.</summary>
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

    private void LoadGroups()
    {
        foreach (var g in PriceGroups) g.PropertyChanged -= OnGroupChanged;
        PriceGroups.Clear();
        foreach (var g in _plan.PriceGroups)
        {
            var option = new PriceGroupOption(g);
            option.PropertyChanged += OnGroupChanged;
            PriceGroups.Add(option);
        }
        OnPropertyChanged(nameof(HasMultiplePrices));
        OnPropertyChanged(nameof(HasCorrectable));
    }

    /// <summary>Planı yeniden okunmuş hâliyle değiştirir; operatörün işaretleri fiyata göre korunur —
    /// bilerek kaldırdığı fiyat grubu (ör. pazarlıklı satış) yeniden onayda sessizce düzelmesin. Arada
    /// beliren yeni fiyat grubu işaretsiz gelir (operatör onu hiç görmedi).</summary>
    private void ReloadPlan(PriceCorrectionPlan fresh)
    {
        var checkedPrices = PriceGroups.Where(g => g.IsChecked).Select(g => g.Price).ToHashSet();
        _plan = fresh;
        LoadGroups();
        foreach (var g in PriceGroups) g.IsChecked = checkedPrices.Contains(g.Price);
        RecomputeSummary();
    }

    private void OnGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PriceGroupOption.IsChecked)) RecomputeSummary();
    }

    /// <summary>İşaretli fiyat gruplarındaki düzeltilebilir etiketler.</summary>
    private IReadOnlyList<Label> Targets()
    {
        var prices = PriceGroups.Where(g => g.IsChecked).Select(g => g.Price).ToHashSet();
        return _plan.Correctable.Where(l => prices.Contains(l.Price)).ToList();
    }

    private void RecomputeSummary()
    {
        _shownFingerprint = _plan.Fingerprint;
        Summary.Clear();
        var targets = Targets();
        var customers = targets.Select(l => l.CustomerId).Distinct(StringComparer.Ordinal).Count();
        var queued = targets.Count(l => !l.PrintedAt.HasValue);
        var printed = targets.Count - queued;
        Summary.Add($"{targets.Count} etiket / {customers} müşteri");
        Summary.Add($"{queued} kuyrukta, {printed} basılmış");
        if (_payments is not null && targets.Count > 0)
        {
            var follow = _payments.Plan(_sessionId, targets.Select(l => (l, 0m)));
            if (follow.RequestedPersons > 0)
                Summary.Add($"{follow.RequestedPersons} kişiye eski tutarla ödeme isteği gitmiş");
            if (follow.Paid.Count > 0)
                Summary.Add($"{follow.Paid.Count} kişinin ödemesi alınmış (dekont/bakiye) — onlara otomatik bir şey gitmez");
        }
        if (_plan.Shipped.Count > 0)
            Summary.Add($"{_plan.Shipped.Count} etiket kargoya verildi, değişmeyecek");
        if (_plan.Excluded.Count > 0)
            Summary.Add($"{_plan.Excluded.Count} etiket (iptal ya da kargo ücreti) düzeltmeye girmez");
        var unchecked_ = PriceGroups.Where(g => !g.IsChecked).Sum(g => g.Group.Count);
        if (unchecked_ > 0)
            Summary.Add($"{unchecked_} etiket işaretsiz fiyatta, değişmeyecek");
    }

    /// <summary>Onay. Yeni fiyat okunur (FİYAT kutusuyla aynı kural); 0 TL ikinci tıklamayla onaylanır.
    /// Uçuştaki baskı ya da arada değişen etiketler düzeltmeyi durdurur (satır içi).</summary>
    [RelayCommand]
    private void Confirm()
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

        // Çekmece açıkken baskı başlamış/bitmiş olabilir: plan yeniden okunur; özet değiştiyse operatör
        // yeni özeti görüp yeniden onaylar.
        var fresh = _labels.PlanPriceCorrection(_labelIds);
        if (!string.Equals(fresh.Fingerprint, _shownFingerprint, StringComparison.Ordinal))
        {
            ReloadPlan(fresh);
            BlockingMessage = "Etiketler bu arada değişti (basıldı, iptal edildi ya da fiyatı değişti). " +
                              "Özeti kontrol edip yeniden onayla.";
            return;
        }

        var targets = Targets();
        if (targets.Count == 0)
        {
            BlockingMessage = "Düzeltilecek etiket yok — en az bir fiyat grubunu işaretle.";
            return;
        }

        var inFlight = _inFlight();
        if (inFlight is not null && targets.Any(l => inFlight.Contains(l.Id)))
        {
            BlockingMessage = "Seçimdeki etiketlerden bazıları şu an yazıcıda. Baskı bitince tekrar deneyin.";
            return;
        }

        PriceCorrectionResult result;
        try
        {
            var ids = targets.Select(l => l.Id).ToList();
            result = CorrectPricesSeam?.Invoke(ids, parsed.Value) ?? _labels.CorrectPrices(ids, parsed.Value);
        }
        catch (PriceCorrectionConflictException ex)
        {
            ReloadPlan(_labels.PlanPriceCorrection(_labelIds));
            BlockingMessage = ex.Message + " Özeti kontrol edip yeniden onayla.";
            return;
        }
        catch (Exception ex)
        {
            BlockingMessage = $"Fiyat düzeltilemedi: {ex.Message}";
            return;
        }

        _applied(result);
        ShowResult(result);
    }

    private void ShowResult(PriceCorrectionResult result)
    {
        IsDone = true;
        var queued = result.Corrected.Count(c => !c.WasPrinted);
        var printed = result.Corrected.Count - queued;
        var lines = new List<string>
        {
            result.Corrected.Count == 0
                ? "Seçilen etiketler zaten bu fiyatta; hiçbir şey değişmedi."
                : $"{result.Corrected.Count} etiket düzeltildi" + (queued, printed) switch
                {
                    (> 0, > 0) => $" ({queued} kuyrukta — yeni fiyatla basılacak, {printed} basılmış)",
                    (> 0, _) => $" ({queued} kuyrukta — yeni fiyatla basılacak)",
                    // Kuyruk kipinde de olur: çekmece açıkken satırlar basıldı.
                    _ => $" ({printed} basılmış — kâğıtlarında eski fiyat yazıyor).",
                },
        };
        if (result.Shipped.Count > 0)
            lines.Add($"{result.Shipped.Count} etiket kargoya verildi, değişmedi: " +
                      string.Join(", ", result.Shipped.Select(l => l.DisplayName ?? l.Username).Distinct().Take(10)));
        ResultText = string.Join("\n", lines);

        // Soru kipe değil sonuca bağlı (K5: "basılmış etiket varsa"): kuyruk kipinde açılan çekmece, açıkken
        // basılan satırları basılmış olarak düzeltir — kâğıtlarında eski fiyat kalır.
        _correctedPrinted = result.CorrectedPrinted;
        if (_correctedPrinted.Count > 0)
        {
            IsReprintAsked = true;
            ReprintQuestion = $"{_correctedPrinted.Count} basılmış etiket düzeltildi — yeniden basılsın mı?";
        }

        if (_payments is not null && result.Corrected.Count > 0)
        {
            var follow = _payments.Plan(_sessionId, result.Corrected.Select(c => (c.Before, c.Delta)));
            foreach (var c in follow.ToResend) ResendRows.Add(new ResendRowViewModel(c));
            foreach (var p in follow.Paid)
                PaidRows.Add($"{p.Display} — ödendi ({(p.ByDekont ? "dekont" : "bakiye")}), fark " +
                             $"{(p.Delta >= 0 ? "+" : "−")}{Math.Abs(p.Delta).ToString("N2", Tr)} TL");
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
    }

    /// <summary>"Yeniden basılsın mı?" → Evet. Kâğıtta "DÜZELTME" işareti; ciro ve ilk baskı anı
    /// değişmez. Basılamazsa (başka baskı sürüyor, yazıcı hatası) soru hata metniyle geri gelir —
    /// "tekrar dene" düğmesi yerinde kalır; yoksa düzeltilen satırları Basılanlar'da yeniden seçmek
    /// gerekirdi.</summary>
    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (!IsReprintAsked) return;
        IsReprintAsked = false;
        ReprintResult = "Yeniden basılıyor…";
        var error = await _reprint(_correctedPrinted.Select(c => c.Before.Id).ToList());
        ReprintResult = error is null
            ? $"{_correctedPrinted.Count} etiket \"DÜZELTME\" işaretiyle yeniden basıldı."
            : error;
        if (error is not null) IsReprintAsked = true;
    }

    [RelayCommand]
    private void SkipReprint()
    {
        if (!IsReprintAsked) return;
        IsReprintAsked = false;
        ReprintResult = "Yeniden basılmadı. Sonra \"Basılanlar\"dan seçip \"Yeniden bas\" diyebilirsin " +
                        "(kâğıtta \"DÜZELTME\" işareti olur).";
    }

    /// <summary>TEK onay: listedekilere düzeltilmiş isteği gönderir; ek soru gerektiren atlanır.</summary>
    [RelayCommand]
    private async Task ResendAsync()
    {
        if (_payments is null || !IsResendAvailable || IsSending) return;
        IsSending = true;
        IsResendAvailable = false;
        try
        {
            var rows = ResendRows.ToList();
            var progress = new Progress<int>(n => ResendNote = $"Gönderiliyor… {n}/{rows.Count}");
            var outcomes = await _payments.ResendAsync(_sessionId, _streamDate, rows.Select(r => r.Candidate).ToList(), progress);
            for (var i = 0; i < outcomes.Count; i++)
            {
                var o = outcomes[i];
                rows[i].NeedsManual = o.Status == ResendStatus.Manual;
                rows[i].OutcomeText = o.Status == ResendStatus.Manual ? "elle bakılmalı: " + o.Note : o.Note;
            }
            var sent = outcomes.Count(o => o.Status != ResendStatus.Manual);
            var manual = outcomes.Count - sent;
            ResendNote = manual == 0
                ? $"{sent} istek gönderildi."
                : $"{sent} istek gönderildi, {manual} istek elle bakılmalı.";
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void Done() => Close?.Invoke();
}
