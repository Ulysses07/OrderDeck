using System.Windows;
using OrderDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.Views;

public partial class BlacklistDialog : Window
{
    public BlacklistDialog()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<BlacklistViewModel>();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
