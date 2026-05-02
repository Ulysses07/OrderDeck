using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Views;

/// <summary>
/// After a parent Label is cancelled, surfaces its tentative-backup Labels so
/// the operator can confirm one as the new real sale. Confirming flips
/// IsTentativeBackup→0 on the existing row and credits customer aggregates
/// retroactively if the spare sticker had already been printed.
/// </summary>
public partial class BackupTransferDialog : Window
{
    private readonly LabelService _labels;

    public ObservableCollection<BackupRowViewModel> Rows { get; } = new();

    public BackupTransferDialog(LabelService labels)
    {
        _labels = labels;
        InitializeComponent();
        BackupsList.ItemsSource = Rows;
    }

    public void Load(Label parentLabel, IReadOnlyList<Label> backups)
    {
        HeaderTitle.Text = $"'{parentLabel.Username}' etiketinin yedekleri ({backups.Count})";
        HeaderSubtitle.Text =
            $"İptal edilen etiketin orijinal fiyatı: {parentLabel.Price.ToString("N2", CultureInfo.CurrentCulture)} TL. " +
            "Bir yedek için fiyatı düzenleyip 'Bu satışı onayla' diyebilir, ya da kapatıp daha sonra dönebilirsin.";
        Rows.Clear();
        foreach (var b in backups)
            Rows.Add(new BackupRowViewModel(b));
    }

    private void OnPromote(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.DataContext is not BackupRowViewModel row) return;

        if (!decimal.TryParse(row.PriceText, NumberStyles.Number,
                CultureInfo.CurrentCulture, out var price)
            && !decimal.TryParse(row.PriceText, NumberStyles.Number,
                CultureInfo.InvariantCulture, out price))
        {
            MessageBox.Show("Geçerli bir fiyat gir (örn: 250 veya 250,00).",
                "Geçersiz fiyat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _labels.ConfirmBackup(row.Backup.Id, newPrice: price);
            row.MarkPromoted();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Yedek onaylanamadı: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

/// <summary>Per-row VM so price text + "promoted" toggle are independent.</summary>
public sealed partial class BackupRowViewModel : ObservableObject
{
    /// <summary>The tentative-backup Label this row represents.</summary>
    public Label Backup { get; }

    [ObservableProperty] private string _priceText;
    [ObservableProperty] private bool _isActionable = true;
    [ObservableProperty] private string _actionButtonText = "Bu satışı onayla";

    public string DisplayName => Backup.Username;
    public string MessageText => string.IsNullOrEmpty(Backup.MessageText) ? "—" : Backup.MessageText;

    public string PlatformIcon => Backup.Platform switch
    {
        "instagram" => "📷",
        "tiktok"    => "🎵",
        "facebook"  => "👥",
        "youtube"   => "▶️",
        _           => "💬"
    };

    public BackupRowViewModel(Label backup)
    {
        Backup = backup;
        _priceText = backup.Price.ToString("N2", CultureInfo.CurrentCulture);
    }

    public void MarkPromoted()
    {
        IsActionable = false;
        ActionButtonText = "✓ Onaylandı";
    }
}
