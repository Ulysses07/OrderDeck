using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

/// <summary>Satırın kısa durum rozeti (K2). Öncelik: kargoda › ödendi › yedek › ödeme istendi.</summary>
public enum PrintedLabelStatus
{
    None,
    PaymentRequested,
    Backup,
    Paid,
    Shipped,
}

/// <summary>Bir "Yazdır" basışı — "Basılanlar" listesinin grubu. Grup tanımı nesne kimliği: aynı basışın
/// bütün satırları aynı örneği taşır (CollectionView gruplaması eşitlikle çalışır).</summary>
public sealed partial class PrintBatchViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public PrintBatchViewModel(string key, long printedAt)
    {
        Key = key;
        PrintedAt = printedAt;
    }

    public string Key { get; }
    public long PrintedAt { get; }

    /// <summary>"21:20 baskısı" — bugün değilse tarihli.</summary>
    public string Title
    {
        get
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(PrintedAt).ToLocalTime();
            var when = local.Date == DateTime.Today ? local.ToString("HH:mm", Tr) : local.ToString("dd.MM HH:mm", Tr);
            return $"{when} baskısı";
        }
    }

    public override string ToString() => Title;
}

/// <summary>"Basılanlar" satırı (K2): grup içi sıra no, kullanıcı adı (kâğıttaki gibi), ürün kodu, fiyat,
/// durum rozeti.</summary>
public sealed partial class PrintedLabelRowViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public PrintedLabelRowViewModel(
        PrintedLabelRow row, PrintBatchViewModel batch, int seq, PrintedLabelStatus status)
    {
        Label = row.Label;
        Batch = batch;
        Seq = seq;
        Status = status;
        IsCorrected = row.PriceCorrectedAt is not null;
    }

    /// <summary>Satır verilen hâli zaten gösteriyor mu — yeniden kurmada aynı satır nesnesi korunur
    /// (görünüm kabını yeniden üretmez). Senkron alanları (SyncedAt, Revision) görünmez; sayılmaz.</summary>
    internal bool Shows(PrintedLabelRow row, PrintBatchViewModel batch, int seq, PrintedLabelStatus status)
        => ReferenceEquals(Batch, batch) && Seq == seq && Status == status
           && IsCorrected == (row.PriceCorrectedAt is not null)
           && Label with { SyncedAt = null, Revision = 0 } == row.Label with { SyncedAt = null, Revision = 0 };

    public Label Label { get; }
    public PrintBatchViewModel Batch { get; }
    public int Seq { get; }
    public PrintedLabelStatus Status { get; }
    public bool IsCorrected { get; }

    public string Id => Label.Id;
    public string CustomerId => Label.CustomerId;

    /// <summary>Kâğıttaki ad (LabelPrintDocument.ResolveDisplayLabel ile aynı kural).</summary>
    public string Display => string.IsNullOrWhiteSpace(Label.DisplayName) ? Label.Username : Label.DisplayName!.Trim();

    public string CodeText => Label.IsShippingFee ? "kargo" : Label.Code ?? "—";
    public string PriceText => Label.Price.ToString("N2", Tr) + " TL";

    public string StatusText => Status switch
    {
        PrintedLabelStatus.Shipped => "kargoda",
        PrintedLabelStatus.Paid => "ödendi",
        PrintedLabelStatus.Backup => "yedek",
        PrintedLabelStatus.PaymentRequested => "ödeme istendi",
        _ => "",
    };

    public bool HasStatus => Status != PrintedLabelStatus.None;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// "Basılanlar" sekmesi (sahip kararları K1/K2/K6/K7): yayının basılmış, iptal edilmemiş etiketleri —
/// müşteri adına göre DEĞİL "Yazdır" basışına göre gruplu, en yeni basış en üstte, grubun içi kâğıdın
/// çıktığı sırada. Arama (kod ya da kullanıcı adı), çoklu seçim, "bu baskıyı seç", "bu kodu seç".
///
/// <para>Etiketler yalnız onları yazan bilgisayarda (K7): liste bu bilgisayarın etiketleridir. Eylemler
/// (fiyatı düzelt, yeniden bas, iptal) kabukta — çekmece ve yazıcı onun.</para>
/// </summary>
public sealed partial class PrintedLabelsViewModel : ObservableObject
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly LabelRepository _labels;
    private readonly Func<string, IReadOnlyCollection<Label>, PaymentBadges>? _paymentBadges;
    private bool _suppressSelectionCount;

    /// <param name="paymentBadges">(yayın, etiketler) → ödeme rozetleri ("ödeme istendi", bakiyeden
    /// "ödendi" — <see cref="CorrectedPaymentRequests.Badges"/>, fiyat düzeltmesinin ödeme listesiyle aynı
    /// tanım). Null = ödeme rozeti hesaplanmaz.</param>
    public PrintedLabelsViewModel(
        LabelRepository labels,
        Func<string, IReadOnlyCollection<Label>, PaymentBadges>? paymentBadges = null)
    {
        _labels = labels;
        _paymentBadges = paymentBadges;
    }

    public ObservableCollection<PrintedLabelRowViewModel> Rows { get; } = new();

    /// <summary>Aramaya uyan satırlar, aynı sırayla — liste buna bağlanır; gruplamayı görünüm yapar
    /// (XAML'deki CollectionViewSource, basışa göre). VM'de CollectionView TUTULMAZ: görünüm onu yaratan
    /// iş parçacığına bağlıdır, baskı sonrası tazeleme ise testte başka iş parçacığından gelir.</summary>
    public ObservableCollection<PrintedLabelRowViewModel> VisibleRows { get; } = new();

    /// <summary>Gösterilen yayın. Null = yayın yok, liste boş.</summary>
    [ObservableProperty] private string? _sessionId;

    /// <summary>Rapordan açılmış, aktif olmayan bir yayın (K6) — başlık şeridi bunu söyler ve "bu
    /// yayına dön" düğmesi çıkar.</summary>
    [ObservableProperty] private bool _isOtherSession;

    [ObservableProperty] private string _otherSessionTitle = "";

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private int _selectedCount;

    [ObservableProperty] private int _totalCount;

    /// <summary>Satır içi sonuç/uyarı ("8 etiket iptal edildi", "yeniden basılamadı …").</summary>
    [ObservableProperty] private string? _message;

    /// <summary>Seçim değişince önceki işlemin satır içi sonucu kalkar.</summary>
    partial void OnSelectedCountChanged(int value) => Message = null;

    /// <summary>Şeritte tutulan en çok sonuç sayısı; fazlası en eski BİLGİ kaydından düşer — iş bekleyen kayıt
    /// (<see cref="AddOutcome"/>) hiç düşmez, şerit o yüzden sınırı aşabilir.</summary>
    internal const int MaxOutcomes = 10;

    /// <summary><see cref="Outcomes"/> ile aynı sırada: kayıt operatörün yapacağı işi taşıyor mu.</summary>
    private readonly List<bool> _outcomeNeedsAction = new();

    /// <summary>Arka planda biten işlerin sonuçları (fiyat düzeltmesinin yeniden baskısı ve düzeltilmiş istek
    /// gönderimi), geliş sırasıyla — kalıcı sonuç şeridi. Uçucu <see cref="Message"/>'ın tersine seçim, başka
    /// ileti, iptal ve tazeleme bunları SİLMEZ; yalnız operatör kapatır (<see cref="DismissOutcomesCommand"/>).
    /// Sonuç dakikalar sonra, operatör başka iş yaparken gelebilir: "elle bakılmalı" adları ve sebepleri
    /// okunmadan kaybolursa o kişilerde eski tutarlı istek kalır ve kime bakılacağı bilinmez.</summary>
    public ObservableCollection<string> Outcomes { get; } = new();

    public bool HasOutcomes => Outcomes.Count > 0;

    /// <summary>Şeride operatörün henüz görmediği sonuç eklendi (sahip kararı F5): "Basılanlar" sekme başlığında
    /// işaret. Sekme açılınca (<see cref="MarkOutcomesRead"/>) ya da "Tamam"la kalkar. Sonuç dakikalar sonra,
    /// operatör Kuyruk sekmesindeyken gelebilir — işaret olmasa şerit hiç görülmeyebilirdi.</summary>
    [ObservableProperty] private bool _hasUnreadOutcomes;

    /// <summary>Şeride bir sonuç ekler (saatiyle). Okunmadı işareti konur.
    ///
    /// <para><paramref name="needsAction"/>: kayıt operatörün yapacağı işi taşıyor — cevaplanmamış yeniden baskı
    /// sorusu, gönderilmemiş düzeltilmiş istek, ödenmişlerin farkı, "elle bakılmalı"/"WhatsApp'ta doğrulanmalı"
    /// adları, basılamamış etiketler. Böyle kayıt sınırla ASLA düşmez: şerit en çok <see cref="MaxOutcomes"/>
    /// kayıtta tutulurken yalnız bilgi kayıtları (biten iş) en eskiden düşer — en yenisi hariç, yeni gelen kayıt
    /// görünmeden silinmesin. Eskiden en eski kayıt hangisiyse o düşüyordu: okunup "Tamam" denmeden açık bırakılan
    /// şeritte yeni sonuçlar biriktikçe cevaplanmamış iş iz bırakmadan siliniyordu. Şerit yalnız operatörün
    /// "Tamam"ıyla boşalır.</para></summary>
    public void AddOutcome(string outcome, bool needsAction = false)
    {
        Outcomes.Add($"{DateTime.Now.ToString("HH:mm", Tr)} — {outcome}");
        _outcomeNeedsAction.Add(needsAction);
        while (Outcomes.Count > MaxOutcomes)
        {
            var oldestInfo = _outcomeNeedsAction.FindIndex(0, Outcomes.Count - 1, a => !a);
            if (oldestInfo < 0) break;
            Outcomes.RemoveAt(oldestInfo);
            _outcomeNeedsAction.RemoveAt(oldestInfo);
        }
        OnPropertyChanged(nameof(HasOutcomes));
        HasUnreadOutcomes = true;
    }

    /// <summary>Operatör sekmeyi açtı: şerit görünüyor, işaret kalkar.</summary>
    public void MarkOutcomesRead() => HasUnreadOutcomes = false;

    /// <summary>Operatör şeridi kapattı ("Tamam").</summary>
    [RelayCommand]
    private void DismissOutcomes()
    {
        Outcomes.Clear();
        _outcomeNeedsAction.Clear();
        OnPropertyChanged(nameof(HasOutcomes));
        HasUnreadOutcomes = false;
    }

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => $"{SelectedCount} seçili";

    /// <summary>Seçili satırlar, listenin sırasıyla (en yeni basış önce, grubun içi kâğıt sırası).</summary>
    public IReadOnlyList<PrintedLabelRowViewModel> SelectedRows => Rows.Where(r => r.IsSelected).ToList();

    public IReadOnlyList<string> SelectedIds => SelectedRows.Select(r => r.Id).ToList();

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisible();
        // Görünmeyen satır seçili kalmasın: araç çubuğu yalnız gördüğün satırlara uygulansın.
        _suppressSelectionCount = true;
        foreach (var r in Rows)
            if (r.IsSelected && !Matches(r)) r.IsSelected = false;
        _suppressSelectionCount = false;
        UpdateSelectedCount();
    }

    private bool Matches(object item)
    {
        if (item is not PrintedLabelRowViewModel r) return false;
        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return Contains(r.Label.Code, q) || Contains(r.Display, q) || Contains(r.Label.Username, q);
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && Tr.CompareInfo.IndexOf(haystack, needle, CompareOptions.IgnoreCase) >= 0;

    /// <summary>Verilen yayının listesini kurar ya da günceller; seçim Id'ye göre korunur.
    ///
    /// <para><b>Silip baştan kurmaz</b> (aynı yayında): satırı değişmeyen satır nesnesi (ve basışı) korunur,
    /// yalnız yeni/değişen satırlar eklenir, kalkanlar çıkarılır. Liste gruplu ve sanallaştırmasız; her
    /// "Yazdır"dan sonra yayının bütün satırlarını yeniden eklemek UI iş parçacığını yüzlerce etikette
    /// saniyeye yakın kilitliyordu — yeni basış grubu başa eklenir, gerisi yerinde kalır.</para></summary>
    /// <param name="withPaymentStatus">Ödeme rozetleri için kişi başına ödeme işi taranır mı.</param>
    public void Load(string? sessionId, bool withPaymentStatus = true)
    {
        var sameSession = string.Equals(sessionId, SessionId, StringComparison.Ordinal);
        var keepSelected = sameSession
            ? Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var oldRows = sameSession
            ? Rows.ToDictionary(r => r.Id, StringComparer.Ordinal)
            : new Dictionary<string, PrintedLabelRowViewModel>(StringComparer.Ordinal);
        var oldBatches = new Dictionary<string, PrintBatchViewModel>(StringComparer.Ordinal);
        foreach (var r in oldRows.Values) oldBatches[r.Batch.Key] = r.Batch;
        SessionId = sessionId;

        var desired = new List<PrintedLabelRowViewModel>();
        if (sessionId is not null)
        {
            var rows = _labels.GetPrintedBySession(sessionId);
            var badges = (withPaymentStatus ? _paymentBadges : null)?.Invoke(sessionId,
                    rows.Select(r => r.Label).ToList())
                ?? PaymentBadges.None;

            // Grup: basış kimliği (048 öncesi: aynı PrintedAt). En yeni basış önce — kimlik basış sırasıyla
            // artar (LabelService.NewPrintBatchId), aynı saniyedeki iki basış da doğru sıralanır. Grubun içi
            // kâğıt sırası (048 öncesi: AddedAt, Id).
            var groups = rows
                .GroupBy(r => r.BatchKey, StringComparer.Ordinal)
                .Select(g => (Key: g.Key, PrintedAt: g.Min(r => r.Label.PrintedAt!.Value), Items: g
                    .OrderBy(r => r.PrintSeq ?? int.MaxValue)
                    .ThenBy(r => r.Label.AddedAt)
                    .ThenBy(r => r.Label.Id, StringComparer.Ordinal)
                    .ToList()))
                .OrderByDescending(g => g.PrintedAt)
                .ThenByDescending(g => g.Key, StringComparer.Ordinal);

            foreach (var g in groups)
            {
                var batch = oldBatches.TryGetValue(g.Key, out var known) && known.PrintedAt == g.PrintedAt
                    ? known
                    : new PrintBatchViewModel(g.Key, g.PrintedAt);
                for (var i = 0; i < g.Items.Count; i++)
                {
                    var row = g.Items[i];
                    var seq = row.PrintSeq ?? i + 1;
                    var status = StatusOf(row, badges);
                    if (oldRows.TryGetValue(row.Label.Id, out var old) && old.Shows(row, batch, seq, status))
                    {
                        desired.Add(old);
                        continue;
                    }
                    desired.Add(new PrintedLabelRowViewModel(row, batch, seq, status)
                    {
                        IsSelected = keepSelected.Contains(row.Label.Id),
                    });
                }
            }
        }

        _suppressSelectionCount = true;
        var kept = desired.ToHashSet();
        foreach (var r in Rows)
            if (!kept.Contains(r)) r.PropertyChanged -= OnRowChanged;
        var existing = Rows.ToHashSet();
        foreach (var r in desired)
            if (!existing.Contains(r)) r.PropertyChanged += OnRowChanged;
        SyncTo(Rows, desired);
        _suppressSelectionCount = false;
        TotalCount = Rows.Count;
        RebuildVisible();
        UpdateSelectedCount();
    }

    /// <summary>Sekme kapalıyken: liste kurulmaz (görünüm kapları gizli listede de üretilir), yalnız sekme
    /// başlığındaki sayı güncellenir. Sekme açılınca <see cref="Load"/>.</summary>
    public void UpdateCount(string? sessionId)
        => TotalCount = sessionId is null ? 0 : _labels.CountPrintedBySession(sessionId);

    private void RebuildVisible() => SyncTo(VisibleRows, Rows.Where(Matches).ToList());

    /// <summary><paramref name="target"/>'ı <paramref name="desired"/>'a en az değişiklikle getirir (nesne
    /// kimliğiyle): kalkanlar çıkarılır, yeniler yerine eklenir, kalanlar yerinde durur.</summary>
    private static void SyncTo<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var want = desired.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!want.Contains(target[i])) target.RemoveAt(i);
        var present = target.ToHashSet();
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            if (present.Contains(desired[i])) target.Move(target.IndexOf(desired[i]), i);
            else target.Insert(i, desired[i]);
        }
    }

    public void Refresh() => Load(SessionId);

    private static PrintedLabelStatus StatusOf(PrintedLabelRow row, PaymentBadges badges)
    {
        if (row.ShipmentStatus == ShipmentStatus.Shipped) return PrintedLabelStatus.Shipped;
        // Dekont kaydedilince etiket açık kargo dosyasına bağlanır (DekontEkleViewModel): ödendi. Etiketi
        // içeren isteğin tamamı bakiyeden düşüldüyse de ödendi — fiyat düzeltme çekmecesiyle aynı tanım.
        if (row.Label.ShipmentId is not null || badges.BalancePaidLabels.Contains(row.Label.Id))
            return PrintedLabelStatus.Paid;
        if (row.Label.IsTentativeBackup) return PrintedLabelStatus.Backup;
        // Etiket düzeyinde: kişinin isteği bu etiketi İÇERİYORSA (basıldıktan sonra gitti) — çekmecenin
        // "istek gitmiş" tanımı. Kişi düzeyinde olsaydı istekten sonra basılan etiket de rozet alırdı.
        if (badges.RequestedLabels.Contains(row.Label.Id)) return PrintedLabelStatus.PaymentRequested;
        return PrintedLabelStatus.None;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrintedLabelRowViewModel.IsSelected) && !_suppressSelectionCount)
            UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = Rows.Count(r => r.IsSelected);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seçim değişti (sayı aynı kalsa da — ör. bir satırın yerine başkası seçildi): kabuk düğmelerin
    /// pasiflik sebebini yeniden hesaplar (spec 2026-10-13 §5.7). Toplu seçimlerde bir kez.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>"Bu baskıyı seç": grubun (görünen) bütün satırları seçime eklenir.</summary>
    [RelayCommand]
    private void SelectBatch(PrintBatchViewModel? batch)
    {
        if (batch is null) return;
        SetSelected(Rows.Where(r => ReferenceEquals(r.Batch, batch) && Matches(r)));
    }

    /// <summary>"Bu kodu seç": yayındaki aynı koddaki BÜTÜN basılmış etiketler, gruplar arası. Arama kutusu
    /// koda ayarlanır — seçilenlerin hepsi görünür olsun. Seçim bu kodun satırlarıyla DEĞİŞTİRİLİR (eklenmez):
    /// arama alt dize eşleşmesi yaptığı için "A1" aramasında A12 satırları da görünür kalır, önceden seçiliyse
    /// seçili kalır — "yalnız A1" sanılan düzeltme A12'lere de uygulanırdı.</summary>
    [RelayCommand]
    private void SelectCode(PrintedLabelRowViewModel? row)
    {
        var code = row?.Label.Code;
        if (string.IsNullOrWhiteSpace(code)) return;
        SearchText = code;
        _suppressSelectionCount = true;
        foreach (var r in Rows) r.IsSelected = string.Equals(r.Label.Code, code, StringComparison.OrdinalIgnoreCase);
        _suppressSelectionCount = false;
        UpdateSelectedCount();
    }

    /// <summary>Araç çubuğundaki "Bu kodu seç": seçili satırların kod(lar)ındaki bütün basılmış etiketler.
    /// Tek kod varsa arama kutusu o koda ayarlanır.</summary>
    [RelayCommand]
    private void SelectCodeOfSelection()
    {
        var codes = Rows.Where(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.Label.Code))
            .Select(r => r.Label.Code!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0) return;
        if (codes.Count == 1)
        {
            SelectCode(Rows.First(r => string.Equals(r.Label.Code, codes[0], StringComparison.OrdinalIgnoreCase)));
            return;
        }
        SetSelected(Rows.Where(r => r.Label.Code is { } c && codes.Contains(c, StringComparer.OrdinalIgnoreCase) && Matches(r)));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _suppressSelectionCount = true;
        foreach (var r in Rows) r.IsSelected = false;
        _suppressSelectionCount = false;
        UpdateSelectedCount();
    }

    private void SetSelected(IEnumerable<PrintedLabelRowViewModel> rows)
    {
        _suppressSelectionCount = true;
        foreach (var r in rows) r.IsSelected = true;
        _suppressSelectionCount = false;
        UpdateSelectedCount();
    }
}
