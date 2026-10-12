using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly Func<string, IReadOnlyCollection<string>, IReadOnlySet<string>>? _requestedCustomers;
    private bool _suppressSelectionCount;

    /// <param name="requestedCustomers">(yayın, müşteri Id'leri) → teslim edilmiş ödeme isteği olan
    /// müşteriler ("ödeme istendi" rozeti). Null = rozet hesaplanmaz.</param>
    public PrintedLabelsViewModel(
        LabelRepository labels,
        Func<string, IReadOnlyCollection<string>, IReadOnlySet<string>>? requestedCustomers = null)
    {
        _labels = labels;
        _requestedCustomers = requestedCustomers;
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

    /// <summary>Verilen yayının listesini (yeniden) kurar; seçim Id'ye göre korunur.</summary>
    /// <param name="withPaymentStatus">"ödeme istendi" rozeti için kişi başına ödeme işi taranır mı. Sekme
    /// kapalıyken her "Yazdır"dan sonra yalnız liste kurulur (tek sorgu).</param>
    public void Load(string? sessionId, bool withPaymentStatus = true)
    {
        var keepSelected = string.Equals(sessionId, SessionId, StringComparison.Ordinal)
            ? Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        SessionId = sessionId;

        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        VisibleRows.Clear();
        if (sessionId is null)
        {
            TotalCount = 0;
            UpdateSelectedCount();
            return;
        }

        var rows = _labels.GetPrintedBySession(sessionId);
        var requested = (withPaymentStatus ? _requestedCustomers : null)?.Invoke(sessionId,
                rows.Select(r => r.Label.CustomerId).Distinct(StringComparer.Ordinal).ToList())
            ?? new HashSet<string>();

        // Grup: basış kimliği (048 öncesi: aynı PrintedAt). En yeni basış önce; grubun içi kâğıt sırası
        // (048 öncesi: AddedAt, Id).
        var groups = rows
            .GroupBy(r => r.BatchKey, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, PrintedAt: g.Min(r => r.Label.PrintedAt!.Value), Items: g
                .OrderBy(r => r.PrintSeq ?? int.MaxValue)
                .ThenBy(r => r.Label.AddedAt)
                .ThenBy(r => r.Label.Id, StringComparer.Ordinal)
                .ToList()))
            .OrderByDescending(g => g.PrintedAt)
            .ThenByDescending(g => g.Key, StringComparer.Ordinal);

        _suppressSelectionCount = true;
        foreach (var g in groups)
        {
            var batch = new PrintBatchViewModel(g.Key, g.PrintedAt);
            for (var i = 0; i < g.Items.Count; i++)
            {
                var row = g.Items[i];
                var vm = new PrintedLabelRowViewModel(row, batch, row.PrintSeq ?? i + 1, StatusOf(row, requested))
                {
                    IsSelected = keepSelected.Contains(row.Label.Id),
                };
                vm.PropertyChanged += OnRowChanged;
                Rows.Add(vm);
            }
        }
        _suppressSelectionCount = false;
        TotalCount = Rows.Count;
        RebuildVisible();
        UpdateSelectedCount();
    }

    private void RebuildVisible()
    {
        VisibleRows.Clear();
        foreach (var r in Rows)
            if (Matches(r)) VisibleRows.Add(r);
    }

    public void Refresh() => Load(SessionId);

    private static PrintedLabelStatus StatusOf(PrintedLabelRow row, IReadOnlySet<string> requested)
    {
        if (row.ShipmentStatus == ShipmentStatus.Shipped) return PrintedLabelStatus.Shipped;
        // Dekont kaydedilince etiket açık kargo dosyasına bağlanır (DekontEkleViewModel): ödendi.
        if (row.Label.ShipmentId is not null) return PrintedLabelStatus.Paid;
        if (row.Label.IsTentativeBackup) return PrintedLabelStatus.Backup;
        if (requested.Contains(row.Label.CustomerId)) return PrintedLabelStatus.PaymentRequested;
        return PrintedLabelStatus.None;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrintedLabelRowViewModel.IsSelected) && !_suppressSelectionCount)
            UpdateSelectedCount();
    }

    private void UpdateSelectedCount() => SelectedCount = Rows.Count(r => r.IsSelected);

    /// <summary>"Bu baskıyı seç": grubun (görünen) bütün satırları seçime eklenir.</summary>
    [RelayCommand]
    private void SelectBatch(PrintBatchViewModel? batch)
    {
        if (batch is null) return;
        SetSelected(Rows.Where(r => ReferenceEquals(r.Batch, batch) && Matches(r)));
    }

    /// <summary>"Bu kodu seç": yayındaki aynı koddaki BÜTÜN basılmış etiketler, gruplar arası. Arama kutusu
    /// koda ayarlanır — seçilenlerin hepsi görünür olsun.</summary>
    [RelayCommand]
    private void SelectCode(PrintedLabelRowViewModel? row)
    {
        var code = row?.Label.Code;
        if (string.IsNullOrWhiteSpace(code)) return;
        SearchText = code;
        SetSelected(Rows.Where(r => string.Equals(r.Label.Code, code, StringComparison.OrdinalIgnoreCase)));
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
