using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services.Drawers;
using OrderDeck.Core.Sales;

// System.Windows.Controls.Label ile OrderDeck.Core.Sales.Label çakışıyor;
// bu dosyada geçen her "Label" satış etiketi.
using Label = OrderDeck.Core.Sales.Label;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// After a parent Label is cancelled, surfaces its tentative-backup Labels so
/// the operator can confirm one as the new real sale. Confirming flips
/// IsTentativeBackup→0 on the existing row and credits customer aggregates
/// retroactively if the spare sticker had already been printed.
///
/// Faz 3'te modal pencereden çekmeceye geçti; gerekçe XAML'in başında.
/// </summary>
public partial class BackupTransferDrawer : UserControl
{
    private readonly LabelService _labels;
    private readonly Drawer _drawer;
    private readonly System.Action<string>? _onConfirmed;

    public ObservableCollection<BackupRowViewModel> Rows { get; } = new();

    private BackupTransferDrawer(
        Drawer drawer, LabelService labels, Label parentLabel, IReadOnlyList<Label> backups,
        System.Action<string>? onConfirmed)
    {
        InitializeComponent();
        _drawer = drawer;
        _labels = labels;
        _onConfirmed = onConfirmed;
        BackupsList.ItemsSource = Rows;

        Subtitle.Text =
            $"İptal edilen etiketin orijinal fiyatı: " +
            $"{parentLabel.Price.ToString("N2", CultureInfo.CurrentCulture)} TL. " +
            "Bir yedek için fiyatı düzenleyip onaylayabilir, ya da kapatıp daha " +
            "sonra dönebilirsin.";

        foreach (var b in backups)
            Rows.Add(new BackupRowViewModel(b));
    }

    /// <summary>Toplu iptal (K3, "Basılanlar"): birden çok etiketin yedekleri TEK çekmecede — her etiket
    /// için ayrı çekmece açılmaz. Her yedek kartının üstünde hangi etiketin yedeği olduğu yazar.</summary>
    private BackupTransferDrawer(
        Drawer drawer, LabelService labels, IReadOnlyList<(Label Parent, IReadOnlyList<Label> Backups)> groups,
        System.Action<string>? onConfirmed)
    {
        InitializeComponent();
        _drawer = drawer;
        _labels = labels;
        _onConfirmed = onConfirmed;
        BackupsList.ItemsSource = Rows;

        Subtitle.Text =
            $"İptal edilen {groups.Count} etiketin yedekleri. Bir yedek için fiyatı düzenleyip " +
            "onaylayabilir, ya da kapatıp daha sonra müşteri detayından dönebilirsin.";

        foreach (var (parent, backups) in groups)
        {
            var header = $"'{parent.DisplayName ?? parent.Username}' etiketinin yedeği — orijinal fiyat " +
                         $"{parent.Price.ToString("N2", CultureInfo.CurrentCulture)} TL";
            foreach (var b in backups)
                Rows.Add(new BackupRowViewModel(b) { GroupHeader = header });
        }
    }

    /// <summary>Çekmece başlığı çağıranda kuruluyor (yedek sayısı orada
    /// biliniyor), gövde yalnız listeyi ve açıklamayı taşıyor.</summary>
    /// <param name="onConfirmed">Yedek onaylandı (Id) — kabuk kuyruktaki kopyayı hemen günceller.</param>
    public static BackupTransferDrawer Create(
        Drawer drawer, LabelService labels, Label parentLabel, IReadOnlyList<Label> backups,
        System.Action<string>? onConfirmed = null)
        => new(drawer, labels, parentLabel, backups, onConfirmed);

    /// <summary>Toplu iptalin yedek çekmecesi: birden çok etiketin yedekleri tek listede.</summary>
    /// <param name="onConfirmed">Yedek onaylandı (Id) — kabuk kuyruktaki kopyayı hemen günceller.</param>
    public static BackupTransferDrawer Create(
        Drawer drawer, LabelService labels, IReadOnlyList<(Label Parent, IReadOnlyList<Label> Backups)> groups,
        System.Action<string>? onConfirmed = null)
        => new(drawer, labels, groups, onConfirmed);

    private void OnPromote(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not BackupRowViewModel row) return;
        row.Promote((id, price) => _labels.ConfirmBackup(id, newPrice: price), _onConfirmed);
    }
}

/// <summary>Per-row VM so price text + "promoted" toggle are independent.</summary>
public sealed partial class BackupRowViewModel : ObservableObject
{
    /// <summary>The tentative-backup Label this row represents.</summary>
    public Label Backup { get; }

    [ObservableProperty] private string _priceText;
    [ObservableProperty] private bool _isActionable = true;
    [ObservableProperty] private string _actionButtonText = "Onayla";
    [ObservableProperty] private string? _errorText;

    /// <summary>Toplu iptalde kartın hangi etiketin yedeği olduğu; tek etiketli çekmecede null.</summary>
    public string? GroupHeader { get; init; }

    public string DisplayName => Backup.Username;
    public string MessageText => string.IsNullOrEmpty(Backup.MessageText) ? "—" : Backup.MessageText;

    /// <summary>Marka ikonu şablonunu seçen ayrım — kartın DataContext'i bu VM
    /// olduğu için etiketteki değeri buradan yüzeye çıkarıyoruz.</summary>
    public string Platform => Backup.Platform;

    public BackupRowViewModel(Label backup)
    {
        Backup = backup;
        _priceText = backup.Price.ToString("N2", CultureInfo.CurrentCulture);
    }

    /// <summary>0 TL ikinci "Onayla"yı bekliyor (düğme "Evet, 0 TL" olur).</summary>
    private bool _zeroConfirmPending;

    /// <summary>Kartın fiyatı — FİYAT kutusuyla AYNI kural (K8; eskiden burada Türkçe biçim önce
    /// deneniyordu, kutuda ise tersi — aynı metin iki ekranda iki farklı fiyattı). Eksi ya da okunamayan
    /// fiyat satır içi reddedilir; 0 TL, kutudaki gibi satır içi onaylanır: ilk tıklama sorar, ikincisi
    /// yazar. Yazılacak fiyat ya da (hata/onay bekleniyor) null.</summary>
    public decimal? ReadPrice()
    {
        var parsed = PriceInput.Parse(PriceText);
        if (!parsed.IsOk)
        {
            ErrorText = parsed.Status == PriceInputStatus.Negative
                ? "Fiyat eksi olamaz."
                : "Geçerli bir fiyat gir (örn: 250 veya 250,00).";
            return null;
        }
        if (parsed.Value == 0m && !_zeroConfirmPending)
        {
            _zeroConfirmPending = true;
            ErrorText = "Fiyat 0 TL — doğruysa tekrar onayla.";
            ActionButtonText = "Evet, 0 TL";
            return null;
        }
        return parsed.Value;
    }

    partial void OnPriceTextChanged(string value)
    {
        if (!_zeroConfirmPending) return;
        _zeroConfirmPending = false;
        ErrorText = null;
        ActionButtonText = "Onayla";
    }

    public void MarkPromoted()
    {
        IsActionable = false;
        ActionButtonText = "Onaylandı";
        ErrorText = null;
    }

    /// <summary>Kartın "Onayla"sı: fiyat okunur (<see cref="ReadPrice"/>), yedek onaylanır
    /// (<paramref name="confirm"/>), sonra <paramref name="confirmed"/> bildirilir — kabuk yedeğin
    /// kuyruktaki kopyasını HEMEN günceller. Çekmece açıkken de "Yazdır"a basılabiliyor; kopya çekmece
    /// kapanınca güncellenseydi arada kâğıda eski fiyat basılır, ciroya yeni fiyat yazılırdı. Hata
    /// MessageBox değil, kartın kendi satırı (spec §6: hiçbir şey pop-up değil).</summary>
    public void Promote(System.Action<string, decimal> confirm, System.Action<string>? confirmed)
    {
        if (ReadPrice() is not { } price) return;
        try
        {
            confirm(Backup.Id, price);
        }
        catch (System.Exception ex)
        {
            ErrorText = $"Yedek onaylanamadı: {ex.Message}";
            return;
        }
        MarkPromoted();
        confirmed?.Invoke(Backup.Id);
    }
}
