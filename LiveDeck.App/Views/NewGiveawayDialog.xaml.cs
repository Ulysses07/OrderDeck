using System.Windows;
using LiveDeck.App.ViewModels;

namespace LiveDeck.App.Views;

public partial class NewGiveawayDialog : Window
{
    public NewGiveawayDialogViewModel ViewModel { get; }

    public NewGiveawayDialog()
    {
        InitializeComponent();
        ViewModel = new NewGiveawayDialogViewModel();
        DataContext = ViewModel;
    }

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
