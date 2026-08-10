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

    public ObservableCollection<BackupRowViewModel> Rows { get; } = new();

    private BackupTransferDrawer(
        Drawer drawer, LabelService labels, Label parentLabel, IReadOnlyList<Label> backups)
    {
        InitializeComponent();
        _drawer = drawer;
        _labels = labels;
        BackupsList.ItemsSource = Rows;

        Subtitle.Text =
            $"İptal edilen etiketin orijinal fiyatı: " +
            $"{parentLabel.Price.ToString("N2", CultureInfo.CurrentCulture)} TL. " +
            "Bir yedek için fiyatı düzenleyip onaylayabilir, ya da kapatıp daha " +
            "sonra dönebilirsin.";

        foreach (var b in backups)
            Rows.Add(new BackupRowViewModel(b));
    }

    /// <summary>Çekmece başlığı çağıranda kuruluyor (yedek sayısı orada
    /// biliniyor), gövde yalnız listeyi ve açıklamayı taşıyor.</summary>
    public static BackupTransferDrawer Create(
        Drawer drawer, LabelService labels, Label parentLabel, IReadOnlyList<Label> backups)
        => new(drawer, labels, parentLabel, backups);

    private void OnPromote(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not BackupRowViewModel row) return;

        // Hata artık MessageBox değil, kartın kendi satırı — spec §6:
        // hiçbir şey pop-up değil.
        if (!decimal.TryParse(row.PriceText, NumberStyles.Number,
                CultureInfo.CurrentCulture, out var price)
            && !decimal.TryParse(row.PriceText, NumberStyles.Number,
                CultureInfo.InvariantCulture, out price))
        {
            row.ErrorText = "Geçerli bir fiyat gir (örn: 250 veya 250,00).";
            return;
        }

        try
        {
            _labels.ConfirmBackup(row.Backup.Id, newPrice: price);
            row.MarkPromoted();
        }
        catch (System.Exception ex)
        {
            row.ErrorText = $"Yedek onaylanamadı: {ex.Message}";
        }
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

    public void MarkPromoted()
    {
        IsActionable = false;
        ActionButtonText = "Onaylandı";
        ErrorText = null;
    }
}
