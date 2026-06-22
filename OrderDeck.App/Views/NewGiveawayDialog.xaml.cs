using System.Windows;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Settings;

namespace OrderDeck.App.Views;

public partial class NewGiveawayDialog : Window
{
    public NewGiveawayDialogViewModel ViewModel { get; }

    public NewGiveawayDialog()
    {
        InitializeComponent();
        ViewModel = new NewGiveawayDialogViewModel();
        DataContext = ViewModel;
    }

    public NewGiveawayDialog(AppSettings settings, AnimationCatalogClient? catalogClient = null)
    {
        InitializeComponent();
        ViewModel = new NewGiveawayDialogViewModel(settings, catalogClient);
        DataContext = ViewModel;
    }

    // Kazanan sayısı stepper'ı — VM'e komut eklemeden, ObservableProperty
    // setter'ı üzerinden 1-50 aralığında ayarlar (Validate ile aynı sınır).
    private void OnWinnerInc(object sender, RoutedEventArgs e) =>
        ViewModel.WinnerCount = System.Math.Min(50, ViewModel.WinnerCount + 1);

    private void OnWinnerDec(object sender, RoutedEventArgs e) =>
        ViewModel.WinnerCount = System.Math.Max(1, ViewModel.WinnerCount - 1);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Validate()) return;
        ViewModel.MarkSaved();
        DialogResult = true;
        Close();
    }
}
