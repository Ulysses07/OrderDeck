using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Pages;

namespace OrderDeck.App.Views.Shell;

public partial class PageHost : UserControl
{
    public PageHost()
    {
        InitializeComponent();
        // Kabuğun DataContext'i MainShellViewModel; sayfa katmanı yığına
        // bağlanmalı, o yüzden kendi DataContext'ini kendi kuruyor.
        // App.Host boş olan tek durum kompozisyon testi (bkz. MainShellView).
        if (App.Host is not null)
            DataContext = App.Host.Services.GetRequiredService<PageStack>();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
        => (DataContext as PageStack)?.Back();
}
