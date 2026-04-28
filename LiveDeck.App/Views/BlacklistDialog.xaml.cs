using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class BlacklistDialog : Window
{
    public BlacklistDialog()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<BlacklistViewModel>();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
